"""Focused Blender failure and file-load lifecycle regressions."""

import importlib
import sys
import tempfile
import threading
from pathlib import Path
from unittest.mock import patch
from types import SimpleNamespace

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blender_fixtures import addon_session


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
    settings.create_backfaces = stage == "backfaces"
    if stage == "backfaces":
        obj.vertex_groups.new(name="BACKFACES").add([0, 1, 2], 1.0, "REPLACE")
    if stage == "transparency":
        obj["xiv_transparency"] = True
    if stage == "shape_mismatch":
        obj.modifiers.new(name="Subdivision", type="SUBSURF")
    obj.hide_set(True)  # Explicit export callers may include hidden objects.
    bpy.context.view_layer.update()
    before_objects = {item.as_pointer() for item in bpy.data.objects}
    before_meshes = {item.as_pointer() for item in bpy.data.meshes}
    before_name = obj.name
    target = {"transparency": "sequential_faces", "backfaces": "create_backfaces"}.get(stage)
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


def run():
    with addon_session("_xiv_ie_correctness_regression") as addon:
        for stage in ("transparency", "backfaces", "shape_mismatch"):
            export_failure_restores_scene(addon, stage)
        file_load_restarts_schedulers(addon)
        active_workers_discard_previous_file(addon)


if __name__ == "__main__":
    run()
