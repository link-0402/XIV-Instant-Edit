"""Standalone regression coverage for Blender bridge validation and async failures."""

import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import types


def _load_modules():
    root = Path(__file__).resolve().parents[1] / "instant_edit"
    package_name = "xiv_instant_edit_server_diagnostics_regression"
    package = types.ModuleType(package_name)
    package.__path__ = [str(root)]
    sys.modules[package_name] = package

    loaded = {}
    for name in ("cache", "diagnostics"):
        spec = importlib.util.spec_from_file_location(f"{package_name}.{name}", root / f"{name}.py")
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        assert spec.loader is not None
        spec.loader.exec_module(module)
        loaded[name] = module

    context = types.ModuleType(f"{package_name}.context")
    context.is_safe_game_model_path = lambda value: (
        isinstance(value, str)
        and value.endswith(".mdl")
        and ":" not in value
        and not value.startswith(("/", "\\"))
        and ".." not in value.split("/")
    )
    sys.modules[context.__name__] = context

    plugin_http = types.ModuleType(f"{package_name}.plugin_http")
    plugin_http.post_json = lambda *_args, **_kwargs: (200, b'{"ok":true}')
    sys.modules[plugin_http.__name__] = plugin_http
    sys.modules.setdefault("bpy", types.SimpleNamespace())

    spec = importlib.util.spec_from_file_location(f"{package_name}.server", root / "server.py")
    server = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = server
    assert spec.loader is not None
    spec.loader.exec_module(server)
    return loaded["cache"], loaded["diagnostics"], server


def _base_import() -> dict:
    return {
        "schema": "instant-edit.context",
        "version": 1,
        "pluginVersion": "1.1.5",
        "pluginInstanceId": "plugin-instance",
        "contextId": "context-id",
        "importId": "import-id",
        "capability": "capability",
        "filePath": r"C:\Temp\model.mdl",
        "sourceGamePath": "chara/equipment/e0001/model/c0101e0001_top.mdl",
        "sourceKind": "mod",
        "resolvedGamePath": "chara/equipment/e0001/model/c0101e0001_top.mdl",
        "destinationState": "ready",
        "managedDestination": r"D:\Penumbra\Example\Files",
        "targetFilePath": r"D:\Penumbra\Example\Files\model.mdl",
        "sourceModDirectory": "Example",
        "sourceModName": "Example",
        "sourceModRootPath": r"D:\Penumbra\Example",
        "targetRelativePath": "Files/model.mdl",
        "targetCollectionId": "",
        "targetCollectionName": "",
        "resourceManifestVersion": 0,
        "resourceManifestStatus": "capture_failed",
        "previewManifestPath": "",
        "callbackPort": 42428,
        "objectIndex": 0,
        "displayName": "Example",
    }


def _expect_code(server, payload, expected: str) -> None:
    try:
        server._ImportHandler._validate_import(payload)
    except server.BridgeRequestError as error:
        assert error.code == expected, (expected, error.code)
        assert error.stage and error.cause and error.remedy
    else:
        raise AssertionError(f"validation unexpectedly accepted {expected}")


def run() -> None:
    cache, _diagnostics, server = _load_modules()
    valid = _base_import()
    assert server._ImportHandler._validate_import(copy.deepcopy(valid))["pluginVersion"] == "1.1.5"

    cases = []
    cases.append((None, "request_not_object"))
    for field, value, code in (
        ("schema", "old.schema", "unsupported_schema"),
        ("version", 99, "unsupported_version"),
        ("pluginInstanceId", "", "invalid_plugin_instance_id"),
        ("filePath", None, "invalid_file_path"),
        ("sourceKind", "other", "invalid_context_state"),
        ("callbackPort", True, "invalid_callback_port"),
        ("objectIndex", -1, "invalid_object_index"),
        ("resourceManifestVersion", "two", "invalid_resource_manifest_version"),
        ("resourceManifestStatus", "pending", "invalid_resource_manifest_status"),
        ("targetCollectionId", "not-a-uuid", "invalid_collection_id"),
    ):
        payload = copy.deepcopy(valid)
        payload[field] = value
        cases.append((payload, code))

    payload = copy.deepcopy(valid)
    payload["version"] = 1
    payload["sourceKind"] = "game"
    cases.append((payload, "unsupported_context_state"))

    payload = copy.deepcopy(valid)
    payload.update({
        "version": 2,
        "sourceKind": "game",
        "destinationState": "new_mod_required",
        "sourceGamePath": r"C:\Game\model.mdl",
        "managedDestination": "",
        "targetFilePath": "",
        "sourceModDirectory": "",
        "sourceModName": "",
        "sourceModRootPath": "",
        "targetRelativePath": "",
    })
    cases.append((payload, "invalid_game_model_path"))

    payload = copy.deepcopy(valid)
    payload["managedDestination"] = ""
    cases.append((payload, "missing_penumbra_destination"))

    payload = copy.deepcopy(valid)
    payload.update({"version": 2, "sourceKind": "game", "destinationState": "new_mod_required"})
    cases.append((payload, "invalid_pending_destination"))

    payload = copy.deepcopy(valid)
    payload.update({"resourceManifestVersion": 2, "resourceManifestStatus": "capture_failed"})
    cases.append((payload, "resource_manifest_mismatch"))

    for options, code in (
        ("invalid", "invalid_import_options"),
        ({"armatureMode": "invalid"}, "invalid_armature_mode"),
        ({"targetObject": ""}, "invalid_armature_target"),
        ({"applyTexturesAndMaterials": "yes"}, "invalid_material_preview_option"),
        ({"excludeBodyAndGeneralMaterials": "yes"}, "invalid_material_exclusion_option"),
        ({"excludeBodyAndGeneralMaterials": True}, "material_exclusion_requires_preview"),
        ({"applyTexturesAndMaterials": True}, "preview_manifest_required"),
    ):
        payload = copy.deepcopy(valid)
        payload["importOptions"] = options
        cases.append((payload, code))

    for payload, expected in cases:
        _expect_code(server, payload, expected)

    with tempfile.TemporaryDirectory(prefix="xiv-ie-server-diagnostics-") as temporary:
        root = cache.configure_cache(temporary, False)
        captured = []
        server._notify_import_failure = lambda data, failure: captured.append((data, failure))
        server.bpy = types.SimpleNamespace(
            context=types.SimpleNamespace(
                mode="OBJECT",
                scene=types.SimpleNamespace(
                    xiv_ie_instant_edit_props=types.SimpleNamespace(
                        last_status=r"Import failed: could not read C:\Users\Example\model.mdl")),
            ),
            ops=types.SimpleNamespace(
                object=types.SimpleNamespace(mode_set=lambda **_kwargs: None),
                xiv_ie=types.SimpleNamespace(instant_import=lambda *_args, **_kwargs: {"CANCELLED"}),
            ),
        )
        queued = copy.deepcopy(valid)
        queued["importOptions"] = server._normalise_import_options(None)
        server._import_queue.put_nowait(queued)
        server.poll_import_queue()
        assert len(captured) == 1
        failure = captured[0][1]
        report_path = root / "diagnostics" / f"{failure['diagnosticId']}.json"
        report_text = report_path.read_text("utf-8")
        assert failure["code"] == "import_cancelled"
        assert report_path.is_file()
        assert r"C:\Users\Example" not in report_text
        assert json.loads(report_text)["technical"]["pluginVersion"] == "1.1.5"

    print("[PASS] import validation codes and asynchronous diagnostics are actionable")


if __name__ == "__main__":
    run()
