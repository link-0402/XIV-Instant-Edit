"""Game skeletons for imported models: validating the plugin's skeleton, the generated armature's
rest pose and hierarchy, model bones the skeleton lacks, placeholder bones without a skeleton,
posing, exporting and animating the armature, and the import bridge. Run through
run_blender_suites.py."""

import importlib
import json
import math
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data


PACKAGE = "_xiv_instant_edit_skeleton_regression"
K = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def require(condition, message):
    if not condition:
        raise AssertionError(message)
    print(f"[PASS] {message}")


def module(name):
    return importlib.import_module(f"{PACKAGE}.{name}")


def quat(axis, degrees):
    """A game rotation as (x, y, z, w)."""
    q = Quaternion(Vector(axis).normalized(), math.radians(degrees))
    return (q.x, q.y, q.z, q.w)


def transform(t=(0, 0, 0), q=(0, 0, 0, 1), s=(1, 1, 1)):
    return list(t) + list(q) + list(s)


# name, parent, reference: a spine, a head, a bone hidden by scaling it to nothing (as mods do),
# and bones whose Y axis points along Blender's -Y, where bone roll is least stable.
SKELETON = [
    ("n_root", -1, transform()),
    ("j_kosi", 0, transform((0.0, 1.02, 0.03), quat((0, 0, 1), 12))),
    ("j_sebo_a", 1, transform((0.08, 0.04, 0.0), quat((1, 0, 0), 21))),
    ("j_kao", 2, transform((0.1, 0.5, 0.02), quat((0.3, 1, 0.2), 35))),
    ("j_f_face", 3, transform((0.01, 0.0, 0.02), quat((0, 0, 1), 90))),
    ("j_hidden", 2, transform((0.05, 0.1, 0.0), quat((0, 1, 0), 10), (0, 0, 0))),
    ("j_hidden_child", 5, transform((0.0, 0.2, 0.0), quat((1, 0, 0), 15))),
    ("j_forward", 0, transform((0.0, 0.9, 0.1), quat((1, 0, 0), 90))),
    ("j_nearly_forward", 0, transform((0.1, 0.9, 0.1), quat((1, 0.0001, 0), 89.999))),
]
ANIMATED = SKELETON[:5]


def payload(bones=SKELETON, **extra):
    return {
        "schema": "instant-edit.skeleton",
        "version": 1,
        "source": "files",
        "race": "c0101",
        "skeletons": ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"],
        "warnings": [],
        "bones": [{"name": name, "parent": parent, "reference": list(values)} for name, parent, values in bones],
        **extra,
    }


def rigid_model(bones):
    """Blender-space rest matrices: rotations and positions composed along the parents."""
    model = []
    for name, parent, values in bones:
        x, y, z, w = values[3:7]
        local = Matrix.Translation(Vector(values[0:3])) @ Quaternion((w, x, y, z)).to_matrix().to_4x4()
        model.append(local if parent < 0 else model[parent] @ local)
    return {name: K @ matrix for (name, _, _), matrix in zip(bones, model)}


def max_difference(a, b):
    return max(abs(a[i][j] - b[i][j]) for i in range(4) for j in range(4))


def rejects(parse, value, code, message):
    diagnostics = module("instant_edit.diagnostics")
    try:
        parse(value)
    except diagnostics.BridgeRequestError as error:
        require(error.code == code, f"{message} ({error.code})")
        return
    raise AssertionError(f"Expected rejection: {message}")


def mesh_object(name, vertices, groups, collection=None):
    """A triangle strip over ``vertices`` with {group: [(vertex, weight)]}."""
    mesh = bpy.data.meshes.new(name)
    faces = [(i, i + 1, i + 2) for i in range(len(vertices) - 2)]
    mesh.from_pydata([tuple(v) for v in vertices], [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    (collection or bpy.context.scene.collection).objects.link(obj)
    for group_name, entries in groups.items():
        group = obj.vertex_groups.new(name=group_name)
        for index, weight in entries:
            group.add([index], weight, "REPLACE")
    return obj


def evaluated_positions(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        return [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


# ----------------------------------------------------------------------


def check_parsing():
    skeleton = module("instant_edit.skeleton")
    parsed = skeleton.parse_skeleton(payload())
    require(parsed.bones == tuple(name for name, _, _ in SKELETON) and list(parsed.parents) == [p for _, p, _ in SKELETON]
            and parsed.race == "c0101" and parsed.source == "files",
            "the plugin's skeleton parses into bones, parents and reference poses")
    require(skeleton.parse_skeleton(parsed.to_payload()).bones == parsed.bones,
            "a parsed skeleton writes back the same payload")
    parse = skeleton.parse_skeleton
    rejects(parse, [], "invalid_skeleton", "a skeleton that is not an object is refused")
    rejects(parse, payload(schema="instant-edit.animation"), "unsupported_skeleton_schema", "another schema is refused")
    rejects(parse, payload(version=2), "unsupported_skeleton_schema", "another version is refused")
    rejects(parse, payload(bones=[]), "invalid_skeleton_bones", "a skeleton without bones is refused")
    rejects(parse, {**payload(), "bones": [{"name": "a", "parent": -1, "reference": transform()}] * 4097},
            "invalid_skeleton_bones", "a skeleton of more than 4096 bones is refused")
    rejects(parse, payload(bones=[("a", -1, transform()), ("a", 0, transform())]),
            "invalid_skeleton_bones", "a skeleton naming a bone twice is refused")
    rejects(parse, payload(bones=[("a", 0, transform())]), "invalid_skeleton_hierarchy",
            "a bone listed before its parent is refused")
    rejects(parse, payload(bones=[("a", -1, transform()), ("b", True, transform())]), "invalid_skeleton_hierarchy",
            "a parent index that is a boolean is refused")
    rejects(parse, payload(bones=[("a", -1, transform()[:9])]), "invalid_skeleton_bones",
            "a reference pose of the wrong length is refused")
    rejects(parse, payload(bones=[("a", -1, transform((float("nan"), 0, 0)))]), "invalid_skeleton_values",
            "a reference pose that is not finite is refused")
    rejects(parse, payload(bones=[("a", -1, transform((10 ** 400, 0, 0)))]), "invalid_skeleton_values",
            "a reference pose too large for a float is refused")
    rejects(parse, payload(bones=[("a", -1, transform(q=(0, 0, 0, 2)))]), "invalid_skeleton_rotation",
            "a reference rotation that is not a unit quaternion is refused")
    rejects(parse, payload(warnings=["x" * 513]), "invalid_skeleton", "overlong warnings are refused")


def check_rest_matrices():
    skeleton = module("instant_edit.skeleton")
    rests = skeleton.rest_matrices(skeleton.parse_skeleton(payload()))
    expected = rigid_model(SKELETON)
    # mathutils computes in single precision.
    worst = max(max_difference(Matrix(rest.tolist()), expected[name]) for (name, _, _), rest in zip(SKELETON, rests))
    require(worst < 1e-6, f"rest matrices are the game's bind pose turned Z up, without scale ({worst:.1e})")
    hidden_child = Matrix(rests[6].tolist())
    require((hidden_child.translation - expected["j_hidden"].translation).length > 0.1,
            "a bone below a bone scaled to nothing keeps its own place")


def check_armature(context):
    skeleton = module("instant_edit.skeleton")
    game = skeleton.parse_skeleton(payload())
    expected = rigid_model(SKELETON)
    kao = expected["j_kao"].translation
    vertices = [kao + Vector((0.02, 0, 0)), kao + Vector((0, 0.03, 0.01)), kao + Vector((0, 0, 0.05)),
                kao + Vector((0.1, 0.1, 0)), Vector((0.3, 0.2, 1.0))]
    body = mesh_object("Skeleton Body", vertices, {
        "j_kao": [(0, 0.4), (1, 0.8), (2, 1.0)],
        "iv_extra": [(0, 0.6), (1, 0.2), (3, 1.0)],
        "j_sebo_a": [(3, 0.3), (4, 1.0)],
    })
    collection = bpy.data.collections.new("Skeleton Import")
    context.scene.collection.children.link(collection)
    created = []
    armature, report = skeleton.create_armature(
        context, collection, "InstantEditArmature", ["j_kao", "iv_extra", "j_sebo_a", "iv_orphan", "j_kao"], [body], game,
        created_objects=created)
    bones = armature.data.bones
    require(created == [armature] and armature.matrix_world == Matrix.Identity(4) and collection in armature.users_collection,
            "the armature object sits at the origin in the import's collection and is recorded for cleanup")
    require(set(bones.keys()) == {name for name, _, _ in SKELETON} | {"iv_extra", "iv_orphan"},
            "the armature has every skeleton bone and every model bone once")
    worst = max(max_difference(bones[name].matrix_local, expected[name]) for name, _, _ in SKELETON)
    require(worst < 1e-5, f"every bone's rest matrix is the game's bind pose (largest difference {worst:.2e})")
    require(all((bones[name].tail_local - bones[name].head_local - skeleton.BONE_LENGTH * expected[name].col[1].xyz).length < 1e-5
                for name, _, _ in SKELETON),
            "bones point along their own Y axis, as the game's do")
    require(all((bones[name].parent.name if bones[name].parent else None) == (SKELETON[parent][0] if parent >= 0 else None)
                for name, parent, _ in SKELETON),
            "bones have the game's parents")
    weights = {0: 0.6, 1: 0.2, 3: 1.0}
    centroid = sum((vertices[i] * w for i, w in weights.items()), Vector()) / sum(weights.values())
    extra = bones["iv_extra"]
    require((extra.head_local - centroid).length < 1e-5 and extra.parent is not None and extra.parent.name == "j_kao"
            and max_difference(extra.matrix_local.to_3x3().to_4x4(), K) < 1e-5,
            "a weighted bone no skeleton has sits at its vertices, under the bone it shares most weight with")
    orphan = bones["iv_orphan"]
    center = sum(vertices, Vector()) / len(vertices)
    require((orphan.head_local - center).length < 1e-5 and orphan.parent is not None,
            "a bone without weighted vertices sits at the model's centre under the nearest bone")
    require(report.added == ["iv_extra", "iv_orphan"] and report.unweighted == ["iv_orphan"]
            and "iv_extra" in report.summary() and "2 bones" in report.summary(),
            "bones no skeleton has are reported")
    no_extra = skeleton.create_armature(context, collection, "Complete", ["j_kao"], [body], game)[1]
    require(no_extra.summary() == "", "a model whose bones are all in the skeleton reports nothing")
    return armature, body, collection


def check_placeholders(context, body, collection):
    skeleton = module("instant_edit.skeleton")
    armature, report = skeleton.create_armature(context, collection, "Placeholder", ["j_kao", "iv_extra"], [body])
    bones = armature.data.bones
    require(set(bones.keys()) == {"j_kao", "iv_extra"} and all(
        bone.parent is None and bone.head_local.length < 1e-9 and (bone.tail_local - Vector((0, 0, 0.1))).length < 1e-9
        for bone in bones),
        "without a skeleton the model's bones sit at the origin without parents, as before")
    require("no rest pose" in report.summary(), "placeholder bones are reported")


def check_posing(context, armature, body):
    body.parent = armature
    modifier = body.modifiers.new(name="Armature", type="ARMATURE")
    modifier.object = armature
    context.view_layer.update()
    rest = evaluated_positions(body)
    require(all((a - b).length < 1e-6 for a, b in zip(rest, [v.co for v in body.data.vertices])),
            "at rest the armature leaves the mesh where the model has it")
    pose_bone = armature.pose.bones["j_sebo_a"]
    pose_bone.rotation_mode = "QUATERNION"
    pose_bone.rotation_quaternion = Quaternion((1, 0, 0), math.radians(30))
    context.view_layer.update()
    posed = evaluated_positions(body)
    sebo_head = armature.data.bones["j_sebo_a"].head_local
    require((armature.pose.bones["j_sebo_a"].head - sebo_head).length < 1e-6,
            "a posed bone turns about its own joint")
    kao = armature.pose.bones["j_kao"]
    moved = kao.matrix @ armature.data.bones["j_kao"].matrix_local.inverted()
    require((kao.head - armature.data.bones["j_kao"].head_local).length > 0.05,
            "its children follow it")
    require((posed[2] - moved @ rest[2]).length < 1e-5 and
            abs((posed[2] - sebo_head).length - (rest[2] - sebo_head).length) < 1e-5,
            "vertices of a child bone turn with it about the parent's joint")
    return pose_bone


def check_export(addon, armature, body, pose_bone):
    """Exports evaluate meshes at the armature's rest pose, so a real rest pose changes nothing."""
    export_module = importlib.import_module(f"{addon.__name__}.mesh.export")
    model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
    importer = importlib.import_module(f"{addon.__name__}.io.model")
    mesh = body.data
    uv = mesh.uv_layers.new(name="uv0")
    uv.uv.foreach_set("vector", [0.5] * (2 * len(mesh.loops)))
    colour = mesh.color_attributes.new(name="vc0", type="FLOAT_COLOR", domain="CORNER")
    colour.data.foreach_set("color", [1.0] * (4 * len(mesh.loops)))
    material = bpy.data.materials.new("SkeletonExportMaterial")
    mesh.materials.append(material)
    body["xiv_material"] = "/mt_c0101e0001_top_a.mtrl"
    body.name = "0.0 Skeleton Body"
    settings = bpy.context.scene.xiv_ie_settings
    settings.model_format = "MDL"
    before = [tuple(v.co) for v in mesh.vertices]
    with tempfile.TemporaryDirectory(prefix="xiv-ie-skeleton-") as folder:
        target = Path(folder) / "skeleton"
        export_module.export_result(target, "MDL", export_objects=[body])
        model = model_module.XIVModel.from_file(str(target) + ".mdl")
        require(set(model.bones) == {"j_kao", "iv_extra", "j_sebo_a"}, "the export names the weighted bones")
        imported = importer.ModelImport.from_file(str(target) + ".mdl", "Skeleton Roundtrip", select_objects=False)
        # The export splits vertices per corner; compare the positions it holds.
        positions = sorted({tuple(round(c, 5) for c in v.co) for obj in imported for v in obj.data.vertices})
        for obj in imported:
            bpy.data.objects.remove(obj, do_unlink=True)
    original = sorted({tuple(round(c, 5) for c in co) for co in before})
    require(len(positions) == len(original) and
            max((Vector(a) - Vector(b)).length for a, b in zip(positions, original)) < 1e-4,
            "a posed armature with the game's rest pose exports the model's own vertex positions")
    require(abs(pose_bone.rotation_quaternion.angle - math.radians(30)) < 1e-6 and armature.data.pose_position == "POSE",
            "the export restores the pose afterwards")


def check_yas_removal(addon, context):
    """Removing YAS groups on export moves their weights to the parent bone, which placeholder
    bones never had."""
    skeleton = module("instant_edit.skeleton")
    export_module = importlib.import_module(f"{addon.__name__}.mesh.export")
    model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
    bones = SKELETON[:3] + [("iv_shiri_l", 1, transform((0.08, -0.05, -0.06), quat((0, 1, 0), 30)))]
    kosi = rigid_model(bones)["j_kosi"].translation
    vertices = [kosi + Vector(offset) for offset in ((0, 0, 0), (0.1, 0, 0), (0, 0.1, 0), (0.1, 0.1, 0))]
    body = mesh_object("0.0 Yas Body", vertices, {
        "j_kosi": [(0, 0.5), (1, 0.3), (2, 1.0), (3, 1.0)],
        "iv_shiri_l": [(0, 0.5)],
        "iv_unknown": [(1, 0.7)],
    })
    uv = body.data.uv_layers.new(name="uv0")
    uv.uv.foreach_set("vector", [0.5] * (2 * len(body.data.loops)))
    body.data.materials.append(bpy.data.materials.new("YasMaterial"))
    body["xiv_material"] = "/mt_c0101e0001_top_a.mtrl"
    collection = bpy.data.collections.new("Yas Import")
    context.scene.collection.children.link(collection)
    armature, report = skeleton.create_armature(
        context, collection, "Yas", ["j_kosi", "iv_shiri_l", "iv_unknown"], [body], skeleton.parse_skeleton(payload(bones)))
    body.parent = armature
    body.modifiers.new(name="Armature", type="ARMATURE").object = armature
    require(report.added == ["iv_unknown"] and armature.data.bones["iv_unknown"].parent.name == "j_kosi",
            "a YAS bone the skeleton lacks hangs from the bone it shares its weights with")
    settings = context.scene.xiv_ie_settings
    settings.model_format = "MDL"
    settings.remove_yas = "REMOVE"
    try:
        with tempfile.TemporaryDirectory(prefix="xiv-ie-skeleton-yas-") as folder:
            target = Path(folder) / "yas"
            export_module.export_result(target, "MDL", export_objects=[body])
            model = model_module.XIVModel.from_file(str(target) + ".mdl")
    finally:
        settings.remove_yas = "KEEP"
    require(model.bones == ["j_kosi"], "exporting without YAS groups moves their weights to their parent bones")


def check_combination(context):
    """Combining two generated armatures keeps every bone's rest pose and parent."""
    skeleton = module("instant_edit.skeleton")
    armatures = importlib.import_module(f"{PACKAGE}.mesh.armatures")
    collection = bpy.data.collections.new("Combined Import")
    context.scene.collection.children.link(collection)
    body, _ = skeleton.create_armature(context, collection, "Body Rig", [], [], skeleton.parse_skeleton(payload(SKELETON[:4])))
    extra = SKELETON[:4] + [("j_ex_h0114_ke_f", 3, transform((0.02, 0.1, 0.08), quat((1, 0, 0), 40)))]
    hair, _ = skeleton.create_armature(context, collection, "Hair Rig", [], [], skeleton.parse_skeleton(payload(extra)))
    for obj in tuple(context.selected_objects):
        obj.select_set(False)
    result, _count = armatures.combine_armatures(context, body, hair)
    expected = rigid_model(extra)
    bone = result.data.bones["j_ex_h0114_ke_f"]
    require(bone.parent.name == "j_kao" and max(max_difference(result.data.bones[name].matrix_local, expected[name])
                                                for name, _, _ in extra) < 1e-5,
            "combining generated armatures keeps the game's rest pose and parents")


def check_animation(context):
    """A take sent for this skeleton is exact on it: every bone gets the game's pose."""
    skeleton = module("instant_edit.skeleton")
    animation = module("instant_edit.animation")
    collection = bpy.data.collections.new("Skeleton Animation")
    context.scene.collection.children.link(collection)
    armature, _ = skeleton.create_armature(
        context, collection, "Animated", [], [], skeleton.parse_skeleton(payload(ANIMATED)))
    random = np.random.default_rng(7)
    frames = []
    for _ in range(3):
        frame = []
        for name, _, values in ANIMATED:
            base = Quaternion((values[6], values[3], values[4], values[5]))
            turn = Quaternion(Vector(random.normal(size=3)).normalized(), math.radians(random.uniform(-40, 40)))
            q = base @ turn
            frame.append(transform(np.array(values[0:3]) + random.uniform(-0.02, 0.02, 3), (q.x, q.y, q.z, q.w)))
        frames.append(frame)
    header = {
        "schema": "instant-edit.animation", "version": 1, "pluginVersion": "1.2.4", "kind": "recording",
        "name": "Skeleton take", "targetObject": armature.name, "keyScale": False, "loop": False, "source": {},
        "bones": [{"name": name, "parent": parent, "reference": values} for name, parent, values in ANIMATED],
        "times": [i / 30 for i in range(len(frames))],
    }
    encoded = json.dumps(header).encode("utf-8")
    body = bytearray(b"XIEA") + (1).to_bytes(4, "little") + len(encoded).to_bytes(4, "little") + encoded
    body += bytes((-len(body)) % 4)
    body += np.asarray(frames, dtype="<f4").tobytes()
    context.scene.render.fps, context.scene.render.fps_base = 30, 1.0
    result = animation.apply_take(animation.parse_take(bytes(body)), context)
    worst = 0.0
    for index, frame in enumerate(frames):
        context.scene.frame_set(context.scene.frame_start + index)
        expected = rigid_model([(name, parent, values) for (name, parent, _), values in zip(ANIMATED, frame)])
        worst = max(worst, max(max_difference(armature.pose.bones[name].matrix, expected[name]) for name, _, _ in ANIMATED))
    require(result["matchedBones"] == len(ANIMATED) and worst < 1e-4,
            f"a take keyed on the generated armature gives every bone the game's pose (largest difference {worst:.1e})")


def check_bridge(addon, context):
    server = module("instant_edit.server")
    skeleton = module("instant_edit.skeleton")
    require(server.IMPORT_SKELETON_CAPABILITY in server._status_payload()["capabilities"],
            "the add-on tells the plugin it takes skeletons with imports")
    base = {
        "schema": "instant-edit.context", "version": 1, "pluginInstanceId": "plugin-instance",
        "contextId": "skeleton-context", "importId": "skeleton-import", "capability": "capability",
        "filePath": r"C:\Temp\instant-edit-import.mdl", "sourceGamePath": "chara/equipment/e0001/model/c0101e0001_top.mdl",
        "objectIndex": 0, "displayName": "Skeleton Model", "callbackPort": 42428,
        "targetFilePath": r"D:\Penumbra\SourceMod\models\original.mdl", "managedDestination": r"D:\Penumbra\SourceMod\models",
        "sourceModDirectory": "SourceModDirectory", "sourceModName": "Source Mod", "sourceModRootPath": r"D:\Penumbra\SourceMod",
        "targetRelativePath": "Files/models/original.mdl", "resourceManifestVersion": 0, "resourceManifestStatus": "capture_failed",
    }
    require(server._ImportHandler._validate_import(base)["skeleton"] is None and
            server._ImportHandler._validate_import({**base, "skeleton": None})["skeleton"] is None,
            "an import without a skeleton is accepted, as from an older plugin")
    validated = server._ImportHandler._validate_import({**base, "skeleton": payload()})
    require(validated["skeleton"]["bones"][1]["name"] == "j_kosi", "an import's skeleton is validated and kept")
    rejects(server._ImportHandler._validate_import, {**base, "skeleton": payload(bones=[("a", 3, transform())])},
            "invalid_skeleton_hierarchy", "an import with an invalid skeleton is refused")
    with tempfile.TemporaryDirectory(prefix="xiv-ie-skeleton-job-") as job:
        staged = skeleton.stage_skeleton({**validated, "cacheJobDirectory": job})
        require("skeleton" not in staged and Path(staged["skeletonPath"]).parent == Path(job)
                and skeleton.load_skeleton(staged["skeletonPath"]).bones == tuple(name for name, _, _ in SKELETON),
                "the skeleton is staged into the import's cache job and read back from there")
        require("skeletonPath" not in skeleton.stage_skeleton({**base, "skeleton": None, "cacheJobDirectory": job}),
                "an import without a skeleton stages no skeleton file")

        calls = []
        fake_ops = SimpleNamespace(
            xiv_ie=SimpleNamespace(instant_import=lambda *args, **kwargs: calls.append(kwargs) or {"FINISHED"}),
            object=bpy.ops.object)
        with patch.object(server, "bpy", SimpleNamespace(context=bpy.context, ops=fake_ops, app=bpy.app)):
            server._import_queue.put_nowait(staged)
            server.poll_import_queue()
        require(calls and calls[0]["skeleton_path"] == staged["skeletonPath"],
                "the queued import hands its skeleton file to the import operator")

        ops = module("instant_edit.ops")
        vertices = [Vector((0, 0.3, 1.5)), Vector((0.1, 0.3, 1.5)), Vector((0, 0.35, 1.6))]

        def fake_import(file_path, import_name, collection=None, context_metadata=None, **kwargs):
            obj = mesh_object("0.0 Skeleton Import", vertices, {"j_kao": [(0, 1.0)], "iv_extra": [(1, 1.0), (2, 1.0)]},
                              collection)
            kwargs["created_objects"].append(obj)
            return (obj,)

        with tempfile.NamedTemporaryFile(suffix=".mdl", delete=False) as handle:
            model_file = Path(handle.name)
        try:
            with patch.object(ops.ModelImport, "from_file", staticmethod(fake_import)), \
                    patch.object(ops.XIVModel, "from_file", staticmethod(lambda _path: SimpleNamespace(bones=["j_kao", "iv_extra"]))), \
                    patch.object(ops, "_request_variant_targets", lambda *_args, **_kwargs: None, create=True), \
                    patch.object(ops, "refresh_variant_targets_after_operation", lambda _context: None):
                result = bpy.ops.xiv_ie.instant_import(
                    "EXEC_DEFAULT", file_path=str(model_file), import_name="Skeleton Import",
                    schema="instant-edit.context", version=1, plugin_instance_id="plugin-instance",
                    context_id="skeleton-context", import_id="skeleton-import", capability="capability",
                    source_game_path="chara/equipment/e0001/model/c0101e0001_top.mdl", object_index=0,
                    callback_port=42428, managed_destination=r"D:\Penumbra\SourceMod\models",
                    target_file_path=r"D:\Penumbra\SourceMod\models\original.mdl",
                    source_mod_directory="SourceModDirectory", source_mod_name="Source Mod",
                    source_mod_root_path=r"D:\Penumbra\SourceMod", target_relative_path="Files/models/original.mdl",
                    resource_manifest_version=0, resource_manifest_status="capture_failed",
                    skeleton_path=staged["skeletonPath"])
        finally:
            model_file.unlink(missing_ok=True)
    props = context.scene.xiv_ie_instant_edit_props
    armature = next(obj for obj in bpy.data.objects if obj.type == "ARMATURE" and obj.get("instant_edit_context_id")
                    == "skeleton-context" or obj.type == "ARMATURE" and obj.get("context_id") == "skeleton-context")
    mesh = next(obj for obj in bpy.data.objects if obj.name.startswith("0.0 Skeleton Import"))
    expected = rigid_model(SKELETON)
    require(result == {"FINISHED"} and mesh.parent == armature and
            max_difference(armature.data.bones["j_kao"].matrix_local, expected["j_kao"]) < 1e-5 and
            armature.data.bones["j_kao"].parent.name == "j_sebo_a",
            "Instant Import builds its armature from the skeleton the plugin sent")
    require("iv_extra" in props.last_status, "Instant Import reports model bones the skeleton lacks")


def check_simple_import(addon, context):
    skeleton = module("instant_edit.skeleton")
    plugin_http = module("instant_edit.plugin_http")
    operators = importlib.import_module(f"{addon.__name__}.operators")
    importer = importlib.import_module(f"{addon.__name__}.io.model")
    requests = []

    def answer(status, body):
        def post_json(port, endpoint, request, **_kwargs):
            requests.append((port, endpoint, request))
            return status, json.dumps(body).encode("utf-8")
        return post_json

    with patch.object(plugin_http, "post_json", answer(200, {"ok": True, "skeleton": payload()})):
        found, reason = skeleton.request_skeleton(r"C:\Mods\Tops\c0101e0001_top.mdl", port=42428)
    require(found is not None and reason == "" and requests[-1] == (
        42428, "/skeleton", {"schema": "instant-edit.skeleton-request", "version": 1, "modelPath": "c0101e0001_top.mdl"}),
        "a model file from disk asks the plugin for its skeleton by its game file name")
    with patch.object(plugin_http, "post_json", answer(404, {"ok": False, "code": "skeleton_not_found", "cause": "No skeleton."})):
        require(skeleton.request_skeleton("c0101e0001_top.mdl", port=42428) == (None, "No skeleton."),
                "the plugin's reason for having no skeleton is passed on")

    def refuse(*_args, **_kwargs):
        raise ConnectionRefusedError()

    with patch.object(plugin_http, "post_json", refuse):
        require(skeleton.request_skeleton("c0101e0001_top.mdl", port=42428)[1] == "the XIV Instant Edit plugin isn't running",
                "without the plugin there is no skeleton, and the report says why")
    count = len(requests)
    require(skeleton.request_skeleton("my edit.mdl", port=42428)[0] is None and len(requests) == count,
            "a file not named like a game model is not looked up")

    vertices = [Vector((0, 0.3, 1.5)), Vector((0.1, 0.3, 1.5)), Vector((0, 0.35, 1.6))]

    def fake_import(file_path, import_name, **kwargs):
        obj = mesh_object("0.0 Simple Skeleton", vertices, {"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]})
        kwargs["created_objects"].append(obj)
        return (obj,)

    settings = context.scene.xiv_ie_settings
    settings.simple_import_use_existing_skeleton = False
    settings.simple_import_set_export_directory = False
    with tempfile.TemporaryDirectory(prefix="xiv-ie-skeleton-simple-") as folder:
        model_file = Path(folder) / "c0101e0001_top.mdl"
        model_file.write_bytes(b"model")
        with patch.object(importer.ModelImport, "from_file", staticmethod(fake_import)), \
                patch.object(operators.XIVModel, "from_file", staticmethod(lambda _path: SimpleNamespace(bones=["j_kao"]))), \
                patch.object(module("instant_edit.ops"), "refresh_variant_targets_after_operation", lambda _context: None), \
                patch.object(plugin_http, "post_json", answer(200, {"ok": True, "skeleton": payload()})):
            result = bpy.ops.xiv_ie.simple_import(filepath=str(model_file))
    armature = next(obj for obj in bpy.data.objects if obj.name.startswith("0.0 Simple Skeleton")).parent
    require(result == {"FINISHED"} and armature is not None and armature.type == "ARMATURE" and
            max_difference(armature.data.bones["j_kao"].matrix_local, rigid_model(SKELETON)["j_kao"]) < 1e-5,
            "Import Model File builds its armature from the plugin's skeleton")


def run():
    with addon_session(PACKAGE) as addon:
        context = bpy.context
        with temporary_scene_data():
            check_parsing()
            check_rest_matrices()
            armature, body, collection = check_armature(context)
            check_placeholders(context, body, collection)
            pose_bone = check_posing(context, armature, body)
            check_export(addon, armature, body, pose_bone)
        with temporary_scene_data():
            check_yas_removal(addon, context)
        with temporary_scene_data():
            check_combination(context)
        with temporary_scene_data():
            check_animation(context)
        with temporary_scene_data():
            check_bridge(addon, context)
        with temporary_scene_data():
            check_simple_import(addon, context)
    print("[RESULT] skeleton regression PASSED")


if __name__ == "__main__":
    try:
        run()
    except Exception as error:
        detail = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error file=Blender-Addon/testing/skeleton_regression.py::Skeleton regression failed: {detail}", flush=True)
        raise
