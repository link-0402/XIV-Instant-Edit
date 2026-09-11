"""Armature combination cases shared by the Blender smoke/correctness suites."""

import importlib
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

import bpy
from mathutils import Euler, Matrix

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data


def rig(name, bones=None):
    data = bpy.data.armatures.new(name + " Data")
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    for selected in tuple(bpy.context.selected_objects):
        selected.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        for name, head, tail, parent, connected in bones or [
            ("root", (0, 0, 0), (0, 0, 1), None, False),
        ]:
            bone = data.edit_bones.new(name)
            bone.head, bone.tail = head, tail
            bone.roll = 0.37
            if parent:
                bone.parent = data.edit_bones[parent]
                bone.use_connect = connected
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def mesh(name, parent=None, modifier_rigs=()):
    data = bpy.data.meshes.new(name + " Data")
    data.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    for index, armature in enumerate(modifier_rigs):
        modifier = obj.modifiers.new(f"Rig {index}", "ARMATURE")
        modifier.object = armature
        modifier.use_deform_preserve_volume = True
    obj.vertex_groups.new(name="root").add([0, 1, 2], 1.0, "REPLACE")
    obj.vertex_groups.new(name="Untouched Empty Group")
    obj["context_id"] = "mesh-original-context"
    obj["xiv_material"] = "/mt_c0101e0001_top_a.mtrl"
    obj.data.uv_layers.new(name="uv0").uv.foreach_set("vector", [0, 0, 1, 0, 0, 1])
    return obj


def require_matrix(actual, expected, message):
    assert max(abs(a - b) for row_a, row_b in zip(actual, expected)
               for a, b in zip(row_a, row_b)) < 2e-5, message


def mesh_payload(obj):
    return (
        obj.name, obj.data.as_pointer(), tuple(c.as_pointer() for c in obj.users_collection),
        tuple((g.name, g.lock_weight) for g in obj.vertex_groups),
        tuple(tuple((g.group, g.weight) for g in v.groups) for v in obj.data.vertices),
        dict(obj.items()),
    )


def assert_combination(addon):
    helper = importlib.import_module(f"{addon.__name__}.mesh.armatures")
    context_module = importlib.import_module(f"{addon.__name__}.instant_edit.context")
    with temporary_scene_data():
        base = rig("Combine Base")
        additional = rig("Combine Extra", [
            ("root", (0, 3, 0), (0, 3, 1), None, False),
            ("extra", (0, 3, 1), (0, 3, 2), "root", True),
            ("tip", (0, 3, 2), (0, 3, 3), "extra", True),
        ])
        other = rig("Unrelated Rig")
        base.matrix_world = Matrix.LocRotScale((2, -1, 0.5), Euler((0.2, 0.1, 0.4)), (1.2, 1.2, 1.2))
        additional.matrix_world = Matrix.LocRotScale((-2, 0.5, 1), Euler((0.1, 0.3, -0.2)), (1.5, 1.5, 1.5))
        additional.data.bones["extra"].use_deform = False
        additional.data.bones["extra"].inherit_scale = "NONE"
        additional.data.bones["extra"].use_inherit_rotation = False
        additional.data.bones["extra"]["source_marker"] = 12
        additional.data.bones["tip"].bbone_segments = 3
        additional.data.bones["tip"].bbone_custom_handle_start = additional.data.bones["extra"]
        base["context_id"] = "base-context"
        base.data["instant_edit_context_id"] = "base-context"
        base.pose.bones["root"].rotation_mode = "XYZ"
        base.pose.bones["root"].rotation_euler = (0.2, 0.1, 0.3)
        base.pose.bones["root"].constraints.new("LIMIT_ROTATION")
        base.pose.bones["root"].keyframe_insert("rotation_euler", frame=1)
        base.driver_add("location", 0).driver.expression = "2"
        base.data.pose_position = "REST"
        # An excluded collection must not prevent the new rig being visible.
        hidden_collection = bpy.data.collections.new("Hidden Combine Meshes")
        bpy.context.scene.collection.children.link(hidden_collection)
        hidden_collection.hide_viewport = True
        parented = mesh("Parent Only", base)
        modified = mesh("Modifier Only", modifier_rigs=(additional,))
        both = mesh("Both Bindings", additional, (base, additional, other))
        bone_parented = mesh("Bone Parent", additional)
        bone_parented.parent_type = "BONE"
        bone_parented.parent_bone = "extra"
        bone_parented.location = (0.4, 0.2, -0.3)
        bone_parented.rotation_euler = (0.2, 0, 0.1)
        bone_parented.scale = (1, 0.7, 1.2)
        relative = mesh("Relative Bone Parent", base)
        relative.parent_type = "BONE"
        relative.parent_bone = "root"
        base.data.bones["root"].use_relative_parent = True
        hidden = mesh("Hidden Mesh", base, (additional,))
        hidden.hide_set(True)
        collection_hidden = mesh("Collection Hidden Mesh", additional)
        bpy.context.scene.collection.objects.unlink(collection_hidden)
        hidden_collection.objects.link(collection_hidden)
        unrelated = mesh("Unrelated Mesh", other, (other,))
        constrained = mesh("Constrained Parent", base)
        constraint = constrained.constraints.new("COPY_LOCATION")
        constraint.target = other
        constraint.influence = 0.3
        zero_scaled = mesh("Zero Scale Mesh", base)
        zero_scaled.scale = (0, 1, 1)
        linked_data = bpy.data.objects.new("Shared Mesh Data", parented.data)
        bpy.context.scene.collection.objects.link(linked_data)
        linked_data.parent = base
        additional.hide_set(True)
        bpy.context.view_layer.update()
        affected = (parented, modified, both, bone_parented, relative, constrained, zero_scaled, linked_data)
        all_meshes = (*affected, hidden, collection_hidden, unrelated)
        worlds = {obj: obj.matrix_world.copy() for obj in all_meshes}
        payloads = {obj: mesh_payload(obj) for obj in all_meshes}
        base_matrix = base.data.bones["root"].matrix_local.copy()
        source_matrices = {b.name: additional.matrix_world @ b.matrix_local for b in additional.data.bones}
        source_heads = {b.name: additional.matrix_world @ b.head_local for b in additional.data.bones}
        source_tails = {b.name: additional.matrix_world @ b.tail_local for b in additional.data.bones}
        base_action = base.animation_data.action
        rigs_before = len(bpy.data.armatures)
        assert additional in helper.available_armatures(bpy.context), "hidden rig missing from selector"
        result = bpy.ops.xiv_ie.combine_armatures(base_armature=base.name, additional_armature=additional.name)
        assert result == {"FINISHED"}
        combined = bpy.context.view_layer.objects.active
        assert combined.type == "ARMATURE" and combined not in (base, additional)
        assert combined.select_get() and combined.visible_get()
        assert set(combined.users_collection) == {bpy.context.scene.collection}
        assert len(bpy.data.armatures) == rigs_before + 1
        assert combined.data not in (base.data, additional.data)
        assert set(combined.data.bones.keys()) == {"root", "extra", "tip"}
        require_matrix(combined.data.bones["root"].matrix_local, base_matrix, "base shared bone changed")
        for name in ("extra", "tip"):
            actual = combined.data.bones[name]
            assert (combined.matrix_world @ actual.head_local - source_heads[name]).length < 2e-5
            assert (combined.matrix_world @ actual.tail_local - source_tails[name]).length < 2e-5
            # Matrix columns are unit length locally; compare world roll axes.
            actual_axis = (combined.matrix_world @ actual.matrix_local).to_3x3().col[2].normalized()
            expected_axis = source_matrices[name].to_3x3().col[2].normalized()
            assert actual_axis.dot(expected_axis) > 0.99999, (name, tuple(actual_axis), tuple(expected_axis))
        assert combined.data.bones["extra"].parent.name == "root"
        assert not combined.data.bones["extra"].use_connect, "mismatched base endpoint snapped added bone"
        assert combined.data.bones["tip"].use_connect
        assert not combined.data.bones["extra"].use_deform
        assert not combined.data.bones["extra"].use_inherit_rotation
        assert combined.data.bones["extra"].inherit_scale == "NONE"
        assert combined.data.bones["extra"]["source_marker"] == 12
        assert combined.data.bones["tip"].bbone_segments == 3
        assert combined.data.bones["tip"].bbone_custom_handle_start == combined.data.bones["extra"]
        assert combined.animation_data is None and combined.data.animation_data is None
        assert not context_module.context_id_for_object(combined)
        assert "instant_edit_context_id" not in combined.data
        for bone in combined.pose.bones:
            require_matrix(bone.matrix_basis, Matrix.Identity(4), "pose transferred")
            assert not bone.constraints
        assert base.hide_get() and additional.hide_get()
        assert base.animation_data.action == base_action and base.animation_data.drivers
        assert base.pose.bones["root"].constraints and base.data.pose_position == "REST"
        assert set(base.data.bones.keys()) == {"root"}
        assert additional.data.bones["extra"].use_connect
        for obj in all_meshes:
            assert mesh_payload(obj) == payloads[obj], f"{obj.name}: geometry/groups/context changed"
            require_matrix(obj.matrix_world, worlds[obj], f"{obj.name}: world transform changed")
        assert parented.parent == combined and not parented.modifiers
        assert modified.parent is None and modified.modifiers[0].object == combined
        assert both.parent == combined
        assert [m.object for m in both.modifiers] == [combined, combined, other]
        assert all(m.use_deform_preserve_volume for m in both.modifiers)
        assert bone_parented.parent == combined and bone_parented.parent_bone == "extra"
        assert bone_parented.parent_type == "BONE"
        assert hidden.parent == base and hidden.modifiers[0].object == additional
        assert collection_hidden.parent == additional and unrelated.parent == other
        assert hidden_collection.hide_viewport
        print("[PASS] Combined rest bones preserve names/hierarchy, visible bindings, weights, and original rigs")


def assert_combination_export(addon):
    exporter = importlib.import_module(f"{addon.__name__}.mesh.export")
    model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
    with temporary_scene_data(), tempfile.TemporaryDirectory() as temporary:
        base = rig("Export Body")
        additional = rig("Export Extras", [
            ("root", (0, 0, 0), (0, 0, 1), None, False),
            ("new_weight_bone", (0, 0, 1), (0, 0, 2), "root", True),
        ])
        obj = mesh("0.0 Combined Export", base, (base,))
        assert bpy.ops.xiv_ie.combine_armatures(base_armature=base.name, additional_armature=additional.name) == {"FINISHED"}
        assert "new_weight_bone" not in obj.vertex_groups
        obj.vertex_groups["root"].remove([0])
        obj.vertex_groups.new(name="new_weight_bone").add([0], 1.0, "REPLACE")
        target = Path(temporary) / "combined"
        exporter.export_result(target, "MDL", export_objects=[obj])
        exported = model_module.XIVModel.from_file(target.with_suffix(".mdl"))
        assert {"root", "new_weight_bone"}.issubset(set(exported.bones))
        assert exported.lods[0].mesh_count == 1
        print("[PASS] Manually weighted added bone survives combined-armature MDL export/round trip")


def assert_combination_failures(addon):
    helper = importlib.import_module(f"{addon.__name__}.mesh.armatures")
    with temporary_scene_data():
        base = rig("Failure Base")
        additional = rig("Failure Additional", [("added", (0, 0, 0), (0, 1, 0), None, False)])
        first = mesh("Failure First", base, (base,))
        second = mesh("Failure Second", additional, (additional,))
        first.rotation_euler = (8.0, 0.2, -7.0)
        first.scale = (-1.0, 2.0, 1.0)
        first.delta_location = (0.3, 0.2, 0.1)
        local_channels = (tuple(first.rotation_euler), tuple(first.scale), tuple(first.delta_location))
        additional.hide_set(True)
        base.select_set(True)
        first.select_set(True)
        bpy.context.view_layer.objects.active = first
        bpy.context.view_layer.update()
        objects_before = {obj.as_pointer() for obj in bpy.data.objects}
        data_before = {data.as_pointer() for data in bpy.data.armatures}
        selection_before = set(bpy.context.selected_objects)
        worlds = [obj.matrix_world.copy() for obj in (first, second)]
        def unchanged():
            assert {obj.as_pointer() for obj in bpy.data.objects} == objects_before
            assert {data.as_pointer() for data in bpy.data.armatures} == data_before
            assert first.parent == base and first.modifiers[0].object == base
            assert second.parent == additional and second.modifiers[0].object == additional
            assert set(bpy.context.selected_objects) == selection_before
            assert bpy.context.view_layer.objects.active == first
            assert not base.hide_get() and additional.hide_get()
            assert bpy.context.mode == "OBJECT"
            assert (tuple(first.rotation_euler), tuple(first.scale), tuple(first.delta_location)) == local_channels
            for obj, world in zip((first, second), worlds):
                require_matrix(obj.matrix_world, world, "rollback moved mesh")
        for left, right in ((base, base), (base, None), (first, additional)):
            try:
                helper.combine_armatures(bpy.context, left, right)
            except ValueError:
                pass
            else:
                raise AssertionError("invalid armature selection accepted")
            unchanged()
        additional.scale = (0, 1, 1)
        try:
            helper.combine_armatures(bpy.context, base, additional)
        except ValueError as error:
            assert "non-invertible" in str(error)
        else:
            raise AssertionError("singular armature accepted")
        additional.scale = (1, 1, 1)
        bpy.context.view_layer.update()
        unchanged()
        bpy.context.view_layer.objects.active = base
        bpy.ops.object.mode_set(mode="EDIT")
        try:
            assert not bpy.ops.xiv_ie.combine_armatures.poll()
            try:
                helper.combine_armatures(bpy.context, base, additional)
            except ValueError as error:
                assert "Object Mode" in str(error)
            else:
                raise AssertionError("Edit Mode combination accepted")
        finally:
            bpy.ops.object.mode_set(mode="OBJECT")
            bpy.context.view_layer.objects.active = first
        unchanged()
        original_reassign = helper._reassign_mesh
        calls = 0
        def fail_after_reassign(*args):
            nonlocal calls
            original_reassign(*args)
            calls += 1
            if calls == 2:
                # Exercise rollback after both parenting and modifiers changed,
                # and after visibility was partially changed.
                base.hide_set(True)
                raise RuntimeError("injected reassignment failure")
        for target, replacement in (
            ("_copy_bone", patch.object(helper, "_copy_bone", side_effect=RuntimeError("injected bone failure"))),
            ("_reassign_mesh", patch.object(helper, "_reassign_mesh", side_effect=fail_after_reassign)),
        ):
            with replacement:
                try:
                    helper.combine_armatures(bpy.context, base, additional)
                except RuntimeError as error:
                    assert "injected" in str(error)
                else:
                    raise AssertionError(f"{target}: failure injection did not run")
            unchanged()
        # No qualifying meshes is a supported rig-only operation, including
        # Blender's normal resolution of an existing result object name.
        first.hide_set(True)
        second.hide_set(True)
        collision = bpy.data.objects.new(f"{base.name} Combined", None)
        bpy.context.scene.collection.objects.link(collision)
        combined, count = helper.combine_armatures(bpy.context, base, additional)
        assert count == 0 and combined.name != collision.name
        assert first.parent == base and second.parent == additional
        print("[PASS] Combine preflight and failure rollback restore state; zero visible meshes is supported")


def assert_linked_mesh_rejected(addon):
    helper = importlib.import_module(f"{addon.__name__}.mesh.armatures")
    with temporary_scene_data(), tempfile.TemporaryDirectory() as temporary:
        base = rig("Local Source Rig")
        original = mesh("Library Mesh", base, (base,))
        library_path = str(Path(temporary) / "library.blend")
        bpy.data.libraries.write(library_path, {original})
        with bpy.data.libraries.load(library_path, link=True) as (_source, destination):
            destination.objects = [original.name]
        linked = destination.objects[0]
        linked_rig = linked.parent
        bpy.context.scene.collection.objects.link(linked)
        bpy.context.scene.collection.objects.link(linked_rig)
        bpy.context.view_layer.update()
        assert not linked.is_editable
        before_objects = {obj.as_pointer() for obj in bpy.data.objects}
        before_rigs = {data.as_pointer() for data in bpy.data.armatures}
        try:
            helper.combine_armatures(bpy.context, base, linked_rig)
        except ValueError as error:
            assert "local and editable" in str(error)
        else:
            raise AssertionError("read-only mesh was reassigned")
        assert {obj.as_pointer() for obj in bpy.data.objects} == before_objects
        assert {data.as_pointer() for data in bpy.data.armatures} == before_rigs
        assert linked.parent == linked_rig and linked.modifiers[0].object == linked_rig
        assert not base.hide_get() and not linked_rig.hide_get()
        print("[PASS] Read-only linked meshes cancel combination before any bindings change")


def run():
    with addon_session("_xiv_ie_armature_regression") as addon:
        assert_combination(addon)
        assert_combination_export(addon)
        assert_combination_failures(addon)
        assert_linked_mesh_rejected(addon)


if __name__ == "__main__":
    run()
