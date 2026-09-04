"""Standalone regression coverage for sanitized bridge diagnostic reports."""

import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import types
import uuid


def _load_modules():
    root = Path(__file__).resolve().parents[1] / "instant_edit"
    package_name = "xiv_instant_edit_diagnostics_regression"
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
    context._value = lambda _owner, _name, default=None: default
    sys.modules[context.__name__] = context
    spec = importlib.util.spec_from_file_location(
        f"{package_name}.plugin_http", root / "plugin_http.py")
    plugin_http = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = plugin_http
    assert spec.loader is not None
    spec.loader.exec_module(plugin_http)
    return loaded["cache"], loaded["diagnostics"], plugin_http


def run() -> None:
    cache, diagnostics, plugin_http = _load_modules()
    with tempfile.TemporaryDirectory(prefix="xiv-ie-diagnostics-test-") as temporary:
        root = cache.configure_cache(temporary, True)

        previous_root = cache.cache_root()
        original_ensure_cache_root = cache.ensure_cache_root

        def fail_ensure_cache_root():
            raise OSError("simulated cache failure")

        cache.ensure_cache_root = fail_ensure_cache_root
        try:
            cache.configure_cache(Path(temporary) / "unavailable", True)
        except OSError:
            pass
        else:
            raise AssertionError("a failed cache update unexpectedly succeeded")
        finally:
            cache.ensure_cache_root = original_ensure_cache_root
        assert cache.cache_root() == previous_root

        class OversizedResponse:
            status = 413

            def getcode(self):
                return self.status

            def read(self, _limit):
                return b"x" * 5

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

        original_urlopen = plugin_http.urllib.request.urlopen
        plugin_http.urllib.request.urlopen = lambda *_args, **_kwargs: OversizedResponse()
        try:
            plugin_http.post_json(
                42424, "/import", {}, timeout=1, max_response_size=4)
        except plugin_http.PluginResponseTooLarge as error:
            assert error.status == 413 and error.body == b"xxxxx"
        else:
            raise AssertionError("an oversized plugin response was not rejected")
        finally:
            plugin_http.urllib.request.urlopen = original_urlopen

        failure = diagnostics.record_failure(
            component="blender_addon",
            operation="import",
            stage="file_staging",
            code="model_file_unavailable",
            cause=r"Could not read C:\Users\Example\Temp\source.mdl",
            remedy="Run Blender and FFXIV as the same user.",
            endpoint="/import",
            http_status=400,
            exception=OSError(r"Access denied: C:\Users\Example\Temp\source.mdl"),
            metadata={
                "source": r"C:\Users\Example\Temp\source.mdl",
                "secondary": "/home/example/source.mdl",
                "response": '{"capability":"private-value"}',
            },
        )
        report = root / "diagnostics" / f"{failure['diagnosticId']}.json"
        assert report.is_file()
        report_text = report.read_text("utf-8")
        report_data = json.loads(report_text)
        assert report_data["code"] == "model_file_unavailable"
        assert "C:\\Users" not in report_text
        assert "/home/example" not in report_text
        assert "private-value" not in report_text
        assert "<local-path>" in report_text
        assert report_data["technical"]["exceptionMessage"] == "Access denied: <local-path>"

        remote_id = uuid.uuid4().hex
        remote = diagnostics.record_remote_failure(
            json.dumps({
                "ok": False,
                "component": "dalamud_plugin",
                "operation": "backup_restore",
                "stage": "authorization",
                "code": "stale_context",
                "cause": "The context expired.",
                "remedy": "Re-import the model.",
                "diagnosticId": remote_id,
                "componentVersion": "1.1.5",
            }).encode("utf-8"),
            410,
            endpoint="/backup/restore",
        )
        assert remote["diagnosticId"] == remote_id
        remote_report = json.loads(
            (root / "diagnostics" / f"{remote_id}.json").read_text("utf-8"))
        assert remote_report["endpoint"] == "backup/restore"
        assert remote_report["technical"]["componentVersion"] == "1.1.5"
        assert remote_report["componentVersions"]["dalamudPlugin"] == "1.1.5"

        protocol = diagnostics.record_protocol_failure(
            b'{"ok":true,"filePath":"C:\\\\Users\\\\Example\\\\result.mdl"',
            200,
            endpoint="/export",
            operation="export",
        )
        protocol_report = root / "diagnostics" / f"{protocol['diagnosticId']}.json"
        assert protocol["code"] == "invalid_success_response"
        assert "C:\\\\Users" not in protocol_report.read_text("utf-8")

        cache.MAX_DIAGNOSTIC_REPORTS = 3
        cache.MAX_DIAGNOSTIC_BYTES = 1024 * 1024
        diagnostics_folder = root / "diagnostics"
        for index in range(5):
            item = diagnostics_folder / f"{uuid.uuid4().hex}.json"
            item.write_text(json.dumps({"index": index}), encoding="utf-8")
            timestamp = time.time() + index
            os.utime(item, (timestamp, timestamp))
        cache.clean_cache(cache.STALE_SECONDS)
        assert len(tuple(diagnostics_folder.glob("*.json"))) == 3

        cache.MAX_DIAGNOSTIC_REPORTS = 100
        cache.MAX_DIAGNOSTIC_BYTES = 25
        for index in range(3):
            item = diagnostics_folder / f"{uuid.uuid4().hex}.json"
            item.write_text("x" * 20, encoding="utf-8")
            timestamp = time.time() + 10 + index
            os.utime(item, (timestamp, timestamp))
        cache.clean_cache(cache.STALE_SECONDS)
        assert sum(item.stat().st_size for item in diagnostics_folder.glob("*.json")) <= 25

        stale = diagnostics_folder / f"{uuid.uuid4().hex}.json"
        stale.write_text("{}", encoding="utf-8")
        old = time.time() - cache.STALE_SECONDS - 60
        os.utime(stale, (old, old))
        cache.clean_cache(cache.STALE_SECONDS)
        assert not stale.exists()

        removed, _bytes = cache.clean_cache()
        assert removed >= 1
        assert not tuple(diagnostics_folder.glob("*.json"))

    print("[PASS] structured diagnostic reports are sanitized, bounded, and cleanable")


if __name__ == "__main__":
    run()
