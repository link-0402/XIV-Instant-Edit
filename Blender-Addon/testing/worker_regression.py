"""Standalone regression coverage for the context revocation and recovery workers."""

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
    return loaded["revocation"], loaded["recovery"], scene


def _drain(results):
    items = []
    while not results.empty():
        items.append(results.get_nowait())
    return items


def revocation_survives_unexpected_failures(revocation) -> None:
    recorded = []
    revocation.record_failure = lambda **kwargs: recorded.append(kwargs)
    record = {"contextId": "context", "importId": "import", "capability": "capability"}

    # A truncated or malformed HTTP response is a failed attempt, not a crash,
    # and the next candidate port is still tried.
    for error in (http.client.IncompleteRead(b""), http.client.BadStatusLine("")):
        calls = []

        def post(port, *_args, **_kwargs):
            calls.append(port)
            if port == 1:
                raise error
            return 200, b'{"ok": true}'

        revocation.post_json = post
        assert revocation._send({**record, "ports": [1, 2]}) is True and calls == [1, 2], type(error)
        calls.clear()
        revocation.post_json = lambda *_args, **_kwargs: (_ for _ in ()).throw(error)
        assert revocation._send({**record, "ports": [1, 2]}) is False, type(error)

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


def recovery_survives_unexpected_failures(recovery, scene) -> None:
    recorded = []
    recovery.record_failure = lambda **kwargs: recorded.append(kwargs)
    payload = {"contextId": "second"}

    for error in (http.client.IncompleteRead(b""), http.client.BadStatusLine("")):
        recovery.post_json = lambda *_args, **_kwargs: (_ for _ in ()).throw(error)
        assert recovery._request_reattach("context", "import", "capability", [1, 2]) is None, type(error)

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
    revocation, recovery, scene = _load_modules()
    revocation_survives_unexpected_failures(revocation)
    recovery_survives_unexpected_failures(recovery, scene)


if __name__ == "__main__":
    run()
