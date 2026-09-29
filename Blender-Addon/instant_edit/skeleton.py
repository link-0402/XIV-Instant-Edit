"""The game's skeleton for imported models: armatures whose rest pose is the game's bind pose.

The plugin sends a model's skeleton with the import: every bone of the character's skeletons (body,
face, hair and any headgear or top skeleton, merged by bone name) in hierarchy order, each with the
index of its parent (-1 for roots) and its reference transform relative to that parent, in FFXIV's
Y-up model space. That is the bone format of an animation take.

A bone's rest matrix is its model-space bind matrix M (the reference transforms composed along the
parents) taken to Blender:
    head      = GAME_TO_BLENDER · M.translation
    rotation  = GAME_TO_BLENDER · M.rotation
so each bone's local axes are the game bone's axes, as in TexTools' FBX export and Yet Another
Devkit. Blender rest bones hold no scale; only rotations and positions are composed (see
`rest_matrices`).

Without a skeleton (an older plugin, or a model the plugin found none for) the armature keeps the
model's bones at the origin without parents, as it always did.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import json
from pathlib import Path
import re

import numpy as np

from .animation import GAME_TO_BLENDER, STRIDE, _matrices, _model_space
from .diagnostics import BridgeRequestError


SCHEMA = "instant-edit.skeleton"
VERSION = 1
REQUEST_SCHEMA = "instant-edit.skeleton-request"
MAX_BONES = 4096
MAX_LIST_ITEMS = 32
# A staged skeleton file of 4096 bones is about 1 MiB.
MAX_FILE_BYTES = 8 * 1024 * 1024
FILE_NAME = "skeleton.json"
# Bones point along their own Y axis, as the game's do; the length only affects the display.
BONE_LENGTH = 0.05
# Game file names of models whose skeleton the plugin can look up: the race (or demihuman,
# monster or weapon ID), kind, set and slot, as in c0201e0123_top.mdl or w0101b0001.mdl.
MODEL_FILE_NAME = re.compile(r"(?i)^[cdmw]\d{4}[abefhtz]\d{4}(?:_[a-z]{3})?\.mdl$")
REQUEST_TIMEOUT_SECONDS = 8
MAX_RESPONSE_BYTES = 2 * 1024 * 1024


@dataclass(frozen=True)
class GameSkeleton:
    bones: tuple[str, ...]
    parents: np.ndarray       # (bones,) parent index, -1 for roots
    reference: np.ndarray     # (bones, 10) reference transform, local to the parent
    source: str = ""          # "character" (read from the live character) or "files"
    race: str = ""
    skeletons: tuple[str, ...] = ()
    warnings: tuple[str, ...] = ()

    def to_payload(self) -> dict:
        return {
            "schema": SCHEMA,
            "version": VERSION,
            "source": self.source,
            "race": self.race,
            "skeletons": list(self.skeletons),
            "warnings": list(self.warnings),
            "bones": [
                {"name": name, "parent": int(parent), "reference": [float(v) for v in values]}
                for name, parent, values in zip(self.bones, self.parents, self.reference)
            ],
        }


@dataclass
class ArmatureReport:
    """What building an armature did, for the import's status line."""
    skeleton: GameSkeleton | None = None
    placeholders: list[str] = field(default_factory=list)  # bones without a rest pose (no skeleton)
    added: list[str] = field(default_factory=list)         # model bones no skeleton has
    unweighted: list[str] = field(default_factory=list)    # of those, bones without weighted vertices
    blocked: list[str] = field(default_factory=list)       # model bones an existing armature couldn't take

    def summary(self) -> str:
        if self.blocked:
            names = ", ".join(self.blocked[:6]) + (f" (+{len(self.blocked) - 6} more)" if len(self.blocked) > 6 else "")
            return (f"the armature is not in the view layer, so {len(self.blocked)} bone"
                    f"{'s' if len(self.blocked) != 1 else ''} the model weights could not be added: {names}")
        if self.skeleton is None and self.placeholders:
            return "bones have no rest pose (no game skeleton was sent)"
        if not self.added:
            return ""
        names = ", ".join(self.added[:6]) + (f" (+{len(self.added) - 6} more)" if len(self.added) > 6 else "")
        count = len(self.added)
        text = (f"{count} bone{'s' if count != 1 else ''} not in the game skeleton "
                f"{'were' if count != 1 else 'was'} placed at {'their' if count != 1 else 'its'} vertices: {names}")
        if self.unweighted:
            text += f" ({len(self.unweighted)} without weights, at the model's centre)"
        return text


def _reject(code: str, cause: str,
            remedy: str = "Update both XIV Instant Edit components and retry the import.") -> None:
    raise BridgeRequestError("request_validation", code, cause, remedy)


def _strings(value, name: str, max_length: int) -> tuple[str, ...]:
    if value is None:
        return ()
    if (not isinstance(value, list) or len(value) > MAX_LIST_ITEMS
            or not all(isinstance(item, str) and len(item) <= max_length for item in value)):
        _reject("invalid_skeleton", f"The skeleton's {name} are invalid.")
    return tuple(value)


def _text(value, name: str, max_length: int) -> str:
    if value is None:
        return ""
    if not isinstance(value, str) or len(value) > max_length:
        _reject("invalid_skeleton", f"The skeleton's {name} is invalid.")
    return value


def parse_skeleton(value) -> GameSkeleton:
    """Validate a skeleton sent by the plugin. Raises BridgeRequestError for bad input."""
    if not isinstance(value, dict):
        _reject("invalid_skeleton", "The skeleton is not an object.")
    if value.get("schema") != SCHEMA or value.get("version") != VERSION:
        _reject("unsupported_skeleton_schema", "The skeleton uses an unsupported schema.",
                "Install matching versions of the Dalamud plugin and Blender add-on.")
    bones = value.get("bones")
    if not isinstance(bones, list) or not 1 <= len(bones) <= MAX_BONES:
        _reject("invalid_skeleton_bones", f"The skeleton must name between 1 and {MAX_BONES} bones.")
    names, parents, reference = [], [], []
    for index, bone in enumerate(bones):
        if not isinstance(bone, dict):
            _reject("invalid_skeleton_bones", "A skeleton bone entry is not an object.")
        name = bone.get("name")
        parent = bone.get("parent")
        values = bone.get("reference")
        if not isinstance(name, str) or not name or len(name) > 128:
            _reject("invalid_skeleton_bones", "A skeleton bone has no valid name.")
        if isinstance(parent, bool) or not isinstance(parent, int) or not -1 <= parent < index:
            _reject("invalid_skeleton_hierarchy",
                    f'Bone "{name[:64]}" does not follow its parent in the skeleton.')
        if (not isinstance(values, list) or len(values) != STRIDE
                or not all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in values)):
            _reject("invalid_skeleton_bones", f'Bone "{name[:64]}" has no valid reference pose.')
        names.append(name)
        parents.append(parent)
        reference.append(values)
    if len(set(names)) != len(names):
        _reject("invalid_skeleton_bones", "The skeleton names a bone more than once.")
    try:
        transforms = np.asarray(reference, dtype=np.float64).reshape(len(names), STRIDE)
    except (OverflowError, ValueError):
        transforms = np.full((len(names), STRIDE), np.nan)
    if not np.isfinite(transforms).all():
        _reject("invalid_skeleton_values", "The skeleton's reference pose contains values that are not finite.")
    if (np.abs(np.linalg.norm(transforms[:, 3:7], axis=1) - 1.0) > 0.01).any():
        _reject("invalid_skeleton_rotation", "The skeleton's reference pose contains rotations that are not unit quaternions.")
    return GameSkeleton(
        bones=tuple(names),
        parents=np.asarray(parents, dtype=np.int64),
        reference=transforms,
        source=_text(value.get("source"), "source", 32),
        race=_text(value.get("race"), "race", 16),
        skeletons=_strings(value.get("skeletons"), "skeleton files", 256),
        warnings=_strings(value.get("warnings"), "warnings", 512),
    )


def rest_matrices(skeleton: GameSkeleton) -> np.ndarray:
    """(bones, 4, 4) rest matrices in Blender's armature space: the game's bind pose, Z up.

    Rest bones can't hold scale, so the reference transforms are composed from their rotations
    and positions alone. Reference poses are unit scale, except in mods that hide bones by
    scaling them to nothing: their children keep their places instead of collapsing onto them."""
    rigid = skeleton.reference.copy()
    rigid[:, 7:10] = 1.0
    return GAME_TO_BLENDER @ _model_space(_matrices(rigid), skeleton.parents)


# ----------------------------------------------------------------------
# Staging: the import request carries the skeleton; the queued import reads it from its job folder.


def stage_skeleton(data: dict) -> dict:
    """Move a validated import request's skeleton into its cache job as a file."""
    skeleton = data.get("skeleton")
    result = {key: value for key, value in data.items() if key != "skeleton"}
    if skeleton is None:
        return result
    job = Path(data["cacheJobDirectory"])
    path = job / FILE_NAME
    path.write_text(json.dumps(skeleton, separators=(",", ":")), encoding="utf-8")
    result["skeletonPath"] = str(path)
    return result


def load_skeleton(path: str | Path) -> GameSkeleton:
    """Read a skeleton staged by `stage_skeleton`."""
    path = Path(path)
    if path.stat().st_size > MAX_FILE_BYTES:
        raise ValueError("the staged skeleton is too large")
    return parse_skeleton(json.loads(path.read_text(encoding="utf-8")))


# ----------------------------------------------------------------------
# Asking the plugin, for model files imported from disk.


def request_skeleton(file_path: str | Path, port: int | None = None) -> tuple[GameSkeleton | None, str]:
    """Ask the running plugin for the skeleton of a model file named like the game's.

    Returns (skeleton, "") or (None, why there is none)."""
    name = str(file_path).replace("\\", "/").rsplit("/", 1)[-1]
    if not MODEL_FILE_NAME.match(name):
        return None, "the file isn't named like a game model (for example c0201e0123_top.mdl)"
    if port is None:
        try:
            from ..preferences import get_prefs
            port = int(get_prefs().instant_edit_plugin_port)
        except Exception:
            return None, "the plugin's port is unknown"
    from .plugin_http import PluginResponseTooLarge, post_json

    payload = {"schema": REQUEST_SCHEMA, "version": 1, "modelPath": name}
    try:
        status, body = post_json(port, "/skeleton", payload, timeout=REQUEST_TIMEOUT_SECONDS,
                                 max_response_size=MAX_RESPONSE_BYTES)
    except PluginResponseTooLarge:
        return None, "the plugin's skeleton was too large"
    except OSError:
        return None, "the XIV Instant Edit plugin isn't running"
    try:
        response = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None, f"the plugin gave an unreadable answer (HTTP {status})"
    if not isinstance(response, dict):
        return None, f"the plugin gave an unreadable answer (HTTP {status})"
    if not 200 <= status < 300:
        message = response.get("cause") or response.get("message")
        return None, str(message or f"the plugin refused (HTTP {status})")[:300]
    try:
        return parse_skeleton(response.get("skeleton")), ""
    except BridgeRequestError as error:
        return None, error.cause


# ----------------------------------------------------------------------
# Armatures.


def _bone_weights(mesh_objects, names: set[str], armature_inverse):
    """For the vertex groups named in ``names``: {bone: [weighted sum of positions, sum of
    weights]}, the weight each shares with other groups on the same vertices, {bone: {other:
    weight}}, and the centre of all vertices. Positions are in armature space."""
    sums = {name: [np.zeros(3), 0.0] for name in names}
    shared = {name: {} for name in names}
    everything = [np.zeros(3), 0]
    for obj in mesh_objects:
        mesh = obj.data
        count = len(mesh.vertices)
        if count == 0:
            continue
        co = np.empty(count * 3, dtype=np.float64)
        mesh.vertices.foreach_get("co", co)
        co = co.reshape(count, 3)
        matrix = armature_inverse @ np.array(obj.matrix_world, dtype=np.float64)
        co = co @ matrix[:3, :3].T + matrix[:3, 3]
        everything[0] += co.sum(axis=0)
        everything[1] += count
        group_names = {group.index: group.name for group in obj.vertex_groups}
        wanted = {index for index, name in group_names.items() if name in names}
        if not wanted:
            continue
        for vertex in mesh.vertices:
            groups = [(group_names.get(element.group), element.weight) for element in vertex.groups
                      if element.weight > 0.0]
            for name, weight in groups:
                if name not in sums:
                    continue
                total = sums[name]
                total[0] += weight * co[vertex.index]
                total[1] += weight
                for other, other_weight in groups:
                    if other != name and other is not None:
                        shared[name][other] = shared[name].get(other, 0.0) + min(weight, other_weight)
    center = everything[0] / everything[1] if everything[1] else np.zeros(3)
    return sums, shared, center


def _added_bone_rests(names, known, known_rest, mesh_objects, armature_inverse):
    """(rest matrix, parent name or None, weighted) for bones the model weights but no skeleton has.

    Each is placed at the weighted centre of its vertices (the model's centre if it weights none),
    on the game's axes, under the skeleton bone it shares the most weight with, else the nearest."""
    sums, shared, center = _bone_weights(mesh_objects, set(names), armature_inverse)
    heads = {name: rest[:3, 3] for name, rest in known_rest.items()}
    result = {}
    for name in names:
        total, weight = sums[name]
        weighted = weight > 0.0
        head = total / weight if weighted else center
        candidates = {other: value for other, value in shared[name].items() if other in known}
        if candidates:
            parent = max(sorted(candidates), key=lambda other: candidates[other])
        elif heads:
            parent = min(sorted(heads), key=lambda other: float(np.linalg.norm(heads[other] - head)))
        else:
            parent = None
        rest = np.identity(4)
        rest[:3, :3] = GAME_TO_BLENDER[:3, :3]
        rest[:3, 3] = head
        result[name] = (rest, parent, weighted)
    return result


def create_armature(context, collection, name: str, bone_names, mesh_objects,
                    skeleton: GameSkeleton | None = None, created_objects: list | None = None,
                    ) -> tuple[object, ArmatureReport]:
    """An armature object in ``collection`` with the game's skeleton and every model bone.

    With ``skeleton`` its bones have the game's rest pose and hierarchy; bones in ``bone_names``
    that the skeleton lacks are placed at their vertices in ``mesh_objects``. Without it, every
    model bone sits at the origin without a parent. The armature object is left at identity and
    becomes the active, selected object; it is added to ``created_objects`` as soon as it exists,
    so a failed import can remove it."""
    import bpy
    from mathutils import Matrix

    report = ArmatureReport(skeleton=skeleton)
    data = bpy.data.armatures.new(name)
    armature = bpy.data.objects.new(name, data)
    collection.objects.link(armature)
    if created_objects is not None:
        created_objects.append(armature)
    for obj in tuple(context.selected_objects):
        obj.select_set(False)
    context.view_layer.objects.active = armature
    armature.select_set(True)

    placements = {}
    rests = {}
    if skeleton is not None:
        rests = dict(zip(skeleton.bones, rest_matrices(skeleton)))
        known = set(skeleton.bones)
        report.added = [bone for bone in dict.fromkeys(bone_names) if bone not in known]
        if report.added:
            placements = _added_bone_rests(report.added, known, rests, mesh_objects, np.identity(4))
            report.unweighted = [bone for bone in report.added if not placements[bone][2]]

    bpy.ops.object.mode_set(mode="EDIT")
    try:
        bones = data.edit_bones
        if skeleton is None:
            report.placeholders = list(dict.fromkeys(bone_names))
            for bone_name in report.placeholders:
                edit_bone = bones.new(bone_name)
                edit_bone.head = (0, 0, 0)
                edit_bone.tail = (0, 0, 0.1)
        else:
            def add(bone_name, rest):
                edit_bone = bones.new(bone_name)
                # A bone without length ignores its matrix.
                edit_bone.head = (0.0, 0.0, 0.0)
                edit_bone.tail = (0.0, BONE_LENGTH, 0.0)
                edit_bone.matrix = Matrix(rest.tolist())
                return edit_bone

            for bone_name in skeleton.bones:
                add(bone_name, rests[bone_name])
            for bone_name, parent in zip(skeleton.bones, skeleton.parents):
                if parent >= 0:
                    bones[bone_name].parent = bones[skeleton.bones[parent]]
            for bone_name in report.added:
                rest, parent, _weighted = placements[bone_name]
                edit_bone = add(bone_name, rest)
                if parent is not None:
                    edit_bone.parent = bones[parent]
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
    return armature, report


def add_bones(context, armature, bone_names, mesh_objects, skeleton: GameSkeleton | None = None) -> ArmatureReport:
    """Add the bones in ``bone_names`` that an existing armature lacks.

    They are placed like create_armature's model bones that no skeleton has: at the weighted centre
    of their vertices in ``mesh_objects``, on the game's axes, under the bone they share the most
    weight with. Edit Mode needs the armature shown; it is hidden again afterwards if it was. An
    armature outside the view layer can't enter Edit Mode, so the report lists the bones instead."""
    import bpy
    from mathutils import Matrix

    report = ArmatureReport(skeleton=skeleton)
    bones = armature.data.bones
    missing = [bone for bone in dict.fromkeys(bone_names) if bones.get(bone) is None]
    if not missing:
        return report
    known = {bone.name for bone in bones}
    rests = {bone.name: np.array(bone.matrix_local, dtype=np.float64) for bone in bones}
    placements = _added_bone_rests(missing, known, rests, mesh_objects,
                                   np.linalg.inv(np.array(armature.matrix_world, dtype=np.float64)))

    disabled = armature.hide_viewport
    hidden = False
    try:
        # The view layer's object list can lag behind a collection just excluded from it, so
        # showing and activating the armature is what tells whether Edit Mode is possible.
        hidden = armature.hide_get()
        armature.hide_viewport = False
        armature.hide_set(False)
        for obj in tuple(context.selected_objects):
            obj.select_set(False)
        context.view_layer.objects.active = armature
        armature.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
    except RuntimeError:
        armature.hide_viewport = disabled
        try:
            armature.hide_set(hidden)
        except RuntimeError:
            pass
        report.blocked = missing
        return report
    report.added = missing
    report.unweighted = [bone for bone in missing if not placements[bone][2]]
    try:
        edit_bones = armature.data.edit_bones
        for bone_name in missing:
            rest, parent, _weighted = placements[bone_name]
            edit_bone = edit_bones.new(bone_name)
            edit_bone.head = (0.0, 0.0, 0.0)
            edit_bone.tail = (0.0, BONE_LENGTH, 0.0)
            edit_bone.matrix = Matrix(rest.tolist())
            if parent is not None:
                edit_bone.parent = edit_bones[parent]
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
        armature.hide_set(hidden)
        armature.hide_viewport = disabled
    return report
