"""Animations sent by the XIV Instant Edit plugin, keyed onto a scene armature.

The plugin sends a take: the game-local transform of every bone (relative to its parent, in
FFXIV's Y-up space) for each sampled frame, together with the skeleton's hierarchy and reference
pose. A take is either an animation file sampled on the skeleton it was made for, or a recording
of a character's live skeleton, which includes the game's bone physics, Customize+ and LivePose.

Bones are matched by name. Each matched pose bone receives the change from the game's reference
pose, re-expressed in that bone's own rest orientation. Scene armatures are imported with their
own bone orientations (glTF, FBX), which this absorbs. With a rest pose built from the same
skeleton the result is exact; with other proportions every bone still turns, and moves, by the
same amount around its own head.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import json
import math
import struct

import numpy as np

from .diagnostics import BridgeRequestError


MAGIC = b"XIEA"
FORMAT_VERSION = 1
SCHEMA = "instant-edit.animation"
KINDS = ("animation", "recording")
# Translation xyz, rotation xyzw, scale xyz.
STRIDE = 10
MAX_BODY_SIZE = 192 * 1024 * 1024
MAX_HEADER_SIZE = 16 * 1024 * 1024
MAX_BONES = 4096
MAX_DURATION_SECONDS = 3600.0
MAX_OUTPUT_FRAMES = 200_000
# Channels whose values stay within this range over the whole take get a single key.
CONSTANT_TOLERANCE = 1e-6
UNDO_MESSAGE = "XIV Instant Edit animation"
METADATA_PROPERTY = "xiv_instant_edit"

# FFXIV is Y-up, Blender Z-up: the model importer maps game (x, y, z) to (x, -z, y).
GAME_TO_BLENDER = np.array([
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 0.0, -1.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
])


@dataclass(frozen=True)
class Take:
    kind: str
    name: str
    target_object: str
    key_scale: bool
    loop: bool
    plugin_version: str
    bones: tuple[str, ...]
    parents: np.ndarray       # (bones,) parent index within the take, -1 for roots
    reference: np.ndarray     # (bones, 10) game reference pose, local to the parent
    times: np.ndarray         # (frames,) seconds, strictly increasing
    samples: np.ndarray       # (frames, bones, 10)
    source: dict = field(default_factory=dict)

    @property
    def duration(self) -> float:
        return float(self.times[-1] - self.times[0])


class AnimationApplyError(Exception):
    """A take that is valid but cannot be applied to the current scene."""

    def __init__(self, code: str, cause: str, remedy: str):
        self.code = code
        self.cause = cause
        self.remedy = remedy
        super().__init__(cause)


def _reject(code: str, cause: str,
            remedy: str = "Update both XIV Instant Edit components and send the animation again.") -> None:
    raise BridgeRequestError("request_validation", code, cause, remedy)


def _text(header: dict, name: str, *, max_length: int, required: bool = False) -> str:
    value = header.get(name, "")
    if value is None and not required:
        return ""
    if not isinstance(value, str) or len(value) > max_length or (required and not value.strip()):
        _reject(f"invalid_{name}", f"The animation's {name} is missing or invalid.")
    return value.strip()


def _source(value) -> dict:
    """Keep only short string facts about the take's origin; they are informational."""
    if not isinstance(value, dict):
        return {}
    result = {}
    for key, item in list(value.items())[:32]:
        if isinstance(key, str) and isinstance(item, str) and len(key) <= 64:
            result[key] = item[:1024]
    return result


def _transforms(values, count: int, what: str) -> np.ndarray:
    array = np.asarray(values, dtype=np.float64).reshape(count, STRIDE)
    if not np.isfinite(array).all():
        _reject("invalid_animation_values", f"The animation's {what} contain values that are not finite.")
    norms = np.linalg.norm(array[:, 3:7], axis=1)
    if (np.abs(norms - 1.0) > 0.01).any():
        _reject("invalid_animation_rotation", f"The animation's {what} contain rotations that are not unit quaternions.")
    return array


def parse_take(body) -> Take:
    """Validate a take received from the plugin. Raises BridgeRequestError for bad input."""
    view = memoryview(body)
    if len(view) < 12 or bytes(view[:4]) != MAGIC:
        _reject("invalid_animation_container", "The request body is not an XIV Instant Edit animation.")
    version, header_length = struct.unpack_from("<II", view, 4)
    if version != FORMAT_VERSION:
        _reject("unsupported_animation_version",
                "The animation uses a format version this add-on does not support.",
                "Install matching versions of the Dalamud plugin and Blender add-on.")
    if header_length > MAX_HEADER_SIZE or 12 + header_length > len(view):
        _reject("invalid_animation_header", "The animation header is truncated or too large.")
    try:
        header = json.loads(bytes(view[12:12 + header_length]).decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        _reject("invalid_animation_header", "The animation header is not valid UTF-8 JSON.")
    if not isinstance(header, dict) or header.get("schema") != SCHEMA or header.get("version") != FORMAT_VERSION:
        _reject("unsupported_animation_schema",
                "The animation header uses an unsupported schema.",
                "Install matching versions of the Dalamud plugin and Blender add-on.")

    kind = header.get("kind")
    if kind not in KINDS:
        _reject("invalid_animation_kind", "The animation kind is not supported.")
    name = _text(header, "name", max_length=256, required=True)
    target = _text(header, "targetObject", max_length=128)
    plugin_version = _text(header, "pluginVersion", max_length=32)
    key_scale = header.get("keyScale", False)
    loop = header.get("loop", False)
    if not isinstance(key_scale, bool) or not isinstance(loop, bool):
        _reject("invalid_animation_options", "The animation options are not booleans.")

    bones = header.get("bones")
    if not isinstance(bones, list) or not 1 <= len(bones) <= MAX_BONES:
        _reject("invalid_animation_bones", f"The animation must name between 1 and {MAX_BONES} bones.")
    names, parents, reference = [], [], []
    for index, bone in enumerate(bones):
        if not isinstance(bone, dict):
            _reject("invalid_animation_bones", "An animation bone entry is not an object.")
        bone_name = bone.get("name")
        parent = bone.get("parent")
        values = bone.get("reference")
        if not isinstance(bone_name, str) or not bone_name or len(bone_name) > 128:
            _reject("invalid_animation_bones", "An animation bone has no valid name.")
        if isinstance(parent, bool) or not isinstance(parent, int) or not -1 <= parent < index:
            _reject("invalid_animation_hierarchy",
                    f'Bone "{bone_name[:64]}" does not follow its parent in the animation skeleton.')
        if (not isinstance(values, list) or len(values) != STRIDE
                or not all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in values)):
            _reject("invalid_animation_bones", f'Bone "{bone_name[:64]}" has no valid reference pose.')
        names.append(bone_name)
        parents.append(parent)
        reference.append(values)
    if len(set(names)) != len(names):
        _reject("invalid_animation_bones", "The animation skeleton names a bone more than once.")

    times = header.get("times")
    if (not isinstance(times, list) or not times
            or not all(isinstance(t, (int, float)) and not isinstance(t, bool) for t in times)):
        _reject("invalid_animation_times", "The animation has no valid frame times.")
    times = np.asarray(times, dtype=np.float64)
    if (not np.isfinite(times).all() or (np.diff(times) <= 0).any()
            or times[-1] - times[0] > MAX_DURATION_SECONDS):
        _reject("invalid_animation_times",
                "The animation's frame times must increase and span at most an hour.")

    offset = 12 + header_length
    offset += (-offset) % 4
    frames, count = len(times), len(names)
    expected = frames * count * STRIDE * 4
    if len(view) - offset != expected:
        _reject("animation_size_mismatch", "The animation data does not match its header.")
    raw = np.frombuffer(view, dtype="<f4", count=frames * count * STRIDE, offset=offset)
    samples = _transforms(raw, frames * count, "samples").reshape(frames, count, STRIDE)
    return Take(
        kind=kind,
        name=name,
        target_object=target,
        key_scale=key_scale,
        loop=loop,
        plugin_version=plugin_version,
        bones=tuple(names),
        parents=np.asarray(parents, dtype=np.int64),
        reference=_transforms(reference, count, "reference pose"),
        times=times,
        samples=samples,
        source=_source(header.get("source")),
    )


# ----------------------------------------------------------------------
# Transform math. Matrices act on column vectors: scale, then rotation, then translation.


def _matrices(values: np.ndarray) -> np.ndarray:
    """(..., 10) game transforms to (..., 4, 4) matrices."""
    q = values[..., 3:7] / np.linalg.norm(values[..., 3:7], axis=-1, keepdims=True)
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    m = np.zeros(values.shape[:-1] + (4, 4))
    m[..., 0, 0] = 1 - 2 * (y * y + z * z)
    m[..., 0, 1] = 2 * (x * y - w * z)
    m[..., 0, 2] = 2 * (x * z + w * y)
    m[..., 1, 0] = 2 * (x * y + w * z)
    m[..., 1, 1] = 1 - 2 * (x * x + z * z)
    m[..., 1, 2] = 2 * (y * z - w * x)
    m[..., 2, 0] = 2 * (x * z - w * y)
    m[..., 2, 1] = 2 * (y * z + w * x)
    m[..., 2, 2] = 1 - 2 * (x * x + y * y)
    m[..., :3, :3] *= values[..., None, 7:10]
    m[..., :3, 3] = values[..., 0:3]
    m[..., 3, 3] = 1.0
    return m


def _model_space(local: np.ndarray, parents: np.ndarray) -> np.ndarray:
    """(..., bones, 4, 4) parent-relative matrices to the skeleton's model space."""
    model = np.empty_like(local)
    for bone, parent in enumerate(parents):
        model[..., bone, :, :] = (local[..., bone, :, :] if parent < 0
                                  else model[..., parent, :, :] @ local[..., bone, :, :])
    return model


def _quaternions(rotation: np.ndarray) -> np.ndarray:
    """(n, 3, 3) rotation matrices to (n, 4) quaternions in Blender's (w, x, y, z) order."""
    r = rotation
    d0, d1, d2 = r[:, 0, 0], r[:, 1, 1], r[:, 2, 2]
    candidates = np.stack((1 + d0 + d1 + d2, 1 + d0 - d1 - d2, 1 - d0 + d1 - d2, 1 - d0 - d1 + d2), axis=1)
    pick = np.argmax(candidates, axis=1)
    root = 0.5 * np.sqrt(np.maximum(candidates[np.arange(len(r)), pick], 1e-300))
    quarter = 0.25 / root
    q = np.empty((len(r), 4))
    options = (
        (root, (r[:, 2, 1] - r[:, 1, 2]) * quarter, (r[:, 0, 2] - r[:, 2, 0]) * quarter, (r[:, 1, 0] - r[:, 0, 1]) * quarter),
        ((r[:, 2, 1] - r[:, 1, 2]) * quarter, root, (r[:, 0, 1] + r[:, 1, 0]) * quarter, (r[:, 0, 2] + r[:, 2, 0]) * quarter),
        ((r[:, 0, 2] - r[:, 2, 0]) * quarter, (r[:, 0, 1] + r[:, 1, 0]) * quarter, root, (r[:, 1, 2] + r[:, 2, 1]) * quarter),
        ((r[:, 1, 0] - r[:, 0, 1]) * quarter, (r[:, 0, 2] + r[:, 2, 0]) * quarter, (r[:, 1, 2] + r[:, 2, 1]) * quarter, root),
    )
    for case, components in enumerate(options):
        rows = pick == case
        for axis in range(4):
            q[rows, axis] = components[axis][rows]
    return q / np.linalg.norm(q, axis=1, keepdims=True)


def _decompose(matrices: np.ndarray):
    """(n, 4, 4) pose-bone matrices to location, (w, x, y, z) rotation and scale.

    The rotation is the nearest one to the linear part, so the parent scaling that Blender and
    Havok combine differently cannot leave a non-orthogonal rotation behind."""
    location = matrices[:, :3, 3].copy()
    linear = matrices[:, :3, :3]
    u, _, vt = np.linalg.svd(linear)
    rotation = u @ vt
    mirrored = np.linalg.det(rotation) < 0
    if mirrored.any():
        u[mirrored, :, 2] *= -1
        rotation[mirrored] = u[mirrored] @ vt[mirrored]
    scale = np.einsum("nij,nij->nj", rotation, linear)
    return location, _quaternions(rotation), scale


def _continuous(quaternions: np.ndarray) -> np.ndarray:
    """Pick each frame's quaternion sign so that interpolation takes the short way."""
    q = quaternions.copy()
    if q[0, 0] < 0:
        q[0] = -q[0]
    if len(q) > 1:
        # Flipping one frame flips its dot product with the next, so the signs accumulate.
        dots = np.einsum("fi,fi->f", quaternions[1:], quaternions[:-1])
        signs = np.cumprod(np.where(dots < 0, -1.0, 1.0)) * (1.0 if quaternions[0, 0] >= 0 else -1.0)
        q[1:] = quaternions[1:] * signs[:, None]
    return q


def resample(times: np.ndarray, samples: np.ndarray, fps: float):
    """Samples at every scene frame over the take's duration, interpolated from the take.

    Translation and scale are interpolated linearly, rotations by normalized linear
    interpolation along the shorter arc."""
    if not math.isfinite(fps) or fps <= 0:
        raise AnimationApplyError(
            "invalid_scene_frame_rate", "The scene frame rate is not a positive number.",
            "Set a valid frame rate under Output Properties > Format and send the animation again.")
    count = int(math.floor((times[-1] - times[0]) * fps + 1e-6)) + 1
    if count > MAX_OUTPUT_FRAMES:
        raise AnimationApplyError(
            "animation_too_long", f"The animation would need {count} frames at the scene frame rate.",
            "Lower the scene frame rate or send a shorter animation.")
    if len(times) == 1:
        return np.zeros(1), samples[:1].copy()
    offsets = np.arange(count) / fps
    wanted = times[0] + offsets
    before = np.clip(np.searchsorted(times, wanted, side="right") - 1, 0, len(times) - 2)
    alpha = np.clip((wanted - times[before]) / (times[before + 1] - times[before]), 0.0, 1.0)[:, None, None]
    a, b = samples[before], samples[before + 1]
    result = a + (b - a) * alpha
    qa, qb = a[..., 3:7], b[..., 3:7]
    qb = np.where((np.sum(qa * qb, axis=-1) < 0)[..., None], -qb, qb)
    q = qa + (qb - qa) * alpha
    result[..., 3:7] = q / np.linalg.norm(q, axis=-1, keepdims=True)
    return offsets, result


def _armature_to_game(reference_model, bones, armature_matrix) -> np.ndarray:
    """Armature space to game space (linear part).

    The armature object's world matrix carries the import's axis and unit conversion, but also
    any turn or scale someone gives the object afterwards. The matched bones' rest heads show the
    conversion itself, so when they disagree with the world matrix by more than a rest pose with
    other proportions can explain, the transform fitted to the heads wins."""
    world = np.linalg.inv(np.linalg.inv(np.asarray(armature_matrix, dtype=np.float64)) @ GAME_TO_BLENDER)
    heads = np.array([np.asarray(rest, dtype=np.float64)[:3, 3] for rest, _bone, _ancestor in bones])
    targets = np.array([reference_model[bone][:3, 3] for _rest, bone, _ancestor in bones])
    if len(heads) < 3:
        return world
    source = heads - heads.mean(axis=0)
    target = targets - targets.mean(axis=0)
    u, s, vt = np.linalg.svd(target.T @ source)
    if s[0] <= 0 or s[1] <= 1e-6 * s[0]:
        return world
    d = 1.0 if np.linalg.det(u @ vt) >= 0 else -1.0
    rotation = u @ np.diag([1.0, 1.0, d]) @ vt
    scale = (s[0] + s[1] + d * s[2]) / (source ** 2).sum()
    world_scale = np.cbrt(abs(np.linalg.det(world[:3, :3])))
    cosine = (np.trace((world[:3, :3] / world_scale).T @ rotation) - 1) / 2
    if np.degrees(np.arccos(np.clip(cosine, -1.0, 1.0))) < 15.0 and 2 / 3 < scale / world_scale < 1.5:
        return world
    fitted = np.eye(4)
    fitted[:3, :3] = scale * rotation
    return fitted


def pose_bases(take: Take, samples: np.ndarray, armature_matrix, bones):
    """Pose-bone matrices (matrix_basis) for ``bones`` at every sample.

    ``bones`` are (rest matrix in armature space, take bone index, index of the bone's nearest
    matched ancestor in the scene armature or -1). Yields one (frames, 4, 4) array per bone, so
    a long recording never holds every bone's matrices at once."""
    reference_local = _matrices(take.reference)
    reference_model = _model_space(reference_local, take.parents)
    armature_to_game = _armature_to_game(reference_model, bones, armature_matrix)
    model_cache = {}

    def model(bone: int) -> np.ndarray:
        # Model space is only needed where the scene armature parents a bone differently.
        if bone not in model_cache:
            parent = int(take.parents[bone])
            local = _matrices(samples[:, bone])
            model_cache[bone] = local if parent < 0 else model(parent) @ local
        return model_cache[bone]

    for rest, bone, ancestor in bones:
        if ancestor == take.parents[bone]:
            delta = np.linalg.inv(reference_local[bone]) @ _matrices(samples[:, bone])
        else:
            # Take the game transform relative to the bone the armature parents it to.
            if ancestor < 0:
                reference, pose = reference_model[bone], model(bone)
            else:
                reference = np.linalg.inv(reference_model[ancestor]) @ reference_model[bone]
                pose = np.linalg.inv(model(ancestor)) @ model(bone)
            delta = np.linalg.inv(reference) @ pose
        # The pose bone's rest orientation as seen from the game bone. Only the linear part
        # is used: a rest pose built from other proportions only moves the bone's head.
        orientation = np.eye(4)
        orientation[:3, :3] = (np.linalg.inv(reference_model[bone]) @ armature_to_game
                               @ np.asarray(rest, dtype=np.float64))[:3, :3]
        yield np.linalg.inv(orientation) @ delta @ orientation


# ----------------------------------------------------------------------
# Scene application.


def resolve_target(context, name: str):
    """The armature a take is keyed onto: the named object, else the active armature, else the
    scene's only armature."""
    scene = context.scene
    if name:
        obj = scene.objects.get(name)
        if obj is not None:
            if obj.type != "ARMATURE":
                raise AnimationApplyError(
                    "animation_target_not_armature", f'Blender object "{name}" is not an armature.',
                    "Enter the name of the scene armature under Animation export in the plugin's options.")
            return obj
    view_layer = getattr(context, "view_layer", None)
    active = view_layer.objects.active if view_layer is not None else None
    if active is not None and active.type == "ARMATURE" and active.name in scene.objects:
        return active
    armatures = [obj for obj in scene.objects if obj.type == "ARMATURE"]
    if len(armatures) == 1:
        return armatures[0]
    missing = f'No object named "{name}" exists and' if name else "No armature was named and"
    raise AnimationApplyError(
        "animation_target_missing",
        f"{missing} the scene has {'no' if not armatures else 'several'} armatures to choose from.",
        "Select the armature in Blender, or enter its name under Animation export in the plugin's options.")


def _fcurve(action, obj, data_path: str, index: int, group: str):
    try:
        return action.fcurve_ensure_for_datablock(obj, data_path, index=index, group_name=group)
    except TypeError:
        pass
    # Blender 4.5 takes no group here: file the curve under its bone in the slot's channelbag.
    curve = action.fcurve_ensure_for_datablock(obj, data_path, index=index)
    slot = obj.animation_data.action_slot
    for layer in action.layers:
        for strip in layer.strips:
            channelbag = strip.channelbag(slot)
            if channelbag is None:
                continue
            channel_group = channelbag.groups.get(group) or channelbag.groups.new(group)
            curve.group = channel_group
            return curve
    return curve


def _linear_interpolation() -> int:
    import bpy

    return bpy.types.Keyframe.bl_rna.properties["interpolation"].enum_items["LINEAR"].value


def _key(action, obj, bone: str, attribute: str, frames: np.ndarray, values: np.ndarray, linear: int) -> None:
    import bpy

    path = f'pose.bones["{bpy.utils.escape_identifier(bone)}"].{attribute}'
    for channel in range(values.shape[1]):
        series = values[:, channel]
        if float(series.max() - series.min()) <= CONSTANT_TOLERANCE:
            keyed_frames, series = frames[:1], series[:1]
        else:
            keyed_frames = frames
        curve = _fcurve(action, obj, path, channel, bone)
        points = curve.keyframe_points
        points.add(len(keyed_frames))
        co = np.empty(2 * len(keyed_frames), dtype=np.float32)
        co[0::2] = keyed_frames
        co[1::2] = series
        points.foreach_set("co", co)
        points.foreach_set("interpolation", np.full(len(keyed_frames), linear, dtype=np.int32))
        curve.update()


def _rotation_channels(mode: str, quaternions: np.ndarray):
    """The pose bone's rotation channel and values for its rotation mode."""
    if mode == "QUATERNION":
        return "rotation_quaternion", quaternions
    if mode == "AXIS_ANGLE":
        w = np.clip(quaternions[:, 0], -1.0, 1.0)
        angle = 2.0 * np.arccos(w)
        sine = np.sqrt(np.maximum(1.0 - w * w, 0.0))
        axis = np.where(sine[:, None] > 1e-8, quaternions[:, 1:4] / np.maximum(sine, 1e-8)[:, None], [0.0, 1.0, 0.0])
        return "rotation_axis_angle", np.column_stack((angle, axis))
    from mathutils import Quaternion

    eulers, previous = [], None
    for value in quaternions:
        euler = Quaternion(value).to_euler(mode, previous) if previous is not None else Quaternion(value).to_euler(mode)
        eulers.append(tuple(euler))
        previous = euler
    return "rotation_euler", np.asarray(eulers)


def _unique_action_name(name: str) -> str:
    # Blender truncates names to 63 bytes; cut on a character boundary first.
    encoded = name.encode("utf-8")[:63]
    return encoded.decode("utf-8", errors="ignore") or "Instant Edit animation"


def apply_take(take: Take, context=None) -> dict:
    """Key ``take`` onto its target armature as a new action. Runs on Blender's main thread."""
    import bpy

    context = context or bpy.context
    scene = context.scene
    armature = resolve_target(context, take.target_object)
    if armature.library is not None or not armature.is_editable:
        raise AnimationApplyError(
            "animation_target_not_editable", f'Armature "{armature.name}" is linked and cannot be animated here.',
            "Make the armature local (Object > Library Override > Make) and send the animation again.")
    if armature.mode == "EDIT":
        armature.update_from_editmode()

    index = {name: position for position, name in enumerate(take.bones)}
    matched = []
    for bone in armature.data.bones:
        if bone.name not in index:
            continue
        ancestor = bone.parent
        while ancestor is not None and ancestor.name not in index:
            ancestor = ancestor.parent
        matched.append((bone, index[bone.name], index[ancestor.name] if ancestor is not None else -1))
    if not matched:
        raise AnimationApplyError(
            "animation_no_matching_bones",
            f'Armature "{armature.name}" has none of the animation\'s {len(take.bones)} bones.',
            "Send the animation to the FFXIV skeleton armature your meshes are weighted to.")

    fps = scene.render.fps / scene.render.fps_base
    offsets, samples = resample(take.times, take.samples, fps)
    bases = pose_bases(take, samples, armature.matrix_world,
                       [(bone.matrix_local, position, ancestor) for bone, position, ancestor in matched])
    count = len(offsets)

    frame_start = scene.frame_start
    frames = frame_start + np.arange(count, dtype=np.float64)
    had_animation_data = armature.animation_data is not None
    animation_data = armature.animation_data or armature.animation_data_create()
    previous_action = animation_data.action
    previous_slot = getattr(animation_data, "action_slot", None)
    if (previous_action is not None and not previous_action.use_fake_user
            and previous_action.users <= 1):
        # Replacing it drops the action's only user, and Blender would discard the
        # user's animation on save. A fake user keeps it.
        previous_action.use_fake_user = True
    action = bpy.data.actions.new(_unique_action_name(take.name))
    action.use_fake_user = True
    animation_data.action = action
    linear = _linear_interpolation()
    skipped = []
    try:
        for (bone, _position, _ancestor), basis in zip(matched, bases):
            if not np.isfinite(basis).all():
                skipped.append(bone.name)
                continue
            location, rotation, scale = _decompose(basis)
            pose_bone = armature.pose.bones[bone.name]
            _key(action, armature, bone.name, "location", frames, location, linear)
            channel, values = _rotation_channels(pose_bone.rotation_mode, _continuous(rotation))
            _key(action, armature, bone.name, channel, frames, values, linear)
            if take.key_scale:
                _key(action, armature, bone.name, "scale", frames, scale, linear)
    except Exception:
        # Leave the armature as it was: its previous action, or none at all.
        animation_data.action = previous_action
        if previous_action is not None and previous_slot is not None:
            animation_data.action_slot = previous_slot
        if not had_animation_data:
            armature.animation_data_clear()
        bpy.data.actions.remove(action)
        raise

    frame_end = frame_start + count - 1
    action.use_frame_range = True
    action.frame_start, action.frame_end = frame_start, max(frame_end, frame_start + 1)
    action.use_cyclic = take.loop
    action[METADATA_PROPERTY] = {
        "kind": take.kind,
        "sourceFrames": int(len(take.times)),
        "frameRate": float(fps),
        "keyedScale": bool(take.key_scale),
        "source": dict(take.source),
    }
    scene.frame_end = frame_end
    scene.frame_set(min(max(scene.frame_current, frame_start), frame_end))

    matched_names = {bone.name for bone, _, _ in matched}
    missing = [name for name in take.bones if name not in matched_names]
    try:
        if not bpy.app.background:
            bpy.ops.ed.undo_push(message=UNDO_MESSAGE)
    except Exception:
        pass
    return {
        "action": action.name,
        "armature": armature.name,
        "kind": take.kind,
        "frames": count,
        "frameRate": float(fps),
        "frameStart": int(frame_start),
        "frameEnd": int(frame_end),
        "matchedBones": len(matched) - len(skipped),
        "missingBoneCount": len(missing),
        "missingBones": missing[:24],
        "skippedBones": skipped[:24],
        "keyedScale": bool(take.key_scale),
    }


def summary(result: dict) -> str:
    """One line for the add-on's status field."""
    text = (f'Animation "{result["action"]}" keyed on "{result["armature"]}": '
            f'{result["matchedBones"]} bones, frames {result["frameStart"]}-{result["frameEnd"]}')
    if result["missingBoneCount"]:
        text += f'; {result["missingBoneCount"]} game bones are not in the armature'
    return text
