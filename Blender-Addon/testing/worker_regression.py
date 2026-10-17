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
    context.ContextSceneError = type("ContextSceneError", (context.ContextValidationError,), {})
    context._value = lambda source, name, default="": source.get(name, default)
    context.reattach_context = lambda *_args, **_kwargs: None
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

    original = plugin_http.urlopen
    plugin_http.urlopen = urlopen
    try:
        yield calls, errors, state
    finally:
        plugin_http.urlopen = original


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


def plugin_calls_bypass_proxies(plugin_http) -> None:
    """A proxy from HTTP_PROXY (or the Windows settings) never sees a call to the loopback plugin."""
    from http.server import BaseHTTPRequestHandler, HTTPServer
    import os
    import socket

    hits = []

    def handler(label):
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length", 0)))
                hits.append((label, self.path))
                body = b'{"ok": true}'
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_args):
                pass
        return Handler

    plugin = HTTPServer(("127.0.0.1", 0), handler("plugin"))
    proxy = HTTPServer(("127.0.0.1", 0), handler("proxy"))
    for server in (plugin, proxy):
        threading.Thread(target=server.serve_forever, daemon=True).start()
    saved = {name: os.environ.get(name) for name in ("HTTP_PROXY", "http_proxy", "NO_PROXY", "no_proxy")}
    try:
        for name in saved:
            os.environ.pop(name, None)
        os.environ["HTTP_PROXY"] = f"http://127.0.0.1:{proxy.server_address[1]}"
        status, body = plugin_http.post_json(
            plugin.server_address[1], "/context/reattach", {"capability": "secret"},
            timeout=5, max_response_size=1024)
        assert status == 200 and body == b'{"ok": true}', (status, body)
        assert hits == [("plugin", "/context/reattach")], hits
    finally:
        for name, value in saved.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
        for server in (plugin, proxy):
            server.shutdown()
            server.server_close()

    # A closed port is told apart from a lost reply: the request never reached the plugin.
    # Windows takes about two seconds to refuse a loopback connection, so allow longer.
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        closed_port = probe.getsockname()[1]
    try:
        plugin_http.post_json(closed_port, "/export", {}, timeout=10, max_response_size=1024)
    except OSError as error:
        assert plugin_http.connection_refused(error), repr(error)
    else:
        raise AssertionError("a closed port answered")
    assert not plugin_http.connection_refused(TimeoutError("timed out"))
    print("[PASS] plugin calls go straight to loopback, past any configured proxy")


def recovery_keeps_the_current_context(recovery) -> None:
    """Recovering a file reattaches every context in it; only the scene's own one updates its record."""
    props = types.SimpleNamespace(
        context_id="current", game_path="current.mdl", object_index=7, context_schema="",
        context_version=0, plugin_instance_id="old", capability="old", managed_destination="", last_status="")
    scene = types.SimpleNamespace(xiv_ie_instant_edit_props=props)

    def answer(context_id):
        return {"contextId": context_id, "sourceGamePath": f"{context_id}.mdl", "objectIndex": 3,
                "schema": "instant-edit.context", "version": 3, "pluginInstanceId": "new",
                "capability": f"{context_id}-capability", "managedDestination": "D:/Mod"}

    recovery._update_scene_properties(answer("other"), scene)
    assert (props.context_id, props.capability, props.game_path) == ("current", "old", "current.mdl"), vars(props)
    recovery._update_scene_properties(answer("current"), scene)
    assert (props.plugin_instance_id, props.capability) == ("new", "current-capability"), vars(props)
    assert props.object_index == 7, "an actor index from an earlier game session was taken on"
    props.context_id = ""
    recovery._update_scene_properties(answer("other"), scene)
    assert props.context_id == "other", vars(props)
    print("[PASS] recovery updates only the scene's current context and never a stale actor index")


def revocation_queue_is_durable_and_self_healing(revocation, plugin_http, temp: Path) -> None:
    import json

    cache = sys.modules[revocation.__name__.rsplit(".", 1)[0] + ".cache"]
    recorded = []
    original_record_failure = revocation.record_failure
    revocation.record_failure = lambda **kwargs: recorded.append(kwargs["code"])

    def queue_file(path, records):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"schema": revocation.SCHEMA, "version": revocation.VERSION,
                                    "revocations": records}), encoding="utf-8")

    first = {"contextId": "first", "importId": "first-import", "capability": "c", "ports": [1]}
    second = {"contextId": "second", "importId": "second-import", "capability": "c", "ports": [1]}
    try:
        # The queue lives in the plugin's config folder, not in the cache (by default in %TEMP%),
        # and a queue an older add-on left in the cache root moves over.
        current, legacy = revocation._queue_path(), revocation._legacy_queue_path()
        assert current.parent == cache.diagnostics_root().parent and temp in current.parents, current
        assert legacy.parent == cache.cache_root(), legacy
        queue_file(current, [first])
        queue_file(legacy, [first, second])
        with revocation._lock:
            assert revocation._load_locked() == [first, second]
        assert not legacy.exists()
        assert revocation._read_records(current) == [first, second]

        # A damaged queue is set aside instead of refusing every later clear and revocation.
        current.write_text("{", encoding="utf-8")
        with revocation._lock:
            assert revocation._load_locked() == []
        assert not current.exists() and list(current.parent.glob(f"{current.name}.*.damaged"))
        assert recorded == ["revocation_queue_damaged"], recorded
        assert revocation.queue_context_revocations([{
            "context_id": "third", "import_id": "third-import", "capability": "c", "callback_port": 42428,
        }]) == 1
        # A damaged legacy queue is deleted: a stray file would stop an unmarked cache being reclaimed.
        legacy.write_text("not json", encoding="utf-8")
        with revocation._lock:
            assert [record["contextId"] for record in revocation._load_locked()] == ["third"]
        assert not legacy.exists() and not list(legacy.parent.glob("*.damaged"))
    finally:
        revocation.record_failure = original_record_failure

    # A revocation the plugin refuses for good is done; one it couldn't handle yet is retried.
    def answering(status, code):
        def urlopen(_request, timeout=0):
            return _Reply(status, json.dumps({"ok": False, "code": code}).encode("utf-8"))
        return urlopen

    original = plugin_http.urlopen
    try:
        for status, code, done in ((410, "stale_context", True), (401, "invalid_capability", True),
                                   (500, "internal_error", False), (404, "endpoint_not_found", False)):
            plugin_http.urlopen = answering(status, code)
            assert revocation._send({**first, "ports": [42428]}) is done, (status, code)
    finally:
        plugin_http.urlopen = original
    print("[PASS] the revocation queue moves out of the cache, survives damage and drops final refusals")


def run() -> None:
    import os
    import tempfile

    # Queues and diagnostic reports go to a throwaway APPDATA and cache, never the real ones.
    with tempfile.TemporaryDirectory(prefix="xiv-ie-worker-") as folder:
        temp = Path(folder).resolve()
        os.environ["APPDATA"] = str(temp / "AppData")
        revocation, recovery, plugin_http, scene = _load_modules()
        sys.modules[revocation.__name__.rsplit(".", 1)[0] + ".cache"]._base_directory = temp / "Temp"
        recovery_keeps_the_current_context(recovery)
        plugin_calls_bypass_proxies(plugin_http)
        revocation_queue_is_durable_and_self_healing(revocation, plugin_http, temp)
        revocation_survives_unexpected_failures(revocation, plugin_http)
        recovery_survives_unexpected_failures(recovery, plugin_http, scene)


if __name__ == "__main__":
    run()
