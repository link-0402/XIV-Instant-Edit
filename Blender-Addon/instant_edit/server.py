# Modified for XIV Instant Edit, 2026.
import json
import re
import socket
import threading
import tomllib
import uuid
from pathlib import Path

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from queue       import Empty, Full, Queue

import bpy

from .context import is_safe_game_model_path
from .cache import CacheStagingError
from .diagnostics import BridgeRequestError, record_failure, sanitize_text
from .plugin_http import post_json


MAX_IMPORT_BODY_SIZE = 1024 * 1024
MAX_IMPORT_QUEUE_SIZE = 32
REQUEST_TIMEOUT_SECONDS = 5
IMPORT_OPTIONS_CAPABILITY = "instant-edit.import-options.v1"
MATERIAL_PREVIEW_CAPABILITY = "instant-edit.material-preview.v1"
CACHE_HANDOFF_CAPABILITY = "instant-edit.cache-handoff.v1"
VANILLA_CONTEXT_CAPABILITY = "instant-edit.vanilla-context.v1"
STRUCTURED_ERRORS_CAPABILITY = "instant-edit.structured-errors.v1"
IMPORT_STATUS_CAPABILITY = "instant-edit.import-status.v1"

_import_queue: Queue = Queue(maxsize=MAX_IMPORT_QUEUE_SIZE)
_server              = None
_thread               = None
_port                 = 42424
_server_error         = ""


def _load_addon_version(manifest_path: Path | None = None) -> str | None:
    """Read the installed extension version from its manifest."""
    manifest_path = manifest_path or Path(__file__).resolve().parents[1] / "blender_manifest.toml"
    try:
        with manifest_path.open("rb") as manifest_file:
            version = tomllib.load(manifest_file).get("version")
    except (OSError, tomllib.TOMLDecodeError):
        return None
    return version.strip() if isinstance(version, str) and version.strip() else None


ADDON_VERSION = _load_addon_version()


def _status_payload() -> dict:
    return {
        "ok": True,
        "ready": True,
        "addon": "XIV Instant Edit",
        "addonId": "xiv_instant_edit",
        "addonVersion": ADDON_VERSION,
        "capabilities": [
            IMPORT_OPTIONS_CAPABILITY,
            MATERIAL_PREVIEW_CAPABILITY,
            CACHE_HANDOFF_CAPABILITY,
            VANILLA_CONTEXT_CAPABILITY,
            STRUCTURED_ERRORS_CAPABILITY,
            IMPORT_STATUS_CAPABILITY,
        ],
    }


def _string(data: dict, *names: str, required: bool = False, max_length: int = 1024) -> str:
    value = next((data.get(name) for name in names if name in data), "")
    if value is None and not required:
        return ""
    if not isinstance(value, str) or len(value) > max_length or (required and not value):
        label = names[0]
        raise BridgeRequestError(
            "request_validation",
            f"invalid_{_snake_case(label)}",
            f"{label} must be a non-empty string" if required else f"{label} must be a string",
            "Update both XIV Instant Edit components and retry the import.",
        )
    return value


def _snake_case(value: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", value).lower()


def _normalise_import_options(value) -> dict:
    """Validate and normalize optional scene setup requested by the plugin."""
    if value is None:
        return {
            "armatureMode": "generated",
            "targetObject": "Skeleton",
            "applyTexturesAndMaterials": False,
            "excludeBodyAndGeneralMaterials": False,
        }
    if not isinstance(value, dict):
        raise BridgeRequestError(
            "request_validation", "invalid_import_options",
            "The import options payload is not an object.",
            "Reset the import options or update both XIV Instant Edit components.")

    mode = value.get("armatureMode", "generated")
    target = value.get("targetObject", "Skeleton")
    if not isinstance(mode, str) or mode not in {"generated", "existing"}:
        raise BridgeRequestError(
            "request_validation", "invalid_armature_mode",
            "The requested armature mode is invalid.",
            "Choose Generated or Existing Skeleton and retry.")
    if not isinstance(target, str) or not target.strip() or len(target) > 128:
        raise BridgeRequestError(
            "request_validation", "invalid_armature_target",
            "The target armature name is empty or longer than 128 characters.",
            "Choose an existing Blender armature with a shorter name.")
    apply_preview = value.get("applyTexturesAndMaterials", False)
    if not isinstance(apply_preview, bool):
        raise BridgeRequestError(
            "request_validation", "invalid_material_preview_option",
            "The material-preview option is not a boolean.",
            "Reset the import options or update both XIV Instant Edit components.")
    exclude_body = value.get("excludeBodyAndGeneralMaterials", False)
    if not isinstance(exclude_body, bool):
        raise BridgeRequestError(
            "request_validation", "invalid_material_exclusion_option",
            "The body-material exclusion option is not a boolean.",
            "Reset the import options or update both XIV Instant Edit components.")
    if exclude_body and not apply_preview:
        raise BridgeRequestError(
            "request_validation", "material_exclusion_requires_preview",
            "Body and general materials can only be excluded when material previews are enabled.",
            "Enable material previews or disable the exclusion option.")
    return {
        "armatureMode": mode,
        "targetObject": target.strip(),
        "applyTexturesAndMaterials": apply_preview,
        "excludeBodyAndGeneralMaterials": exclude_body,
    }


def _failure(
    status: int | None,
    operation: str,
    stage: str,
    code: str,
    cause: str,
    remedy: str,
    *,
    endpoint: str,
    exception: BaseException | None = None,
    metadata: dict | None = None,
) -> dict:
    payload = record_failure(
        component="blender_addon",
        operation=operation,
        stage=stage,
        code=code,
        cause=cause,
        remedy=remedy,
        endpoint=endpoint,
        http_status=status,
        exception=exception,
        metadata={"addonVersion": ADDON_VERSION or "unknown", **(metadata or {})},
    )
    print(
        "XIV Instant Edit bridge failure "
        f"{payload['diagnosticId'][:8]}: {operation}/{stage}/{code}: {payload['cause']}"
    )
    return payload


def _send_import_failure_callback(data: dict, failure: dict) -> None:
    port = data.get("callbackPort", 0)
    if not isinstance(port, int) or not 1 <= port <= 65535:
        return
    payload = {
        "schema": "instant-edit.import-status",
        "version": 1,
        "pluginInstanceId": data.get("pluginInstanceId", ""),
        "contextId": data.get("contextId", ""),
        "importId": data.get("importId", ""),
        "capability": data.get("capability", ""),
        "status": "failed",
        "component": failure.get("component", "blender_addon"),
        "operation": failure.get("operation", "import"),
        "stage": failure.get("stage", "import_processing"),
        "code": failure.get("code", "import_processing_failed"),
        "cause": failure.get("cause", "The queued Blender import failed."),
        "remedy": failure.get("remedy", "Review the Blender diagnostic report and retry."),
        "diagnosticId": failure.get("diagnosticId", ""),
    }
    try:
        status, body = post_json(
            port, "/import/status", payload,
            timeout=2, max_response_size=64 * 1024)
        if not 200 <= status < 300:
            print(
                "XIV Instant Edit: import failure callback was rejected "
                f"with HTTP {status}: {sanitize_text(body.decode('utf-8', errors='replace'))}"
            )
    except Exception as error:
        print(f"XIV Instant Edit: could not deliver import failure callback: {sanitize_text(error)}")


def _notify_import_failure(data: dict, failure: dict) -> None:
    threading.Thread(
        target=_send_import_failure_callback,
        args=(dict(data), dict(failure)),
        name="XIV Instant Edit import failure callback",
        daemon=True,
    ).start()


def _request_metadata(data: dict | None) -> dict:
    if not isinstance(data, dict):
        return {"pluginVersion": "unknown"}
    options = data.get("importOptions")
    return {
        "pluginVersion": data.get("pluginVersion", "unknown"),
        "protocolSchema": data.get("schema", "unknown"),
        "protocolVersion": data.get("version", "unknown"),
        "importId": data.get("importId", "unknown"),
        "sourceKind": data.get("sourceKind", "unknown"),
        "objectIndex": data.get("objectIndex", "unknown"),
        "materialPreviewRequested": (
            options.get("applyTexturesAndMaterials", False)
            if isinstance(options, dict) else False
        ),
    }


class _ImportHandler(BaseHTTPRequestHandler):

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(REQUEST_TIMEOUT_SECONDS)

    def do_GET(self) -> None:
        if self.path.rstrip("/") == "/status":
            self._respond(200, _status_payload())
        else:
            self._respond(404, _failure(
                404, "http_request", "routing", "endpoint_not_found",
                "The requested Blender bridge endpoint does not exist.",
                "Update both XIV Instant Edit components and verify the configured Blender port.",
                endpoint=self.path,
            ))

    def do_POST(self) -> None:
        if self.path.rstrip("/") != "/import":
            self._respond(404, _failure(
                404, "http_request", "routing", "endpoint_not_found",
                "The requested Blender bridge endpoint does not exist.",
                "Update both XIV Instant Edit components and verify the configured Blender port.",
                endpoint=self.path,
            ))
            return

        data = None
        try:
            length_header = self.headers.get("Content-Length")
            if length_header is None:
                self._respond(411, _failure(
                    411, "import", "request_receipt", "content_length_missing",
                    "The import request did not include a Content-Length header.",
                    "Update both XIV Instant Edit components and retry.",
                    endpoint=self.path,
                ))
                return

            try:
                length = int(length_header)
            except ValueError as error:
                self._respond(400, _failure(
                    400, "import", "request_receipt", "content_length_invalid",
                    "The import request contains an invalid Content-Length header.",
                    "Update both XIV Instant Edit components and retry.",
                    endpoint=self.path, exception=error,
                ))
                return
            if length < 0:
                self._respond(400, _failure(
                    400, "import", "request_receipt", "content_length_invalid",
                    "The import request contains a negative Content-Length value.",
                    "Update both XIV Instant Edit components and retry.",
                    endpoint=self.path,
                ))
                return
            if length > MAX_IMPORT_BODY_SIZE:
                self._respond(413, _failure(
                    413, "import", "request_receipt", "request_body_too_large",
                    "The import request metadata exceeds the 1 MiB limit.",
                    "Disable material previews or update both XIV Instant Edit components.",
                    endpoint=self.path,
                ))
                return

            body = self.rfile.read(length)
            if len(body) != length:
                self._respond(400, _failure(
                    400, "import", "request_receipt", "request_body_incomplete",
                    "The connection ended before Blender received the complete import request.",
                    "Retry the import and check local security software if it happens repeatedly.",
                    endpoint=self.path,
                ))
                return

            try:
                data = json.loads(body)
            except UnicodeDecodeError as error:
                self._respond(400, _failure(
                    400, "import", "request_parsing", "invalid_utf8",
                    "The import request body is not valid UTF-8.",
                    "Update both XIV Instant Edit components and retry.",
                    endpoint=self.path, exception=error,
                ))
                return
            except json.JSONDecodeError as error:
                self._respond(400, _failure(
                    400, "import", "request_parsing", "invalid_json",
                    "The import request body is not valid JSON.",
                    "Update both XIV Instant Edit components and retry.",
                    endpoint=self.path, exception=error,
                ))
                return
            data = self._validate_import(data)
            from .cache import stage_import

            data = stage_import(data)
        except (socket.timeout, TimeoutError):
            self._respond(408, _failure(
                408, "import", "request_receipt", "request_body_timeout",
                "Blender timed out while receiving the import request.",
                "Retry the import and check for heavy system load or local filtering software.",
                endpoint=self.path,
            ))
            return
        except (BridgeRequestError, CacheStagingError) as error:
            self._respond(400, _failure(
                400, "import", error.stage, error.code, error.cause, error.remedy,
                endpoint=self.path, exception=error,
                metadata=_request_metadata(data),
            ))
            return
        except Exception as e:
            self._respond(400, _failure(
                400, "import", "file_staging", "handoff_staging_failed",
                "Blender could not validate or copy the import handoff.",
                "Verify the configured cache directory is writable and retry the import.",
                endpoint=self.path, exception=e,
                metadata=_request_metadata(data),
            ))
            return

        try:
            _import_queue.put_nowait(data)
        except Full:
            cache_job = data.get("cacheJobDirectory", "")
            if cache_job:
                from .cache import remove_job
                remove_job(cache_job)
            self._respond(503, _failure(
                503, "import", "queueing", "import_queue_full",
                "Blender's XIV Instant Edit import queue is full.",
                "Wait for current imports to finish, then retry.",
                endpoint=self.path,
                metadata=_request_metadata(data),
            ))
            return
        self._respond(200, {"ok": True, "queued": True, "cached": True})

    @staticmethod
    def _validate_import(data) -> dict:
        if not isinstance(data, dict):
            raise BridgeRequestError(
                "request_validation", "request_not_object",
                "The import request JSON root is not an object.",
                "Update both XIV Instant Edit components and retry.")

        import_options = _normalise_import_options(data.get("importOptions"))
        if data.get("schema") != "instant-edit.context":
            raise BridgeRequestError(
                "request_validation", "unsupported_schema",
                "The import request uses an unsupported schema.",
                "Install matching versions of the Dalamud plugin and Blender add-on.")
        version = data.get("version")
        if isinstance(version, bool) or version not in {1, 2}:
            raise BridgeRequestError(
                "request_validation", "unsupported_version",
                "The import request uses an unsupported protocol version.",
                "Install matching versions of the Dalamud plugin and Blender add-on.")

        plugin_instance_id = _string(data, "pluginInstanceId", required=True, max_length=256)
        plugin_version = _string(data, "pluginVersion", max_length=32)
        context_id = _string(data, "contextId", required=True, max_length=256)
        import_id = _string(data, "importId", required=True, max_length=256)
        capability = _string(data, "capability", required=True, max_length=1024)
        file_path = _string(data, "filePath", required=True, max_length=4096)
        source_game_path = _string(data, "sourceGamePath", required=True, max_length=4096)
        source_kind = _string(data, "sourceKind", max_length=16) or "mod"
        resolved_game_path = _string(data, "resolvedGamePath", max_length=4096) or source_game_path
        destination_state = _string(data, "destinationState", max_length=32) or "ready"
        managed_destination = _string(data, "managedDestination", max_length=4096)
        target_file_path = _string(data, "targetFilePath", max_length=4096)
        source_mod_directory = _string(data, "sourceModDirectory", max_length=256)
        source_mod_name = _string(data, "sourceModName", max_length=512)
        source_mod_root_path = _string(data, "sourceModRootPath", max_length=4096)
        target_relative_path = _string(data, "targetRelativePath", max_length=4096)
        target_collection_id = _string(data, "targetCollectionId", max_length=64)
        target_collection_name = _string(data, "targetCollectionName", max_length=512)
        preview_manifest_path = _string(data, "previewManifestPath", max_length=4096)
        display_name = _string(data, "displayName", max_length=255)
        if import_options["applyTexturesAndMaterials"] and not preview_manifest_path:
            raise BridgeRequestError(
                "request_validation", "preview_manifest_required",
                "Material previews were requested without a preview manifest.",
                "Regenerate the import preview or disable material previews and retry.")
        if source_kind not in {"mod", "game"} or destination_state not in {"ready", "new_mod_required"}:
            raise BridgeRequestError(
                "request_validation", "invalid_context_state",
                "The import source or destination state is invalid.",
                "Refresh the model list and retry with matching plugin and add-on versions.")
        if version == 1 and (source_kind != "mod" or destination_state != "ready"):
            raise BridgeRequestError(
                "request_validation", "unsupported_context_state",
                "This protocol version cannot represent the requested import context.",
                "Update both XIV Instant Edit components and retry.")
        if source_kind == "game" and (
            not is_safe_game_model_path(source_game_path) or
            not is_safe_game_model_path(resolved_game_path)
        ):
            raise BridgeRequestError(
                "request_validation", "invalid_game_model_path",
                "The game-data import contains an invalid model path.",
                "Refresh the on-screen model list and select the model again.")
        if destination_state == "ready" and not all((managed_destination, target_file_path,
                                                       source_mod_directory, source_mod_name)):
            raise BridgeRequestError(
                "request_validation", "missing_penumbra_destination",
                "The import context is missing its Penumbra destination.",
                "Refresh the model list in the plugin and retry the import.")
        if destination_state == "new_mod_required" and (
            source_kind != "game" or any((managed_destination, target_file_path,
                                          source_mod_directory, source_mod_name,
                                          source_mod_root_path, target_relative_path))
        ):
            raise BridgeRequestError(
                "request_validation", "invalid_pending_destination",
                "A new game-data import contains unexpected Penumbra destination data.",
                "Update both XIV Instant Edit components and retry.")
        if target_collection_id:
            try:
                if uuid.UUID(target_collection_id).int == 0:
                    raise ValueError
            except (ValueError, AttributeError) as error:
                raise BridgeRequestError(
                    "request_validation", "invalid_collection_id",
                    "The target Penumbra collection identifier is invalid.",
                    "Refresh the model list and select the target again.") from error

        callback_port = data.get("callbackPort")
        if isinstance(callback_port, bool) or not isinstance(callback_port, int) or not 1 <= callback_port <= 65535:
            raise BridgeRequestError(
                "request_validation", "invalid_callback_port",
                "The plugin callback port is outside the valid range.",
                "Correct the listener port in XIV Instant Edit settings and retry.")
        object_index = data.get("objectIndex")
        if isinstance(object_index, bool) or not isinstance(object_index, int) or not 0 <= object_index <= 65535:
            raise BridgeRequestError(
                "request_validation", "invalid_object_index",
                "The selected game object index is outside the valid range.",
                "Refresh the on-screen model list and select the object again.")
        resource_manifest_version = data.get("resourceManifestVersion")
        if isinstance(resource_manifest_version, bool) or not isinstance(resource_manifest_version, int):
            raise BridgeRequestError(
                "request_validation", "invalid_resource_manifest_version",
                "The resource manifest version is invalid.",
                "Update both XIV Instant Edit components and retry.")
        resource_manifest_status = data.get("resourceManifestStatus")
        if resource_manifest_status not in {"capture_failed", "ready"}:
            raise BridgeRequestError(
                "request_validation", "invalid_resource_manifest_status",
                "The resource manifest status is invalid.",
                "Update both XIV Instant Edit components and retry.")
        expected_manifest_version = 2 if resource_manifest_status == "ready" else 0
        if resource_manifest_version != expected_manifest_version:
            raise BridgeRequestError(
                "request_validation", "resource_manifest_mismatch",
                "The resource manifest version and status do not agree.",
                "Update both XIV Instant Edit components and retry.")

        return {
            **data,
            "pluginInstanceId": plugin_instance_id,
            "pluginVersion": plugin_version,
            "contextId": context_id,
            "importId": import_id,
            "capability": capability,
            "filePath": file_path,
            "sourceGamePath": source_game_path,
            "sourceKind": source_kind,
            "resolvedGamePath": resolved_game_path,
            "destinationState": destination_state,
            "managedDestination": managed_destination,
            "targetFilePath": target_file_path,
            "sourceModDirectory": source_mod_directory,
            "sourceModName": source_mod_name,
            "sourceModRootPath": source_mod_root_path,
            "targetRelativePath": target_relative_path,
            "targetCollectionId": target_collection_id,
            "targetCollectionName": target_collection_name,
            "resourceManifestVersion": resource_manifest_version,
            "resourceManifestStatus": resource_manifest_status,
            "previewManifestPath": preview_manifest_path,
            "callbackPort": callback_port,
            "objectIndex": object_index,
            "name": display_name,
            "importOptions": import_options,
        }


    def _respond(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args) -> None:
        pass


def start_server(port: int = 42424) -> bool:
    """Starts the HTTP listener that receives import commands from the XIV Instant Edit plugin."""
    global _server, _thread, _port, _server_error

    if _server is not None:
        if port == _port:
            return True
        stop_server()

    try:
        server = ThreadingHTTPServer(("127.0.0.1", port), _ImportHandler)
    except OSError as e:
        _server = None
        _thread = None
        _server_error = str(e)
        _failure(
            500,
            "addon_startup",
            "listener_startup",
            "listener_unavailable",
            "Blender could not start the XIV Instant Edit listener.",
            "Close other applications using the configured port or choose a different Blender listen port.",
            endpoint="/startup",
            exception=e,
        )
        print(f"XIV Instant Edit: could not listen on port {port}: {_server_error}")
        return False

    _port   = port
    _server = server
    _server_error = ""
    _thread = threading.Thread(target=_server.serve_forever, daemon=True)
    _thread.start()
    print(f"XIV Instant Edit: listening on port {port}")
    return True


def set_server_port(port: int) -> bool:
    return start_server(port)


def get_server_error() -> str:
    return _server_error


def stop_server() -> None:
    global _server, _thread, _port

    if _server is not None:
        try:
            _server.shutdown()
            _server.server_close()
        except Exception:
            pass
        _server = None
        _thread = None
        _port   = 42424


def poll_import_queue() -> float:
    """Timer callback that runs pending imports on Blender's main thread."""
    try:
        while True:
            try:
                data = _import_queue.get_nowait()
            except Empty:
                break
            try:
                if bpy.context.mode != "OBJECT":
                    bpy.ops.object.mode_set(mode="OBJECT")

                result = bpy.ops.xiv_ie.instant_import(
                    "EXEC_DEFAULT",
                    file_path=data.get("filePath", ""),
                    object_index=int(data.get("objectIndex", -1)),
                    import_name=data.get("name", ""),
                    callback_port=data.get("callbackPort", 0),
                    schema=data.get("schema", ""),
                    version=int(data.get("version", 0)),
                    plugin_instance_id=data.get("pluginInstanceId", ""),
                    context_id=data.get("contextId", ""),
                    capability=data.get("capability", ""),
                    source_game_path=data.get("sourceGamePath", ""),
                    source_kind=data.get("sourceKind", "mod"),
                    resolved_game_path=data.get("resolvedGamePath", data.get("sourceGamePath", "")),
                    destination_state=data.get("destinationState", "ready"),
                    managed_destination=data.get("managedDestination", ""),
                    target_file_path=data.get("targetFilePath", ""),
                    source_mod_directory=data.get("sourceModDirectory", ""),
                    source_mod_name=data.get("sourceModName", ""),
                    source_mod_root_path=data.get("sourceModRootPath", ""),
                    target_relative_path=data.get("targetRelativePath", ""),
                    target_collection_id=data.get("targetCollectionId", ""),
                    target_collection_name=data.get("targetCollectionName", ""),
                    resource_manifest_version=int(data.get("resourceManifestVersion", 0)),
                    resource_manifest_status=data.get("resourceManifestStatus", "capture_failed"),
                    import_id=data.get("importId", ""),
                    armature_mode=data.get("importOptions", {}).get("armatureMode", "generated"),
                    armature_target=data.get("importOptions", {}).get("targetObject", "Skeleton"),
                    apply_textures_and_materials=data.get("importOptions", {}).get("applyTexturesAndMaterials", False),
                    preview_manifest_path=data.get("previewManifestPath", ""),
                    cache_job_directory=data.get("cacheJobDirectory", ""),
                )
                if result != {"FINISHED"}:
                    props = getattr(bpy.context.scene, "xiv_ie_instant_edit_props", None)
                    reported = getattr(props, "last_status", "") if props is not None else ""
                    cause = reported.removeprefix("Import failed:").strip() or \
                        "Blender cancelled the queued model import."
                    failure = _failure(
                        None, "import", "import_processing", "import_cancelled",
                        cause,
                        "Correct the reported Blender scene or model issue, then retry the import.",
                        endpoint="/import/status",
                        metadata={
                            **_request_metadata(data),
                            "operatorResult": ",".join(sorted(result)),
                        },
                    )
                    _notify_import_failure(data, failure)
            except Exception as e:
                failure = _failure(
                    None, "import", "import_processing", "import_processing_failed",
                    "Blender encountered an unexpected error while processing the queued import.",
                    "Review the diagnostic report, correct the model or scene issue, and retry.",
                    endpoint="/import/status", exception=e,
                    metadata=_request_metadata(data),
                )
                _notify_import_failure(data, failure)
    except Exception as e:
        _failure(
            None, "import", "queueing", "import_queue_processing_failed",
            "Blender could not process the XIV Instant Edit import queue.",
            "Restart Blender and retry the import.",
            endpoint="/import/status", exception=e,
            metadata={"exceptionMessage": sanitize_text(e)},
        )

    return 0.5
