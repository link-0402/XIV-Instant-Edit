"""Whole-character sends from the plugin: the character entry of an import, the send's shared
armature (built by its first import from the character's skeleton, extended by later models), a
weapon's own armature hung from the bone that holds it and exported at the origin, a new send of
the same character replacing the previous one, poses keyed onto the send's armature, the import
queue running imports before animations, and models shown as the game draws them (hidden parts
hidden but exported, the game's shape keys on but not exported). Run through run_blender_suites.py."""

import importlib
import json
import math
from pathlib import Path
import sys
import tempfile
import threading
from types import SimpleNamespace
from unittest.mock import patch

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data


PACKAGE = "_xiv_instant_edit_character_regression"
K = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
SEND_A = "a" * 32
SEND_B = "b" * 32
SEND_C = "c" * 32
KEY = "Firstname Lastname@73"


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


# The character's whole skeleton, as the plugin merges it: body bones, a hair bone from the hair
# skeleton, and the right-hand weapon bone.
CHARACTER = [
    ("n_root", -1, transform()),
    ("j_kosi", 0, transform((0.0, 1.02, 0.03), quat((0, 0, 1), 12))),
    ("j_sebo_a", 1, transform((0.08, 0.04, 0.0), quat((1, 0, 0), 21))),
    ("j_kao", 2, transform((0.1, 0.5, 0.02), quat((0.3, 1, 0.2), 35))),
    ("j_ex_h0114_ke_f", 3, transform((0.02, 0.1, 0.08), quat((1, 0, 0), 40))),
    ("j_ude_a_r", 2, transform((-0.2, 0.3, 0.0), quat((0, 0, 1), -70))),
    ("n_buki_r", 5, transform((0.0, 0.35, 0.02), quat((0, 1, 0), 25))),
]
# A weapon's own skeleton: its root repeats the body's n_root.
WEAPON = [
    ("n_root", -1, transform()),
    ("j_buki", 0, transform((0.0, 0.3, 0.0), quat((1, 0, 0), 10))),
]
# Where the weapon sits on n_buki_r, in the game's space.
WEAPON_OFFSET = transform((0.01, 0.12, -0.03), quat((0.2, 1, 0.1), 60))


def payload(bones, race="c0801"):
    return {
        "schema": "instant-edit.skeleton", "version": 1, "source": "character", "race": race,
        "skeletons": ["chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb"], "warnings": [],
        "bones": [{"name": name, "parent": parent, "reference": list(values)} for name, parent, values in bones],
    }


def rigid_model(bones):
    """Blender-space rest matrices: rotations and positions composed along the parents."""
    model = []
    for _name, parent, values in bones:
        x, y, z, w = values[3:7]
        local = Matrix.Translation(Vector(values[0:3])) @ Quaternion((w, x, y, z)).to_matrix().to_4x4()
        model.append(local if parent < 0 else model[parent] @ local)
    return {name: K @ matrix for (name, _, _), matrix in zip(bones, model)}


def game_matrix(values):
    x, y, z, w = values[3:7]
    return Matrix.Translation(Vector(values[0:3])) @ Quaternion((w, x, y, z)).to_matrix().to_4x4()


def max_difference(a, b):
    return max(abs(a[i][j] - b[i][j]) for i in range(4) for j in range(4))


def entry(send_id=SEND_A, role="body", key=KEY, name="Firstname Lastname", armature="Skeleton", attach=None,
          attributes=None, shapes=None):
    value = {"sendId": send_id, "key": key, "name": name, "role": role, "armatureName": armature}
    if attach is not None:
        value["attach"] = attach
    if attributes is not None:
        value["enabledAttributes"] = attributes
    if shapes is not None:
        value["enabledShapes"] = shapes
    return value


def mesh_object(name, vertices, groups, collection=None):
    """A triangle strip over ``vertices`` with {group: [(vertex, weight)]}, carrying the material
    metadata an imported mesh has."""
    mesh = bpy.data.meshes.new(name)
    faces = [(i, i + 1, i + 2) for i in range(len(vertices) - 2)]
    mesh.from_pydata([tuple(v) for v in vertices], [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    (collection or bpy.context.scene.collection).objects.link(obj)
    for group_name, weights in groups.items():
        group = obj.vertex_groups.new(name=group_name)
        for index, weight in weights:
            group.add([index], weight, "REPLACE")
    for field, value in (("xiv_material", "/mt_c0801e0001_top_a.mtrl"), ("original_material", "/mt_c0801e0001_top_a.mtrl"),
                         ("material_index", 0), ("mesh_index", 0), ("submesh_index", 0)):
        obj[field] = value
    return obj


def evaluated_positions(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        return [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


class Importer:
    """Runs Instant Import the way the queue does, with the model file and its meshes faked."""

    def __init__(self, folder):
        self.folder = Path(folder)
        self.count = 0

    def run(self, context_id, character=None, bones=(), vertices=(), groups=None, skeleton=None, name=None,
            fail_armature=False, parts=None, attributes=(), shapes=()):
        """``parts``: (part name, its attributes, its shape keys as {name: vertex offset}) for a model
        of several parts; one part without attributes otherwise. ``attributes`` and ``shapes`` are the
        model's own, in the order the game's masks follow."""
        ops = module("instant_edit.ops")
        skeleton_module = module("instant_edit.skeleton")
        character_module = module("instant_edit.character")
        self.count += 1
        job = self.folder / f"job{self.count}"
        job.mkdir()
        model_file = job / "model.mdl"
        model_file.write_bytes(b"model")
        skeleton_path = ""
        if skeleton is not None:
            skeleton_path = skeleton_module.stage_skeleton({"skeleton": skeleton, "cacheJobDirectory": str(job)})["skeletonPath"]
        label = name or f"Model {self.count}"

        def fake_import(file_path, import_name, collection=None, context_metadata=None, **kwargs):
            created = []
            for part_name, part_attributes, part_shapes in parts or [(f"0.0 {import_name}", (), {})]:
                obj = mesh_object(part_name, vertices, groups or {}, collection)
                for attribute in part_attributes:
                    obj[attribute] = True
                if part_shapes:
                    obj.shape_key_add(name="Basis")
                    for shape_name, offset in part_shapes.items():
                        key = obj.shape_key_add(name=shape_name)
                        for point in key.data:
                            point.co = point.co + Vector(offset)
                        key.value = 0.0
                kwargs["created_objects"].append(obj)
                created.append(obj)
            return tuple(created)

        def broken_armature(*_args, **_kwargs):
            raise RuntimeError("armature failure")

        model = SimpleNamespace(bones=list(bones), attributes=list(attributes),
                                shapes=[SimpleNamespace(name=shape) for shape in shapes])
        with patch.object(ops.ModelImport, "from_file", staticmethod(fake_import)), \
                patch.object(ops.XIVModel, "from_file", staticmethod(lambda _path: model)), \
                patch.object(ops, "refresh_variant_targets_after_operation", lambda _context: None), \
                patch.object(skeleton_module, "create_armature",
                             broken_armature if fail_armature else skeleton_module.create_armature):
            call = lambda **kwargs: bpy.ops.xiv_ie.instant_import("EXEC_DEFAULT", **kwargs)
            if fail_armature:
                # Blender turns an operator's error report into an exception.
                def call(**kwargs):
                    try:
                        return bpy.ops.xiv_ie.instant_import("EXEC_DEFAULT", **kwargs)
                    except RuntimeError:
                        return {"CANCELLED"}
            result = call(
                file_path=str(model_file), import_name=label,
                schema="instant-edit.context", version=1, plugin_instance_id="plugin-instance",
                context_id=context_id, import_id=f"import-{context_id}", capability="capability",
                source_game_path="chara/equipment/e0001/model/c0801e0001_top.mdl",
                resolved_game_path="chara/equipment/e0001/model/c0801e0001_top.mdl", object_index=0,
                callback_port=42428, managed_destination=r"D:\Penumbra\SourceMod\models",
                target_file_path=r"D:\Penumbra\SourceMod\models\original.mdl",
                source_mod_directory="SourceModDirectory", source_mod_name="Source Mod",
                source_mod_root_path=r"D:\Penumbra\SourceMod", target_relative_path="Files/models/original.mdl",
                resource_manifest_version=0, resource_manifest_status="capture_failed",
                skeleton_path=skeleton_path,
                character=character_module.to_property(character_module.parse_request(character)) if character else "")
        if parts:
            names = {part_name for part_name, _attributes, _shapes in parts}
            return result, sorted((obj for obj in bpy.data.objects if obj.name in names), key=lambda obj: obj.name)
        mesh = next((obj for obj in bpy.data.objects if obj.name.startswith(f"0.0 {label}")), None)
        return result, mesh


# ----------------------------------------------------------------------


def rejects(value, message):
    character = module("instant_edit.character")
    try:
        character.parse_request(value)
    except ValueError:
        print(f"[PASS] {message}")
        return
    raise AssertionError(f"Expected rejection: {message}")


def check_parsing():
    character = module("instant_edit.character")
    require(character.parse_request(None) is None, "an import without a character entry is a single import")
    parsed = character.parse_request(entry(role="weapon", attach={"bone": "n_buki_r", "offset": WEAPON_OFFSET}))
    require(parsed["sendId"] == SEND_A and parsed["role"] == "weapon" and parsed["attach"]["bone"] == "n_buki_r"
            and len(parsed["attach"]["offset"]) == 10 and parsed["armatureName"] == "Skeleton",
            "a weapon's character entry keeps its send, role, bone and place")
    restored = character.from_property(character.to_property(parsed))
    require(restored.send_id == SEND_A and restored.attach_bone == "n_buki_r" and restored.key == KEY
            and len(restored.attach_offset) == 10 and character.from_property("") is None,
            "the entry survives the operator property")
    rejects([], "a character entry that is not an object is refused")
    rejects(entry(send_id="A" * 32), "a send id that is not lowercase hex is refused")
    rejects(entry(send_id="a" * 31), "a short send id is refused")
    rejects(entry(role="minion"), "an unknown role is refused")
    rejects(entry(key=""), "an empty key is refused")
    rejects(entry(name="x" * 129), "an overlong name is refused")
    rejects(entry(armature="x" * 64), "an armature name Blender would cut is refused")
    rejects(entry(attach={"bone": "n_buki_r", "offset": WEAPON_OFFSET}), "a body model hanging from a bone is refused")
    rejects(entry(role="weapon", attach={"bone": "", "offset": WEAPON_OFFSET}), "a weapon without a bone name is refused")
    rejects(entry(role="weapon", attach={"bone": "n_buki_r", "offset": WEAPON_OFFSET[:9]}),
            "a weapon place of the wrong length is refused")
    rejects(entry(role="weapon", attach={"bone": "n_buki_r", "offset": [float("nan")] + WEAPON_OFFSET[1:]}),
            "a weapon place that is not finite is refused")
    rejects(entry(role="weapon", attach={"bone": "n_buki_r", "offset": transform(q=(0, 0, 0, 2))}),
            "a weapon rotation that is not a unit quaternion is refused")
    rejects(entry(role="weapon", attach={"bone": "n_buki_r", "offset": transform(s=(1, 0, 1))}),
            "a weapon scaled to nothing is refused")
    rejects(entry(role="weapon", attach={"bone": "n_buki_r", "offset": [True] + WEAPON_OFFSET[1:]}),
            "a weapon place holding booleans is refused")

    drawn = character.from_property(character.to_property(character.parse_request(
        entry(attributes=[0xFFFFFFF2, 7], shapes=5))))
    require(drawn.enabled_attributes == (0xFFFFFFF2, 7) and drawn.enabled_shapes == 5,
            "a model's draw state, the masks of each drawn copy and its shape keys, survives the operator property")
    unknown = character.from_property(character.to_property(character.parse_request(entry(attributes=None, shapes=None))))
    require(unknown.enabled_attributes == () and unknown.enabled_shapes is None and restored.enabled_attributes == (),
            "an entry without a draw state draws every part and turns no shape key on")
    rejects(entry(attributes=5), "enabled attributes that are not a list are refused")
    rejects(entry(attributes=[-1]), "a negative attribute mask is refused")
    rejects(entry(attributes=[1 << 32]), "an attribute mask wider than the game's 32 bits is refused")
    rejects(entry(attributes=[True]), "an attribute mask that is a boolean is refused")
    rejects(entry(attributes=[1] * 17), "more attribute masks than a model has copies is refused")
    rejects(entry(shapes="5"), "enabled shapes that are not a number are refused")
    rejects(entry(shapes=1 << 32), "a shape mask wider than the game's 32 bits is refused")


def check_queue(context):
    server = module("instant_edit.server")
    status = server._status_payload()
    require(server.CHARACTER_IMPORT_CAPABILITY in status["capabilities"] and status["pendingImports"] == 0,
            "the add-on tells the plugin it takes character sends, and how many imports are pending")
    base = {
        "schema": "instant-edit.context", "version": 1, "pluginInstanceId": "plugin-instance",
        "contextId": "queue-context", "importId": "queue-import", "capability": "capability",
        "filePath": r"C:\Temp\instant-edit-import.mdl", "sourceGamePath": "chara/equipment/e0001/model/c0801e0001_top.mdl",
        "objectIndex": 0, "displayName": "Queue Model", "callbackPort": 42428,
        "targetFilePath": r"D:\Penumbra\SourceMod\models\original.mdl", "managedDestination": r"D:\Penumbra\SourceMod\models",
        "sourceModDirectory": "SourceModDirectory", "sourceModName": "Source Mod", "sourceModRootPath": r"D:\Penumbra\SourceMod",
        "targetRelativePath": "Files/models/original.mdl", "resourceManifestVersion": 0, "resourceManifestStatus": "capture_failed",
    }
    validated = server._ImportHandler._validate_import({**base, "character": entry()})
    require(validated["character"]["sendId"] == SEND_A and server._ImportHandler._validate_import(base)["character"] is None,
            "an import's character entry is validated and kept")
    diagnostics = module("instant_edit.diagnostics")
    try:
        server._ImportHandler._validate_import({**base, "character": entry(role="pet")})
    except diagnostics.BridgeRequestError as error:
        require(error.code == "invalid_character", "an import with an invalid character entry is refused")
    else:
        raise AssertionError("an invalid character entry was accepted")

    calls = []
    fake_ops = SimpleNamespace(
        xiv_ie=SimpleNamespace(instant_import=lambda *args, **kwargs: calls.append(kwargs) or {"FINISHED"}),
        object=bpy.ops.object)
    animation = module("instant_edit.animation")
    take = animation.Take(
        kind="recording", name="Queued pose", target_object="Missing", key_scale=False, loop=False, plugin_version="2.0.0",
        bones=("n_root",), parents=np.array([-1]), reference=np.array([transform()], dtype=np.float64),
        times=np.array([0.0]), samples=np.array([[transform()]], dtype=np.float64))
    job = {"take": take, "done": threading.Event(), "result": None, "failure": None}
    with patch.object(server, "bpy", SimpleNamespace(context=bpy.context, ops=fake_ops, app=bpy.app)):
        server._import_queue.put_nowait({**validated, "cacheJobDirectory": ""})
        server._import_queue.put_nowait({**validated, "contextId": "queue-context-2", "cacheJobDirectory": ""})
        server._animation_queue.put_nowait(job)
        require(server._status_payload()["pendingImports"] == 2, "queued imports are counted as pending")
        server.poll_import_queue()
        require(len(calls) == 1 and not job["done"].is_set() and server._status_payload()["pendingImports"] == 1,
                "each poll runs one import, and animations wait while imports are pending")
        server.poll_import_queue()
        require(len(calls) == 2 and not job["done"].is_set() and server._status_payload()["pendingImports"] == 0,
                "the next import runs on the next poll")
        server.poll_import_queue()
    require(job["done"].is_set() and job["failure"] is not None,
            "an animation queued after imports is keyed once they are done")
    require(json.loads(calls[0]["character"])["sendId"] == SEND_A,
            "the queued import hands its character entry to the import operator")


def check_send(context, importer):
    """Send A: two body models and a weapon; returns the send's armature and meshes."""
    character = module("instant_edit.character")
    ops = module("instant_edit.ops")
    expected = rigid_model(CHARACTER)
    kao = expected["j_kao"].translation
    skeleton = payload(CHARACTER)
    decoy = bpy.data.objects.new("Skeleton", bpy.data.armatures.new("Skeleton"))
    context.scene.collection.objects.link(decoy)

    result, top = importer.run(
        "context-top", entry(), bones=["j_kao", "j_sebo_a"], skeleton=skeleton, name="Top",
        vertices=[kao + Vector((0.02, 0, 0)), kao + Vector((0, 0.03, 0.01)), kao + Vector((0, 0, 0.05))],
        groups={"j_kao": [(0, 1.0), (1, 0.5)], "j_sebo_a": [(1, 0.5), (2, 1.0)]})
    armature = character.body_armature(context.scene, SEND_A)
    require(result == {"FINISHED"} and armature is not None and armature.name == "Skeleton.001",
            "the send's first import builds its armature, named as the plugin asks when the name is free")
    require(set(armature.data.bones.keys()) == {name for name, _, _ in CHARACTER}
            and max(max_difference(armature.data.bones[name].matrix_local, expected[name]) for name, _, _ in CHARACTER) < 1e-5,
            "the armature has every bone of the character's skeleton at its rest pose, hair bones included")
    require(top.parent == armature and any(m.type == "ARMATURE" and m.object == armature for m in top.modifiers)
            and armature.hide_get(), "the first model is bound to the send's hidden armature")

    extra_vertices = [kao + Vector((0.1, 0.1, 0.0)), kao + Vector((0.12, 0.1, 0.02)), kao + Vector((0.1, 0.14, 0.0))]
    result, hair = importer.run(
        "context-hair", entry(), bones=["j_ex_h0114_ke_f", "iv_hair_extra", "j_kao"], skeleton=skeleton, name="Hair",
        vertices=extra_vertices, groups={"j_ex_h0114_ke_f": [(0, 1.0)], "iv_hair_extra": [(1, 1.0), (2, 1.0)], "j_kao": [(2, 0.2)]})
    armatures = [obj for obj in context.scene.objects if obj.type == "ARMATURE" and obj.get(character.SEND_PROPERTY) == SEND_A]
    added = armature.data.bones.get("iv_hair_extra")
    centre = (extra_vertices[1] + extra_vertices[2]) / 2
    require(result == {"FINISHED"} and armatures == [armature] and hair.parent == armature,
            "later models of the send bind to the same armature")
    require(added is not None and (added.head_local - centre).length < 1e-5 and added.parent.name == "j_kao"
            and armature.hide_get(),
            "a bone a later model weights that the armature lacks is added at its vertices, and the armature stays hidden")
    require("iv_hair_extra" in context.scene.xiv_ie_instant_edit_props.last_status,
            "the status names the bone the armature gained")

    weapon_vertices = [Vector((0.0, 0.0, 0.1)), Vector((0.02, 0.0, 0.3)), Vector((0.0, 0.03, 0.5))]
    result, weapon = importer.run(
        "context-weapon", entry(role="weapon", attach={"bone": "n_buki_r", "offset": WEAPON_OFFSET}),
        bones=["n_root", "j_buki"], skeleton=payload(WEAPON), name="Weapon",
        vertices=weapon_vertices, groups={"n_root": [(0, 1.0)], "j_buki": [(1, 1.0), (2, 1.0)]})
    weapon_armature = weapon.parent
    require(result == {"FINISHED"} and weapon_armature is not None and weapon_armature != armature
            and weapon_armature.get(character.ROLE_PROPERTY) == "weapon"
            and weapon_armature.parent == armature and weapon_armature.parent_type == "BONE"
            and weapon_armature.parent_bone == "n_buki_r",
            "a weapon gets an armature of its own, hung from the bone that holds it")
    rest_place = armature.matrix_world @ armature.data.bones["n_buki_r"].matrix_local @ game_matrix(WEAPON_OFFSET) @ K.inverted()
    require(max_difference(weapon_armature.matrix_world, rest_place) < 1e-5,
            "the weapon sits where the game shows it on that bone")
    holder = character.character_collection(context.scene, character.CharacterImport(SEND_A, KEY, "", "body", "Skeleton"),
                                            create=False)
    require(holder is not None and {child.name for child in holder.children} >= {
        c.name for c in bpy.data.collections if c.get("context_id") in ("context-top", "context-hair", "context-weapon")}
        and len(holder.children) == 3 and armature.name in holder.objects,
        "the send's model collections and armature live in one collection for the character")
    refs = {ref.context_id for ref in ops._valid_export_contexts(context)}
    problems = []
    for context_id in ("context-top", "context-hair", "context-weapon"):
        try:
            module("instant_edit.context").validate_context(context_id, context.scene)
        except Exception as error:
            problems.append(f"{context_id}: {error}")
    require({"context-top", "context-hair", "context-weapon"} <= refs,
            f"each model keeps its own Instant Edit context, valid for Quick Export {problems or ''}")
    return armature, top, hair, weapon, weapon_vertices


def check_weapon_follows(context, armature, weapon, weapon_vertices):
    pose_bone = armature.pose.bones["n_buki_r"]
    pose_bone.rotation_mode = "QUATERNION"
    pose_bone.rotation_quaternion = Quaternion((1, 0, 0), math.radians(30))
    context.view_layer.update()
    placed = armature.matrix_world @ pose_bone.matrix @ game_matrix(WEAPON_OFFSET) @ K.inverted()
    positions = evaluated_positions(weapon)
    require(max((positions[i] - placed @ weapon_vertices[i]).length for i in range(len(weapon_vertices))) < 1e-5,
            "the weapon follows its bone when the character is posed")
    return pose_bone


def check_weapon_export(addon, context, weapon, weapon_armature, weapon_vertices):
    """A hung weapon exports its own vertex positions, not its place in the character's hand."""
    export_module = importlib.import_module(f"{addon.__name__}.mesh.export")
    model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
    importer = importlib.import_module(f"{addon.__name__}.io.model")
    mesh = weapon.data
    uv = mesh.uv_layers.new(name="uv0")
    uv.uv.foreach_set("vector", [0.5] * (2 * len(mesh.loops)))
    colour = mesh.color_attributes.new(name="vc0", type="FLOAT_COLOR", domain="CORNER")
    colour.data.foreach_set("color", [1.0] * (4 * len(mesh.loops)))
    mesh.materials.append(bpy.data.materials.new("WeaponMaterial"))
    context.scene.xiv_ie_settings.model_format = "MDL"
    hung = weapon_armature.matrix_world.copy()
    with tempfile.TemporaryDirectory(prefix="xiv-ie-character-") as folder:
        target = Path(folder) / "weapon"
        export_module.export_result(target, "MDL", export_objects=[weapon])
        model = model_module.XIVModel.from_file(str(target) + ".mdl")
        imported = importer.ModelImport.from_file(str(target) + ".mdl", "Weapon Roundtrip", select_objects=False)
        positions = sorted({tuple(round(c, 5) for c in v.co) for obj in imported for v in obj.data.vertices})
        for obj in imported:
            bpy.data.objects.remove(obj, do_unlink=True)
    original = sorted({tuple(round(c, 5) for c in co) for co in weapon_vertices})
    require(set(model.bones) == {"n_root", "j_buki"} and len(positions) == len(original) and
            max((Vector(a) - Vector(b)).length for a, b in zip(positions, original)) < 1e-4,
            "a weapon hung from a character bone exports at the origin, where its own skeleton puts it")
    require(max_difference(weapon_armature.matrix_world, hung) < 1e-6 and weapon_armature.parent_bone == "n_buki_r",
            "the export hangs the weapon back on its bone afterwards")


def check_pose(context, armature):
    animation = module("instant_edit.animation")
    bones = CHARACTER[:4]
    frame = [transform(values[0:3], quat((1, 0.2, 0), 25) if name == "j_sebo_a" else values[3:7])
             for name, _parent, values in bones]
    header = {
        "schema": "instant-edit.animation", "version": 1, "pluginVersion": "2.0.0", "kind": "recording",
        "name": "Pose", "targetObject": "Skeleton", "targetCharacter": SEND_A, "keyScale": False, "loop": False,
        "source": {}, "bones": [{"name": name, "parent": parent, "reference": values} for name, parent, values in bones],
        "times": [0.0],
    }
    encoded = json.dumps(header).encode("utf-8")
    body = bytearray(b"XIEA") + (1).to_bytes(4, "little") + len(encoded).to_bytes(4, "little") + encoded
    body += bytes((-len(body)) % 4)
    body += np.asarray([frame], dtype="<f4").tobytes()
    scene = context.scene
    scene.frame_start, scene.frame_end, scene.frame_current = 1, 120, 40
    decoy = bpy.data.objects["Skeleton"]
    context.view_layer.objects.active = decoy
    take = animation.parse_take(bytes(body))
    result = animation.apply_take(take, context)
    require(take.target_character == SEND_A and result["armature"] == armature.name,
            "a pose sent for a character send is keyed on its armature, not the object named Skeleton or the active one")
    require(scene.frame_end == 120 and armature.animation_data.action is not None,
            "a single-frame pose keeps the scene's frame range")
    expected = rigid_model([(name, parent, values) for (name, parent, _), values in zip(bones, frame)])
    context.view_layer.update()
    require(max_difference(armature.pose.bones["j_sebo_a"].matrix, expected["j_sebo_a"]) < 1e-4,
            "the pose moves the character's bones")
    header["targetCharacter"] = "d" * 32
    encoded = json.dumps(header).encode("utf-8")
    body = bytearray(b"XIEA") + (1).to_bytes(4, "little") + len(encoded).to_bytes(4, "little") + encoded
    body += bytes((-len(body)) % 4)
    body += np.asarray([frame], dtype="<f4").tobytes()
    try:
        animation.apply_take(animation.parse_take(bytes(body)), context)
    except animation.AnimationApplyError as error:
        require(error.code == "animation_character_missing",
                "a pose for a send that is not in the scene is refused instead of keyed elsewhere")
    else:
        raise AssertionError("a pose for a missing send was keyed")


def check_replace(context, importer):
    character = module("instant_edit.character")
    revocation = module("instant_edit.revocation")
    single_result, single = importer.run(
        "context-single", None, bones=["j_kao"], skeleton=payload(CHARACTER), name="Single",
        vertices=[Vector((0, 0, 1)), Vector((0.1, 0, 1)), Vector((0, 0.1, 1))], groups={"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]})
    holder_a = next(c for c in bpy.data.collections if c.get(character.SEND_PROPERTY) == SEND_A
                    and c.get(character.KIND_PROPERTY) == "character")
    keepsake = bpy.data.objects.new("User Keepsake", None)
    holder_a.objects.link(keepsake)
    other_result, _other = importer.run(
        "context-other", entry(send_id=SEND_C, key="Someone Else@73", name="Someone Else"), bones=["j_kao"],
        skeleton=payload(CHARACTER), name="Other", vertices=[Vector((0, 0, 1)), Vector((0.1, 0, 1)), Vector((0, 0.1, 1))],
        groups={"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]})
    # Part of the previous send is selected and active, as after clicking it to look at it; the
    # replacement removes it while the import runs.
    previous = next(obj for obj in context.view_layer.objects if obj.get(character.SEND_PROPERTY) == SEND_A)
    for obj in tuple(context.selected_objects):
        obj.select_set(False)
    previous.select_set(True)
    context.view_layer.objects.active = previous
    queued, scheduled = [], []
    with patch.object(revocation, "queue_context_revocations", lambda collections: queued.extend(
            c.get("context_id") for c in collections) or len(collections)), \
            patch.object(revocation, "schedule_revocations", lambda: scheduled.append(True)):
        result, new_top = importer.run(
            "context-top-b", entry(send_id=SEND_B), bones=["j_kao"], skeleton=payload(CHARACTER), name="Top B",
            vertices=[Vector((0, 0, 1)), Vector((0.1, 0, 1)), Vector((0, 0.1, 1))], groups={"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]})
    names = {obj.name for obj in bpy.data.objects}
    require(result == {"FINISHED"} and single_result == {"FINISHED"} and other_result == {"FINISHED"},
            "the imports around the replacement succeed")
    require(context.view_layer.objects.active is None and not context.selected_objects,
            "a replaced object that was selected doesn't fail the import; nothing removed stays selected")
    require(not any(obj.get(character.SEND_PROPERTY) == SEND_A for obj in bpy.data.objects)
            and not any(c.get("context_id") in ("context-top", "context-hair", "context-weapon") for c in bpy.data.collections),
            "a new send of the character removes the previous send's armatures, models and model collections")
    require(not any(c.get(character.SEND_PROPERTY) == SEND_A for c in bpy.data.collections),
            "and the previous send's character collection")
    require(sorted(queued) == ["context-hair", "context-top", "context-weapon"] and scheduled,
            "the plugin is told to forget the replaced contexts")
    require("User Keepsake" in names and bpy.data.objects["User Keepsake"].name in context.scene.collection.objects,
            "an object someone put into the replaced send's collection moves to the scene instead")
    require(single.name in names and any(c.get("context_id") == "context-single" for c in bpy.data.collections)
            and character.body_armature(context.scene, SEND_C) is not None,
            "single imports and other characters' sends stay")
    require(new_top.parent is not None and new_top.parent == character.body_armature(context.scene, SEND_B),
            "the new send builds its own armature")


def check_failure_cleanup(context, importer):
    character = module("instant_edit.character")
    result, _mesh = importer.run(
        "context-broken", entry(send_id="e" * 32, key="Broken@1", name="Broken"), bones=["j_kao"],
        skeleton=payload(CHARACTER), name="Broken", vertices=[Vector((0, 0, 1)), Vector((0.1, 0, 1)), Vector((0, 0.1, 1))],
        groups={"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]}, fail_armature=True)
    require(result == {"CANCELLED"} and not any(c.get(character.SEND_PROPERTY) == "e" * 32 for c in bpy.data.collections)
            and not any(c.get("context_id") == "context-broken" for c in bpy.data.collections),
            "a failed first import of a send leaves no empty character collection behind")


SEND_F = "f" * 32
# A model's attributes and shapes, in the order the game's masks follow.
DRAW_ATTRIBUTES = ["atr_nek", "atr_tv_a", "atr_tv_b", "atr_gv_a", "atr_gv_e"]
DRAW_SHAPES = ["shp_brw_a", "shpx_wr_yab"]
DRAW_PARTS = [
    ("0.0 Body", (), {"shp_brw_a": (0.0, 0.0, 0.01), "shpx_wr_yab": (0.02, 0.0, 0.0)}),
    ("0.1 Neck", ("atr_nek",), {}),
    ("1.0 Robe A", ("atr_tv_a",), {}),
    ("2.0 Robe B", ("atr_tv_b",), {}),
    ("3.0 Long Nails", ("atr_gv_a", "atr_gv_e"), {}),
    ("4.0 Fingertips", ("atr_gv_e",), {}),
]


def _exportable(obj):
    """Give a test mesh what an MDL export needs besides triangles and weights."""
    mesh = obj.data
    uv = mesh.uv_layers.new(name="uv0")
    uv.uv.foreach_set("vector", [0.5] * (2 * len(mesh.loops)))
    colour = mesh.color_attributes.new(name="vc0", type="FLOAT_COLOR", domain="CORNER")
    colour.data.foreach_set("color", [1.0] * (4 * len(mesh.loops)))
    mesh.materials.append(bpy.data.materials.get("DrawnMaterial") or bpy.data.materials.new("DrawnMaterial"))


def check_draw_state(addon, context, importer):
    """A model sent as the game draws it: the parts it hides are imported hidden and marked, the
    shape keys it has on are on, and exports still write the whole model with those keys off."""
    character = module("instant_edit.character")
    ops = module("instant_edit.ops")
    io_model = importlib.import_module(f"{addon.__name__}.io.model")
    model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
    export_module = importlib.import_module(f"{addon.__name__}.mesh.export")
    importer_module = importlib.import_module(f"{addon.__name__}.io.model.importer")
    kao = rigid_model(CHARACTER)["j_kao"].translation
    vertices = [kao + Vector((0.02, 0, 0)), kao + Vector((0, 0.03, 0.01)), kao + Vector((0, 0, 0.05))]
    # The send's first model, without a draw state: every part is drawn. The model under test comes
    # second, so selecting the sole Context doesn't replace its status line.
    _result, first = importer.run(
        "context-drawn-first", entry(send_id=SEND_F, key="Drawn@73", name="Drawn"), bones=["j_kao"],
        skeleton=payload(CHARACTER), name="First", vertices=vertices, groups={"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]})
    require(first is not None and not character.is_game_hidden(first) and not first.hide_get(),
            "a model sent without a draw state shows every part")
    # Two copies of the model are drawn, one with nail option a and one with e; none has both.
    result, objects = importer.run(
        "context-drawn", entry(send_id=SEND_F, key="Drawn@73", name="Drawn", attributes=[0b01101, 0b10101], shapes=0b10),
        bones=["j_kao"], skeleton=payload(CHARACTER), vertices=vertices, groups={"j_kao": [(0, 1.0), (1, 1.0), (2, 1.0)]},
        parts=DRAW_PARTS, attributes=DRAW_ATTRIBUTES, shapes=DRAW_SHAPES)
    by_name = {obj.name: obj for obj in objects}
    hidden = {name for name, obj in by_name.items() if character.is_game_hidden(obj)}
    require(result == {"FINISHED"} and len(by_name) == 6 and hidden == {"1.0 Robe A", "3.0 Long Nails"}
            and all(obj.hide_get() == (name in hidden) for name, obj in by_name.items()),
            "parts the game doesn't draw are imported hidden and marked; a part is drawn when one drawn copy enables all of its attributes")
    require("2 parts the character doesn't show now are hidden" in context.scene.xiv_ie_instant_edit_props.last_status,
            "the status line counts the hidden parts")
    body = by_name["0.0 Body"]
    keys = body.data.shape_keys.key_blocks
    require(keys["shpx_wr_yab"].value == 1.0 and keys["shp_brw_a"].value == 0.0
            and body.get(character.GAME_SHAPES_PROPERTY) == "shpx_wr_yab",
            "the shape keys the game has on are turned on and remembered")
    require(importer_module.occupied_mesh_group_ids(context) == {0, 1, 2, 3, 4}
            and importer_module.visible_mesh_group_ids() == {0, 2, 4},
            "later imports keep clear of the groups of hidden parts too, which export with their model")

    ref = module("instant_edit.context").validate_context("context-drawn", context.scene)
    require({obj.name for obj in ops.export_objects_for_scope(ref, "VISIBLE")} == set(by_name) | {first.name}
            and {obj.name for obj in ops.export_objects_for_scope(ref, "CURRENT_COLLECTION")} == set(by_name),
            "every export scope takes a model's hidden parts with its visible ones")
    shown = [obj for obj in objects if obj.name not in hidden]
    for obj in shown:
        obj.hide_set(True)
    require({obj.name for obj in ops.export_objects_for_scope(ref, "VISIBLE")} == {first.name},
            "a model with every visible part hidden by hand exports nothing, not its hidden parts either")
    for obj in shown:
        obj.hide_set(False)

    # The Mesh Groups list shows the visible parts only, but the hidden ones export with them, so a
    # reorder keeps their IDs apart: a part can't take a hidden part's number, and moving a group
    # renumbers the groups of hidden parts too.
    materials = importlib.import_module(f"{addon.__name__}.materials")
    mesh_list = importlib.import_module(f"{addon.__name__}.mesh_list")
    ids = module("instant_edit.context").mesh_ids_from_name
    parts = mesh_list.list_parts(shown)
    fingertips = next(part for part in parts if by_name["4.0 Fingertips"] in part.objects)
    placement = mesh_list.moved_part(parts, fingertips.ident, 3, 0)
    try:
        materials.commit_mesh_id_plan(mesh_list.placement_plan(parts, placement), shown)
        refused = ""
    except ValueError as error:
        refused = str(error)
    require("3.0 Long Nails" in refused and ids(by_name["4.0 Fingertips"])[:2] == (4, 0),
            "a part dropped on the number of a part the game hides is refused, and nothing is renamed")
    placement = mesh_list.moved_group(parts, 2, 0)
    materials.commit_mesh_id_plan(mesh_list.placement_plan(parts, placement), shown, (2, 0))
    require(ids(by_name["2.0 Robe B"])[:2] == (0, 0) and ids(by_name["0.0 Body"])[:2] == (1, 0)
            and ids(by_name["1.0 Robe A"])[:2] == (2, 0) and ids(by_name["3.0 Long Nails"])[:2] == (3, 0),
            "moving a group renumbers the groups of hidden parts the way it does the listed ones")
    parts = mesh_list.list_parts(shown)
    placement = mesh_list.moved_group(parts, 0, 2)
    materials.commit_mesh_id_plan(mesh_list.placement_plan(parts, placement), shown, (0, 2))
    require(all(obj.name == name for name, obj in by_name.items()), "moving the group back restores every name")

    for obj in objects:
        _exportable(obj)
    settings = context.scene.xiv_ie_settings
    settings.model_format = "MDL"
    original = {tuple(round(c, 4) for c in co) for co in vertices}
    for keep in (False, True):
        settings.keep_shapekeys = keep
        with tempfile.TemporaryDirectory(prefix="xiv-ie-character-") as folder:
            target = Path(folder) / "drawn"
            export_module.export_result(target, "MDL", export_objects=ops.export_objects_for_scope(ref, "CURRENT_COLLECTION"))
            model = model_module.XIVModel.from_file(str(target) + ".mdl")
            imported = io_model.ModelImport.from_file(str(target) + ".mdl", "Drawn Roundtrip", select_objects=False)
            positions = {tuple(round(c, 4) for c in v.co) for obj in imported for v in obj.data.vertices}
            for obj in imported:
                bpy.data.objects.remove(obj, do_unlink=True)
        require(len(model.submeshes) == 6 and set(DRAW_ATTRIBUTES) <= set(model.attributes),
                f"keep shape keys {keep}: the export writes the whole model, its hidden parts and their attributes too")
        require(positions == original and ({shape.name for shape in model.shapes} == set(DRAW_SHAPES)) == keep,
                f"keep shape keys {keep}: the model's basis is written without the shape keys the game turns on")
        require(all(obj.hide_get() == (obj.name in hidden) for obj in objects) and keys["shpx_wr_yab"].value == 1.0,
                f"keep shape keys {keep}: afterwards the hidden parts are hidden again and the game's shape keys on again")
    settings.keep_shapekeys = False


def check_add_bones_outside_view_layer(context):
    skeleton = module("instant_edit.skeleton")
    game = skeleton.parse_skeleton(payload(CHARACTER))
    collection = bpy.data.collections.new("Excluded Rig")
    context.scene.collection.children.link(collection)
    armature, _report = skeleton.create_armature(context, collection, "Excluded", [], [], game)
    context.view_layer.layer_collection.children[collection.name].exclude = True
    report = skeleton.add_bones(context, armature, ["j_kao", "iv_new"], [], game)
    require(report.blocked == ["iv_new"] and "view layer" in report.summary() and "iv_new" not in armature.data.bones,
            "an armature outside the view layer reports the bones it could not take")


def run():
    with addon_session(PACKAGE) as addon:
        context = bpy.context
        check_parsing()
        with temporary_scene_data():
            check_queue(context)
        with temporary_scene_data(), tempfile.TemporaryDirectory(prefix="xiv-ie-character-jobs-") as folder:
            importer = Importer(folder)
            armature, _top, _hair, weapon, weapon_vertices = check_send(context, importer)
            check_weapon_follows(context, armature, weapon, weapon_vertices)
            check_weapon_export(addon, context, weapon, weapon.parent, weapon_vertices)
            check_pose(context, armature)
            check_replace(context, importer)
            check_failure_cleanup(context, importer)
        with temporary_scene_data(), tempfile.TemporaryDirectory(prefix="xiv-ie-character-jobs-") as folder:
            check_draw_state(addon, context, Importer(folder))
        with temporary_scene_data():
            check_add_bones_outside_view_layer(context)
    print("[RESULT] character regression PASSED")


if __name__ == "__main__":
    try:
        run()
    except Exception as error:
        detail = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error file=Blender-Addon/testing/character_regression.py::Character regression failed: {detail}", flush=True)
        raise
