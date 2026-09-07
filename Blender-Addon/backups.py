"""Model backup discovery and safe local file operations."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import re
import shutil
import uuid


BACKUP_RE = re.compile(
    r"^(?P<original>.+\.(?:mdl|fbx))\.(?P<stamp>\d{8}T\d{6}\.\d{6}Z)\.bak$",
    re.IGNORECASE,
)
LEGACY_RE = re.compile(r"^(?P<original>.+\.(?:mdl|fbx))\.bak$", re.IGNORECASE)


@dataclass(frozen=True)
class BackupEntry:
    path: Path
    original_name: str
    created: datetime
    timestamped: bool


def target_folder(
    settings,
    context=None,
    *,
    persist: bool = True,
) -> tuple[Path | None, str]:
    """Return the active Quick Export folder, or the Simple Export folder."""
    if context is not None:
        try:
            from .instant_edit.context import ContextValidationError
            from .instant_edit.ops import export_destination_context

            ref = export_destination_context(context, persist=persist)
            from .instant_edit.ops import selected_variant_target
            selected = selected_variant_target(context.scene.xiv_ie_instant_edit_props)
            backup_target_id = getattr(selected, "backup_target_id", "") or ref.backup_target_id
            backup_directory_value = getattr(selected, "backup_directory", "") or ref.backup_directory
            folder = Path(backup_directory_value).expanduser().resolve()
            if backup_target_id and folder.name == backup_target_id:
                return folder, "Quick Export target"
        except (ContextValidationError, OSError, ValueError):
            pass

    value = getattr(settings, "export_directory", "")
    if not value:
        return None, "Simple Export folder"
    try:
        folder = Path(value).expanduser().resolve()
    except OSError:
        return None, "Simple Export folder"
    if not folder.is_dir():
        return None, "Simple Export folder"
    from .instant_edit.cache import backup_directory
    name = (getattr(settings, "export_name", "") or "model").strip()
    suffix = {"MDL": ".mdl", "FBX": ".fbx", "GLTF": ".gltf"}.get(
        getattr(settings, "model_format", "MDL"), ".mdl")
    if name.casefold().endswith(suffix):
        name = name[:-len(suffix)]
    return backup_directory(folder / f"{name}{suffix}"), "Simple Export folder"


def parse_backup(path: Path) -> BackupEntry | None:
    if path.is_symlink() or not path.is_file() or path.name.startswith("."):
        return None
    match = BACKUP_RE.match(path.name)
    if match:
        try:
            created = datetime.strptime(match.group("stamp"), "%Y%m%dT%H%M%S.%fZ").replace(tzinfo=timezone.utc)
        except ValueError:
            return None
        return BackupEntry(path, match.group("original"), created, True)
    match = LEGACY_RE.match(path.name)
    if not match:
        return None
    try:
        created = datetime.fromtimestamp(path.stat().st_mtime, timezone.utc)
    except OSError:
        return None
    return BackupEntry(path, match.group("original"), created, False)


def list_backups(folder: Path | None) -> list[BackupEntry]:
    if folder is None or not folder.is_dir():
        return []
    entries = []
    for path in folder.iterdir():
        entry = parse_backup(path)
        if entry is not None:
            entries.append(entry)
    return sorted(entries, key=lambda item: (item.created, item.path.name), reverse=True)


def _safe_child(folder: Path, name: str) -> Path:
    folder = folder.resolve()
    path = (folder / name).resolve()
    if path.parent != folder:
        raise ValueError("backup path must be directly inside the target folder")
    return path


def backup_name(original_name: str, now: datetime | None = None) -> str:
    stamp = (now or datetime.now(timezone.utc)).astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return f"{original_name}.{stamp}.bak"


def create_backup(folder: Path, original_name: str) -> Path | None:
    """Copy an existing MDL/FBX before replacement; return None if absent."""
    if Path(original_name).name != original_name or Path(original_name).suffix.lower() not in {".mdl", ".fbx"}:
        raise ValueError("unsupported model filename")
    source = _safe_child(folder, original_name)
    if not source.is_file():
        return None
    from .instant_edit.cache import backup_directory
    history = backup_directory(source, create=True)
    for _ in range(8):
        destination = _safe_child(history, backup_name(original_name))
        if not destination.exists():
            shutil.copyfile(source, destination)
            return destination
    raise FileExistsError("could not allocate a unique backup filename")


def restore_local(folder: Path, entry: BackupEntry) -> Path:
    entry_path = _safe_child(folder, entry.path.name)
    marker = _safe_child(folder, ".target.json")
    if not marker.is_file():
        raise ValueError("managed backup target metadata is missing")
    import json
    target = Path(json.loads(marker.read_text(encoding="utf-8"))["targetPath"]).resolve()
    from .instant_edit.cache import backup_directory
    if target.name != entry.original_name or backup_directory(target).resolve() != folder.resolve():
        raise ValueError("managed backup target does not match this backup")
    if not entry_path.is_file():
        raise FileNotFoundError(entry.path.name)
    if target.exists():
        create_backup(target.parent, target.name)
    temporary = _safe_child(target.parent, f".xiv-ie-restore-{uuid.uuid4().hex}.tmp")
    try:
        shutil.copyfile(entry_path, temporary)
        temporary.replace(target)
    finally:
        if temporary.exists():
            temporary.unlink()
    return target


def clear_backups(folder: Path | None) -> int:
    removed = 0
    for entry in list_backups(folder):
        try:
            entry.path.unlink()
            removed += 1
        except OSError:
            pass
    return removed
