"""Apply Racial Scaling: the Tools panel reshapes meshes through the plugin's deformer, and they still export."""

import importlib
import json
import math
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data


PACKAGE = "_xiv_instant_edit_racial_scaling"
MATERIAL = "/chara/equipment/e6000/material/v0001/mt_c0201e6000_top_a.mtrl"
GAME_TO_BLENDER = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def require(condition, message: str) -> None:
    if not condition:
        print(f"[FAIL] {message}")
        raise AssertionError(message)
    print(f"[PASS] {message}")


def module(name: str):
    return importlib.import_module(f"{PACKAGE}.{name}")


def deformer_matrix(scale: float, degrees: float, translation) -> list[float]:
    """A 3×4 row-major game-space matrix: a uniform scale, a turn about game Y, then a move."""
    turn = Matrix.Rotation(math.radians(degrees), 3, "Y") * scale
    return [turn[0][0], turn[0][1], turn[0][2], translation[0],
            turn[1][0], turn[1][1], turn[1][2], translation[1],
            turn[2][0], turn[2][1], turn[2][2], translation[2]]


BONES = {
    "j_sebo_a": deformer_matrix(1.1, 12.0, (0.01, 0.02, 0.03)),
    "j_mune_l": deformer_matrix(0.9, -8.0, (-0.02, 0.0, 0.01)),
    "j_kao": deformer_matrix(1.4, 0.0, (0.0, 0.1, 0.0)),  # No vertex group names it.
}


AU_RA_BONES = {
    "j_sebo_a": deformer_matrix(1.2, -5.0, (0.0, -0.03, 0.02)),
    "j_kosi": deformer_matrix(1.05, 3.0, (0.01, 0.0, 0.0)),
}
# What the fixture plugin answers for each target race of a c0201 model.
DEFORMERS = {0: (801, BONES), 801: (801, BONES), 1401: (1401, AU_RA_BONES), 201: (201, {})}


def plugin():
    """A post_json stand-in that answers each request with its target race's deformer."""
    requests = []

    def post(port, endpoint, body, *, timeout, max_response_size):
        requests.append((port, endpoint, body))
        race, bones = DEFORMERS[body["race"]]
        return 200, json.dumps(response(race, bones)).encode("utf-8")

    return post, requests


def response(race: int = 801, bones=BONES) -> dict:
    return {"ok": True, "modelRace": 201, "race": race, "raceLabel": "Female Miqo'te",
            "bones": [{"name": name, "matrix": matrix} for name, matrix in bones.items()]}


def reply(payload: dict, status: int = 200):
    """A post_json stand-in that records requests and answers with ``payload``."""
    requests = []

    def post(port, endpoint, body, *, timeout, max_response_size):
        requests.append((port, endpoint, body))
        return status, json.dumps(payload).encode("utf-8")

    return post, requests


def rig():
    data = bpy.data.armatures.new("Rig")
    obj = bpy.data.objects.new("Rig", data)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    for index, name in enumerate(("j_kosi", "j_sebo_a", "j_mune_l")):
        bone = data.edit_bones.new(name)
        bone.head = (0.0, 0.0, float(index))
        bone.tail = (0.0, 0.0, float(index) + 1.0)
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def part(name: str, armature, weights) -> bpy.types.Object:
    """Two triangles; ``weights`` holds each vertex's {group: weight}."""
    points = [(0.1, 0.2, 1.0), (0.4, 0.1, 1.2), (0.2, 0.5, 1.4), (0.5, 0.4, 0.9)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(points, [], [(0, 1, 2), (1, 3, 2)])
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = armature
    obj.modifiers.new(name="Armature", type="ARMATURE").object = armature
    for index, groups in enumerate(weights):
        for group, weight in groups.items():
            target = obj.vertex_groups.get(group) or obj.vertex_groups.new(name=group)
            target.add([index], weight, "REPLACE")
    layer = mesh.uv_layers.new(name="uv0")
    layer.uv.foreach_set("vector", np.linspace(0.1, 0.9, len(mesh.loops) * 2, dtype=np.float32))
    obj["xiv_material"] = MATERIAL
    return obj


def select_only(*objects) -> None:
    bpy.context.view_layer.update()
    for obj in bpy.context.view_layer.objects:
        obj.select_set(obj in objects)
    bpy.context.view_layer.objects.active = objects[0]


def expected_matrix(weights, bones=BONES) -> Matrix:
    """The blended 4×4 game-space matrix of one vertex, worked out with mathutils."""
    rows = np.zeros((3, 4))
    total = 0.0
    for group, weight in weights.items():
        values = bones.get(group)
        matrix = np.array(values).reshape(3, 4) if values else np.eye(3, 4)
        rows += weight * matrix
        total += weight
    rows = rows / total if total else np.eye(3, 4)
    return Matrix((tuple(rows[0]), tuple(rows[1]), tuple(rows[2]), (0, 0, 0, 1)))


def corner_normals(mesh) -> np.ndarray:
    values = np.empty(len(mesh.loops) * 3, dtype=np.float32)
    mesh.corner_normals.foreach_get("vector", values)
    return values.reshape(-1, 3)


WEIGHTS = (
    {"j_sebo_a": 1.0},
    {"j_kosi": 1.0},
    {"j_sebo_a": 0.5, "j_mune_l": 0.5},
    {"j_sebo_a": 0.25, "j_kosi": 0.75},
)


def check_reshape() -> None:
    racial = module("instant_edit.racial_scaling")
    plugin_http = module("instant_edit.plugin_http")
    with temporary_scene_data():
        armature = rig()
        body = part("0.0 Top", armature, WEIGHTS)
        body.location = (0.3, -0.2, 0.1)
        body.rotation_euler = (0.0, 0.0, math.radians(30.0))
        body.scale = (1.5, 1.5, 1.5)
        body.shape_key_add(name="Basis")
        shape = body.shape_key_add(name="shp_wrst")
        shape.value = 0.0  # Blender starts keys added from Python at 1.0; export would bake it.
        for index, point in enumerate(shape.data):
            point.co = Vector(point.co) + Vector((0.0, 0.01 * index, 0.02))
        tilted = [Vector((0.2, 0.1, 1.0)).normalized(), Vector((0.0, 0.3, 1.0)).normalized(),
                  Vector((-0.2, 0.0, 1.0)).normalized(), Vector((0.1, -0.1, 1.0)).normalized()]
        body.data.normals_split_custom_set_from_vertices(tilted)
        bpy.context.view_layer.update()

        world = body.matrix_world.copy()
        to_game = GAME_TO_BLENDER.inverted() @ world
        before = [Vector(vertex.co) for vertex in body.data.vertices]
        shape_before = [Vector(point.co) for point in shape.data]
        loop_vertices = [loop.vertex_index for loop in body.data.loops]
        normals_before = corner_normals(body.data)

        post, requests = reply(response())
        select_only(body)
        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="0")
        require(result == {"FINISHED"}, "Apply Racial Scaling finishes")
        require(len(requests) == 1 and requests[0][1] == "/racial-deformer"
                and requests[0][2] == {"schema": racial.REQUEST_SCHEMA, "version": 1, "modelRace": 201, "race": 0},
                "the tool asks the plugin for the model race's deformer for the player")

        def moved(co, weights, bones=BONES):
            return to_game.inverted() @ (expected_matrix(weights, bones) @ (to_game @ co))

        error = max((Vector(body.data.vertices[index].co) - moved(co, WEIGHTS[index])).length
                    for index, co in enumerate(before))
        require(error < 1e-5, f"vertices move by their bones' blended matrices in game space (error {error:.2e})")
        require((Vector(body.data.vertices[1].co) - before[1]).length < 1e-6,
                "a vertex weighted only to a bone the deformer doesn't list stays put")
        keys = body.data.shape_keys.key_blocks
        error = max((Vector(keys["Basis"].data[index].co) - Vector(body.data.vertices[index].co)).length
                    for index in range(len(before)))
        require(error < 1e-6, "the basis shape key follows the vertices")
        error = max((Vector(keys["shp_wrst"].data[index].co) - moved(co, WEIGHTS[index])).length
                    for index, co in enumerate(shape_before))
        require(error < 1e-5, f"shape keys move with their vertices (error {error:.2e})")

        linear = to_game.to_3x3()
        normals_after = corner_normals(body.data)
        worst = 0.0
        for loop, vertex in enumerate(loop_vertices):
            game = linear.inverted().transposed() @ Vector(normals_before[loop])
            game = expected_matrix(WEIGHTS[vertex]).to_3x3() @ game
            want = (linear.transposed() @ game).normalized()
            worst = max(worst, (Vector(normals_after[loop]) - want).length)
        # Custom normals are stored compressed, so they come back to about 1e-3.
        require(body.data.has_custom_normals and worst < 5e-3,
                f"custom normals turn with the deformer (error {worst:.2e})")
        require(body.data.get(racial.APPLIED_PROPERTY) == "c0201 to c0801" and racial.APPLIED_PROPERTY not in body,
                "the mesh data records what was applied, not the object")
        require(not body.get(racial.PROPERTY), "reshaped meshes don't get the preview-only property")
        require(racial.simple_export_warning([body]) == "" and racial.shaped_race(body) == 801,
                "Simple Export doesn't warn about a reshaped mesh, which is shaped for the race it was reshaped for")
        top = "chara/equipment/e6000/model/c0201e6000_top.mdl"
        require("can't go into a c0201 model" in racial.mismatch_refusal([body], top) and
                racial.mismatch_refusal([body], "chara/equipment/e6000/model/c0801e6000_top.mdl") == "" and
                racial.race_model([body], top) == (801, ""),
                "Quick Export writes a reshaped mesh as the race's own model only, not back into the c0201 model")

        def error_from(points):
            return max((Vector(vertex.co) - point).length for vertex, point in zip(body.data.vertices, points))

        post, requests = plugin()
        with patch.object(plugin_http, "post_json", post):
            # From is ignored for a reshaped mesh: it keeps the race it was made for.
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="101", race="1401")
        error = error_from([moved(co, WEIGHTS[index], AU_RA_BONES) for index, co in enumerate(before)])
        require(result == {"FINISHED"} and error < 1e-5
                and body.data.get(racial.APPLIED_PROPERTY) == "c0201 to c1401"
                and {(request[2]["modelRace"], request[2]["race"]) for request in requests} == {(201, 1401)},
                f"another race replaces the scaling, kept with the mark, instead of stacking on it (error {error:.2e})")
        error = max((Vector(keys["shp_wrst"].data[index].co) - moved(co, WEIGHTS[index], AU_RA_BONES)).length
                    for index, co in enumerate(shape_before))
        require(error < 1e-5, f"shape keys follow the replaced scaling (error {error:.2e})")

        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="1401")
        require(result == {"CANCELLED"} and body.data.get(racial.APPLIED_PROPERTY) == "c0201 to c1401",
                "applying the race a mesh is already shaped for changes nothing")

        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="201")
        error = error_from(before)
        require(result == {"FINISHED"} and error < 1e-5 and racial.APPLIED_PROPERTY not in body.data,
                f"the model's own race takes the scaling off (error {error:.2e})")
        worst = max((Vector(a) - Vector(b)).length for a, b in zip(corner_normals(body.data), normals_before))
        require(worst < 5e-3, f"custom normals come back too (error {worst:.2e})")

        select_only(body)
        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="801")
        error = error_from([moved(co, WEIGHTS[index]) for index, co in enumerate(before)])
        require(result == {"FINISHED"} and error < 1e-5 and body.data.get(racial.APPLIED_PROPERTY) == "c0201 to c0801",
                f"a mesh taken back to its own race can be reshaped again (error {error:.2e})")
        stored = racial.applied_matrices(body)
        require(stored is not None and set(stored) == {"j_sebo_a", "j_mune_l"}
                and all(np.array_equal(stored[name], np.reshape(BONES[name], (3, 4))) for name in stored),
                "the mesh data keeps the matrices it was reshaped with, for its own vertex groups")

        # A skeleton mod or another collection changes what the plugin answers in between.
        other = {name: deformer_matrix(1.3, 20.0, (0.05, 0.0, 0.0)) for name in BONES}
        with patch.dict(DEFORMERS, {801: (801, other)}), patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="201")
        error = error_from(before)
        require(result == {"FINISHED"} and error < 1e-5 and racial.APPLIED_MATRICES_PROPERTY not in body.data,
                f"taking the scaling off undoes the matrices applied, not the collection's current ones (error {error:.2e})")

        with patch.object(plugin_http, "post_json", post):
            bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="801")
            del body.data[racial.APPLIED_MATRICES_PROPERTY]
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="201")
        error = error_from(before)
        require(result == {"FINISHED"} and error < 1e-5,
                f"a mark from before the matrices were kept is taken off with the plugin's (error {error:.2e})")
        with patch.object(plugin_http, "post_json", post):
            bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="801")

        check_export(body, world)


def check_export(body, world) -> None:
    """The reshaped mesh goes through a real Simple Export to MDL and comes back in its new shape."""
    import_module = module("io.model")
    with tempfile.TemporaryDirectory(prefix="xiv-ie-racial-") as folder:
        settings = bpy.context.scene.xiv_ie_settings
        settings.export_directory = folder
        settings.export_name = "c0801e6000_top"
        settings.model_format = "MDL"
        select_only(body)
        result = bpy.ops.xiv_ie.simple_export()
        path = Path(folder) / "c0801e6000_top.mdl"
        require(result == {"FINISHED"} and path.is_file(), "Simple Export writes the reshaped mesh")
        created = []
        import_module.ModelImport.from_file(str(path), "reimport", select_objects=False, created_objects=created)
        meshes = [obj for obj in created if obj.type == "MESH"]
        require(len(meshes) == 1, "the exported model imports again")
        # Export splits vertices along normal seams, so compare the distinct positions.
        points = sorted({tuple(round(c, 3) for c in obj.matrix_world @ Vector(v.co)) for obj in meshes
                         for v in obj.data.vertices})
        want = sorted({tuple(round(c, 3) for c in world @ Vector(v.co)) for v in body.data.vertices})
        require(len(points) == len(want) and all((Vector(a) - Vector(b)).length < 2e-3 for a, b in zip(points, want)),
                "the exported file keeps the reshaped shape")
        for obj in created:
            bpy.data.objects.remove(obj, do_unlink=True)


def check_undo() -> None:
    """Undo takes the mark back with the shape, so the mesh can be reshaped again. Not inside
    temporary_scene_data: undo reloads every datablock, so it cleans up by name."""
    racial = module("instant_edit.racial_scaling")
    plugin_http = module("instant_edit.plugin_http")
    armature = rig()
    body = part("0.0 Undo", armature, WEIGHTS)
    names = (armature.name, body.name)
    try:
        before = [Vector(vertex.co) for vertex in body.data.vertices]
        select_only(body)
        bpy.ops.ed.undo_push(message="before racial scaling")
        post, _requests = plugin()
        with patch.object(plugin_http, "post_json", post):
            bpy.ops.xiv_ie.apply_racial_scaling("EXEC_DEFAULT", True, model_race="201", race="801")
        body = bpy.data.objects[names[1]]
        require(body.data.get(racial.APPLIED_PROPERTY) == "c0201 to c0801", "the undoable run reshapes the mesh")
        bpy.ops.ed.undo()
        body = bpy.data.objects[names[1]]
        error = max((Vector(vertex.co) - point).length for vertex, point in zip(body.data.vertices, before))
        require(error < 1e-6 and racial.APPLIED_PROPERTY not in body.data and racial.APPLIED_PROPERTY not in body
                and racial.APPLIED_MATRICES_PROPERTY not in body.data,
                "undo takes the racial scaling and its mark off together")
        select_only(body)
        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="1401")
        require(result == {"FINISHED"} and body.data.get(racial.APPLIED_PROPERTY) == "c0201 to c1401",
                "a mesh can be reshaped again after an undo")
    finally:
        for name in reversed(names):
            obj = bpy.data.objects.get(name)
            if obj is not None:
                data = obj.data
                bpy.data.objects.remove(obj, do_unlink=True)
                if data is not None and data.users == 0:
                    (bpy.data.meshes if isinstance(data, bpy.types.Mesh) else bpy.data.armatures).remove(data)


def check_skips_and_errors() -> None:
    racial = module("instant_edit.racial_scaling")
    plugin_http = module("instant_edit.plugin_http")
    with temporary_scene_data():
        armature = rig()
        preview = part("0.0 Preview", armature, WEIGHTS)
        preview[racial.PROPERTY] = "c0201 to c0801"
        loose = part("0.1 Loose", armature, ({}, {}, {}, {}))
        select_only(preview)
        with patch.object(plugin_http, "post_json", reply(response())[0]):
            try:
                bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="801")
            except RuntimeError as error:
                message = str(error)
            else:
                message = ""
        require("sent racially scaled for preview" in message, "a preview-scaled import alone is refused with a reason")
        mesh = part("0.2 Shared", armature, WEIGHTS)
        twin = mesh.copy()
        bpy.context.scene.collection.objects.link(twin)
        before = {obj.name: [Vector(v.co) for v in obj.data.vertices] for obj in (preview, loose, mesh)}

        post, _requests = reply(response())
        select_only(mesh, twin, preview, loose)
        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="801")
        require(result == {"FINISHED"}, "the tool reshapes what it can among the selection")
        require(all((Vector(v.co) - co).length < 1e-9 for v, co in zip(preview.data.vertices, before[preview.name])),
                "meshes sent racially scaled for preview are left alone")
        require(all((Vector(v.co) - co).length < 1e-9 for v, co in zip(loose.data.vertices, before[loose.name]))
                and not loose.get(racial.APPLIED_PROPERTY), "unweighted meshes stay put and unmarked")
        moved = [(Vector(v.co) - co).length for v, co in zip(mesh.data.vertices, before[mesh.name])]
        require(max(moved) > 1e-3 and moved[1] < 1e-9, "the shared mesh data moves")
        reference = (Vector(mesh.data.vertices[0].co) - before[mesh.name][0]).length
        require(abs(moved[0] - reference) < 1e-9 and twin.data == mesh.data,
                "mesh data shared by two objects is reshaped once")

        select_only(loose)
        for payload, status, wanted, label in (
            ({"ok": False, "code": "endpoint_not_found", "error": "the requested bridge endpoint was not found"}, 404,
             "too old", "an old plugin without the endpoint"),
            ({"ok": False, "code": "racial_scaling_unavailable",
              "error": "Race 0201 is not an ancestor of 0101 in the racial deformer file."}, 422,
             "can't reshape c0201 models for Male Midlander", "a race the deformer can't reach"),
            ({"ok": True, "race": 801, "bones": [{"name": "j_kosi", "matrix": [1, 2]}]}, 200,
             "malformed", "a malformed answer"),
        ):
            post, _requests = reply(payload, status)
            with patch.object(plugin_http, "post_json", post):
                try:
                    bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="101")
                except RuntimeError as error:
                    message = str(error)
                else:
                    message = ""
            require(wanted in message, f"the tool reports {label}")

        def offline(*_args, **_kwargs):
            raise OSError("connection refused")

        with patch.object(plugin_http, "post_json", offline):
            try:
                racial.request_deformer(201, 0, port=42428)
            except ValueError as error:
                message = str(error)
        require("isn't running" in message, "the tool says when the plugin isn't running")

        post, _requests = reply(response(race=201, bones={}))
        select_only(loose)
        with patch.object(plugin_http, "post_json", post):
            result = bpy.ops.xiv_ie.apply_racial_scaling(model_race="201", race="0")
        require(result == {"CANCELLED"}, "a model already of the target race is left alone")


def check_race_detection() -> None:
    racial = module("instant_edit.racial_scaling")
    context = module("instant_edit.context")
    with temporary_scene_data():
        armature = rig()
        tagged = part("0.0 Tagged", armature, WEIGHTS)
        context.tag_object(tagged, {"source_game_path": "chara/equipment/e6000/model/c0201e6000_top.mdl"})
        require(racial.detect_model_race(tagged) == 201, "the import's game path names the model's race")
        body = part("0.0 Body", armature, WEIGHTS)
        context.tag_object(body, {"source_game_path": "chara/human/c1401/obj/body/b0001/model/c1401b0001_top.mdl"})
        require(racial.detect_model_race(body) == 1401, "a body's game path names its race")
        named = part("0.0 Named", armature, WEIGHTS)
        collection = bpy.data.collections.new("c0801e6001_dwn")
        bpy.context.scene.collection.children.link(collection)
        collection.objects.link(named)
        require(racial.detect_model_race(named) == 801, "a model file name in a collection's name counts")
        plain = part("0.0 Plain", armature, WEIGHTS)
        require(racial.detect_model_race(plain) is None, "no race is guessed without a model name")
        npc = part("0.0 c0104e0001_top", armature, WEIGHTS)
        require(racial.detect_model_race(npc) is None, "NPC races aren't offered")
        require(racial.race_label(801) == "Female Miqo'te (c0801)" and len(racial.PLAYER_RACES) == 18
                and racial.PLAYER_RACES[-1] == (1801, "Female Viera"), "the player races are listed in code order")


def run() -> None:
    with addon_session(PACKAGE):
        check_reshape()
        check_skips_and_errors()
        check_race_detection()
        check_undo()


if __name__ == "__main__":
    try:
        run()
    except Exception as error:
        detail = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error file=Blender-Addon/testing/racial_scaling_regression.py::Racial scaling regression failed: {detail}",
              flush=True)
        raise
