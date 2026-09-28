"""The armature the Pose section shows, and the actions that can pose it.

Showing an action assigns it to the armature, so the timeline frame picks the
pose. Switching to the rest pose uses the armature's own Rest Position toggle,
which leaves the action in place. Exports always evaluate the rest pose.
Deleting an action removes it from the file, since a fake user or an armature
still showing it would otherwise keep it.
"""

import re

import bpy

from .instant_edit.context import ContextValidationError
from .mesh.export import armature_for_object
from .properties import get_settings


_BONE_PATH = re.compile(r'^pose\.bones\["((?:[^"\\]|\\.)*)"\]')
_ESCAPE = re.compile(r"\\(.)")


def pose_armature(context):
    """The armature chosen in the Pose section, or else the one detected for the scene."""
    chosen = get_settings().pose_armature
    if chosen is not None and chosen.type == "ARMATURE" and chosen.name in context.view_layer.objects:
        return chosen
    return detected_armature(context)


def detected_armature(context):
    """The active object's armature, else the selected Context's, else the only one."""
    objects = context.view_layer.objects
    active = objects.active
    if active is not None:
        if active.type == "ARMATURE":
            return active
        if active.type == "MESH":
            armature = armature_for_object(active)
            if armature is not None and armature.name in objects:
                return armature
    from .instant_edit.ops import export_destination_context

    try:
        ref = export_destination_context(context, persist=False)
    except (ContextValidationError, ReferenceError):
        ref = None
    if ref is not None:
        for obj in ref.collection.all_objects:
            armature = armature_for_object(obj) if obj.type == "MESH" else None
            if armature is not None and armature.name in objects:
                return armature
    armatures = [obj for obj in objects if obj.type == "ARMATURE" and obj.visible_get()]
    return armatures[0] if len(armatures) == 1 else None


def _fcurves(action):
    layers = getattr(action, "layers", None)
    if layers:
        for layer in layers:
            for strip in layer.strips:
                for channelbag in getattr(strip, "channelbags", ()):
                    yield from channelbag.fcurves
        return
    # Blender 4.5 still reads actions keyed the legacy way.
    yield from getattr(action, "fcurves", ())


def _keyed_bones(action):
    """The names of the bones ``action`` keys, once per curve."""
    for curve in _fcurves(action):
        match = _BONE_PATH.match(curve.data_path)
        if match:
            yield _ESCAPE.sub(r"\1", match.group(1))


def animates_bones(action, bones) -> bool:
    """Whether ``action`` keys any of these bones."""
    return any(bones.get(name) is not None for name in _keyed_bones(action))


def _constraint_actions() -> set:
    """Actions that Action constraints play, such as the one per changed bone of MagicFit's
    Customize+ rig: parts of a rig, not poses to show or delete."""
    return {
        constraint.action
        for obj in bpy.data.objects if obj.type == "ARMATURE" and obj.pose is not None
        for bone in obj.pose.bones
        for constraint in bone.constraints
        if constraint.type == "ACTION" and constraint.action is not None
    }


def _fbx_import(action) -> bool:
    """Whether Blender's FBX importer made ``action``: it names them "Object|Take", and every
    round trip through FBX adds another copy with a longer name."""
    return "|" in action.name


def pose_actions(armature) -> list:
    """The actions that key any bone of ``armature``, by name, except those of rigs and FBX
    imports, which stay in Blender's own action selector."""
    bones = armature.data.bones
    rigged = _constraint_actions()
    actions = [
        action for action in bpy.data.actions
        if action not in rigged and not _fbx_import(action) and animates_bones(action, bones)
    ]
    return sorted(actions, key=lambda action: action.name.casefold())


def shown_action(armature):
    animation_data = armature.animation_data
    return animation_data.action if animation_data is not None else None


def _keep(action) -> None:
    # Swapping an action out drops one of its users; Blender discards unused
    # actions on save, so keep one that is about to lose its last user.
    if action is not None and not action.use_fake_user and action.users <= 1:
        action.use_fake_user = True


def show_action(context, armature, action) -> None:
    """Pose ``armature`` with ``action``, or take its action off when ``action`` is None."""
    animation_data = armature.animation_data
    previous = animation_data.action if animation_data is not None else None
    if previous is not None and previous != action:
        _keep(previous)
    if action is None:
        if animation_data is not None:
            animation_data.action = None
        return
    animation_data = animation_data or armature.animation_data_create()
    animation_data.action = action
    if animation_data.action_slot is None:
        slots = list(animation_data.action_suitable_slots)
        if slots:
            animation_data.action_slot = slots[0]
    armature.data.pose_position = "POSE"
    start, end = (int(round(value)) for value in action.frame_range)
    scene = context.scene
    if not start <= scene.frame_current <= end:
        scene.frame_set(start)


def delete_action(action) -> None:
    """Delete ``action`` from the file. Armatures showing it lose it, and the bones it keyed go
    back to their rest pose instead of keeping the frame it last posed."""
    bones = set(_keyed_bones(action))
    for obj in bpy.data.objects:
        animation_data = obj.animation_data if obj.type == "ARMATURE" else None
        if animation_data is None or animation_data.action != action:
            continue
        animation_data.action = None
        for name in bones:
            bone = obj.pose.bones.get(name)
            if bone is None:
                continue
            bone.location = (0.0, 0.0, 0.0)
            bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
            bone.rotation_euler = (0.0, 0.0, 0.0)
            bone.rotation_axis_angle = (0.0, 0.0, 1.0, 0.0)
            bone.scale = (1.0, 1.0, 1.0)
    bpy.data.actions.remove(action)


def toggle_rest_pose(armature) -> str:
    """Switch ``armature`` between its rest pose and its pose; return the new position."""
    data = armature.data
    data.pose_position = "REST" if data.pose_position == "POSE" else "POSE"
    return data.pose_position
