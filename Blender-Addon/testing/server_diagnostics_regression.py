"""Standalone regression coverage for Blender bridge validation and async failures."""

import copy
import http.client
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


def _expect_cache_code(server, payload, expected: str) -> None:
    try:
        server._ImportHandler._validate_cache_settings(payload)
    except server.BridgeRequestError as error:
        assert error.code == expected, (expected, error.code)
        assert error.stage and error.cause and error.remedy
    else:
        raise AssertionError(f"cache validation unexpectedly accepted {expected}")


def _stub_blender(server, operator) -> None:
    server.bpy = types.SimpleNamespace(
        context=types.SimpleNamespace(
            mode="OBJECT",
            scene=types.SimpleNamespace(
                xiv_ie_instant_edit_props=types.SimpleNamespace(last_status="")),
        ),
        ops=types.SimpleNamespace(
            object=types.SimpleNamespace(mode_set=lambda **_kwargs: None),
            xiv_ie=types.SimpleNamespace(instant_import=operator),
        ),
    )


def _model_file(directory: Path) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    model = directory / "model.mdl"
    model.write_bytes(b"\x06\x00\x00\x01model")
    return model


def _check_failed_imports_release_their_cache_job(server, cache, valid, temporary, captured) -> None:
    """A failed queued import must not leave its staged job active until restart."""
    cache.configure_cache(temporary, True)
    model = _model_file(Path(temporary) / "job-handoff")

    def raising_operator(*_args, **_kwargs):
        raise RuntimeError("Operator poll() failed, context is incorrect")

    for label, operator, code in (
        ("cancelled", lambda *_args, **_kwargs: {"CANCELLED"}, "import_cancelled"),
        ("raised", raising_operator, "import_processing_failed"),
    ):
        staged = cache.stage_import({"filePath": str(model)})
        job = Path(staged["cacheJobDirectory"])
        assert job.is_dir() and job.resolve() in cache._active_jobs
        queued = {**copy.deepcopy(valid), **staged,
                  "importOptions": server._normalise_import_options(None)}
        _stub_blender(server, operator)
        captured.clear()
        server._import_queue.put_nowait(queued)
        server.poll_import_queue()
        assert len(captured) == 1 and captured[0][1]["code"] == code, (label, captured)
        assert not job.exists(), f"{label} import left its cache job on disk"
        assert job.resolve() not in cache._active_jobs, f"{label} import left its cache job active"

    # An import that succeeded already finished its job; finishing again is harmless.
    staged = cache.stage_import({"filePath": str(model)})
    job = Path(staged["cacheJobDirectory"])
    cache.finish_job(job)
    _stub_blender(server, lambda *_args, **_kwargs: {"FINISHED"})
    captured.clear()
    server._import_queue.put_nowait({**copy.deepcopy(valid), **staged,
                                     "importOptions": server._normalise_import_options(None)})
    server.poll_import_queue()
    assert not captured and not job.exists()


def _request(port: int, method: str, path: str, body: str | None = None, headers: dict | None = None):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        connection.request(method, path, body=body, headers=headers or {})
        response = connection.getresponse()
        return response.status, json.loads(response.read().decode("utf-8"))
    finally:
        connection.close()


def _check_listener_rejects_browser_requests(server, cache, valid, temporary) -> None:
    """Only plugin-style requests are served: no Origin, a loopback Host, a JSON body."""
    cache.configure_cache(temporary, True)
    assert server.start_server(0)
    port = server._server.server_address[1]
    try:
        assert _request(port, "GET", "/status")[0] == 200
        assert _request(port, "GET", "/status", headers={"Host": f"localhost:{port}"})[0] == 200

        for headers, code in (
            ({"Host": f"attacker.example:{port}"}, "host_not_allowed"),
            ({"Host": f"127.0.0.1.attacker.example:{port}"}, "host_not_allowed"),
            ({"Host": f"localhost:{port + 1}"}, "host_not_allowed"),
            ({"Host": "127.0.0.1"}, "host_not_allowed"),
            ({"Origin": "http://attacker.example"}, "origin_not_allowed"),
            ({"Origin": "null"}, "origin_not_allowed"),
        ):
            status, body = _request(port, "GET", "/status", headers=headers)
            assert status == 403 and body["code"] == code and body["ok"] is False, (headers, status, body)
            assert "diagnosticId" not in body, "a rejected request must not write a diagnostic report"

        plugin_headers = {"Content-Type": "application/json; charset=utf-8"}
        cache_payload = json.dumps({
            "schema": "instant-edit.cache-settings", "version": 1,
            "cacheDirectory": str(Path(temporary) / "attacker-cache"), "automaticCleanup": True})
        base_before = cache.cache_base_directory()
        model = _model_file(Path(temporary) / "listener-handoff")
        import_payload = json.dumps({**copy.deepcopy(valid), "filePath": str(model)})
        imports = cache.cache_root() / "imports"

        for endpoint, payload in (("/settings/cache", cache_payload), ("/import", import_payload)):
            for headers, expected_status, code in (
                ({"Content-Type": "text/plain"}, 415, "unsupported_media_type"),
                ({"Content-Type": "text/plain;charset=UTF-8"}, 415, "unsupported_media_type"),
                ({"Content-Type": "application/x-www-form-urlencoded"}, 415, "unsupported_media_type"),
                ({"Content-Type": "multipart/form-data; boundary=x"}, 415, "unsupported_media_type"),
                ({}, 415, "unsupported_media_type"),
                ({**plugin_headers, "Origin": "http://attacker.example"}, 403, "origin_not_allowed"),
                ({**plugin_headers, "Host": f"attacker.example:{port}"}, 403, "host_not_allowed"),
            ):
                status, body = _request(port, "POST", endpoint, payload, headers)
                assert (status, body["code"]) == (expected_status, code), (endpoint, headers, status, body)
                assert cache.cache_base_directory() == base_before, "a rejected request changed the cache directory"
                assert server._import_queue.empty(), "a rejected request queued an import"
                assert not any(imports.iterdir()), "a rejected request staged an import job"

        # The plugin's own requests still work.
        settings_payload = json.dumps({
            "schema": "instant-edit.cache-settings", "version": 1,
            "cacheDirectory": str(base_before), "automaticCleanup": True})
        status, body = _request(port, "POST", "/settings/cache", settings_payload, plugin_headers)
        assert status == 200 and body["ok"], (status, body)
        status, body = _request(port, "POST", "/import", import_payload, plugin_headers)
        assert status == 200 and body["queued"], (status, body)
        queued = server._import_queue.get_nowait()
        assert Path(queued["cacheJobDirectory"]).is_dir()
        cache.remove_job(queued["cacheJobDirectory"])
    finally:
        server.stop_server()


def run() -> None:
    cache, _diagnostics, server = _load_modules()
    valid = _base_import()
    assert server._ImportHandler._validate_import(copy.deepcopy(valid))["pluginVersion"] == "1.1.5"

    valid_cache = {"schema": "instant-edit.cache-settings", "version": 1,
                   "cacheDirectory": str(Path(tempfile.gettempdir()).resolve()),
                   "automaticCleanup": True}
    assert server._ImportHandler._validate_cache_settings(valid_cache)["cacheDirectory"]
    for payload, expected in (
        ({**valid_cache, "schema": "old.schema"}, "unsupported_schema"),
        ({**valid_cache, "version": 2}, "unsupported_version"),
        ({**valid_cache, "cacheDirectory": "relative/cache"}, "invalid_cache_directory"),
        ({**valid_cache, "cacheDirectory": r"\\server\share"}, "invalid_cache_directory"),
        ({**valid_cache, "automaticCleanup": "yes"}, "invalid_automatic_cleanup"),
    ):
        _expect_cache_code(server, payload, expected)

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
        cache.configure_cache(temporary, False)
        status = server._status_payload()
        assert server.TEXTURE_CACHE_CAPABILITY in status["capabilities"]
        assert status["cacheRoot"] == str(cache.cache_root())
        diagnostics_folder = Path(temporary) / "diagnostics"
        cache.diagnostics_root = lambda: diagnostics_folder
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
        report_path = diagnostics_folder / f"{failure['diagnosticId']}.json"
        report_text = report_path.read_text("utf-8")
        assert failure["code"] == "import_cancelled"
        assert report_path.is_file()
        assert r"C:\Users\Example" not in report_text
        assert json.loads(report_text)["technical"]["pluginVersion"] == "1.1.5"

        _check_failed_imports_release_their_cache_job(server, cache, valid, temporary, captured)
        print("[PASS] failed queued imports release their staged cache job")
        _check_listener_rejects_browser_requests(server, cache, valid, temporary)
        print("[PASS] listener rejects browser-originated requests and serves plugin requests")

    print("[PASS] import validation codes and asynchronous diagnostics are actionable")


if __name__ == "__main__":
    run()
