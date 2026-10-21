"""Custom hair skeletons: a hair rig in Blender that Quick Export sends as the model's own skeleton.

A rig is an armature whose ``xiv_hair_rig`` custom property says which race it is for and how its
bones form chains, as JSON: {"race": "c0801", "chains": [{"kind": "back", "bones": [...]}]}. Its
``j_ex_hair_`` bones are the custom skeleton; any other bone (j_kao and the body) is the game's own.
The hair meshes are weighted to those bones and deformed by the armature through an Armature
modifier. Quick Export sends the bones in the game's Y-up model space as ``hairSkeleton``, and the
plugin builds the skeleton file and points the hair's EST entry at it.
"""

import json

from mathutils import Matrix

from ..materials import group_mesh_objects
from .animation import GAME_TO_BLENDER

RIG_PROPERTY = "xiv_hair_rig"
BONE_PREFIX = "j_ex_hair_"
# A mesh's bone table holds at most 64 bones.
MAX_MESH_BONES = 64
_BLENDER_TO_GAME = Matrix(GAME_TO_BLENDER.tolist()).inverted()


def _listed(names) -> str:
    names = sorted(names)
    return f"{', '.join(names[:3])}{'…' if len(names) > 3 else ''}"


def _rig_armatures(objects) -> list:
    """The armatures of the meshes' Armature modifiers that carry RIG_PROPERTY, in name order."""
    found = {}
    for obj in objects:
        if getattr(obj, "type", None) != "MESH":
            continue
        for modifier in obj.modifiers:
            armature = getattr(modifier, "object", None) if modifier.type == "ARMATURE" else None
            if armature is not None and RIG_PROPERTY in armature:
                found[armature.name] = armature
    return [found[name] for name in sorted(found)]


def _weighted_bone_count(objects) -> int:
    """How many distinct vertex groups weight some vertex of ``objects``."""
    if len({group.name for obj in objects for group in obj.vertex_groups}) <= MAX_MESH_BONES:
        # Within the limit even counting unweighted groups, so skip the vertex scan.
        return 0
    names = set()
    for obj in objects:
        groups = {group.index: group.name for group in obj.vertex_groups}
        for vertex in obj.data.vertices:
            for element in vertex.groups:
                if element.weight > 0.0 and element.group in groups:
                    names.add(groups[element.group])
    return len(names)


def _rig_bones(armature) -> list[dict]:
    """The armature's BONE_PREFIX bones, parents first, in the game's model space."""
    ordered = []
    pending = [bone for bone in armature.data.bones if bone.parent is None]
    pending.reverse()
    while pending:
        bone = pending.pop()
        ordered.append(bone)
        pending.extend(reversed(bone.children))
    bones = []
    for bone in ordered:
        if not bone.name.startswith(BONE_PREFIX):
            continue
        parent = bone.parent.name if bone.parent is not None else ""
        translation, rotation, _scale = (
            _BLENDER_TO_GAME @ armature.matrix_world @ bone.matrix_local).decompose()
        rotation.normalize()
        bones.append({
            "name": bone.name,
            "parent": parent if parent.startswith(BONE_PREFIX) else "j_kao",
            "model": [*translation, rotation.x, rotation.y, rotation.z, rotation.w, 1.0, 1.0, 1.0],
        })
    return bones


def hair_skeleton_payload(objects) -> tuple[dict | None, str]:
    """Return the hairSkeleton Quick Export sends for the meshes' hair rig, and any problem.

    ``(None, "")`` means the meshes use no rig: no ``j_ex_hair_`` vertex group and no rig armature.
    ``None`` comes with a message when the rig can't be sent as it is.
    """
    meshes = [obj for obj in objects if getattr(obj, "type", None) == "MESH"]
    hair_groups = {
        group.name for obj in meshes for group in obj.vertex_groups if group.name.startswith(BONE_PREFIX)
    }
    armatures = _rig_armatures(meshes)
    if not armatures:
        if not hair_groups:
            return None, ""
        return None, (
            f"The meshes are weighted to hair bones ({_listed(hair_groups)}) but no armature of theirs "
            f"carries a hair rig ({RIG_PROPERTY})."
        )
    if len(armatures) > 1:
        return None, (
            f"The meshes use more than one hair rig ({_listed(armature.name for armature in armatures)}). "
            "Deform them all with one."
        )
    armature = armatures[0]
    try:
        rig = json.loads(armature[RIG_PROPERTY])
    except (TypeError, ValueError):
        rig = None
    if not isinstance(rig, dict):
        return None, f"The hair rig ({RIG_PROPERTY}) on {armature.name} is not valid JSON."
    missing = hair_groups - {bone.name for bone in armature.data.bones}
    if missing:
        return None, f"{armature.name} has no bone for the hair groups {_listed(missing)}."
    for group in group_mesh_objects(meshes):
        weighted = _weighted_bone_count(group.objects)
        if weighted > MAX_MESH_BONES:
            return None, (
                f"Mesh {group.mesh_index} is weighted to {weighted} bones; "
                f"a mesh can use at most {MAX_MESH_BONES}."
            )
    chains = rig.get("chains")
    return {
        "race": rig.get("race"),
        "bones": _rig_bones(armature),
        "chains": chains if isinstance(chains, list) and chains else [],
    }, ""
