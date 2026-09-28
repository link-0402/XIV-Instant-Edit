"""Focused Blender failure and file-load lifecycle regressions."""

import http.client
import importlib
import sys
import tempfile
import threading
from pathlib import Path
from unittest.mock import patch
from types import SimpleNamespace
from urllib.error import URLError

import bpy
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session, temporary_scene_data
from armature_regression import assert_combination_failures, assert_linked_mesh_rejected, mesh, rig


def export_failure_restores_scene(addon, stage):
    handler = importlib.import_module(f"{addon.__name__}.io.model.handler")
    exporter = importlib.import_module(f"{addon.__name__}.mesh.export")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.object.armature_add()
    rig = bpy.context.object
    mesh = bpy.data.meshes.new("FailureMeshData")
    mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
    obj = bpy.data.objects.new("0.0 FailureMesh", mesh)
    bpy.context.collection.objects.link(obj)
    obj.parent = rig
    obj.shape_key_add(name="Basis")
    obj.shape_key_add(name="shp_one").value = 0.25
    obj.shape_key_add(name="shp_two").value = 0.75
    settings = bpy.context.scene.xiv_ie_settings
    settings.keep_shapekeys = True
    if stage == "transparency":
        obj["xiv_transparency"] = True
    if stage == "shape_mismatch":
        obj.modifiers.new(name="Subdivision", type="SUBSURF")
    obj.hide_set(True)  # Explicit export callers may include hidden objects.
    bpy.context.view_layer.update()
    before_objects = {item.as_pointer() for item in bpy.data.objects}
    before_meshes = {item.as_pointer() for item in bpy.data.meshes}
    before_name = obj.name
    target = {"transparency": "sequential_faces"}.get(stage)
    if target:
        failure = patch.object(handler, target, side_effect=RuntimeError("injected preparation failure"))
    else:
        # Fail after all shape copies have been allocated, before processing them.
        original_graph = bpy.context.evaluated_depsgraph_get
        calls = 0
        def graph():
            nonlocal calls
            calls += 1
            if calls == 2:
                raise RuntimeError("injected preparation failure")
            return original_graph()
        failure = patch.object(handler, "bpy", SimpleNamespace(
            context=SimpleNamespace(evaluated_depsgraph_get=graph, view_layer=bpy.context.view_layer),
            data=bpy.data))
    try:
        with tempfile.TemporaryDirectory() as temporary, failure:
            try:
                exporter.export_result(Path(temporary) / "failure", "MDL", export_objects=[obj])
            except RuntimeError as error:
                assert "injected preparation failure" in str(error), error
            else:
                raise AssertionError(f"{stage}: failure injection did not run")
        assert {item.as_pointer() for item in bpy.data.objects} == before_objects, stage
        assert {item.as_pointer() for item in bpy.data.meshes} == before_meshes, stage
        assert obj.name == before_name and obj.hide_get(), stage
        assert [key.value for key in obj.data.shape_keys.key_blocks[1:]] == [0.25, 0.75]
        print(f"[PASS] {stage} failure restores source state and removes temporary geometry")
    finally:
        bpy.ops.wm.read_factory_settings(use_empty=True)


def file_load_restarts_schedulers(addon):
    bridge = importlib.import_module(f"{addon.__name__}.instant_edit")
    recovery = importlib.import_module(f"{addon.__name__}.instant_edit.recovery")
    revocation = importlib.import_module(f"{addon.__name__}.instant_edit.revocation")
    recovery.cancel_recovery()
    recovery.schedule_recovery()
    bridge._context_visibility_changed(None, None)
    old_generation = recovery._recovery_generation
    old_revocation_generation = revocation._generation
    revocation._worker_running = True
    bpy.app.timers.register(revocation._poll_results, first_interval=60)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    assert recovery._recovery_generation > old_generation
    assert revocation._generation > old_revocation_generation
    assert bpy.app.timers.is_registered(recovery._run_scheduled_recovery)
    assert not revocation._worker_running
    bridge._context_visibility_changed(None, None)
    assert bpy.app.timers.is_registered(bridge._run_visibility_check)
    print("[PASS] file loading restarts recovery and clears stale scheduler flags")


def active_workers_discard_previous_file(addon):
    bridge = importlib.import_module(f"{addon.__name__}.instant_edit")
    recovery = importlib.import_module(f"{addon.__name__}.instant_edit.recovery")
    revocation = importlib.import_module(f"{addon.__name__}.instant_edit.revocation")
    recovery.cancel_recovery()
    revocation.cancel_revocations()
    recovery_started, revocation_started, release = (threading.Event() for _ in range(3))
    old_recovery = recovery._recovery_generation
    old_revocation = revocation._generation
    record = {"contextId": "previous-context", "importId": "previous-import",
              "capability": "test-capability", "ports": [42428]}
    with revocation._lock:
        revocation._save_locked([record])

    def reattach(*_args):
        recovery_started.set()
        assert release.wait(5), "test did not release recovery worker"
        return {"contextId": record["contextId"]}

    def revoke(_record):
        revocation_started.set()
        assert release.wait(5), "test did not release revocation worker"
        return True

    workers = [
        threading.Thread(target=recovery._recover_worker,
                         args=(old_recovery, [(record["contextId"], record["importId"], record["capability"], [42428])])),
        threading.Thread(target=revocation._worker, args=(old_revocation, [record])),
    ]
    with patch.object(recovery, "_request_reattach", side_effect=reattach), \
            patch.object(revocation, "_send", side_effect=revoke), \
            patch.object(bridge, "schedule_revocations"), \
            patch.object(recovery, "apply_authoritative_context") as apply:
        try:
            for worker in workers:
                worker.start()
            assert recovery_started.wait(5) and revocation_started.wait(5)
            bpy.ops.wm.read_factory_settings(use_empty=True)
        finally:
            release.set()
            for worker in workers:
                worker.join(5)
                assert not worker.is_alive()
        # A reopened saved file may contain the same context ID; its collection
        # must still be protected from results belonging to the previous file.
        with patch.object(recovery, "context_collections", return_value=[{"context_id": record["contextId"]}]):
            recovery._poll_recovery_results()
        revocation._poll_results()
        apply.assert_not_called()
        with revocation._lock:
            assert revocation._load_locked() == [record], "stale completion removed a durable revocation"

    # A fresh generation can retry the same durable record and finish normally.
    with patch.object(revocation, "_send", return_value=True):
        revocation._worker(revocation._generation, [record])
        revocation._poll_results()
    with revocation._lock:
        assert revocation._load_locked() == []
    print("[PASS] old workers cannot update a loaded file; durable revocations remain retryable")


def duplicate_face_keeps_corner_layers_aligned(addon):
    import numpy as np

    importer = importlib.import_module(f"{addon.__name__}.io.model.importer")
    # The second triangle is a back face reusing the first one's vertices, which
    # Mesh.validate() removes; the corner layers must shrink with it.
    indices = np.array([0, 1, 2, 2, 1, 0, 1, 3, 2], dtype=np.int32)
    positions = np.zeros(4, dtype=[("position", np.float32, 4)])
    positions["position"] = [(0, 0, 0, 1), (1, 0, 0, 1), (0, 1, 0, 1), (1, 1, 0, 1)]
    vertex_data = np.zeros(4, dtype=[("normal", np.float32, 4), ("uv0", np.float32, 2),
                                     ("colour0", np.uint8, 4)])
    vertex_data["normal"] = (0, 0, 1, 0)
    vertex_data["uv0"] = [(0.1, 0.2), (0.3, 0.4), (0.5, 0.6), (0.7, 0.8)]
    vertex_data["colour0"] = [(10, 0, 0, 255), (20, 0, 0, 255), (30, 0, 0, 255), (40, 0, 0, 255)]
    flags = SimpleNamespace(uv0=True, uv1=False, col_count=1, normals=True, tangents=False, flow=False)
    mesh = importer.ModelImport._create_blend_mesh(flags, {0: positions, 1: vertex_data}, indices, 4)
    try:
        assert len(mesh.polygons) == 2, len(mesh.polygons)
        vertex_indices = np.empty(len(mesh.loops), dtype=np.int32)
        mesh.loops.foreach_get("vertex_index", vertex_indices)
        uvs = np.empty(len(mesh.loops) * 2, dtype=np.float32)
        mesh.uv_layers["uv0"].uv.foreach_get("vector", uvs)
        expected_uvs = importer.get_uv0({1: vertex_data})[0][vertex_indices]
        assert np.allclose(uvs.reshape(-1, 2), expected_uvs), (uvs, expected_uvs)
        colours = np.empty(len(mesh.loops) * 4, dtype=np.float32)
        mesh.color_attributes["vc0"].data.foreach_get("color", colours)
        expected_colours = vertex_data["colour0"][vertex_indices] / 255.0
        assert np.allclose(colours.reshape(-1, 4), expected_colours), (colours, expected_colours)
    finally:
        bpy.data.meshes.remove(mesh)
    print("[PASS] duplicate back faces are dropped with their UV and colour corners")


def vertex_group_cleanup_removes_every_matching_group(addon):
    handler = importlib.import_module(f"{addon.__name__}.io.model.handler")
    with temporary_scene_data():
        # Groups that survive cleanup are exported as bones, so every group
        # without a bone must go, including runs of adjacent ones. This pins the
        # remove-while-iterating loops: bpy collections iterate over a snapshot,
        # so they must not skip the group after each removal.
        skeleton = rig("Cleanup Rig")
        obj = mesh("Cleanup Mesh", parent=skeleton)
        for name in ("Helper 1", "Helper 2", "Helper 3"):
            obj.vertex_groups.new(name=name)
        handler.SceneHandler().handle_vertex_groups([obj])
        names = [group.name for group in obj.vertex_groups]
        assert names == ["root"], f"groups without a bone survived cleanup: {names}"

    print("[PASS] vertex group cleanup removes every group without a bone")


def export_copies_keep_their_rig(addon):
    objects = importlib.import_module(f"{addon.__name__}.mesh.objects")
    with temporary_scene_data():
        skeleton = rig("Copy Rig")
        empty = bpy.data.objects.new("Copy Empty", None)
        bpy.context.scene.collection.objects.link(empty)
        sources = (
            mesh("Copy Parented", parent=skeleton),
            # Rigged only by an Armature modifier, which the exporter supports.
            mesh("Copy Modifier Only", modifier_rigs=(skeleton,)),
            # Parented to a non-armature object; assigning that parent to the
            # copy's Armature modifier used to raise.
            mesh("Copy Under Empty", parent=empty, modifier_rigs=(skeleton,)),
        )
        depsgraph = bpy.context.evaluated_depsgraph_get()
        for source in sources:
            copy = objects.copy_mesh_object(source, depsgraph)
            try:
                rigs = [modifier.object for modifier in copy.modifiers if modifier.type == "ARMATURE"]
                assert rigs == [skeleton], f"{source.name}: copy lost its rig ({rigs})"
            finally:
                objects.safe_object_delete(copy)
    print("[PASS] export copies keep the armature of modifier-rigged and non-armature-parented meshes")


def bounding_boxes_merge_to_their_union(addon):
    bbox = importlib.import_module(f"{addon.__name__}.xivpy.model.bbox")

    def box(low, high):
        return bbox.BoundingBox.from_array(np.array([low, high], dtype=np.float32))

    merged = box((-1, -2, -3), (4, 5, 6)).merge(box((-0.5, -1, -1), (2, 3, 2)))
    assert [float(v) for v in merged.min[:3]] == [-1, -2, -3], merged.min
    assert [float(v) for v in merged.max[:3]] == [4, 5, 6], f"merge lost the existing maximum: {merged.max}"
    merged = merged.merge(box((-9, 0, 0), (1, 1, 20)))
    assert [float(v) for v in merged.min[:3]] == [-9, -2, -3], merged.min
    assert [float(v) for v in merged.max[:3]] == [4, 5, 20], merged.max
    print("[PASS] bounding boxes merge to the union of every mesh")


def import_accessors_leave_shared_streams_alone(addon):
    accessors = importlib.import_module(f"{addon.__name__}.io.model.imp.accessors")
    count = 8
    rng = np.random.default_rng(1)
    position_dtype = np.dtype([("position", np.float16, (4,))])
    tangent_dtype = np.dtype([("normal", np.float16, (4,)), ("tangent", np.ubyte, (4,))])
    streams = {0: np.zeros(count, dtype=position_dtype), 1: np.zeros(count, dtype=tangent_dtype)}
    streams[0]["position"][:, :3] = rng.uniform(-2, 2, (count, 3)).astype(np.float16)
    streams[1]["tangent"][:] = rng.integers(0, 256, (count, 4), dtype=np.uint8)
    streams[1]["tangent"][:, 2] = 0  # bytes that must flip to 255, not wrap to 0
    streams[1]["tangent"][:, 3] = 255
    raw = {key: array.copy() for key, array in streams.items()}

    expected = raw[0]["position"].astype(np.float32)[:, :3]
    expected[:, 1], expected[:, 2] = -expected[:, 2].copy(), expected[:, 1].copy()

    # Submesh vertex ranges are views into the shared streams and can overlap.
    for start, size in ((0, 4), (2, 4), (6, 2)):
        submesh = {key: array[start:start + size] for key, array in streams.items()}
        converted = accessors.get_positions(submesh).astype(np.float32)
        assert np.allclose(converted, expected[start:start + size], atol=1e-3), \
            f"vertices {start}..{start + size} were converted more than once"
        accessors.get_bitangents(submesh)
    assert all(np.array_equal(streams[key], raw[key]) for key in streams), \
        "reading a submesh changed the shared vertex streams"

    tangents = raw[1]["tangent"][:, :3].astype(np.float32) / 127.5 - 1
    tangents /= np.linalg.norm(tangents, axis=1, keepdims=True)
    direction = accessors.get_bitangents(streams)[:, :3]
    direction /= np.linalg.norm(direction, axis=1, keepdims=True)
    assert np.allclose(direction[:, 0], tangents[:, 0], atol=1e-5)
    assert np.allclose(direction[:, 1], -tangents[:, 2], atol=1e-5), \
        "a byte-encoded axis flip wrapped instead of negating"
    assert np.allclose(direction[:, 2], tangents[:, 1], atol=1e-5)

    shape = accessors.get_shape_positions(streams, np.array([6, 7]), np.array([1, 2]))
    reference = expected.copy()
    reference[[1, 2]] = expected[[6, 7]]
    assert np.allclose(shape.astype(np.float32), reference, atol=1e-3)
    assert np.array_equal(streams[0], raw[0]), "shape positions changed the shared stream"
    print("[PASS] import accessors convert each vertex once and leave shared streams untouched")


def clear_quick_backups_uses_the_plugin_transport(addon):
    ops = importlib.import_module(f"{addon.__name__}.instant_edit.ops")
    ref = SimpleNamespace(
        plugin_instance_id="plugin", context_id="context", capability="capability",
        backup_target_id="a" * 64, callback_port=42428)
    calls = []

    def respond(status, body):
        def post(port, endpoint, payload, *, timeout, max_response_size):
            calls.append((port, endpoint, payload))
            return status, body
        return post

    def clear(post_json):
        with patch.object(ops, "export_destination_context", return_value=ref), \
                patch.object(ops, "selected_variant_target", return_value=None), \
                patch.object(ops, "post_json", side_effect=post_json):
            return ops.clear_quick_backups(bpy.context)

    result = clear(respond(200, b'{"ok": true, "code": "backups_cleared"}'))
    assert result["ok"] is True
    assert calls == [(42428, "/backup/clear", {
        "schema": "instant-edit.backup-clear", "version": 1, "pluginInstanceId": "plugin",
        "contextId": "context", "capability": "capability", "backupTargetId": "a" * 64,
    })], calls

    try:
        clear(respond(409, b'{"ok": false, "code": "destination_not_ready", "cause": "not ready"}'))
    except ops.PluginResponseError as error:
        assert error.status == 409 and error.code == "destination_not_ready", (error.status, error.code)
    else:
        raise AssertionError("a plugin rejection did not raise")

    def unreachable(*_args, **_kwargs):
        raise URLError("connection refused")

    try:
        clear(unreachable)
    except ops.PluginResponseError as error:
        assert error.code == "plugin_connection_failed", error.code
    else:
        raise AssertionError("an unreachable plugin did not raise a plugin error")

    # Something other than the plugin answering on its port replies with a
    # garbled status line. That must be a plugin error too, not a raw
    # http.client exception, and it goes through the real post_json.
    plugin_http = importlib.import_module(f"{addon.__name__}.instant_edit.plugin_http")

    def garbled(*_args, **_kwargs):
        raise http.client.BadStatusLine("NOT HTTP\r\n")

    with patch.object(ops, "export_destination_context", return_value=ref), \
            patch.object(ops, "selected_variant_target", return_value=None), \
            patch.object(plugin_http.urllib.request, "urlopen", side_effect=garbled):
        try:
            ops.clear_quick_backups(bpy.context)
        except ops.PluginResponseError as error:
            assert error.code == "plugin_connection_failed", error.code
        else:
            raise AssertionError("a garbled plugin reply did not raise a plugin error")
    print("[PASS] clearing Quick Export backups reaches the plugin and reports its failures")


def failed_import_releases_its_cache_job(addon):
    cache = importlib.import_module(f"{addon.__name__}.instant_edit.cache")
    job = cache.create_job("imports")
    (job / "model.mdl").write_bytes(b"model")
    try:
        bpy.ops.xiv_ie.instant_import(
            "EXEC_DEFAULT", file_path=str(job / "missing.mdl"), cache_job_directory=str(job))
    except RuntimeError as error:
        assert "not found" in str(error), error
    else:
        raise AssertionError("importing a missing model did not fail")
    assert not job.exists(), "a failed import left its staged model on disk"
    assert job.resolve() not in cache._active_jobs, "a failed import left its cache job active"
    print("[PASS] an import that cannot find its model still releases its cache job")


def quick_export_format_override_is_restored(addon):
    ops = importlib.import_module(f"{addon.__name__}.instant_edit.ops")
    settings = bpy.context.scene.xiv_ie_settings
    original = settings.model_format
    try:
        for chosen in ("FBX", "GLTF", "MDL"):
            settings.model_format = chosen
            with ops._exporting_as_mdl():
                assert settings.model_format == "MDL"
            assert settings.model_format == chosen
            try:
                with ops._exporting_as_mdl():
                    raise RuntimeError("export failed")
            except RuntimeError:
                pass
            assert settings.model_format == chosen, "a failed export changed Simple Export's format"
    finally:
        settings.model_format = original
    print("[PASS] Quick Export and mashups leave the Simple Export format as the user set it")


def run():
    with addon_session("_xiv_ie_correctness_regression") as addon:
        duplicate_face_keeps_corner_layers_aligned(addon)
        assert_combination_failures(addon)
        assert_linked_mesh_rejected(addon)
        for stage in ("transparency", "shape_mismatch"):
            export_failure_restores_scene(addon, stage)
        vertex_group_cleanup_removes_every_matching_group(addon)
        export_copies_keep_their_rig(addon)
        bounding_boxes_merge_to_their_union(addon)
        import_accessors_leave_shared_streams_alone(addon)
        clear_quick_backups_uses_the_plugin_transport(addon)
        failed_import_releases_its_cache_job(addon)
        quick_export_format_override_is_restored(addon)
        file_load_restarts_schedulers(addon)
        active_workers_discard_previous_file(addon)


if __name__ == "__main__":
    run()
