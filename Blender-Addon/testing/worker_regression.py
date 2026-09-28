"""Standalone regression coverage for the context revocation and recovery workers."""

import contextlib
import http.client
import importlib.util
from pathlib import Path
import sys
import threading
import types


def _load_modules():
    root = Path(__file__).resolve().parents[1] / "instant_edit"
    package_name = "xiv_instant_edit_worker_regression"
    package = types.ModuleType(package_name)
    package.__path__ = [str(root)]
    sys.modules[package_name] = package

    scene = types.SimpleNamespace(xiv_ie_instant_edit_props=types.SimpleNamespace(last_status=""))
    timers = types.SimpleNamespace(
        is_registered=lambda _function: False,
        register=lambda *_args, **_kwargs: None,
        unregister=lambda _function: None,
    )
    sys.modules["bpy"] = types.SimpleNamespace(
        app=types.SimpleNamespace(timers=timers),
        context=types.SimpleNamespace(scene=scene),
    )

    context = types.ModuleType(f"{package_name}.context")
    context.ContextValidationError = type("ContextValidationError", (ValueError,), {})
    context._value = lambda source, name, default="": source.get(name, default)
    context.apply_authoritative_context = lambda *_args, **_kwargs: None
    context.context_collections = lambda _scene: []
    context.validate_context = lambda *_args, **_kwargs: None
    sys.modules[context.__name__] = context

    loaded = {}
    for name in ("cache", "diagnostics", "plugin_http", "revocation", "recovery"):
        spec = importlib.util.spec_from_file_location(f"{package_name}.{name}", root / f"{name}.py")
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        assert spec.loader is not None
        spec.loader.exec_module(module)
        loaded[name] = module
    return loaded["revocation"], loaded["recovery"], loaded["plugin_http"], scene


def _drain(results):
    items = []
    while not results.empty():
        items.append(results.get_nowait())
    return items


class _Reply:
    def __init__(self, status, body):
        self.status = status
        self.body = body

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def getcode(self):
        return self.status

    def read(self, _size=-1):
        return self.body


@contextlib.contextmanager
def _plugin_urlopen(plugin_http, body):
    """Answer per port: port 1 breaks the HTTP exchange with each malformed reply in turn."""
    calls = []
    errors = (http.client.IncompleteRead(b""), http.client.BadStatusLine(""))
    state = {"error": errors[0]}

    def urlopen(request, timeout=0):
        port = int(request.full_url.split("//", 1)[1].split("/", 1)[0].split(":")[1])
        calls.append(port)
        if port == 1:
            raise state["error"]
        return _Reply(200, body)

    original = plugin_http.urllib.request.urlopen
    plugin_http.urllib.request.urlopen = urlopen
    try:
        yield calls, errors, state
    finally:
        plugin_http.urllib.request.urlopen = original


def revocation_survives_unexpected_failures(revocation, plugin_http) -> None:
    recorded = []
    revocation.record_failure = lambda **kwargs: recorded.append(kwargs)
    record = {"contextId": "context", "importId": "import", "capability": "capability"}

    # A malformed HTTP reply is a failed attempt, not a crash, and the next
    # candidate port is still tried. This goes through the real post_json.
    with _plugin_urlopen(plugin_http, b'{"ok": true}') as (calls, errors, state):
        for error in errors:
            state["error"] = error
            calls.clear()
            assert revocation._send({**record, "ports": [1, 2]}) is True and calls == [1, 2], type(error)
            calls.clear()
            assert revocation._send({**record, "ports": [1, 1]}) is False and calls == [1, 1], type(error)

    # An unexpected error on one record neither stops the remaining records
    # nor prevents the result that clears _worker_running.
    good = {"contextId": "good", "importId": "good-import", "capability": "capability", "ports": [1]}
    bad = {"contextId": "bad", "importId": "bad-import", "capability": "capability", "ports": [1]}

    def send(item):
        if item is bad:
            raise RuntimeError("unexpected failure")
        return True

    revocation._send = send
    _drain(revocation._results)
    revocation._generation = 7
    revocation._worker_running = True
    worker = threading.Thread(target=revocation._worker, args=(7, [bad, good]))
    worker.start()
    worker.join(5)
    assert not worker.is_alive()
    assert _drain(revocation._results) == [(7, [("good", "good-import")])]
    assert [item["code"] for item in recorded] == ["context_revocation_failed"]

    # The result is posted even when the worker itself is being torn down.
    revocation._send = lambda _record: (_ for _ in ()).throw(SystemExit())
    try:
        revocation._worker(7, [bad])
    except SystemExit:
        pass
    assert _drain(revocation._results) == [(7, [])]

    # With the result delivered, polling releases the worker flag so the next
    # schedule_revocations() call can start a new worker.
    revocation._results.put((7, []))
    revocation._worker_running = True
    assert revocation._poll_results() is None
    assert revocation._worker_running is False
    print("[PASS] revocation worker survives unexpected failures and always reports back")


def recovery_survives_unexpected_failures(recovery, plugin_http, scene) -> None:
    recorded = []
    recovery.record_failure = lambda **kwargs: recorded.append(kwargs)
    payload = {"contextId": "second"}

    reply = b'{"ok": true, "context": {"contextId": "second"}}'
    with _plugin_urlopen(plugin_http, reply) as (calls, errors, state):
        for error in errors:
            state["error"] = error
            calls.clear()
            assert recovery._request_reattach("context", "import", "capability", [1, 2]) == payload, type(error)
            assert calls == [1, 2], type(error)
            calls.clear()
            assert recovery._request_reattach("context", "import", "capability", [1, 1]) is None, type(error)
            assert calls == [1, 1], type(error)

    def request(context_id, *_args):
        if context_id == "first":
            raise RuntimeError("unexpected failure")
        return payload

    recovery._request_reattach = request
    _drain(recovery._recovery_results)
    recovery._recovery_generation = 3
    recovery._recovery_counts.clear()
    recovery._recovery_counts[3] = [0, 2]
    requests = [("first", "i", "c", [1]), ("second", "i", "c", [1])]
    worker = threading.Thread(target=recovery._recover_worker, args=(3, requests))
    worker.start()
    worker.join(5)
    assert not worker.is_alive()
    assert _drain(recovery._recovery_results) == [
        ("result", 3, "first", None),
        ("result", 3, "second", payload),
        ("done", 3, "", None),
    ]
    assert [item["code"] for item in recorded] == ["context_reattach_failed"]

    # "done" is posted even when the worker is being torn down.
    recovery._request_reattach = lambda *_args: (_ for _ in ()).throw(SystemExit())
    try:
        recovery._recover_worker(3, requests)
    except SystemExit:
        pass
    assert _drain(recovery._recovery_results) == [("done", 3, "", None)]

    # Polling the messages of a run whose first request failed reaches "done":
    # it reports the failure, releases the generation's counters and stops the
    # timer (returns None) instead of polling forever.
    for message in (
        ("result", 3, "first", None),
        ("result", 3, "second", payload),
        ("done", 3, "", None),
    ):
        recovery._recovery_results.put(message)
    recovery._recovery_counts[3] = [0, 2]
    assert recovery._poll_recovery_results() is None
    assert 3 not in recovery._recovery_counts
    assert "could not reconnect" in scene.xiv_ie_instant_edit_props.last_status
    print("[PASS] recovery worker survives unexpected failures and always reports back")


def run() -> None:
    revocation, recovery, plugin_http, scene = _load_modules()
    revocation_survives_unexpected_failures(revocation, plugin_http)
    recovery_survives_unexpected_failures(recovery, plugin_http, scene)


if __name__ == "__main__":
    run()
