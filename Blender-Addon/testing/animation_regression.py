"""Animations received from the plugin: take parsing, the game-to-armature conversion, keying,
and the /animation endpoint. Run through run_blender_suites.py."""

import importlib
import json
import math
from pathlib import Path
import struct
import sys
import threading
import time
import urllib.error
import urllib.request
from unittest.mock import patch

import bpy
import numpy as np
from mathutils import Euler, Matrix, Quaternion, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data


PACKAGE = "_xiv_instant_edit_animation_regression"
K = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def require(condition, message):
    if not condition:
        raise AssertionError(message)
    print(f"[PASS] {message}")


def rejects(parse, body, code, message):
    module = importlib.import_module(f"{PACKAGE}.instant_edit.diagnostics")
    try:
        parse(body)
    except module.BridgeRequestError as error:
        require(error.code == code, f"{message} ({error.code})")
        return
    raise AssertionError(f"Expected rejection: {message}")


# ----------------------------------------------------------------------
# Game-side fixtures, written the way the plugin writes them.


def quat(axis, degrees):
    """A game rotation as (x, y, z, w)."""
    q = Quaternion(Vector(axis).normalized(), math.radians(degrees))
    return (q.x, q.y, q.z, q.w)


def transform(t=(0, 0, 0), q=(0, 0, 0, 1), s=(1, 1, 1)):
    return list(t) + list(q) + list(s)


# name, parent, reference
SKELETON = [
    ("n_root", -1, transform()),
    ("j_kosi", 0, transform((0.0, 1.02, 0.03), quat((0, 0, 1), 12))),
    ("j_sebo_a", 1, transform((0.08, 0.04, 0.0), quat((1, 0, 0), 21))),
    ("j_mune_l", 2, transform((0.05, 0.11, 0.08), quat((0.3, 1, 0.2), 35))),
    ("j_asi_a_l", 1, transform((0.1, -0.05, 0.09), quat((0.2, 0.1, 1), -150))),
    ("j_asi_b_l", 4, transform((0.42, 0.0, 0.0), quat((0, 0, 1), 8))),
    ("j_asi_c_l", 5, transform((0.39, 0.0, 0.0), quat((0, 1, 0), 5))),
]
NAMES = [name for name, _, _ in SKELETON]
PARENTS = [parent for _, parent, _ in SKELETON]


def game_matrix(values):
    t = Vector(values[0:3])
    x, y, z, w = values[3:7]
    s = values[7:10]
    return (Matrix.Translation(t) @ Quaternion((w, x, y, z)).to_matrix().to_4x4()
            @ Matrix.Diagonal((s[0], s[1], s[2], 1.0)))


def model_matrices(local_values):
    model = []
    for bone, parent in enumerate(PARENTS):
        local = game_matrix(local_values[bone])
        model.append(local if parent < 0 else model[parent] @ local)
    return model


def moving_frames(count, rate=30.0, seed=3):
    """Game-local samples: every bone turns about its own axis and the hips bob."""
    random = np.random.default_rng(seed)
    axes = random.normal(size=(len(SKELETON), 3))
    speeds = random.uniform(20, 90, size=len(SKELETON))
    times = [i / rate for i in range(count)]
    frames = []
    for t in times:
        frame = []
        for bone, (_, _, reference) in enumerate(SKELETON):
            base = Quaternion((reference[6], reference[3], reference[4], reference[5]))
            turn = Quaternion(Vector(axes[bone]).normalized(), math.radians(speeds[bone] * t))
            q = base @ turn
            translation = list(reference[0:3])
            if NAMES[bone] == "j_kosi":
                translation[1] += 0.03 * math.sin(2 * math.pi * t)
            frame.append(transform(translation, (q.x, q.y, q.z, q.w)))
        frames.append(frame)
    return times, frames


def take_body(names=NAMES, parents=PARENTS, references=None, times=None, frames=None, **options):
    references = references or [reference for _, _, reference in SKELETON]
    header = {
        "schema": "instant-edit.animation",
        "version": 1,
        "pluginVersion": "1.2.4",
        "kind": options.get("kind", "recording"),
        "name": options.get("name", "Regression take"),
        "targetObject": options.get("target", "Skeleton"),
        "keyScale": options.get("key_scale", False),
        "loop": options.get("loop", False),
        "source": {"character": "Regression"},
        "bones": [{"name": n, "parent": p, "reference": list(r)} for n, p, r in zip(names, parents, references)],
        "times": list(times),
    }
    data = json.dumps(header).encode("utf-8")
    head = b"XIEA" + struct.pack("<II", 1, len(data)) + data
    head += b"\0" * ((-len(head)) % 4)
    return head + np.asarray(frames, dtype="<f4").tobytes()


# ----------------------------------------------------------------------
# Scene fixtures.


def orientation_offsets(seed=11):
    """Per-bone rest orientations unlike the game's, as glTF or FBX importers create them."""
    random = np.random.default_rng(seed)
    return {name: Quaternion(Vector(random.normal(size=3)).normalized(), random.uniform(0, math.pi)).to_matrix().to_4x4()
            for name in NAMES}


def build_armature(name="Skeleton", world=Matrix.Identity(4), bones=None, parents=None, head_offsets=None):
    """An armature whose rest pose is the game reference pose, with other bone orientations."""
    reference_model = model_matrices([reference for _, _, reference in SKELETON])
    offsets = orientation_offsets()
    data = bpy.data.armatures.new(name + " Data")
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.matrix_world = world
    bpy.context.view_layer.update()
    for selected in tuple(bpy.context.selected_objects):
        selected.select_set(False)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    inverse_world = world.inverted()
    world_rotation = world.to_quaternion().to_matrix().to_4x4()
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        for bone in bones or NAMES:
            index = NAMES.index(bone)
            head = inverse_world @ (K @ reference_model[index]).to_translation()
            if head_offsets and bone in head_offsets:
                head += Vector(head_offsets[bone])
            rotation = world_rotation.inverted() @ K @ reference_model[index].to_quaternion().to_matrix().to_4x4() @ offsets[bone]
            edit = data.edit_bones.new(bone)
            edit.head, edit.tail = (0, 0, 0), (0, 0.05, 0)
            edit.matrix = Matrix.Translation(head) @ rotation
        for bone in bones or NAMES:
            parent = (parents or {}).get(bone, PARENTS[NAMES.index(bone)])
            parent_name = parent if isinstance(parent, str) else (NAMES[parent] if parent >= 0 else None)
            if parent_name and parent_name in data.edit_bones:
                data.edit_bones[bone].parent = data.edit_bones[parent_name]
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def world_pose(obj, bone):
    return obj.matrix_world @ obj.pose.bones[bone].matrix


def angle_between(a, b):
    """Angle between two rotations; q and -q are the same rotation. atan2 keeps small angles
    measurable in mathutils' single precision, where acos(w) rounds them to zero."""
    difference = a.rotation_difference(b)
    return 2 * math.atan2(Vector((difference.x, difference.y, difference.z)).length, abs(difference.w))


def check_pose(obj, frame_values, bones, tolerance, message):
    """Pose bone heads sit on the game joints and every bone turned as far as the game bone."""
    model = model_matrices(frame_values)
    reference = model_matrices([reference for _, _, reference in SKELETON])
    worst_position = worst_angle = 0.0
    for bone in bones:
        index = NAMES.index(bone)
        pose = world_pose(obj, bone)
        rest = obj.matrix_world @ obj.data.bones[bone].matrix_local
        expected_head = (K @ model[index]).to_translation()
        worst_position = max(worst_position, (pose.to_translation() - expected_head).length)
        game_turn = (K @ model[index]).to_quaternion() @ (K @ reference[index]).to_quaternion().inverted()
        scene_turn = pose.to_quaternion() @ rest.to_quaternion().inverted()
        worst_angle = max(worst_angle, angle_between(game_turn, scene_turn))
    require(worst_position < tolerance and worst_angle < tolerance,
            f"{message} (head error {worst_position:.2e}, angle error {worst_angle:.2e})")


# ----------------------------------------------------------------------


def run_parse_cases(animation):
    times, frames = moving_frames(4)
    take = animation.parse_take(take_body(times=times, frames=frames, key_scale=True, loop=True))
    require(take.bones == tuple(NAMES) and list(take.parents) == PARENTS and take.samples.shape == (4, len(NAMES), 10),
            "a take keeps its bone names, hierarchy and per-frame samples")
    require(take.kind == "recording" and take.key_scale and take.loop and take.target_object == "Skeleton"
            and take.source == {"character": "Regression"},
            "a take keeps its kind, options, target and source facts")
    require(abs(take.duration - 0.1) < 1e-9, "a take's duration spans its first to last frame time")

    body = take_body(times=times, frames=frames)
    rejects(animation.parse_take, b"XIEB" + body[4:], "invalid_animation_container", "a body without the take magic is refused")
    rejects(animation.parse_take, body[:4] + struct.pack("<I", 2) + body[8:], "unsupported_animation_version",
            "an unknown format version is refused")
    rejects(animation.parse_take, body[:-4], "animation_size_mismatch", "truncated samples are refused")
    rejects(animation.parse_take, take_body(times=[0.0, 0.1, 0.1, 0.2], frames=frames), "invalid_animation_times",
            "frame times must increase")
    rejects(animation.parse_take, take_body(parents=[-1, 0, 1, 2, 5, 4, 5], times=times, frames=frames),
            "invalid_animation_hierarchy", "a bone listed before its parent is refused")
    rejects(animation.parse_take, take_body(names=NAMES[:-1] + ["j_kosi"], times=times, frames=frames),
            "invalid_animation_bones", "a skeleton naming a bone twice is refused")
    broken = [[list(bone) for bone in frame] for frame in frames]
    broken[2][3][6] = 3.0
    rejects(animation.parse_take, take_body(times=times, frames=broken), "invalid_animation_rotation",
            "rotations that are not unit quaternions are refused")
    broken[2][3][6] = float("nan")
    rejects(animation.parse_take, take_body(times=times, frames=broken), "invalid_animation_values",
            "non-finite samples are refused")
    header = json.loads(body[12:12 + struct.unpack_from("<I", body, 8)[0]])
    header["kind"] = "pose"
    data = json.dumps(header).encode()
    wrong_kind = b"XIEA" + struct.pack("<II", 1, len(data)) + data
    wrong_kind += b"\0" * ((-len(wrong_kind)) % 4) + np.asarray(frames, dtype="<f4").tobytes()
    rejects(animation.parse_take, wrong_kind, "invalid_animation_kind", "an unknown take kind is refused")


def run_conversion_cases(animation):
    scene = bpy.context.scene
    scene.render.fps, scene.render.fps_base = 30, 1.0
    scene.frame_start = 1
    times, frames = moving_frames(31)
    take = animation.parse_take(take_body(times=times, frames=frames, name="Walk loop"))

    with temporary_scene_data():
        world = Matrix.Translation((2.0, -1.0, 0.5)) @ Euler((0.3, -0.2, 1.1)).to_matrix().to_4x4() @ Matrix.Scale(0.01, 4)
        rig = build_armature(world=world)
        rig.pose.bones["j_asi_b_l"].rotation_mode = "YXZ"
        rig.pose.bones["j_sebo_a"].rotation_mode = "AXIS_ANGLE"
        result = animation.apply_take(take)
        require(result["armature"] == "Skeleton" and result["action"] == "Walk loop" and result["frames"] == 31
                and result["frameStart"] == 1 and result["frameEnd"] == 31 and result["matchedBones"] == len(NAMES)
                and result["missingBoneCount"] == 0,
                "a take lands on the named armature as an action spanning its frames")
        require(rig.animation_data.action.name == "Walk loop" and rig.animation_data.action.use_fake_user
                and scene.frame_end == 31, "the action is kept with a fake user and the scene range covers it")
        for frame in (1, 9, 31):
            scene.frame_set(frame)
            check_pose(rig, frames[frame - 1], NAMES, 2e-4,
                       f"frame {frame} reproduces the game's model-space pose through a scaled, rotated armature object "
                       "with arbitrary rest orientations, Euler and axis-angle bones")
        paths = {curve.data_path for curve in _fcurves(rig.animation_data.action)}
        require('pose.bones["j_asi_b_l"].rotation_euler' in paths and 'pose.bones["j_sebo_a"].rotation_axis_angle' in paths
                and 'pose.bones["j_kosi"].rotation_quaternion' in paths,
                "each bone is keyed in its own rotation mode")
        require(not any(path.endswith(".scale") for path in paths), "scale is left to the scene unless requested")
        root = [curve for curve in _fcurves(rig.animation_data.action)
                if curve.data_path == 'pose.bones["n_root"].location']
        require(root and all(len(curve.keyframe_points) == 1 for curve in root),
                "channels that never change get a single key")
        metadata = rig.animation_data.action[animation.METADATA_PROPERTY]
        require(metadata["kind"] == "recording" and metadata["source"]["character"] == "Regression",
                "the action records where the take came from")

    with temporary_scene_data():
        # The scene armature has no j_asi_b_l: its child hangs straight from j_asi_a_l.
        rig = build_armature(bones=[n for n in NAMES if n != "j_asi_b_l"], parents={"j_asi_c_l": "j_asi_a_l"})
        extra = None
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode="EDIT")
        try:
            extra = rig.data.edit_bones.new("scene_only_helper")
            extra.head, extra.tail = (0, 0, 0), (0, 0.1, 0)
            extra.parent = rig.data.edit_bones["j_kosi"]
        finally:
            bpy.ops.object.mode_set(mode="OBJECT")
        result = animation.apply_take(take)
        require(result["missingBoneCount"] == 1 and result["missingBones"] == ["j_asi_b_l"],
                "game bones the armature lacks are reported")
        scene.frame_set(17)
        check_pose(rig, frames[16], [n for n in NAMES if n != "j_asi_b_l"], 2e-4,
                   "a bone parented differently in the scene still follows the game's model-space pose")
        require(not any("scene_only_helper" in curve.data_path for curve in _fcurves(rig.animation_data.action)),
                "armature bones the take does not name stay unkeyed")

    with temporary_scene_data():
        # Other proportions: the knee and ankle sit elsewhere, but turn like the game bones.
        rig = build_armature(head_offsets={"j_asi_b_l": (0.0, 0.05, -0.04), "j_asi_c_l": (0.02, 0.0, 0.03)})
        animation.apply_take(take)
        scene.frame_set(21)
        model = model_matrices(frames[20])
        reference = model_matrices([r for _, _, r in SKELETON])
        worst = 0.0
        for bone in ("j_asi_b_l", "j_asi_c_l"):
            index = NAMES.index(bone)
            game_turn = (K @ model[index]).to_quaternion() @ (K @ reference[index]).to_quaternion().inverted()
            scene_turn = world_pose(rig, bone).to_quaternion() @ (rig.matrix_world @ rig.data.bones[bone].matrix_local).to_quaternion().inverted()
            worst = max(worst, angle_between(game_turn, scene_turn))
        require(worst < 2e-4, f"a rest pose with other proportions still turns every bone like the game (error {worst:.2e})")

    with temporary_scene_data():
        scene.render.fps = 24
        rig = build_armature()
        # Uneven recording times, resampled onto the scene's 24 fps.
        uneven = [0.0, 0.011, 0.029, 0.05, 0.061, 0.083, 0.1]
        dense_times, dense_frames = moving_frames(121, rate=120.0, seed=5)
        picked = [round(t * 120) for t in uneven]
        body = take_body(times=[dense_times[i] for i in picked], frames=[dense_frames[i] for i in picked], key_scale=True)
        result = animation.apply_take(animation.parse_take(body))
        require(result["frames"] == 3 and result["frameEnd"] == scene.frame_start + 2,
                "uneven sample times are resampled onto the scene frame rate")
        scene.frame_set(scene.frame_start + 1)
        # 1/24 s lies between the 0.029 s and 0.05 s samples; the densely sampled truth is close.
        check_pose(rig, dense_frames[5], NAMES, 3e-3, "resampled frames interpolate between the recorded samples")
        require(any(curve.data_path.endswith(".scale") for curve in _fcurves(rig.animation_data.action)),
                "scale is keyed when the take asks for it")
        scene.render.fps = 30


def run_failure_cases(animation):
    times, frames = moving_frames(3)
    take = animation.parse_take(take_body(times=times, frames=frames, name="Interrupted take"))
    with temporary_scene_data():
        rig = build_armature()
        before = bpy.data.actions.new("Previous action")
        rig.animation_data_create()
        rig.animation_data.action = before
        original = animation._key
        calls = []

        def failing(*args, **kwargs):
            calls.append(args[2])
            if len(calls) > 3:
                raise RuntimeError("forced keying failure")
            return original(*args, **kwargs)

        with patch.object(animation, "_key", failing):
            try:
                animation.apply_take(take)
            except RuntimeError:
                pass
            else:
                raise AssertionError("the forced keying failure did not surface")
        require(rig.animation_data.action == before and "Interrupted take" not in bpy.data.actions,
                "an animation that fails part-way leaves the armature's previous action in place")
        rig.animation_data_clear()
        with patch.object(animation, "_key", failing):
            try:
                animation.apply_take(take)
            except RuntimeError:
                pass
        require(rig.animation_data is None, "an armature that had no animation data gets none from a failed animation")
        bpy.data.actions.remove(before)


def run_target_cases(animation):
    times, frames = moving_frames(3)
    with temporary_scene_data():
        first = build_armature("Body Rig")
        second = build_armature("Other Rig")
        bpy.context.view_layer.objects.active = second
        result = animation.apply_take(animation.parse_take(take_body(times=times, frames=frames, target="Missing")))
        require(result["armature"] == "Other Rig", "without the named object the active armature is used")
        bpy.context.view_layer.objects.active = None
        try:
            animation.apply_take(animation.parse_take(take_body(times=times, frames=frames, target="Missing")))
        except animation.AnimationApplyError as error:
            require(error.code == "animation_target_missing", "several armatures and none named or active is refused")
        else:
            raise AssertionError("an ambiguous target was accepted")
        mesh = bpy.data.objects.new("Skeleton", bpy.data.meshes.new("Not a rig"))
        bpy.context.scene.collection.objects.link(mesh)
        try:
            animation.apply_take(animation.parse_take(take_body(times=times, frames=frames, target="Skeleton")))
        except animation.AnimationApplyError as error:
            require(error.code == "animation_target_not_armature", "a named object that is not an armature is refused")
        else:
            raise AssertionError("a mesh target was accepted")
        bpy.data.objects.remove(mesh)
        bpy.data.objects.remove(first)
        result = animation.apply_take(animation.parse_take(take_body(times=times, frames=frames, target="")))
        require(result["armature"] == "Other Rig", "the scene's only armature is used when none is named")
        unrelated = build_armature("Unrelated", bones=["n_root"])
        unrelated.data.bones["n_root"].name = "root"
        try:
            animation.apply_take(animation.parse_take(take_body(times=times, frames=frames, target="Unrelated")))
        except animation.AnimationApplyError as error:
            require(error.code == "animation_no_matching_bones", "an armature without any of the take's bones is refused")
        else:
            raise AssertionError("an armature without matching bones was accepted")


def run_endpoint_cases(server):
    from http.server import ThreadingHTTPServer

    status = server._status_payload()
    require(server.ANIMATION_IMPORT_CAPABILITY in status["capabilities"], "the add-on advertises animation import")
    times, frames = moving_frames(5)
    with temporary_scene_data():
        build_armature()
        listener = ThreadingHTTPServer(("127.0.0.1", 0), server._ImportHandler)
        thread = threading.Thread(target=listener.serve_forever, daemon=True)
        thread.start()
        try:
            def post(body):
                outcome = {}

                def send():
                    request = urllib.request.Request(
                        f"http://127.0.0.1:{listener.server_address[1]}/animation", data=body, method="POST",
                        headers={"Content-Type": "application/octet-stream"})
                    try:
                        with urllib.request.urlopen(request, timeout=30) as response:
                            outcome["status"], outcome["body"] = response.status, json.loads(response.read())
                    except urllib.error.HTTPError as error:
                        outcome["status"], outcome["body"] = error.code, json.loads(error.read())

                sender = threading.Thread(target=send, daemon=True)
                sender.start()
                deadline = time.monotonic() + 30
                while sender.is_alive() and time.monotonic() < deadline:
                    server.process_animation_queue()
                    time.sleep(0.01)
                sender.join(1)
                return outcome

            done = post(take_body(times=times, frames=frames, name="Endpoint take"))
            require(done.get("status") == 200 and done["body"]["applied"] and done["body"]["action"] == "Endpoint take"
                    and done["body"]["matchedBones"] == len(NAMES),
                    "the endpoint keys a take on Blender's main thread and reports the result")
            require(bpy.context.scene.xiv_ie_instant_edit_props.last_status.startswith('Animation "Endpoint take"'),
                    "the add-on's status line reports the keyed animation")
            bad = post(b"XIEA" + struct.pack("<II", 1, 1000) + b"{}")
            require(bad.get("status") == 400 and bad["body"]["code"] == "invalid_animation_header"
                    and bad["body"]["operation"] == "animation_import",
                    "a malformed take is refused with a structured failure")
            missing = post(take_body(times=times, frames=frames, target="Missing", names=["a", "b", "c", "d", "e", "f", "g"]))
            require(missing.get("status") == 422 and missing["body"]["code"] == "animation_no_matching_bones"
                    and missing["body"]["stage"] == "animation_processing",
                    "a take the scene cannot use reports why from the main thread")
        finally:
            listener.shutdown()
            listener.server_close()


def _fcurves(action):
    if hasattr(action, "fcurves"):
        return list(action.fcurves)
    return [curve for layer in action.layers for strip in layer.strips
            for bag in strip.channelbags for curve in bag.fcurves]


if __name__ == "__main__":
    with addon_session(PACKAGE):
        animation_module = importlib.import_module(f"{PACKAGE}.instant_edit.animation")
        server_module = importlib.import_module(f"{PACKAGE}.instant_edit.server")
        run_parse_cases(animation_module)
        run_conversion_cases(animation_module)
        run_failure_cases(animation_module)
        run_target_cases(animation_module)
        run_endpoint_cases(server_module)
    print(f"Animation regression passed on Blender {bpy.app.version_string}")
