"""Animations read back from a scene armature for the XIV Instant Edit plugin.

The plugin sends the skeleton of the clip it edits and the rate it wants samples at. The armature's
motion is read over the scene's Start to End range, as Blender shows it, and turned back into game
transforms with the inverse of the send's math (see `animation.game_from_bases`): each matched bone
relative to its nearest matched ancestor, or in model space without one. The plugin puts bones the
armature lacks back from its own copy of the motion.

An armature whose motion is its action alone (no constraints, drivers or NLA) is read from the
action's keys. Anything else is read frame by frame from the scene, so constraints and IK count.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import math
import re
import struct

import numpy as np

from .animation import (
    FORMAT_VERSION,
    MAGIC,
    MAX_BODY_SIZE,
    MAX_OUTPUT_FRAMES,
    METADATA_PROPERTY,
    SCALE_TOLERANCE,
    STRIDE,
    AnimationApplyError,
    GameSkeleton,
    _matrices,
    _source,
    game_from_bases,
    match_bones,
    parse_bones,
    resolve_target,
)
from .diagnostics import BridgeRequestError


REQUEST_SCHEMA = "instant-edit.animation-read-request"
SCHEMA = "instant-edit.animation-read"
KIND = "blender"
MAX_REQUEST_SIZE = 4 * 1024 * 1024
MIN_RATE = 1.0
MAX_RATE = 240.0
_SEND_ID = re.compile(r"[0-9a-f]{32}")


@dataclass(frozen=True)
class ReadRequest:
    plugin_version: str
    target_object: str
    target_character: str
    rate: float
    clip_game_path: str
    clip_name: str
    skeleton: GameSkeleton


def _reject(code: str, cause: str,
            remedy: str = "Update both XIV Instant Edit components and read the animation again.") -> None:
    raise BridgeRequestError("request_validation", code, cause, remedy)


def _text(data: dict, name: str, max_length: int) -> str:
    value = data.get(name, "")
    if value is None:
        return ""
    if not isinstance(value, str) or len(value) > max_length:
        _reject("invalid_read_request", f"The read request's {name} is invalid.")
    return value.strip()


def parse_read_request(body) -> ReadRequest:
    """Validate a read request from the plugin. Raises BridgeRequestError for bad input."""
    try:
        data = json.loads(bytes(body).decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        _reject("invalid_read_request", "The read request is not valid UTF-8 JSON.")
    if not isinstance(data, dict):
        _reject("invalid_read_request", "The read request is not a JSON object.")
    if data.get("schema") != REQUEST_SCHEMA or data.get("version") != FORMAT_VERSION:
        _reject("unsupported_read_schema", "The read request uses an unsupported schema.",
                "Install matching versions of the Dalamud plugin and Blender add-on.")
    rate = data.get("rate")
    if (isinstance(rate, bool) or not isinstance(rate, (int, float)) or not math.isfinite(rate)
            or not MIN_RATE <= rate <= MAX_RATE):
        _reject("invalid_read_rate", "The read request's sample rate is invalid.")
    character = data.get("targetCharacter") or ""
    if not isinstance(character, str) or (character and not _SEND_ID.fullmatch(character)):
        _reject("invalid_target_character", "The read request's character send is invalid.")
    clip = data.get("clip") or {}
    if not isinstance(clip, dict):
        _reject("invalid_read_request", "The read request's clip is invalid.")
    return ReadRequest(
        plugin_version=_text(data, "pluginVersion", 32),
        target_object=_text(data, "targetObject", 128),
        target_character=character,
        rate=float(rate),
        clip_game_path=_text(clip, "gamePath", 1024),
        clip_name=_text(clip, "clip", 256),
        skeleton=parse_bones(data.get("bones")),
    )


def encode_response(header: dict, samples: np.ndarray) -> bytes:
    """The XIEA container the plugin's AnimationTakeFormat also writes: magic, version, header
    length, UTF-8 JSON header, zero padding to four bytes, little-endian float32 samples."""
    data = json.dumps(header).encode("utf-8")
    head = MAGIC + struct.pack("<II", FORMAT_VERSION, len(data)) + data
    head += b"\0" * ((-len(head)) % 4)
    return head + np.ascontiguousarray(samples, dtype="<f4").tobytes()


def _metadata(action) -> dict:
    if action is None:
        return {}
    value = action.get(METADATA_PROPERTY)
    if value is None:
        return {}
    value = value.to_dict() if hasattr(value, "to_dict") else value
    return value if isinstance(value, dict) else {}


def _animated(data) -> bool:
    return data is not None and (data.action is not None or any(not track.mute for track in data.nla_tracks))


def _keys_only(armature, matched, names) -> bool:
    """Whether the pose of the matched bones is their own keys and nothing else, so they can be read
    directly: no constraints, drivers or NLA, and the armature's other bones between them at rest."""
    data = armature.animation_data
    if data.action is None or len(data.drivers) or getattr(data, "use_tweak_mode", False):
        return False
    if any(not track.mute for track in data.nla_tracks):
        return False
    if abs(data.action_influence - 1.0) > 1e-9 or data.action_blend_type != "REPLACE":
        return False
    if data.action_extrapolation != "HOLD":
        return False
    if any(len(pose_bone.constraints) for pose_bone in armature.pose.bones):
        return False
    return not _moving_between(armature, matched, names)


def _moving_between(armature, matched, names) -> bool:
    """Whether a bone the skeleton lacks, above a matched bone and below its matched ancestor, is keyed
    or posed: the send keeps such bones at rest, and only the scene shows what they do."""
    import bpy

    named = set(names)
    keyed = {path for path, _index in _curves(armature)}
    identity = np.eye(4)
    seen = set()
    for bone, _index, _ancestor in matched:
        parent = bone.parent
        while parent is not None and parent.name not in named and parent.name not in seen:
            seen.add(parent.name)
            prefix = f'pose.bones["{bpy.utils.escape_identifier(parent.name)}"].'
            if any(path.startswith(prefix) for path in keyed):
                return True
            if not np.allclose(np.array(armature.pose.bones[parent.name].matrix_basis), identity, atol=1e-9):
                return True
            parent = parent.parent
    return False


def _curves(armature) -> dict:
    """The F-curves of the armature's action for its slot that Blender plays, by (data path, index)."""
    data = armature.animation_data
    action = data.action
    curves = []
    slot = getattr(data, "action_slot", None)
    if hasattr(action, "layers") and slot is not None:
        for layer in action.layers:
            for strip in layer.strips:
                bag = strip.channelbag(slot)
                if bag is not None:
                    curves.extend(bag.fcurves)
    elif hasattr(action, "fcurves"):
        curves.extend(action.fcurves)
    # Blender leaves muted curves out, and curves in a muted group.
    return {(curve.data_path, curve.array_index): curve for curve in curves
            if not curve.mute and not (curve.group is not None and curve.group.mute)}


def _channel(curves, path: str, current, frames: np.ndarray) -> np.ndarray:
    """(frames, len(current)) values of a pose-bone property: its curves where it has them, its
    current value where it doesn't."""
    values = np.empty((len(frames), len(current)))
    for index, fallback in enumerate(current):
        curve = curves.get((path, index))
        values[:, index] = fallback if curve is None else [curve.evaluate(frame) for frame in frames]
    return values


def _bases_from_keys(armature, matched, frames: np.ndarray) -> np.ndarray:
    import bpy
    from mathutils import Euler, Quaternion

    curves = _curves(armature)
    bases = np.empty((len(matched), len(frames), 4, 4))
    for slot, (bone, _index, _ancestor) in enumerate(matched):
        pose_bone = armature.pose.bones[bone.name]
        prefix = f'pose.bones["{bpy.utils.escape_identifier(bone.name)}"].'
        location = _channel(curves, prefix + "location", tuple(pose_bone.location), frames)
        scale = _channel(curves, prefix + "scale", tuple(pose_bone.scale), frames)
        mode = pose_bone.rotation_mode
        if mode == "QUATERNION":
            wxyz = _channel(curves, prefix + "rotation_quaternion", tuple(pose_bone.rotation_quaternion), frames)
        elif mode == "AXIS_ANGLE":
            values = _channel(curves, prefix + "rotation_axis_angle", tuple(pose_bone.rotation_axis_angle), frames)
            wxyz = np.array([tuple(Quaternion(v[1:4], v[0])) if np.linalg.norm(v[1:4]) > 1e-12 else (1.0, 0.0, 0.0, 0.0)
                             for v in values])
        else:
            values = _channel(curves, prefix + "rotation_euler", tuple(pose_bone.rotation_euler), frames)
            wxyz = np.array([tuple(Euler(v, mode).to_quaternion()) for v in values])
        norms = np.linalg.norm(wxyz, axis=1, keepdims=True)
        wxyz = np.where(norms > 1e-12, wxyz / np.maximum(norms, 1e-12), [1.0, 0.0, 0.0, 0.0])
        transforms = np.concatenate((location, wxyz[:, [1, 2, 3, 0]], scale), axis=1)
        bases[slot] = _matrices(transforms)
    return bases


def _bases_from_scene(scene, armature, matched, names, frames: np.ndarray) -> np.ndarray:
    """Visual bases: each bone's pose relative to its matched ancestor as Blender shows it."""
    bases = np.empty((len(matched), len(frames), 4, 4))
    current, subframe = scene.frame_current, scene.frame_subframe
    # Changing frames plays the action over pose changes the user hasn't keyed; they come back after.
    posed = [(pose_bone, tuple(pose_bone.location), tuple(pose_bone.rotation_quaternion), tuple(pose_bone.rotation_euler),
              tuple(pose_bone.rotation_axis_angle), tuple(pose_bone.scale)) for pose_bone in armature.pose.bones]
    try:
        for column, frame in enumerate(frames):
            whole = math.floor(frame)
            scene.frame_set(int(whole), subframe=float(frame - whole))
            for slot, (bone, _index, ancestor) in enumerate(matched):
                pose_bone = armature.pose.bones[bone.name]
                parent = bone.parent
                if (parent is None and ancestor < 0) or (parent is not None and ancestor >= 0 and parent.name == names[ancestor]):
                    basis = armature.convert_space(pose_bone=pose_bone, matrix=pose_bone.matrix,
                                                   from_space="POSE", to_space="LOCAL")
                    bases[slot, column] = np.array(basis)
                    continue
                pose = np.array(pose_bone.matrix)
                rest = np.array(bone.matrix_local)
                if ancestor >= 0:
                    above = armature.data.bones[names[ancestor]]
                    rest = np.linalg.inv(np.array(above.matrix_local)) @ rest
                    pose = np.linalg.inv(np.array(armature.pose.bones[above.name].matrix)) @ pose
                bases[slot, column] = np.linalg.inv(rest) @ pose
    finally:
        scene.frame_set(current, subframe=subframe)
        for pose_bone, location, quaternion, euler, axis_angle, scale in posed:
            pose_bone.location = location
            pose_bone.rotation_quaternion = quaternion
            pose_bone.rotation_euler = euler
            pose_bone.rotation_axis_angle = axis_angle
            pose_bone.scale = scale
    return bases


def _check_hierarchy(skeleton: GameSkeleton, matched) -> None:
    """Refuses an armature that hangs a matched bone under a matched bone that isn't one of its game
    ancestors: no game transform relative to that bone puts it back in the game skeleton."""
    for _bone, index, ancestor in matched:
        if ancestor < 0:
            continue
        parent = int(skeleton.parents[index])
        while parent >= 0 and parent != ancestor:
            parent = int(skeleton.parents[parent])
        if parent != ancestor:
            raise AnimationApplyError(
                "animation_hierarchy_mismatch",
                f'Bone "{skeleton.bones[index]}" sits under "{skeleton.bones[ancestor]}" in Blender, '
                "but not in the game skeleton.",
                "Parent it as in the game skeleton, then read the animation again.")


def read_motion(request: ReadRequest, context=None) -> tuple[dict, bytes]:
    """The armature's motion as the plugin asked for it: a summary and the reply body. Runs on
    Blender's main thread."""
    import bpy

    context = context or bpy.context
    scene = context.scene
    armature = resolve_target(context, request.target_object, request.target_character)
    data = armature.animation_data
    if not _animated(data):
        raise AnimationApplyError(
            "animation_none", "The armature has no animation.",
            "Send the animation to Blender from the Timeline, or give the armature an action.")
    if armature.data.pose_position == "REST":
        raise AnimationApplyError(
            "animation_rest_position", "The armature shows its rest position.",
            "Switch the armature to Pose Position in its Armature properties.")
    if armature.mode == "EDIT":
        raise AnimationApplyError(
            "animation_armature_edit_mode", "The armature is in Edit Mode.",
            "Leave Edit Mode, then read the animation again.")
    names = request.skeleton.bones
    matched = sorted(match_bones(armature, names), key=lambda match: match[1])
    if not matched:
        raise AnimationApplyError(
            "animation_no_matching_bones",
            f'Armature "{armature.name}" has none of the animation\'s {len(names)} bones.',
            "Read the animation from the FFXIV skeleton armature it was sent to.")
    _check_hierarchy(request.skeleton, matched)

    fps = scene.render.fps / scene.render.fps_base
    start, end = scene.frame_start, scene.frame_end
    metadata = _metadata(data.action)
    duration = (end - start) / fps
    sent = metadata.get("duration")
    rate = metadata.get("frameRate")
    # The range and frame rate the send keyed: the sent length, which a 24 fps scene can't hold exactly.
    if (metadata.get("frameStart") == start and metadata.get("frameEnd") == end
            and isinstance(rate, (int, float)) and abs(rate - fps) <= 1e-6 * fps
            and isinstance(sent, (int, float)) and math.isfinite(sent) and sent > 0):
        duration = float(sent)
    count = int(round(duration * request.rate)) + 1 if duration > 0 else 1
    if count < 2:
        raise AnimationApplyError(
            "animation_read_too_short", "The scene's End frame must come after its Start.",
            "Set the scene's Start and End to the animation's frames.")
    if count > MAX_OUTPUT_FRAMES or count * len(matched) * STRIDE * 4 > MAX_BODY_SIZE:
        raise AnimationApplyError(
            "animation_too_long", f"The animation would need {count} samples.",
            "Shorten the scene's frame range.")
    times = duration * np.arange(count) / (count - 1)
    frames = start + times * fps

    keys = _keys_only(armature, matched, names)
    bases = (_bases_from_keys(armature, matched, frames) if keys
             else _bases_from_scene(scene, armature, matched, names, frames))
    scales = np.linalg.norm(bases[..., :3, :3], axis=-2)
    scaled = bool((np.abs(scales - 1.0) > SCALE_TOLERANCE).any())
    keyed_scale = bool(metadata["keyedScale"]) if "keyedScale" in metadata else scaled
    # A scale on a motion sent without it: the plugin keeps the clip's scale, so it says so.
    scale_ignored = scaled and not keyed_scale
    bones = [(bone.matrix_local, index, ancestor) for bone, index, ancestor in matched]
    samples = game_from_bases(request.skeleton, bases, armature.matrix_world, bones, keyed_scale)

    found = {index for _bone, index, _ancestor in matched}
    missing = [name for index, name in enumerate(names) if index not in found]
    action = data.action.name if data.action is not None else ""
    header = {
        "schema": SCHEMA,
        "version": FORMAT_VERSION,
        "kind": KIND,
        "action": action,
        "armature": armature.name,
        "frameStart": int(start),
        "frameEnd": int(end),
        "sceneRate": float(fps),
        "duration": float(duration),
        "keyedScale": keyed_scale,
        "scaleIgnored": scale_ignored,
        "evaluated": "keys" if keys else "scene",
        "source": _source(metadata.get("source")),
        "times": [float(t) for t in times],
        "bones": [{"index": int(index), "ancestor": int(ancestor)} for _bone, index, ancestor in matched],
        "missingBoneCount": len(missing),
        "missingBones": missing[:24],
    }
    summary = {
        "action": action,
        "armature": armature.name,
        "frames": count,
        "evaluated": header["evaluated"],
        "matchedBones": len(matched),
        "missingBoneCount": len(missing),
    }
    return summary, encode_response(header, samples)
