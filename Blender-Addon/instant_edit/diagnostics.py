"""Structured, path-safe diagnostics for the local Dalamud/Blender bridge."""

from __future__ import annotations

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import threading
import tomllib
import uuid


WINDOWS_PATH = re.compile(r"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\"'<>|]*")
UNIX_PATH = re.compile(r"(?<![A-Za-z0-9_.-])/(?:[^\s\"'<>|/]+/)+[^\s\"'<>|]*")
SECRET_JSON_VALUE = re.compile(
    r'(?i)("(?:capability|token|secret|pluginInstanceId)"\s*:\s*")[^"]*(")')
_write_lock = threading.RLock()


class BridgeRequestError(ValueError):
    """Expected bridge rejection with a stable code and actionable remedy."""

    def __init__(self, stage: str, code: str, cause: str, remedy: str):
        self.stage = stage
        self.code = code
        self.cause = cause
        self.remedy = remedy
        super().__init__(cause)


def sanitize_text(value: object, max_length: int = 4096) -> str:
    """Remove absolute local paths before text reaches responses or report files."""
    text = str(value or "")
    text = WINDOWS_PATH.sub("<local-path>", text)
    text = UNIX_PATH.sub("<local-path>", text)
    text = SECRET_JSON_VALUE.sub(r"\1<redacted>\2", text)
    return text[:max_length]


def new_diagnostic_id() -> str:
    return uuid.uuid4().hex


def _installed_addon_version() -> str:
    try:
        manifest = Path(__file__).resolve().parents[1] / "blender_manifest.toml"
        with manifest.open("rb") as manifest_file:
            version = tomllib.load(manifest_file).get("version")
        return sanitize_text(version, 32) if isinstance(version, str) and version else "unknown"
    except (OSError, tomllib.TOMLDecodeError):
        return "unknown"


def failure_payload(
    *,
    component: str,
    operation: str,
    stage: str,
    code: str,
    cause: str,
    remedy: str,
    diagnostic_id: str | None = None,
) -> dict:
    try:
        diagnostic_id = uuid.UUID(str(diagnostic_id)).hex if diagnostic_id else new_diagnostic_id()
    except (ValueError, AttributeError):
        diagnostic_id = new_diagnostic_id()
    safe_cause = sanitize_text(cause, 2048)
    return {
        "ok": False,
        "error": safe_cause,  # Legacy compatibility.
        "component": sanitize_text(component, 64),
        "operation": sanitize_text(operation, 64),
        "stage": sanitize_text(stage, 64),
        "code": sanitize_text(code, 128),
        "cause": safe_cause,
        "remedy": sanitize_text(remedy, 2048),
        "diagnosticId": diagnostic_id,
    }


def write_diagnostic(
    payload: dict,
    *,
    endpoint: str,
    http_status: int | None = None,
    exception: BaseException | None = None,
    metadata: dict | None = None,
) -> bool:
    """Persist one sanitized JSON diagnostic under the configured cache root."""
    try:
        from .cache import STALE_SECONDS, automatic_cleanup_enabled, ensure_cache_root, clean_cache

        diagnostic_id = str(payload.get("diagnosticId", ""))
        uuid.UUID(diagnostic_id)
        metadata_values = metadata or {}
        technical = {
            sanitize_text(key, 128): sanitize_text(value)
            for key, value in metadata_values.items()
        }
        if exception is not None and "exceptionMessage" not in technical:
            technical["exceptionMessage"] = sanitize_text(exception)
        report = {
            "schema": "instant-edit.diagnostic",
            "version": 1,
            "timestampUtc": datetime.now(timezone.utc).isoformat(),
            "diagnosticId": diagnostic_id,
            "component": sanitize_text(payload.get("component", "unknown"), 64),
            "operation": sanitize_text(payload.get("operation", "unknown"), 64),
            "stage": sanitize_text(payload.get("stage", "unknown"), 64),
            "code": sanitize_text(payload.get("code", "unknown_error"), 128),
            "cause": sanitize_text(payload.get("cause", payload.get("error", "unknown failure"))),
            "remedy": sanitize_text(payload.get("remedy", "")),
            "endpoint": sanitize_text(endpoint.lstrip("/"), 256),
            "httpStatus": http_status,
            "exceptionType": type(exception).__name__ if exception is not None else None,
            "componentVersions": {
                "blenderAddon": sanitize_text(
                    metadata_values.get("addonVersion") or _installed_addon_version(), 32),
                "dalamudPlugin": sanitize_text(
                    metadata_values.get("pluginVersion") or
                    metadata_values.get("componentVersion") or "unknown", 32),
            },
            "technical": technical,
        }
        with _write_lock:
            folder = ensure_cache_root() / "diagnostics"
            folder.mkdir(exist_ok=True)
            path = folder / f"{diagnostic_id}.json"
            temporary = folder / f".{diagnostic_id}.{uuid.uuid4().hex}.tmp"
            try:
                with temporary.open("x", encoding="utf-8", newline="\n") as stream:
                    json.dump(report, stream, indent=2, sort_keys=True)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, path)
            finally:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    pass
            if automatic_cleanup_enabled():
                clean_cache(STALE_SECONDS)
        return True
    except Exception as error:
        print(f"XIV Instant Edit: could not write diagnostic report: {sanitize_text(error)}")
        return False


def record_failure(
    *,
    component: str,
    operation: str,
    stage: str,
    code: str,
    cause: str,
    remedy: str,
    endpoint: str,
    http_status: int | None = None,
    exception: BaseException | None = None,
    metadata: dict | None = None,
    diagnostic_id: str | None = None,
) -> dict:
    payload = failure_payload(
        component=component,
        operation=operation,
        stage=stage,
        code=code,
        cause=cause,
        remedy=remedy,
        diagnostic_id=diagnostic_id,
    )
    write_diagnostic(
        payload,
        endpoint=endpoint,
        http_status=http_status,
        exception=exception,
        metadata=metadata,
    )
    return payload


def record_remote_failure(
    body: bytes,
    status: int,
    *,
    endpoint: str,
    default_operation: str = "request",
) -> dict:
    """Normalize a structured or legacy remote rejection and persist it locally."""
    try:
        decoded = body.decode("utf-8")
        result = json.loads(decoded)
    except (UnicodeError, json.JSONDecodeError):
        decoded = body.decode("utf-8", errors="replace")
        result = None
    if not isinstance(result, dict):
        result = {
            "component": "dalamud_plugin",
            "operation": default_operation,
            "stage": "response_handling",
            "code": "legacy_http_error",
            "cause": "The Dalamud plugin rejected the request without providing diagnostic details.",
            "remedy": "Update and restart both XIV Instant Edit components, then retry.",
        }
    return record_failure(
        component=str(result.get("component", "dalamud_plugin")),
        operation=str(result.get("operation", default_operation)),
        stage=str(result.get("stage", "response_handling")),
        code=str(result.get("code", "legacy_http_error")),
        cause=str(result.get(
            "cause", result.get("error", result.get("message", "The plugin rejected the request.")))),
        remedy=str(result.get(
            "remedy", "Update and restart both XIV Instant Edit components, then retry.")),
        endpoint=endpoint,
        http_status=status,
        metadata={
            "response": sanitize_text(decoded, 8192),
            "componentVersion": str(result.get("componentVersion", "unknown")),
        },
        diagnostic_id=str(result.get("diagnosticId", "")) or None,
    )


def record_protocol_failure(
    body: bytes,
    status: int,
    *,
    endpoint: str,
    operation: str,
    code: str = "invalid_success_response",
    cause: str = "The Dalamud plugin returned an invalid success response.",
) -> dict:
    """Persist a local diagnostic when a nominally successful response is unusable."""
    return record_failure(
        component="dalamud_plugin",
        operation=operation,
        stage="response_parsing",
        code=code,
        cause=cause,
        remedy="Update and restart the XIV Instant Edit Dalamud plugin, then retry.",
        endpoint=endpoint,
        http_status=status,
        metadata={"response": body.decode("utf-8", errors="replace")},
    )
