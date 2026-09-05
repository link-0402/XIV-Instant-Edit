"""Owned cache directories and safe import handoff staging."""

from __future__ import annotations

from pathlib import Path
import json
import shutil
import tempfile
import threading
import time
import uuid
import hashlib
import re
from datetime import datetime, timezone


CACHE_SCHEMA = "instant-edit.cache"
CACHE_VERSION = 1
CACHE_FOLDER = "XIV-Instant-Edit"
STALE_SECONDS = 24 * 60 * 60
BACKUP_RETENTION_SECONDS = 30 * 24 * 60 * 60
MAX_MODEL_BYTES = 512 * 1024 * 1024
MAX_PREVIEW_BYTES = 1024 * 1024 * 1024
MAX_PREVIEW_FILES = 2048
MAX_DIAGNOSTIC_REPORTS = 100
MAX_DIAGNOSTIC_BYTES = 20 * 1024 * 1024
BACKUP_FILE_RE = re.compile(
    r"^.+\.(?:mdl|fbx)\.(?P<stamp>\d{8}T\d{6}\.\d{6}Z)\.bak$", re.IGNORECASE
)

_lock = threading.RLock()
_base_directory = Path(tempfile.gettempdir())
_automatic_cleanup = True
_active_jobs: set[Path] = set()


class CacheStagingError(ValueError):
    def __init__(self, stage: str, code: str, cause: str, remedy: str):
        self.stage = stage
        self.code = code
        self.cause = cause
        self.remedy = remedy
        super().__init__(cause)


def configure_cache(base_directory: str | Path, automatic_cleanup: bool) -> Path:
    """Update the thread-safe cache configuration without accessing Blender APIs."""
    global _base_directory, _automatic_cleanup
    base = Path(base_directory or tempfile.gettempdir()).expanduser().resolve()
    if base.exists() and not base.is_dir():
        raise ValueError("cache base path must be a directory")
    with _lock:
        previous_base = _base_directory
        previous_cleanup = _automatic_cleanup
        _base_directory = base
        _automatic_cleanup = bool(automatic_cleanup)
    try:
        return ensure_cache_root()
    except Exception:
        # Do not leave the process pointed at a broken directory. Otherwise a
        # later bridge failure can lose its diagnostic report as a secondary
        # effect of a failed preference update.
        with _lock:
            _base_directory = previous_base
            _automatic_cleanup = previous_cleanup
        raise


def automatic_cleanup_enabled() -> bool:
    with _lock:
        return _automatic_cleanup


def cache_root() -> Path:
    with _lock:
        return (_base_directory / CACHE_FOLDER).resolve()


def _marker_path(root: Path) -> Path:
    return root / ".instant-edit-cache.json"


def ensure_cache_root() -> Path:
    root = cache_root()
    root.mkdir(parents=True, exist_ok=True)
    marker = _marker_path(root)
    if marker.exists():
        try:
            payload = json.loads(marker.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as error:
            raise ValueError("cache ownership marker is unreadable") from error
        if payload != {"schema": CACHE_SCHEMA, "version": CACHE_VERSION}:
            raise ValueError("cache ownership marker is invalid")
    else:
        if any(root.iterdir()):
            raise ValueError("cache directory is not empty and has no XIV Instant Edit ownership marker")
        marker.write_text(
            json.dumps({"schema": CACHE_SCHEMA, "version": CACHE_VERSION}),
            encoding="utf-8",
        )
    for kind in ("imports", "exports", "diagnostics", "backups"):
        (root / kind).mkdir(exist_ok=True)
    return root


def _uuid_name(value: str) -> str:
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError) as error:
        raise ValueError("cache job id must be a UUID") from error
    return parsed.hex


def create_job(kind: str, job_id: str | None = None) -> Path:
    if kind not in {"imports", "exports"}:
        raise ValueError("unsupported cache job type")
    name = _uuid_name(job_id or uuid.uuid4().hex)
    parent = ensure_cache_root() / kind
    job = parent / name
    job.mkdir(parents=False, exist_ok=False)
    with _lock:
        _active_jobs.add(job.resolve())
    return job


def _owned_job(path: str | Path) -> Path | None:
    raw_candidate = Path(path)
    if raw_candidate.is_symlink():
        return None
    candidate = raw_candidate.resolve()
    root = cache_root()
    try:
        relative = candidate.relative_to(root)
    except ValueError:
        return None
    if len(relative.parts) != 2 or relative.parts[0] not in {"imports", "exports"}:
        return None
    try:
        _uuid_name(relative.parts[1])
    except ValueError:
        return None
    if not _marker_path(root).is_file() or (root / relative.parts[0]).is_symlink():
        return None
    return candidate


def remove_job(path: str | Path) -> bool:
    job = _owned_job(path)
    if job is None or not job.is_dir():
        return False
    with _lock:
        _active_jobs.discard(job)
    shutil.rmtree(job)
    return True


def finish_job(path: str | Path) -> bool:
    """Mark a job inactive and remove it immediately when auto-cleanup is enabled."""
    job = _owned_job(path)
    if job is None:
        return False
    with _lock:
        _active_jobs.discard(job)
        automatic = _automatic_cleanup
    return remove_job(job) if automatic else True


def _directory_size(path: Path) -> int:
    total = 0
    for item in path.rglob("*"):
        try:
            if item.is_file() and not item.is_symlink():
                total += item.stat().st_size
        except OSError:
            continue
    return total


def clean_cache(older_than_seconds: float | None = None) -> tuple[int, int]:
    """Remove owned cache jobs/reports and return (items, bytes)."""
    root = ensure_cache_root()
    cutoff = None if older_than_seconds is None else time.time() - older_than_seconds
    removed = 0
    bytes_removed = 0
    for kind in ("imports", "exports"):
        for candidate in tuple((root / kind).iterdir()):
            job = _owned_job(candidate)
            if job is None or not job.is_dir():
                continue
            with _lock:
                if job in _active_jobs:
                    continue
            try:
                if cutoff is not None and job.stat().st_mtime > cutoff:
                    continue
                bytes_removed += _directory_size(job)
                shutil.rmtree(job)
                removed += 1
            except OSError:
                continue

    # Backup histories are deliberately longer-lived than transient jobs.
    backup_cutoff = time.time() - BACKUP_RETENTION_SECONDS
    for target in tuple((root / "backups").iterdir()):
        if target.is_symlink() or not target.is_dir() or not re_full_hash(target.name):
            continue
        for candidate in tuple(target.iterdir()):
            try:
                match = BACKUP_FILE_RE.match(candidate.name)
                created = datetime.strptime(
                    match.group("stamp"), "%Y%m%dT%H%M%S.%fZ"
                ).replace(tzinfo=timezone.utc).timestamp() if match else None
                if (candidate.is_symlink() or not candidate.is_file() or
                        created is None or created > backup_cutoff):
                    continue
                bytes_removed += candidate.stat().st_size
                candidate.unlink()
                removed += 1
            except (OSError, ValueError):
                continue

    diagnostics = root / "diagnostics"
    reports = []
    for candidate in tuple(diagnostics.iterdir()):
        try:
            if candidate.is_symlink() or not candidate.is_file() or candidate.suffix.casefold() != ".json":
                continue
            uuid.UUID(candidate.stem)
            stat = candidate.stat()
            if cutoff is None or stat.st_mtime <= cutoff:
                bytes_removed += stat.st_size
                candidate.unlink()
                removed += 1
            else:
                reports.append((stat.st_mtime, stat.st_size, candidate))
        except (OSError, ValueError):
            continue

    reports.sort(reverse=True)
    retained_count = 0
    retained_bytes = 0
    for _modified, size, candidate in reports:
        if retained_count < MAX_DIAGNOSTIC_REPORTS and retained_bytes + size <= MAX_DIAGNOSTIC_BYTES:
            retained_count += 1
            retained_bytes += size
            continue
        try:
            candidate.unlink()
            removed += 1
            bytes_removed += size
        except OSError:
            continue
    return removed, bytes_removed


def re_full_hash(value: str) -> bool:
    return len(value) == 64 and all(char in "0123456789abcdef" for char in value)


def backup_directory(target_file: str | Path, create: bool = False) -> Path:
    """Return the managed Simple Export history keyed by canonical output path."""
    target = Path(target_file).expanduser().resolve()
    target_id = hashlib.sha256(str(target).casefold().encode("utf-8")).hexdigest()
    directory = ensure_cache_root() / "backups" / target_id
    if create:
        directory.mkdir(exist_ok=True)
        marker = directory / ".target.json"
        if not marker.exists():
            marker.write_text(json.dumps({"targetPath": str(target)}), encoding="utf-8")
    return directory


def stage_import(data: dict) -> dict:
    """Copy a validated handoff into the add-on-owned cache before queueing."""
    source_value = Path(data.get("filePath", ""))
    try:
        source_model = source_value.resolve()
        source_is_valid = (
            not source_value.is_symlink()
            and source_model.suffix.casefold() == ".mdl"
            and source_model.is_file()
        )
        model_size = source_model.stat().st_size if source_is_valid else 0
    except OSError as error:
        raise CacheStagingError(
            "file_staging", "model_file_unavailable",
            "Blender could not access the temporary model file.",
            "Verify Blender and FFXIV run as the same Windows user, then retry the import.") from error
    if not source_is_valid:
        raise CacheStagingError(
            "file_staging", "model_file_unavailable",
            "Blender could not access the temporary model file.",
            "Verify Blender and FFXIV run as the same Windows user, then retry the import.")
    if model_size <= 0 or model_size > MAX_MODEL_BYTES:
        raise CacheStagingError(
            "file_staging", "model_size_unsupported",
            "The temporary model file is empty or exceeds the 512 MiB import limit.",
            "Verify the source model is valid and reduce its size before retrying.")

    try:
        job = create_job("imports")
    except (OSError, ValueError) as error:
        raise CacheStagingError(
            "file_staging", "cache_unavailable",
            "Blender could not create an import job in the configured cache.",
            "Choose a writable cache directory in the add-on preferences, then retry.") from error
    try:
        target_model = job / source_model.name
        try:
            shutil.copyfile(source_model, target_model)
        except OSError as error:
            raise CacheStagingError(
                "file_staging", "model_copy_failed",
                "Blender could not copy the temporary model into its configured cache.",
                "Verify the source file and cache directory are accessible to Blender, then retry.") from error
        result = dict(data)
        result["filePath"] = str(target_model)
        result["cacheJobDirectory"] = str(job)

        manifest_value = data.get("previewManifestPath", "")
        if manifest_value:
            raw_manifest = Path(manifest_value)
            try:
                manifest = raw_manifest.resolve()
                preview_root = manifest.parent.resolve()
            except OSError as error:
                raise CacheStagingError(
                    "preview_staging", "preview_manifest_unavailable",
                    "Blender could not access the material preview manifest.",
                    "Disable material previews or verify the preview handoff is accessible to Blender.") from error
            if (
                manifest.name != "materials.json"
                or preview_root.name != "preview"
                or preview_root.parent.resolve() != source_model.parent.resolve()
                or raw_manifest.is_symlink()
                or not manifest.is_file()
            ):
                raise CacheStagingError(
                    "preview_staging", "preview_manifest_invalid",
                    "The material preview manifest is missing or outside the expected handoff bundle.",
                    "Disable material previews and retry, or recreate the import with matching plugin and add-on versions.")

            target_preview = job / "preview"
            target_preview.mkdir()
            total_bytes = 0
            file_count = 0
            for source in preview_root.rglob("*"):
                if source.is_dir():
                    continue
                file_count += 1
                if file_count > MAX_PREVIEW_FILES or source.is_symlink():
                    raise CacheStagingError(
                        "preview_staging", "preview_bundle_unsafe",
                        "The material preview bundle contains too many files or a symbolic link.",
                        "Disable material previews or remove symbolic links from the preview source.")
                resolved = source.resolve()
                try:
                    relative = resolved.relative_to(preview_root)
                except ValueError as error:
                    raise CacheStagingError(
                        "preview_staging", "preview_bundle_unsafe",
                        "The material preview bundle contains a file outside its source directory.",
                        "Disable material previews and retry the import.") from error
                if resolved.suffix.casefold() not in {".json", ".rgba"}:
                    raise CacheStagingError(
                        "preview_staging", "preview_file_unsupported",
                        "The material preview bundle contains an unsupported file type.",
                        "Recreate the preview bundle with JSON and RGBA files only.")
                try:
                    size = resolved.stat().st_size
                except OSError as error:
                    raise CacheStagingError(
                        "preview_staging", "preview_file_unavailable",
                        "Blender could not read a file in the material preview bundle.",
                        "Disable material previews or verify the preview files are accessible to Blender.") from error
                total_bytes += size
                if total_bytes > MAX_PREVIEW_BYTES:
                    raise CacheStagingError(
                        "preview_staging", "preview_bundle_too_large",
                        "The material preview bundle exceeds the 1 GiB limit.",
                        "Disable material previews or reduce the number and size of preview textures.")
                destination = target_preview / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                try:
                    shutil.copyfile(resolved, destination)
                except OSError as error:
                    raise CacheStagingError(
                        "preview_staging", "preview_copy_failed",
                        "Blender could not copy the material preview bundle into its cache.",
                        "Choose a writable cache directory or disable material previews, then retry.") from error
            target_manifest = target_preview / "materials.json"
            if not target_manifest.is_file():
                raise CacheStagingError(
                    "preview_staging", "preview_manifest_missing",
                    "The material preview bundle does not contain materials.json.",
                    "Recreate the preview bundle or disable material previews before retrying.")
            result["previewManifestPath"] = str(target_manifest)
        return result
    except Exception:
        remove_job(job)
        raise
