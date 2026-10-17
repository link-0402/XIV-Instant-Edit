"""Reconnect persisted XIV Instant Edit scene contexts to the Dalamud plugin."""

import json
import queue
import threading
from urllib.error import URLError

import bpy

from .context import (
    ContextSceneError,
    ContextValidationError,
    _value,
    context_collections,
    reattach_context,
)
from .plugin_http import PluginResponseTooLarge, candidate_ports, post_json
from .diagnostics import record_failure, record_protocol_failure, record_remote_failure


MAX_RESPONSE_SIZE = 64 * 1024
REQUEST_TIMEOUT_SECONDS = 2
_recovery_scheduled = False
_recovery_generation = 0
_recovery_results = queue.Queue()
_recovery_counts = {}


def reattach_collection(collection, scene=None) -> bool:
    """Reattach one saved collection and replace only its routing metadata."""
    context_id = _value(collection, "context_id", "")
    import_id = _value(collection, "import_id", "")
    capability = _value(collection, "capability", "")
    if not all(isinstance(value, str) and value for value in (context_id, import_id, capability)):
        return False

    payload = _request_reattach(context_id, import_id, capability, candidate_ports(collection))
    return apply_reattach(collection, payload, scene or bpy.context.scene)


def reattach_request(collection) -> tuple | None:
    """What reattaching ``collection`` asks the plugin, read on the main thread so the request
    itself can run on a worker (see request_reattach); None when the collection can't reattach."""
    values = (
        _value(collection, "context_id", ""),
        _value(collection, "import_id", ""),
        _value(collection, "capability", ""),
    )
    if not all(isinstance(value, str) and value for value in values):
        return None
    return (*values, candidate_ports(collection))


def request_reattach(request: tuple | None) -> dict | None:
    """Ask the plugin for a context (see reattach_request). Safe on a worker thread."""
    return None if request is None else _request_reattach(*request)


def apply_reattach(collection, payload: dict | None, scene) -> bool:
    """Take on the plugin's reattach answer on the main thread; the collection is left as it was
    when the answer doesn't validate."""
    if payload is None:
        return False
    scene_problem = None
    try:
        reattach_context(collection, payload, scene)
    except ContextSceneError as error:
        # Reconnected: only the scene's objects need fixing, which export reports too.
        scene_problem = error
    except ContextValidationError:
        return False
    _update_scene_properties(payload, scene)
    props = getattr(scene, "xiv_ie_instant_edit_props", None)
    if props is not None:
        props.last_status = ("XIV Instant Edit context reconnected." if scene_problem is None
                             else f"XIV Instant Edit context reconnected; fix this before exporting: {scene_problem}")
    return True


def _request_reattach(
    context_id: str,
    import_id: str,
    capability: str,
    ports: list[int],
) -> dict | None:
    """Perform only HTTP/JSON work so this function is safe on a worker thread."""
    request_payload = {
        "schema": "instant-edit.reattach",
        "version": 1,
        "contextId": context_id,
        "importId": import_id,
        "capability": capability,
    }

    for port in ports:
        try:
            status, body = post_json(
                port, "/context/reattach", request_payload,
                timeout=REQUEST_TIMEOUT_SECONDS, max_response_size=MAX_RESPONSE_SIZE)
            if not 200 <= status < 300:
                record_remote_failure(
                    body, status, endpoint="/context/reattach",
                    default_operation="context_reattach")
                continue
            try:
                result = json.loads(body.decode("utf-8"))
            except (UnicodeError, json.JSONDecodeError):
                record_protocol_failure(
                    body, status, endpoint="/context/reattach",
                    operation="context_reattach")
                continue
            payload = result.get("context") if isinstance(result, dict) and result.get("ok") else None
            if not isinstance(payload, dict):
                record_protocol_failure(
                    body, status, endpoint="/context/reattach",
                    operation="context_reattach")
                continue
            return payload
        except PluginResponseTooLarge as error:
            record_protocol_failure(
                error.body,
                error.status,
                endpoint="/context/reattach",
                operation="context_reattach",
                code="response_too_large",
                cause="The Dalamud plugin returned a response larger than the bridge limit.",
            )
            continue
        except (URLError, TimeoutError, OSError, ValueError, UnicodeError):
            continue

    return None


def recover_saved_contexts() -> None:
    """Start recovery without blocking Blender's main/UI thread on HTTP timeouts."""
    global _recovery_generation
    scene = bpy.context.scene
    if scene is None:
        return

    collections = context_collections(scene)
    if not collections:
        return

    requests = [request for request in map(reattach_request, collections) if request is not None]
    if not requests:
        return

    _recovery_generation += 1
    generation = _recovery_generation
    _recovery_counts[generation] = [0, len(requests)]
    threading.Thread(
        target=_recover_worker,
        args=(generation, requests),
        name="InstantEditContextRecovery",
        daemon=True,
    ).start()
    if not bpy.app.timers.is_registered(_poll_recovery_results):
        bpy.app.timers.register(_poll_recovery_results, first_interval=0.1)


def _recover_worker(generation: int, requests: list[tuple]) -> None:
    try:
        for context_id, import_id, capability, ports in requests:
            try:
                payload = _request_reattach(context_id, import_id, capability, ports)
            except Exception as error:
                # This context stays disconnected; the others still get a try.
                payload = None
                record_failure(
                    component="blender_addon",
                    operation="context_recovery",
                    stage="reattach_request",
                    code="context_reattach_failed",
                    cause="Blender hit an unexpected error while reconnecting a saved context.",
                    remedy="Re-import the model if the context remains disconnected.",
                    endpoint="/context/reattach",
                    exception=error,
                )
            _recovery_results.put(("result", generation, context_id, payload))
    finally:
        # Always post "done". Without it the poll timer never stops and this
        # generation's entry in _recovery_counts is never released.
        _recovery_results.put(("done", generation, "", None))


def _poll_recovery_results():
    """Apply worker results through Blender's timer, which runs on the main thread."""
    while True:
        try:
            kind, generation, context_id, payload = _recovery_results.get_nowait()
        except queue.Empty:
            break
        if generation != _recovery_generation:
            _recovery_counts.pop(generation, None)
            continue
        if kind == "result":
            counts = _recovery_counts.get(generation)
            collection = next((item for item in context_collections(bpy.context.scene)
                               if _value(item, "context_id", "") == context_id), None)
            if payload is not None and collection is not None:
                if apply_reattach(collection, payload, bpy.context.scene) and counts is not None:
                    counts[0] += 1
        elif kind == "done":
            recovered, total = _recovery_counts.pop(generation, [0, 0])
            failed = total - recovered
            props = getattr(bpy.context.scene, "xiv_ie_instant_edit_props", None)
            if props is not None and failed:
                props.last_status = (
                    f"Recovered {recovered} XIV Instant Edit context(s); "
                    f"{failed} context(s) could not reconnect. Re-import if needed."
                )
            elif props is not None and recovered > 1:
                props.last_status = f"Reconnected {recovered} XIV Instant Edit contexts."
            return None
    return 0.1


def _update_scene_properties(payload: dict, scene) -> None:
    """Refresh the scene's record of its current context when the reattached one is that context.

    Recovering a saved file reattaches every context in it; whichever answered last must not
    become the current one. The plugin's objectIndex is the actor the model was imported from in
    an earlier game session, so it is not taken on.
    """
    props = getattr(scene, "xiv_ie_instant_edit_props", None)
    if props is None:
        return
    context_id = payload.get("contextId", "")
    if props.context_id and props.context_id != context_id:
        return
    props.game_path = payload.get("sourceGamePath", "")
    props.context_id = context_id
    props.context_schema = payload.get("schema", "")
    props.context_version = payload.get("version", 0)
    props.plugin_instance_id = payload.get("pluginInstanceId", "")
    props.capability = payload.get("capability", "")
    props.managed_destination = payload.get("managedDestination", "")


def _run_scheduled_recovery():
    global _recovery_scheduled
    _recovery_scheduled = False
    try:
        recover_saved_contexts()
    except Exception as error:
        record_failure(
            component="blender_addon",
            operation="context_recovery",
            stage="recovery_startup",
            code="context_recovery_failed",
            cause="Blender could not start recovery of saved XIV Instant Edit contexts.",
            remedy="Restart Blender and re-import any context that remains disconnected.",
            endpoint="/context/reattach",
            exception=error,
        )
        print(f"XIV Instant Edit: context recovery failed: {error}")
    return None


def schedule_recovery() -> None:
    global _recovery_scheduled
    if _recovery_scheduled and bpy.app.timers.is_registered(_run_scheduled_recovery):
        return
    _recovery_scheduled = True
    try:
        bpy.app.timers.register(_run_scheduled_recovery, first_interval=1.0)
    except Exception as error:
        _recovery_scheduled = False
        record_failure(
            component="blender_addon",
            operation="context_recovery",
            stage="recovery_scheduling",
            code="recovery_timer_unavailable",
            cause="Blender could not schedule saved-context recovery.",
            remedy="Restart Blender and re-import any context that remains disconnected.",
            endpoint="/context/reattach",
            exception=error,
        )


def cancel_recovery() -> None:
    global _recovery_scheduled, _recovery_generation
    _recovery_generation += 1
    _recovery_counts.clear()
    try:
        bpy.app.timers.unregister(_run_scheduled_recovery)
    except Exception:
        pass
    try:
        bpy.app.timers.unregister(_poll_recovery_results)
    except Exception:
        pass
    _recovery_scheduled = False
