"""Combine rest skeletons without changing the original armature data."""

from dataclasses import dataclass
from math import isfinite

import bpy
from mathutils import Matrix

from ..instant_edit.context import CONTEXT_METADATA_FIELDS


def available_armatures(context):
    """Include hidden rigs, but not objects excluded from the active view layer."""
    return sorted(
        (obj for obj in context.view_layer.objects if obj.type == "ARMATURE"),
        key=lambda obj: obj.name.casefold(),
    )


@dataclass
class _MeshBinding:
    obj: object
    parent: object
    parent_type: str
    parent_bone: str
    parent_inverse: object
    effective_parent: object
    modifiers: tuple

    @classmethod
    def capture(cls, context, obj, originals):
        return cls(
            obj, obj.parent, obj.parent_type, obj.parent_bone,
            obj.matrix_parent_inverse.copy(),
            (_parent_matrix(context, obj.parent, obj.parent_type, obj.parent_bone)
             if obj.parent in originals else None),
            tuple((mod, mod.object) for mod in obj.modifiers
                  if mod.type == "ARMATURE" and mod.object in originals),
        )

    def restore(self):
        self.obj.parent = self.parent
        self.obj.parent_type = self.parent_type
        self.obj.parent_bone = self.parent_bone
        self.obj.matrix_parent_inverse = self.parent_inverse
        for modifier, original in self.modifiers:
            modifier.object = original


def _validate_transform(obj):
    if not all(isfinite(value) for row in obj.matrix_world for value in row):
        raise ValueError(f'Armature "{obj.name}" has a non-finite transform.')
    try:
        return obj.matrix_world.inverted()
    except ValueError as error:
        raise ValueError(f'Armature "{obj.name}" has a non-invertible transform.') from error


def _parent_matrix(context, parent, parent_type, parent_bone):
    """Let Blender evaluate bone-parent offsets and relative parenting rules."""
    probe = bpy.data.objects.new("Combined Armature Parent Probe", None)
    try:
        context.scene.collection.objects.link(probe)
        probe.parent = parent
        probe.parent_type = parent_type
        probe.parent_bone = parent_bone
        probe.matrix_parent_inverse = Matrix.Identity(4)
        probe.matrix_basis = Matrix.Identity(4)
        context.view_layer.update()
        return probe.matrix_world.copy()
    finally:
        bpy.data.objects.remove(probe, do_unlink=True)


def _copy_bone(source, target, transform):
    # Bone.matrix_local supplies the rest orientation, including roll. Copy the
    # length first because setting EditBone.matrix does not set its length.
    target.head = source.head_local
    target.tail = source.tail_local
    target.matrix = source.matrix_local
    # Copy the shared scalar/array settings rather than a version-specific list
    # of B-Bone, envelope, and inheritance properties. Geometry and references
    # are handled separately, after all missing bones exist.
    excluded = {"name", "head", "tail", "length", "matrix", "roll", "use_connect"}
    for prop in target.bl_rna.properties:
        name = prop.identifier
        if (name in excluded or prop.is_readonly
                or prop.type not in {"BOOLEAN", "INT", "FLOAT", "STRING", "ENUM"}
                or name not in source.bl_rna.properties):
            continue
        value = getattr(source, name)
        setattr(target, name, tuple(value) if getattr(prop, "is_array", False) else value)
    for name, value in source.items():
        target[name] = value
    target.transform(transform, scale=True, roll=False)
    # Blender's Python EditBone.transform applies a 4x4 matrix to its roll
    # direction as a point, including translation. Transform that direction
    # with the linear part explicitly so translated rigs retain their roll.
    target.align_roll(transform.to_3x3() @ source.matrix_local.to_3x3().col[2])


def _append_bones(result, additional, transform):
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        bones = result.data.edit_bones
        missing = [bone for bone in additional.data.bones if bone.name not in bones]
        for source in missing:
            _copy_bone(source, bones.new(source.name), transform)
        for source in missing:
            target = bones[source.name]
            if source.parent:
                target.parent = bones[source.parent.name]
                target.use_connect = (
                    source.use_connect
                    and (target.head - target.parent.tail).length <= 1e-6
                )
            for name in ("bbone_custom_handle_start", "bbone_custom_handle_end"):
                handle = getattr(source, name)
                if handle:
                    setattr(target, name, bones[handle.name])
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")


def _reassign_mesh(binding, originals, result, context):
    obj = binding.obj
    if binding.parent in originals:
        parent_matrix = _parent_matrix(context, result, binding.parent_type, binding.parent_bone)
        obj.parent = result
        # Preserve the complete parent contribution instead of decomposing the
        # mesh's world matrix. This also retains shear, zero mesh scales, local
        # transform channels, and the input to existing mesh constraints.
        obj.matrix_parent_inverse = (
            parent_matrix.inverted() @ binding.effective_parent @ binding.parent_inverse
        )
    for modifier, _original in binding.modifiers:
        modifier.object = result


def _same_matrix(a, b, tolerance=1e-4):
    return all(abs(x - y) <= tolerance for row_a, row_b in zip(a, b) for x, y in zip(row_a, row_b))


def matching_layout(base, additional):
    """True when two armatures sit in the same place and every bone they share has the same
    parent and rest transform, so either one's meshes deform the same on the other."""
    if not _same_matrix(base.matrix_world, additional.matrix_world):
        return False
    shared = [bone for bone in additional.data.bones if bone.name in base.data.bones]
    if not shared:
        return False
    for bone in shared:
        other = base.data.bones[bone.name]
        if (bone.parent.name if bone.parent else None) != (other.parent.name if other.parent else None):
            return False
        if not _same_matrix(bone.matrix_local, other.matrix_local):
            return False
    return True


def merge_into(context, base, additional, meshes):
    """Move meshes from additional onto base, adding the bones base lacks, then delete
    additional. Only for armatures that pass matching_layout."""
    if any(bone.name not in base.data.bones for bone in additional.data.bones):
        active = context.view_layer.objects.active
        context.view_layer.objects.active = base
        try:
            _append_bones(base, additional, Matrix.Identity(4))
        finally:
            context.view_layer.objects.active = active
    # Every child moves, not only the meshes: deleting additional would drop the rest to the
    # world origin. Both armatures share a world matrix, so each child's parent inverse still
    # holds, but setting the parent resets it.
    for obj in [*additional.children, *(obj for obj in meshes if obj.parent != additional)]:
        if obj.parent == additional:
            parent_inverse = obj.matrix_parent_inverse.copy()
            obj.parent = base
            obj.matrix_parent_inverse = parent_inverse
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE" and modifier.object == additional:
                modifier.object = base
    data = additional.data
    bpy.data.objects.remove(additional, do_unlink=True)
    if data.users == 0:
        bpy.data.armatures.remove(data)


def combine_armatures(context, base, additional):
    """Return (new rig, reassigned mesh count), restoring all state on failure."""
    if context.mode != "OBJECT":
        raise ValueError("Switch to Object Mode to combine armatures.")
    candidates = available_armatures(context)
    if base is None or additional is None or base not in candidates or additional not in candidates:
        raise ValueError("Choose two armatures in the active view layer.")
    if base == additional:
        raise ValueError("Choose two different armatures.")
    context.view_layer.update()
    base_inverse = _validate_transform(base)
    _validate_transform(additional)
    originals = (base, additional)
    visible = tuple(context.visible_objects)
    bindings = [
        _MeshBinding.capture(context, obj, originals) for obj in visible
        if obj.type == "MESH" and (
            obj.parent in originals or any(
                mod.type == "ARMATURE" and mod.object in originals for mod in obj.modifiers
            )
        )
    ]
    for binding in bindings:
        if not binding.obj.is_editable or binding.obj.override_library is not None:
            raise ValueError(f'Mesh "{binding.obj.name}" must be local and editable before reassignment.')

    selected = tuple(context.selected_objects)
    active = context.view_layer.objects.active
    hidden = [(obj, obj.hide_get(view_layer=context.view_layer)) for obj in originals]
    result = data = None
    try:
        data = base.data.copy()
        data.animation_data_clear()
        for name in CONTEXT_METADATA_FIELDS:
            for key in (name, f"instant_edit_{name}"):
                if key in data:
                    del data[key]
        data.pose_position = "POSE"
        # A fresh object gives neutral pose channels and no copied constraints,
        # drivers, object parents, actions, or import-context identity.
        result = bpy.data.objects.new(f"{base.name} Combined", data)
        context.scene.collection.objects.link(result)
        result.matrix_world = base.matrix_world.copy()
        result.show_in_front = base.show_in_front
        space = context.space_data
        if space and space.type == "VIEW_3D" and space.local_view:
            result.local_view_set(space, True)
        for obj in selected:
            obj.select_set(False)
        result.select_set(True)
        context.view_layer.objects.active = result
        _append_bones(result, additional, base_inverse @ additional.matrix_world)
        context.view_layer.update()
        for binding in bindings:
            _reassign_mesh(binding, originals, result, context)
        for obj in originals:
            obj.hide_set(True, view_layer=context.view_layer)
        context.view_layer.update()
        return result, len(bindings)
    except Exception:
        if result is not None and result.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        for binding in bindings:
            binding.restore()
        for obj, was_hidden in hidden:
            obj.hide_set(was_hidden, view_layer=context.view_layer)
        if result is not None:
            bpy.data.objects.remove(result, do_unlink=True)
        if data is not None and data.users == 0:
            bpy.data.armatures.remove(data)
        for obj in selected:
            obj.select_set(True)
        context.view_layer.objects.active = active
        context.view_layer.update()
        raise
