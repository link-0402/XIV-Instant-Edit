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
from .cache import CacheStagingError, STALE_SECONDS, cache_root
from .diagnostics import BridgeRequestError, record_failure, sanitize_text
from .plugin_http import post_json
from .validation import ValidationError, validate_string


MAX_IMPORT_BODY_SIZE = 1024 * 1024
MAX_IMPORT_QUEUE_SIZE = 32
REQUEST_TIMEOUT_SECONDS = 5
IMPORT_OPTIONS_CAPABILITY = "instant-edit.import-options.v1"
MATERIAL_PREVIEW_CAPABILITY = "instant-edit.material-preview.v1"
CACHE_HANDOFF_CAPABILITY = "instant-edit.cache-handoff.v1"
TEXTURE_CACHE_CAPABILITY = "instant-edit.texture-cache.v1"
CACHE_SETTINGS_CAPABILITY = "instant-edit.cache-settings.v1"
VANILLA_CONTEXT_CAPABILITY = "instant-edit.vanilla-context.v1"
STRUCTURED_ERRORS_CAPABILITY = "instant-edit.structured-errors.v1"
IMPORT_STATUS_CAPABILITY = "instant-edit.import-status.v1"
ANIMATION_IMPORT_CAPABILITY = "instant-edit.animation-import.v1"
# Imports may carry the model's game skeleton (see skeleton.py).
IMPORT_SKELETON_CAPABILITY = "instant-edit.import-skeleton.v1"
MAX_ANIMATION_QUEUE_SIZE = 4
# How long an animation request waits for Blender's main thread to key it, so the plugin
# can report the result. Blender busy with a modal tool keeps the request queued instead.
ANIMATION_APPLY_WAIT_SECONDS = 20

_import_queue: Queue = Queue(maxsize=MAX_IMPORT_QUEUE_SIZE)
_animation_queue: Queue = Queue(maxsize=MAX_ANIMATION_QUEUE_SIZE)
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
        "cacheRoot": str(cache_root()),
        "capabilities": [
            IMPORT_OPTIONS_CAPABILITY,
            MATERIAL_PREVIEW_CAPABILITY,
            CACHE_HANDOFF_CAPABILITY,
            TEXTURE_CACHE_CAPABILITY,
            CACHE_SETTINGS_CAPABILITY,
            VANILLA_CONTEXT_CAPABILITY,
            STRUCTURED_ERRORS_CAPABILITY,
            IMPORT_STATUS_CAPABILITY,
            ANIMATION_IMPORT_CAPABILITY,
            IMPORT_SKELETON_CAPABILITY,
        ],
    }


def _string(data: dict, *names: str, required: bool = False, max_length: int = 1024) -> str:
    value = next((data.get(name) for name in names if name in data), "")
    label = names[0]
    message = f"{label} must be a non-empty string" if required else f"{label} must be a string"
    try:
        return validate_string(value, message, max_length=max_length, allow_none=not required, require_non_empty=required)
    except ValidationError as error:
        raise BridgeRequestError(
            "request_validation",
            f"invalid_{_snake_case(label)}",
            str(error),
            "Update both XIV Instant Edit components and retry the import.",
        ) from error


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


def _stage_import_skeleton(data: dict) -> dict:
    """Write a staged import's skeleton into its cache job, where the queued import reads it."""
    from .cache import remove_job
    from .skeleton import stage_skeleton

    try:
        return stage_skeleton(data)
    except OSError as error:
        remove_job(data.get("cacheJobDirectory", ""))
        raise CacheStagingError(
            "file_staging", "skeleton_staging_failed",
            "Blender could not write the model's skeleton into its cache.",
            "Verify the configured cache directory is writable, then retry the import.") from error


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

    def _refuse_browser_request(self) -> bool:
        """Refuse what a web page could send (CSRF, DNS rebinding); the plugin is a local client."""
        host = (self.headers.get("Host") or "").strip().lower()
        hostname = host.rsplit(":", 1)[0] if ":" in host else host
        if (
            (not host or hostname in ("127.0.0.1", "localhost"))
            and self.headers.get("Origin") is None
            and self.headers.get("Sec-Fetch-Site") is None
        ):
            return False
        self._respond(403, {
            "ok": False,
            "code": "request_forbidden",
            "message": "The Blender bridge only accepts requests from local applications.",
        })
        return True

    def do_GET(self) -> None:
        if self._refuse_browser_request():
            return
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
        if self._refuse_browser_request():
            return
        endpoint = self.path.rstrip("/")
        if endpoint == "/animation":
            self._handle_animation()
            return
        is_cache_settings = endpoint == "/settings/cache"
        if endpoint != "/import" and not is_cache_settings:
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
            if is_cache_settings:
                data = self._validate_cache_settings(data)
                from .cache import STALE_SECONDS, clean_cache, configure_cache
                try:
                    root = configure_cache(data["cacheDirectory"], data["automaticCleanup"])
                except Exception as error:
                    raise BridgeRequestError(
                        "cache_configuration", "cache_configuration_failed",
                        "Blender could not apply the cache directory from the in-game plugin.",
                        "Set a writable local cache directory in XIV Instant Edit's in-game settings, then retry.") from error
                if data["automaticCleanup"]:
                    try:
                        clean_cache(STALE_SECONDS)
                    except Exception as error:
                        print(f"XIV Instant Edit: cache cleanup after synchronization failed: {sanitize_text(error)}")
                self._respond(200, {
                    "ok": True,
                    "cacheDirectory": str(data["cacheDirectory"]),
                    "cacheRoot": str(root),
                })
                return

            data = self._validate_import(data)
            from .cache import stage_import

            data = _stage_import_skeleton(stage_import(data))
        except (socket.timeout, TimeoutError):
            self._respond(408, _failure(
                408, "import", "request_receipt", "request_body_timeout",
                "Blender timed out while receiving the import request.",
                "Retry the import and check for heavy system load or local filtering software.",
                endpoint=self.path,
            ))
            return
        except (BridgeRequestError, CacheStagingError) as error:
            operation = "cache_settings" if is_cache_settings else "import"
            self._respond(400, _failure(
                400, operation, error.stage, error.code, error.cause, error.remedy,
                endpoint=self.path, exception=error,
                metadata=_request_metadata(data),
            ))
            return
        except Exception as e:
            operation = "cache_settings" if is_cache_settings else "import"
            self._respond(400, _failure(
                400, operation, "cache_configuration" if is_cache_settings else "file_staging",
                "cache_configuration_failed" if is_cache_settings else "handoff_staging_failed",
                "Blender could not apply the cache directory from the in-game plugin." if is_cache_settings else "Blender could not validate or copy the import handoff.",
                "Set a writable local cache directory in XIV Instant Edit's in-game settings, then retry." if is_cache_settings else "Verify the configured cache directory is writable and retry the import.",
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

    def _handle_animation(self) -> None:
        """Receive a take, have Blender's main thread key it, and report the result."""
        from .animation import MAX_BODY_SIZE, parse_take

        try:
            length_header = self.headers.get("Content-Length")
            try:
                length = int(length_header) if length_header is not None else -1
            except ValueError:
                length = -1
            if length < 0:
                status = 411 if length_header is None else 400
                self._respond(status, _failure(
                    status, "animation_import", "request_receipt", "content_length_invalid",
                    "The animation request has no valid Content-Length header.",
                    "Update both XIV Instant Edit components and retry.",
                    endpoint=self.path,
                ))
                return
            if length > MAX_BODY_SIZE:
                self._respond(413, _failure(
                    413, "animation_import", "request_receipt", "request_body_too_large",
                    f"The animation exceeds Blender's {MAX_BODY_SIZE // (1024 * 1024)} MiB limit.",
                    "Record a shorter take, then send it again.",
                    endpoint=self.path,
                ))
                return
            body = self.rfile.read(length)
            if len(body) != length:
                self._respond(400, _failure(
                    400, "animation_import", "request_receipt", "request_body_incomplete",
                    "The connection ended before Blender received the complete animation.",
                    "Send the animation again and check local security software if it happens repeatedly.",
                    endpoint=self.path,
                ))
                return
            take = parse_take(body)
        except (socket.timeout, TimeoutError):
            self._respond(408, _failure(
                408, "animation_import", "request_receipt", "request_body_timeout",
                "Blender timed out while receiving the animation.",
                "Send the animation again and check for heavy system load or local filtering software.",
                endpoint=self.path,
            ))
            return
        except BridgeRequestError as error:
            self._respond(400, _failure(
                400, "animation_import", error.stage, error.code, error.cause, error.remedy,
                endpoint=self.path, exception=error,
            ))
            return

        job = {"take": take, "done": threading.Event(), "result": None, "failure": None}
        try:
            _animation_queue.put_nowait(job)
        except Full:
            self._respond(503, _failure(
                503, "animation_import", "queueing", "animation_queue_full",
                "Blender is still keying earlier animations.",
                "Wait until they appear in Blender, then send the animation again.",
                endpoint=self.path,
            ))
            return
        if not job["done"].wait(ANIMATION_APPLY_WAIT_SECONDS):
            self._respond(202, {"ok": True, "queued": True, "applied": False})
            return
        if job["failure"] is not None:
            self._respond(422, job["failure"])
            return
        self._respond(200, {"ok": True, "queued": False, "applied": True, **job["result"]})

    @staticmethod
    def _validate_cache_settings(data) -> dict:
        if not isinstance(data, dict):
            raise BridgeRequestError(
                "request_validation", "request_not_object",
                "The cache settings JSON root is not an object.",
                "Update both XIV Instant Edit components and retry.")
        if data.get("schema") != "instant-edit.cache-settings":
            raise BridgeRequestError(
                "request_validation", "unsupported_schema",
                "The cache settings request uses an unsupported schema.",
                "Update both XIV Instant Edit components and retry.")
        if data.get("version") != 1:
            raise BridgeRequestError(
                "request_validation", "unsupported_version",
                "The cache settings request uses an unsupported protocol version.",
                "Update both XIV Instant Edit components and retry.")
        directory = _string(data, "cacheDirectory", required=True, max_length=4096).strip()
        if (directory.startswith(("\\\\", "//")) or
                not (Path(directory).is_absolute() or re.match(r"^[A-Za-z]:[\\/]", directory))):
            raise BridgeRequestError(
                "request_validation", "invalid_cache_directory",
                "The cache directory must be a local absolute path.",
                "Set a local absolute cache directory in XIV Instant Edit's in-game settings.")
        automatic_cleanup = data.get("automaticCleanup", True)
        if not isinstance(automatic_cleanup, bool):
            raise BridgeRequestError(
                "request_validation", "invalid_automatic_cleanup",
                "automaticCleanup must be a boolean.",
                "Update both XIV Instant Edit components and retry.")
        return {**data, "cacheDirectory": directory, "automaticCleanup": automatic_cleanup}

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
        if isinstance(version, bool) or version not in {1, 2, 3}:
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
        source_mod_stable_id = _string(data, "sourceModStableId", max_length=64)
        source_mod_name = _string(data, "sourceModName", max_length=512)
        source_mod_root_path = _string(data, "sourceModRootPath", max_length=4096)
        target_relative_path = _string(data, "targetRelativePath", max_length=4096)
        target_collection_id = _string(data, "targetCollectionId", max_length=64)
        target_collection_name = _string(data, "targetCollectionName", max_length=512)
        backup_target_id = _string(data, "backupTargetId", max_length=128)
        backup_directory = _string(data, "backupDirectory", max_length=4096)
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
        if source_mod_stable_id:
            try:
                if uuid.UUID(source_mod_stable_id).int == 0:
                    raise ValueError
            except (ValueError, AttributeError, TypeError) as error:
                raise BridgeRequestError(
                    "request_validation", "invalid_mod_identity",
                    "The Penumbra mod identity is invalid.",
                    "Refresh the model list and retry the import.") from error
        if source_kind == "game" and source_mod_stable_id:
            raise BridgeRequestError(
                "request_validation", "unexpected_mod_identity",
                "Game-data imports cannot contain a Penumbra mod identity.",
                "Refresh the model list and retry the import.")
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
        if version >= 3 and destination_state == "ready" and (
            len(backup_target_id) != 64
            or any(char not in "0123456789abcdef" for char in backup_target_id)
            or not backup_directory
            or Path(backup_directory).name != backup_target_id
        ):
            raise BridgeRequestError(
                "request_validation", "invalid_backup_target",
                "The import context is missing its managed backup target.",
                "Update both XIV Instant Edit components and re-import the model.")
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
        # Optional: the model's game skeleton. Without it the armature gets placeholder bones.
        skeleton = data.get("skeleton")
        if skeleton is not None:
            from .skeleton import parse_skeleton

            skeleton = parse_skeleton(skeleton).to_payload()

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
            "sourceModStableId": source_mod_stable_id,
            "sourceModName": source_mod_name,
            "sourceModRootPath": source_mod_root_path,
            "targetRelativePath": target_relative_path,
            "targetCollectionId": target_collection_id,
            "targetCollectionName": target_collection_name,
            "resourceManifestVersion": resource_manifest_version,
            "resourceManifestStatus": resource_manifest_status,
            "backupTargetId": backup_target_id,
            "backupDirectory": backup_directory,
            "previewManifestPath": preview_manifest_path,
            "callbackPort": callback_port,
            "objectIndex": object_index,
            "name": display_name,
            "importOptions": import_options,
            "skeleton": skeleton,
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


class _BridgeServer(ThreadingHTTPServer):
    # With SO_REUSEADDR, Windows lets a second Blender bind the same port without an error, and
    # the plugin then reaches either one. SO_EXCLUSIVEADDRUSE exists only on Windows.
    allow_reuse_address = not hasattr(socket, "SO_EXCLUSIVEADDRUSE")
    allow_reuse_port = False


def start_server(port: int = 42424) -> bool:
    """Starts the HTTP listener that receives import commands from the XIV Instant Edit plugin."""
    global _server, _thread, _port, _server_error

    if _server is not None:
        if port == _port:
            return True
        stop_server()

    try:
        server = _BridgeServer(("127.0.0.1", port), _ImportHandler)
    except OSError as e:
        _server = None
        _thread = None
        _port = port
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


def server_status() -> tuple[bool, int, str]:
    """Return whether the listener runs, its (last requested) port, and the startup error."""
    return _server is not None, _port, _server_error


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


def _set_last_status(text: str) -> None:
    props = getattr(bpy.context.scene, "xiv_ie_instant_edit_props", None)
    if props is not None:
        props.last_status = text


def process_animation_queue() -> None:
    """Key the animations received since the last call. Runs on Blender's main thread."""
    from .animation import AnimationApplyError, apply_take, summary

    while True:
        try:
            job = _animation_queue.get_nowait()
        except Empty:
            return
        take = job["take"]
        metadata = {
            "pluginVersion": take.plugin_version or "unknown",
            "animationKind": take.kind,
            "bones": len(take.bones),
            "frames": len(take.times),
        }
        try:
            job["result"] = apply_take(take)
            _set_last_status(summary(job["result"]))
        except AnimationApplyError as error:
            job["failure"] = _failure(
                422, "animation_import", "animation_processing", error.code, error.cause, error.remedy,
                endpoint="/animation", metadata=metadata,
            )
            _set_last_status(f"Animation failed: {error.cause}")
        except Exception as error:
            job["failure"] = _failure(
                500, "animation_import", "animation_processing", "animation_processing_failed",
                "Blender encountered an unexpected error while keying the animation.",
                "Review the diagnostic report, then send the animation again.",
                endpoint="/animation", exception=error, metadata=metadata,
            )
            _set_last_status("Animation failed: Blender encountered an unexpected error.")
        finally:
            job["done"].set()


def poll_import_queue() -> float:
    """Timer callback that runs pending imports on Blender's main thread."""
    try:
        process_animation_queue()
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
                    source_mod_stable_id=data.get("sourceModStableId", ""),
                    source_mod_name=data.get("sourceModName", ""),
                    source_mod_root_path=data.get("sourceModRootPath", ""),
                    target_relative_path=data.get("targetRelativePath", ""),
                    target_collection_id=data.get("targetCollectionId", ""),
                    target_collection_name=data.get("targetCollectionName", ""),
                    resource_manifest_version=int(data.get("resourceManifestVersion", 0)),
                    resource_manifest_status=data.get("resourceManifestStatus", "capture_failed"),
                    backup_target_id=data.get("backupTargetId", ""),
                    backup_directory=data.get("backupDirectory", ""),
                    import_id=data.get("importId", ""),
                    armature_mode=data.get("importOptions", {}).get("armatureMode", "generated"),
                    armature_target=data.get("importOptions", {}).get("targetObject", "Skeleton"),
                    apply_textures_and_materials=data.get("importOptions", {}).get("applyTexturesAndMaterials", False),
                    preview_manifest_path=data.get("previewManifestPath", ""),
                    skeleton_path=data.get("skeletonPath", ""),
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
