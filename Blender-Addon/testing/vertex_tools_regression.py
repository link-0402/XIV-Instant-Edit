"""Vertex Data tools, export requirements, Pose tools, shortcuts and the listener retry."""

import importlib
import re
import socket
import sys
import tempfile
from pathlib import Path

import bpy
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data


PACKAGE = "_xiv_instant_edit_vertex_tools"
MATERIAL = "/chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"


def require(condition, message: str) -> None:
    if not condition:
        print(f"[FAIL] {message}")
        raise AssertionError(message)
    print(f"[PASS] {message}")


def module(name: str):
    return importlib.import_module(f"{PACKAGE}.{name}")


def rig(name: str = "Rig", bones=("j_kosi", "j_sebo_a")):
    data = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    for index, bone_name in enumerate(bones):
        bone = data.edit_bones.new(bone_name)
        bone.head = (0.0, 0.0, float(index))
        bone.tail = (0.0, 0.0, float(index) + 1.0)
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def part(name: str, armature, *, faces=((0, 1, 2),), groups=None, parent_bone: str = "",
         uv_names=("uv0",)):
    """A small mesh named like an Instant Edit part, weighted to ``armature`` by ``groups``."""
    points = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (1.0, 1.0, 0.0)]
    count = max(index for face in faces for index in face) + 1
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(points[:count], [], [tuple(face) for face in faces])
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = armature
    if parent_bone:
        obj.parent_type = "BONE"
        obj.parent_bone = parent_bone
    else:
        obj.modifiers.new(name="Armature", type="ARMATURE").object = armature
    groups = {"j_kosi": (range(count), 1.0)} if groups is None else groups
    for group, (indices, weight) in groups.items():
        obj.vertex_groups.new(name=group).add(list(indices), weight, "REPLACE")
    for layer_index, uv_name in enumerate(uv_names):
        layer = mesh.uv_layers.new(name=uv_name)
        values = np.linspace(0.1, 0.9, len(mesh.loops) * 2, dtype=np.float32) + 0.01 * layer_index
        layer.uv.foreach_set("vector", values)
    obj["xiv_material"] = MATERIAL
    return obj


def colour_layer(obj, name: str, rgba, data_type: str = "FLOAT_COLOR"):
    layer = obj.data.color_attributes.new(name, type=data_type, domain="CORNER")
    values = np.tile(np.asarray(rgba, dtype=np.float32), len(obj.data.loops))
    layer.data.foreach_set("color" if data_type == "FLOAT_COLOR" else "color_srgb", values)
    return layer


def layer_values(layer, attribute: str = "color") -> np.ndarray:
    size = 2 if attribute == "vector" else 4
    values = np.empty(len(layer.data if attribute != "vector" else layer.uv) * size, dtype=np.float32)
    (layer.uv if attribute == "vector" else layer.data).foreach_get(attribute, values)
    return values.reshape(-1, size)


def select_only(*objects) -> None:
    for obj in bpy.context.selected_objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def export(objects, folder, name: str):
    """Export ``objects`` to an MDL and read back its model and LOD 0 vertex streams."""
    settings = bpy.context.scene.xiv_ie_settings
    settings.model_format = "MDL"
    target = Path(folder) / name
    module("mesh.export").export_result(target, "MDL", export_objects=list(objects))
    model = module("xivpy.model").XIVModel.from_file(str(target) + ".mdl")
    buffer = model.buffers[model.header.vert_offset[0]:]
    create = module("io.model.imp.streams").create_stream_arrays
    streams = [
        create(buffer, mesh, model.vertex_declarations[index], index)
        for index, mesh in enumerate(model.meshes[:model.lods[0].mesh_count])
    ]
    return model, streams, str(target) + ".mdl"


def check_vertex_data_tools() -> None:
    with temporary_scene_data(), tempfile.TemporaryDirectory(prefix="xiv-ie-vertex-") as folder:
        armature = rig()
        obj = part("0.0 Vertex Part", armature)
        vc1 = colour_layer(obj, "vc0", (0.2, 0.3, 0.4, 0.6))
        vc2 = colour_layer(obj, "vc1", (0.5, 0.1, 0.1, 0.8))
        flow = colour_layer(obj, "xiv_flow", (1.0, 0.5, 1.0, 1.0))
        select_only(obj)

        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_UV2") == {"CANCELLED"},
                "Clear UV2 leaves a mesh without UV2 alone")
        require(bpy.ops.xiv_ie.vertex_data(action="COPY_UV1_TO_UV2") == {"FINISHED"}
                and [layer.name for layer in obj.data.uv_layers] == ["uv0", "uv1"],
                "Copy UV1 to UV2 adds uv1 to a mesh that had only UV1")
        require(np.allclose(layer_values(obj.data.uv_layers["uv0"], "vector"),
                            layer_values(obj.data.uv_layers["uv1"], "vector")),
                "UV2 holds a copy of UV1")
        _model, streams, _path = export([obj], folder, "copied")
        uv = streams[0][1]["uv0"]
        require(uv.shape[1] == 4 and np.allclose(uv[:, 2:4], uv[:, 0:2]),
                "the exported model's UV2 equals its UV1")

        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_UV2") == {"FINISHED"}, "Clear UV2 runs on UV2")
        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_COLOR1") == {"FINISHED"}, "Clear Vertex Color 1 runs")
        require(np.allclose(layer_values(vc1)[:, :3], 1.0) and np.allclose(layer_values(vc1)[:, 3], 0.6),
                "clearing vertex color 1 whitens it and keeps its alpha")
        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_COLOR2") == {"FINISHED"}, "Clear Vertex Color 2 runs")
        require(np.allclose(layer_values(vc2)[:, :3], 0.0) and np.allclose(layer_values(vc2)[:, 3], 0.8),
                "clearing vertex color 2 blackens it and keeps its alpha")
        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_FLOW") == {"FINISHED"}
                and np.allclose(layer_values(flow)[:, :2], 0.5),
                "clearing flow data centres the flow colour")
        _model, streams, cleared_path = export([obj], folder, "cleared")
        tex = streams[0][1]
        require(np.allclose(tex["uv0"][:, 2:4], 0.0), "the exported UV2 is zero")
        require(np.all(tex["colour0"].view(np.uint8)[:, :3] == 255)
                and np.all(tex["colour0"].view(np.uint8)[:, 3] == round(0.6 * 255)),
                "the exported vertex color 1 is white with its alpha")
        require(np.all(tex["colour1"].view(np.uint8)[:, :3] == 0)
                and np.all(tex["colour1"].view(np.uint8)[:, 3] == round(0.8 * 255)),
                "the exported vertex color 2 is black with its alpha")
        require(np.all(tex["flow"].view(np.uint8) == np.array((127, 127, 127, 255), np.uint8)),
                "cleared flow exports as the zero vector vanilla models use")

        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_ALPHA1") == {"FINISHED"}
                and np.allclose(layer_values(vc1)[:, 3], 1.0),
                "Clear Vertex Alpha 1 makes vertex color 1 opaque")

        imported = module("io.model.importer").ModelImport.from_file(
            cleared_path, "Flow Roundtrip", select_objects=False)
        imported_flow = imported[0].data.color_attributes["xiv_flow"]
        require(np.allclose(layer_values(imported_flow)[:, :2], 0.5),
                "importing zero flow keeps it at the centre instead of inventing a direction")
        imported[0].name = "0.0 Reimported"
        imported[0]["xiv_material"] = MATERIAL
        imported[0].parent = armature
        imported[0].modifiers.new(name="Armature", type="ARMATURE").object = armature
        imported[0].vertex_groups.new(name="j_kosi").add(list(range(len(imported[0].data.vertices))), 1.0, "REPLACE")
        _model, streams, _path = export([imported[0]], folder, "reexported")
        require(np.all(streams[0][1]["flow"].view(np.uint8) == np.array((127, 127, 127, 255), np.uint8)),
                "zero flow survives an import and export round trip")

        directed = part("0.0 Directed Flow", armature)
        colour_layer(directed, "xiv_flow", (1.0, 0.5, 1.0, 1.0))
        _model, streams, _path = export([directed], folder, "directed")
        flow_bytes = streams[0][1]["flow"].view(np.uint8)
        require(not np.any(np.all(flow_bytes == np.array((127, 127, 127, 255), np.uint8), axis=1)),
                "a flow direction still exports as a direction")

        legacy = part("0.0 Legacy", armature, uv_names=("UVMap",))
        byte_colour = colour_layer(legacy, "vc0", (0.25, 0.5, 0.75, 0.5), data_type="BYTE_COLOR")
        select_only(legacy)
        require(bpy.ops.xiv_ie.vertex_data(action="COPY_UV1_TO_UV2") == {"FINISHED"}
                and [layer.name for layer in legacy.data.uv_layers] == ["UVMap", "UVMap UV2"],
                "Copy UV1 to UV2 names the new map so an unnumbered UV1 keeps its place")
        _model, streams, _path = export([legacy], folder, "legacy")
        tex = streams[0][1]
        require(tex["uv0"].shape[1] == 4 and np.allclose(tex["uv0"][:, 2:4], tex["uv0"][:, 0:2]),
                "the legacy mesh exports its copied UV2")
        require(np.all(np.abs(tex["colour0"].view(np.uint8)[:, 3].astype(int) - 128) <= 1),
                "a byte color's alpha exports unchanged instead of gamma-converted")
        require(np.allclose(layer_values(byte_colour, "color_srgb")[:, 3], 0.5, atol=0.01),
                "exporting leaves the byte color itself alone")

        bare = part("0.0 Bare", armature)
        select_only(bare)
        require(bpy.ops.xiv_ie.vertex_data(action="CLEAR_COLOR2") == {"CANCELLED"}
                and not bare.data.color_attributes,
                "clearing a channel a mesh does not have changes nothing")
        bpy.ops.object.mode_set(mode="EDIT")
        try:
            require(not bpy.ops.xiv_ie.vertex_data.poll(), "the vertex data tools wait for Object Mode")
        finally:
            bpy.ops.object.mode_set(mode="OBJECT")


def check_export_requirements() -> None:
    export_module = module("mesh.export")
    exceptions = module("io.model.com.exceptions")
    with temporary_scene_data(), tempfile.TemporaryDirectory(prefix="xiv-ie-weights-") as folder:
        armature = rig()
        unweighted = part("0.0 No Weights", armature, groups={})
        partial = part("0.1 Partial", armature, groups={"j_kosi": ((0, 1), 1.0)})
        stray = part("0.2 Stray Group", armature, groups={"Group": ((0, 1, 2), 1.0)})
        mixed = part("0.3 Mixed", armature, groups={
            "Helper A": ((0, 1, 2), 0.5), "Helper B": ((0, 1, 2), 0.5), "j_kosi": ((0, 1, 2), 1.0)})
        follower = part("0.4 Follower", armature, groups={}, parent_bone="j_sebo_a")

        require(export_module.check_weights([unweighted, partial, stray, mixed, follower])
                == ["0.0 No Weights", "0.2 Stray Group"],
                "the quick weight check names meshes without any bone group")
        for obj, expected in ((unweighted, "0.0 No Weights (all)"), (partial, "0.1 Partial (1 of 3)"),
                              (stray, "0.2 Stray Group (all)")):
            try:
                export([obj], folder, "refused")
            except exceptions.XIVMeshError as error:
                require(expected in str(error), f"export refuses {expected}")
            else:
                raise AssertionError(f"{obj.name} exported without bone weights")
        require(not hasattr(bpy.context.scene.xiv_ie_settings, "remove_yas"), "the YAS Groups option is gone")
        model, _streams, _path = export([mixed, follower], folder, "accepted")
        require(model.bones == ["j_kosi", "j_sebo_a"],
                "every non-bone group is dropped and a bone-parented mesh follows its bone")

        quad = part("0.0 Quad", armature, faces=((0, 1, 3, 2),))
        require(export_module.check_triangulation([quad, mixed]) == ["0.0 Quad"], "quads are found")
        for obj in (unweighted, partial, stray, mixed, follower):
            obj.hide_set(True)
        settings = bpy.context.scene.xiv_ie_settings
        settings.export_directory = folder
        settings.export_name = "quad"
        settings.model_format = "FBX"
        try:
            select_only(quad)
            # Background Blender raises an operator's error report.
            try:
                bpy.ops.xiv_ie.simple_export()
            except RuntimeError as error:
                message = str(error)
            else:
                message = ""
            require("Not Triangulated: 0.0 Quad" in message and not (Path(folder) / "quad.fbx").exists(),
                    "Simple Export refuses untriangulated meshes in every format")
        finally:
            settings.model_format = "MDL"


def check_pose_tools() -> None:
    pose = module("pose")
    with temporary_scene_data():
        armature = rig("Pose Rig")
        other = rig("Other Rig", bones=("j_other",))
        body = part("0.0 Posed Body", armature)
        walk = bpy.data.actions.new("Walk")
        unrelated = bpy.data.actions.new("Unrelated")
        try:
            data = armature.animation_data_create()
            data.action = walk
            curve = walk.fcurve_ensure_for_datablock(armature, 'pose.bones["j_kosi"].location', index=0)
            curve.keyframe_points.insert(1, 0.0)
            curve.keyframe_points.insert(20, 1.0)
            data.action = None
            other_data = other.animation_data_create()
            other_data.action = unrelated
            other_curve = unrelated.fcurve_ensure_for_datablock(other, 'pose.bones["j_other"].location', index=0)
            other_curve.keyframe_points.insert(1, 0.0)
            other_data.action = None

            select_only(body)
            context = bpy.context
            require(pose.pose_armature(context) == armature, "the Pose section finds the active mesh's armature")
            require(pose.pose_actions(armature) == [walk], "only actions that key the armature's bones are offered")
            context.scene.frame_set(50)
            require(bpy.ops.xiv_ie.show_pose_action(action="Walk") == {"FINISHED"}
                    and armature.animation_data.action == walk
                    and armature.animation_data.action_slot is not None
                    and armature.data.pose_position == "POSE"
                    and 1 <= context.scene.frame_current <= 20,
                    "showing an action assigns it, poses the armature and moves into its frames")
            require(bpy.ops.xiv_ie.toggle_rest_pose() == {"FINISHED"} and armature.data.pose_position == "REST",
                    "the toggle switches to the rest pose")
            require(bpy.ops.xiv_ie.toggle_rest_pose() == {"FINISHED"} and armature.data.pose_position == "POSE",
                    "the toggle switches back to the action")
            walk.use_fake_user = False
            require(bpy.ops.xiv_ie.show_pose_action(action="") == {"FINISHED"}
                    and armature.animation_data.action is None and walk.use_fake_user,
                    "taking an action off keeps it from being discarded on save")

            context.scene.xiv_ie_settings.pose_armature = other
            require(pose.pose_armature(context) == other, "a chosen armature wins over the detected one")
            context.scene.xiv_ie_settings.pose_armature = None
        finally:
            for action in (walk, unrelated):
                if action.name in bpy.data.actions:
                    bpy.data.actions.remove(action)


def check_shortcuts() -> None:
    keymaps = module("keymaps")
    keyconfigs = bpy.context.window_manager.keyconfigs
    keyconfigs.update()
    for idname, label in keymaps.SHORTCUTS:
        _keymap, item = keymaps.user_shortcut(bpy.context, idname)
        require(item is not None and item.type == "NONE",
                f"{label} has an assignable shortcut that starts without a key")


def check_listener_retry() -> None:
    server = module("instant_edit.server")
    props = bpy.context.scene.xiv_ie_instant_edit_props
    blocker = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    blocker.bind(("127.0.0.1", 0))
    blocker.listen(1)
    port = blocker.getsockname()[1]
    try:
        require(not server.start_server(port) and not server.server_status()[0],
                "the listener cannot start while another program holds the port")
        require(not server.retry_server(), "retrying while the port is taken fails quietly")
        props.last_status = f"{server.LISTENER_UNAVAILABLE} on port {port}: in use"
        blocker.close()
        require(server.poll_listener() == server.LISTENER_RETRY_SECONDS and server.server_status()[:2] == (True, port),
                "the listener takes over the port once it is free")
        require(props.last_status == f"Listening for the plugin on port {port} again.",
                "the status reports the reconnected listener")
        server.stop_server()
        require(not server.retry_server(), "a stopped listener stays stopped")
    finally:
        blocker.close()
        server.stop_server()


def check_ui_icons() -> None:
    """An unknown icon name aborts a panel's whole draw (STATUS_WARNING did on Blender 4.5)."""
    ui = module("ui")
    source = (Path(__file__).resolve().parents[1] / "ui.py").read_text(encoding="utf-8")
    icons = set(re.findall(r'icon="([A-Z0-9_]+)"', source))
    icons |= set(re.findall(r'"([A-Z][A-Z0-9_]+)" if ', source)) | set(re.findall(r' else "([A-Z][A-Z0-9_]+)"', source))
    icons |= {icon for _title, icon, _text, _actions in ui._VERTEX_DATA_SECTIONS}
    icons |= set(ui._SEVERITY_ICONS.values())
    known = set(bpy.types.UILayout.bl_rna.functions["prop"].parameters["icon"].enum_items.keys())
    unknown = sorted(icons - known)
    require(not unknown, f"every sidebar icon exists in Blender {bpy.app.version_string} (unknown: {unknown})")


def run() -> None:
    with addon_session(PACKAGE):
        check_vertex_data_tools()
        check_export_requirements()
        check_pose_tools()
        check_shortcuts()
        check_listener_retry()
        check_ui_icons()


if __name__ == "__main__":
    try:
        run()
    except Exception as error:
        detail = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error file=Blender-Addon/testing/vertex_tools_regression.py::Vertex tools regression failed: {detail}",
              flush=True)
        raise
