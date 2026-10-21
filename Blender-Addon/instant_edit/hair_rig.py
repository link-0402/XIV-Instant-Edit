"""Custom hair skeletons: a hair rig in Blender that Quick Export sends as the model's own skeleton.

A rig is an armature whose ``xiv_hair_rig`` custom property says which race it is for and how its
bones form chains, as JSON: {"race": "c0801", "chains": [{"kind": "back", "bones": [...]}]}. Its
``j_ex_hair_`` bones are the custom skeleton; any other bone (j_kao and the body) is the game's own.
The hair meshes are weighted to those bones and deformed by the armature through an Armature
modifier. Quick Export sends the bones in the game's Y-up model space as ``hairSkeleton``, and the
plugin builds the skeleton file and points the hair's EST entry at it. Each bone is turned so its
game X axis runs from head to tail, as the vanilla hair bones point.
"""

import json
import re

from mathutils import Matrix

from ..materials import _EST_RACE_PATTERN, group_mesh_objects
from .animation import GAME_TO_BLENDER

RIG_PROPERTY = "xiv_hair_rig"
BONE_PREFIX = "j_ex_hair_"
# The plugin's checks (HairSkeletonRequest.Validate), repeated here to name what fails.
CHAIN_KINDS = ("bangs", "side", "back", "ponytail")
MAX_RIG_BONES = 64
_BONE_NAME = re.compile(r"j_ex_hair_[0-9]{2}_[a-z]")
# The vanilla hair skeleton's bones. A rig points the EST entry at its own skeleton, so they don't load.
_VANILLA_HAIR_BONE = re.compile(r"j_ex_h[0-9]{4}_")
# A mesh's bone table holds at most 64 bones.
MAX_MESH_BONES = 64
_SCALE_TOLERANCE = 1e-4
# A chain's tip must be this far from its last bone, and each chain bone from the one before, in metres.
_MIN_CHAIN_TIP = 0.001
_BLENDER_TO_GAME = Matrix(GAME_TO_BLENDER.tolist()).inverted()
# The game's hair bones point along X, Blender's along Y: this turn makes the bone's Y its X.
_ALONG_X = Matrix(((0.0, -1.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0), (0.0, 0.0, 1.0, 0.0), (0.0, 0.0, 0.0, 1.0)))


def _listed(names) -> str:
    names = sorted(names)
    return f"{', '.join(names[:3])}{'…' if len(names) > 3 else ''}"


def _weighted_groups(obj, wanted) -> set[str]:
    """The names of ``obj``'s vertex groups that ``wanted`` accepts and that weight some vertex."""
    candidates = {group.index: group.name for group in obj.vertex_groups if wanted(group.name)}
    found = set()
    if not candidates:
        return found
    for vertex in obj.data.vertices:
        for element in vertex.groups:
            if element.weight > 0.0 and element.group in candidates:
                found.add(candidates[element.group])
        if len(found) == len(candidates):
            break
    return found


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


def _rig_matrix(armature, reset_scaling: bool) -> tuple[Matrix | None, str]:
    """The armature's world matrix as the mesh export sees it, or None with the problem.

    Reset Armature Scaling exports at scale 1, so the armature's own scale is divided out.
    """
    scale = tuple(armature.scale)
    basis = armature.matrix_world.to_3x3()
    sizes = [abs(value) for value in basis.to_scale()]
    if (min(scale) <= 0.0 or basis.determinant() <= 0.0
            or max(scale) - min(scale) > _SCALE_TOLERANCE * max(scale)
            or max(sizes) - min(sizes) > _SCALE_TOLERANCE * max(sizes)):
        return None, (
            f"The hair rig {armature.name} has a negative or non-uniform scale "
            f"({', '.join(f'{value:g}' for value in scale)}). Give it one positive scale on every axis."
        )
    matrix = armature.matrix_world.copy()
    if reset_scaling:
        matrix = matrix @ Matrix.Diagonal((*(1.0 / value for value in scale), 1.0))
    return matrix, ""


def _rig_bones(armature, matrix) -> list[dict]:
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
        translation, rotation, _scale = (_BLENDER_TO_GAME @ matrix @ bone.matrix_local @ _ALONG_X).decompose()
        rotation.normalize()
        bones.append({
            "name": bone.name,
            "parent": parent if parent.startswith(BONE_PREFIX) else "j_kao",
            "model": [*translation, rotation.x, rotation.y, rotation.z, rotation.w, 1.0, 1.0, 1.0],
        })
    return bones


def _game_point(matrix, point) -> list[float]:
    """An armature-space point in the game's model space."""
    return list(_BLENDER_TO_GAME @ matrix @ point)


def _chains_issue(chains, armature, matrix) -> str:
    """Why the rig's chains break the plugin's rules, or ""."""
    armature_name = armature.name
    bone_names = {bone.name for bone in armature.data.bones if bone.name.startswith(BONE_PREFIX)}
    if not isinstance(chains, list):
        return f"The chains of the hair rig on {armature_name} are not a list."
    chained = set()
    for chain in chains:
        kind = chain.get("kind") if isinstance(chain, dict) else None
        members = chain.get("bones") if isinstance(chain, dict) else None
        if not isinstance(kind, str) or kind not in CHAIN_KINDS:
            return (
                f"The hair rig on {armature_name} has a chain of kind {kind!r}; "
                f"a chain is {', '.join(CHAIN_KINDS)}."
            )
        if not isinstance(members, list) or not members:
            return f"The {kind} chain of the hair rig on {armature_name} has no bones."
        for name in members:
            if not isinstance(name, str) or name not in bone_names:
                return f"The {kind} chain names {name!r}, which is not a {BONE_PREFIX} bone of {armature_name}."
            if name in chained:
                return f"{name} is in more than one chain of the hair rig on {armature_name}."
            chained.add(name)
        for previous, name in zip(members, members[1:]):
            parent = armature.data.bones[name].parent
            if parent is None or parent.name != previous:
                return (
                    f"{name} follows {previous} in the {kind} chain of the hair rig on {armature_name}, "
                    f"but its parent is {parent.name if parent is not None else 'none'}. "
                    "List a chain's bones from the root down."
                )
        last = armature.data.bones[members[-1]]
        head = _game_point(matrix, last.head_local)
        tip = _game_point(matrix, last.tail_local)
        if sum((a - b) ** 2 for a, b in zip(head, tip)) < _MIN_CHAIN_TIP ** 2:
            return f"The {kind} chain of the hair rig on {armature_name} ends in {last.name}, which has no length."
        # An unconnected child can start at its parent's head; the plugin needs a direction between them.
        for previous, name in zip(members, members[1:]):
            start = _game_point(matrix, armature.data.bones[previous].head_local)
            end = _game_point(matrix, armature.data.bones[name].head_local)
            if sum((a - b) ** 2 for a, b in zip(start, end)) < _MIN_CHAIN_TIP ** 2:
                return (
                    f"{name} sits on {previous} in the {kind} chain of the hair rig on {armature_name}. "
                    "Move it at least 1 mm away."
                )
    return ""


def hair_skeleton_payload(objects, reset_scaling: bool = False) -> tuple[dict | None, str]:
    """Return the hairSkeleton Quick Export sends for the meshes' hair rig, and any problem.

    ``(None, "")`` means the meshes use no rig: no mesh is weighted to a ``j_ex_hair_`` vertex group,
    whatever their armature carries. ``None`` comes with a message naming the bone or mesh when the
    rig can't be sent as it is. ``reset_scaling`` is the Reset Armature Scaling setting.
    """
    meshes = [obj for obj in objects if getattr(obj, "type", None) == "MESH"]
    weighted_hair = {obj: _weighted_groups(obj, lambda name: name.startswith(BONE_PREFIX)) for obj in meshes}
    hair_groups = set().union(*weighted_hair.values())
    if not hair_groups:
        return None, ""
    armatures = _rig_armatures([obj for obj, groups in weighted_hair.items() if groups])
    if not armatures:
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
    misnamed = [
        bone.name for bone in armature.data.bones
        if bone.name.startswith(BONE_PREFIX) and not _BONE_NAME.fullmatch(bone.name)
    ]
    if misnamed:
        return None, (
            f"{armature.name} has hair bones the game can't use: {_listed(misnamed)}. "
            "Name them j_ex_hair_ with two digits, an underscore and one lower-case letter (j_ex_hair_01_a)."
        )
    race = rig.get("race")
    if not isinstance(race, str) or not _EST_RACE_PATTERN.fullmatch(race):
        return None, f"The hair rig on {armature.name} is for race {race!r}, not a player race such as c0801."
    matrix, scale_issue = _rig_matrix(armature, reset_scaling)
    if scale_issue:
        return None, scale_issue
    bones = _rig_bones(armature, matrix)
    if not 1 <= len(bones) <= MAX_RIG_BONES:
        return None, (
            f"{armature.name} has {len(bones)} {BONE_PREFIX} bones; a hair skeleton holds 1 to {MAX_RIG_BONES}."
        )
    chains = rig.get("chains")
    chains = [] if chains is None else chains
    chains_issue = _chains_issue(chains, armature, matrix)
    if chains_issue:
        return None, chains_issue
    chains = [
        {**chain, "tip": _game_point(matrix, armature.data.bones[chain["bones"][-1]].tail_local)}
        for chain in chains
    ]
    missing = hair_groups - {bone.name for bone in armature.data.bones}
    if missing:
        return None, f"{armature.name} has no bone for the hair groups {_listed(missing)}."
    for obj in sorted(meshes, key=lambda mesh: mesh.name):
        vanilla = _weighted_groups(obj, _VANILLA_HAIR_BONE.match)
        if vanilla:
            return None, (
                f"{obj.name} is weighted to the hair skeleton's own bones ({_listed(vanilla)}), which don't "
                "load with a hair rig. Move those weights to the rig's bones."
            )
    for group in group_mesh_objects(meshes):
        weighted = _weighted_bone_count(group.objects)
        if weighted > MAX_MESH_BONES:
            return None, (
                f"Mesh {group.mesh_index} is weighted to {weighted} bones; "
                f"a mesh can use at most {MAX_MESH_BONES}."
            )
    return {"race": race, "bones": bones, "chains": chains}, ""
