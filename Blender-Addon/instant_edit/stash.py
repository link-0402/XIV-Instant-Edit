"""The plugin's stash as Blender sees it: its folders, the folder Save to Stash writes a model
into, and blender-exports.json, the list of models Blender saved into a folder. Blender writes
that list; the plugin only reads it (Dalamud-Plugin/Studio/Services/Stash/BlenderExports.cs).
No Blender APIs, so plain Python can test it."""

from __future__ import annotations

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import time

from .cache import cache_root


STASH_FOLDER = "package-imports"
EXPORTS_FILE = "blender-exports.json"
EXPORTS_SCHEMA = "instant-edit.blender-exports"
EXPORTS_VERSION = 1
# The subfolder of a stash folder Save to Stash writes its models into.
EXPORTS_FOLDER = "Blender"
# Files only an extraction or Blender writes into a stash folder. Keep in sync with
# StashFolders.IsExtraction in Dalamud-Plugin/Studio/Services/Stash/StashFolders.cs.
STASH_MARKERS = ("stash.json", "extract-report.txt", "extract-files.txt", EXPORTS_FILE)
# The plugin (or a virus scanner) may hold the list open as Blender replaces it, and Windows
# refuses the replace meanwhile.
REPLACE_ATTEMPTS = 5
REPLACE_DELAY_SECONDS = 0.1


def stash_root() -> Path:
    """The cache's package-imports folder, which holds the stash's folders."""
    return cache_root() / STASH_FOLDER


def is_stash_folder(folder: Path) -> bool:
    """Whether an extraction or Blender made ``folder``: one of their files sits directly in it."""
    for name in STASH_MARKERS:
        try:
            if (folder / name).is_file():
                return True
        except OSError:
            continue
    return False


def stash_folders() -> list[Path]:
    """The stash's folders, the most recently changed first; none when there is no stash yet."""
    try:
        entries = list(stash_root().iterdir())
    except OSError:
        return []
    folders = []
    for entry in entries:
        try:
            if entry.is_dir() and is_stash_folder(entry):
                folders.append(entry)
        except OSError:
            continue
    return sorted(folders, key=_changed_at, reverse=True)


def _changed_at(folder: Path) -> float:
    try:
        return folder.stat().st_mtime
    except OSError:
        return 0.0


def target_folder(source_files, chosen: str | None, name: str) -> Path:
    """The stash folder Save to Stash writes into, the first that applies: the folder the user
    chose (a folder name; empty means automatic), the stash folder holding one of the model's
    source files, or a new folder named ``name``. Creates nothing. Raises ValueError for a
    chosen folder or name that isn't one folder name."""
    root = stash_root()
    if chosen and chosen.strip():
        return root / _folder_name(chosen)
    for source in source_files or ():
        parts = _parts_under(source, root)
        # A file sitting loose in the stash root isn't in any stash folder.
        if parts is not None and len(parts) >= 2:
            return root / parts[0]
    return root / _folder_name(name)


def export_sources(source_files) -> list[str]:
    """The files to record as a model's sources: ``source_files`` as full paths with '/', each
    once and in order. A model Save to Stash wrote, imported back, stands for the files its stash
    folder's blender-exports.json lists for it, so saving it again keeps the package's own files as
    the sources. A list that can't be read names nothing."""
    root = stash_root()
    lists = {}
    found = []
    for source in source_files or ():
        listed = _listed_sources(source, root, lists)
        found.extend([source] if listed is None else listed)
    return _full_paths(found)


def _listed_sources(source, root: Path, lists: dict) -> list | None:
    """The sources a stash folder's blender-exports.json lists for ``source``; None when it lists
    no such file. ``lists`` keeps each folder's entries, read once."""
    parts = _parts_under(source, root)
    if parts is None or len(parts) < 2:
        return None
    folder = parts[0].casefold()
    if folder not in lists:
        try:
            lists[folder] = _read_models(root / parts[0] / EXPORTS_FILE)
        except (OSError, ValueError):
            lists[folder] = []
    relative = "/".join(parts[1:]).casefold()
    for model in lists[folder]:
        if model["file"].strip().replace("\\", "/").casefold() == relative:
            sources = model.get("sources")
            return [item for item in sources if isinstance(item, str)] if isinstance(sources, list) else []
    return None


def record_export(folder, file, sources) -> Path:
    """Lists ``file``, a model Save to Stash wrote into ``folder``, in the folder's
    blender-exports.json with the files it was made from. Its earlier entry is replaced and the
    others are kept. ``file`` is a full path inside ``folder`` or one relative to it.

    The list is written to a .tmp beside it, which then replaces it. A list that is missing or
    isn't one of ours starts over; one that is there but can't be read raises OSError, and one of
    ours of another version raises ValueError: both are left alone. Returns the list's path."""
    folder = Path(folder)
    relative = _relative_file(file, folder)
    entry = {
        "file": relative,
        "exportedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sources": _full_paths(sources),
    }
    path = folder / EXPORTS_FILE
    models = []
    replaced = False
    for model in _read_models(path):
        if model["file"].strip().replace("\\", "/").casefold() != relative.casefold():
            models.append(model)
        elif not replaced:
            models.append(entry)
            replaced = True
    if not replaced:
        models.append(entry)
    _write_replacing(path, {"schema": EXPORTS_SCHEMA, "version": EXPORTS_VERSION, "models": models})
    return path


def _folder_name(value: str) -> str:
    """``value`` as one folder directly in the stash root."""
    if (not isinstance(value, str) or not value.strip() or value in (".", "..")
            or any(character in value for character in "/\\:")):
        raise ValueError(f"{value!r} is not a folder name")
    return value


def _parts_under(path, root: Path) -> tuple[str, ...] | None:
    """``path``'s parts below ``root``, both resolved and compared ignoring case; None when it
    isn't under ``root``."""
    if not isinstance(path, (str, os.PathLike)) or not os.fspath(path).strip():
        return None
    try:
        full = Path(path).expanduser().resolve()
        base = Path(root).resolve()
    except (OSError, RuntimeError, ValueError):
        return None
    head = [part.casefold() for part in base.parts]
    if [part.casefold() for part in full.parts[:len(head)]] != head:
        return None
    return full.parts[len(head):]


def _relative_file(file, folder: Path) -> str:
    """``file`` relative to ``folder`` with '/', as the plugin reads it."""
    text = os.fspath(file)
    if Path(text).is_absolute():
        parts = _parts_under(text, folder) or ()
    else:
        parts = tuple(text.replace("\\", "/").split("/"))
    if not parts or any(part in ("", ".", "..") or ":" in part for part in parts):
        raise ValueError(f"{text!r} is not a file inside {folder}")
    return "/".join(parts)


def _full_paths(sources) -> list[str]:
    """The source files as full paths with '/', each once, in order."""
    paths = []
    for source in sources or ():
        if not isinstance(source, (str, os.PathLike)) or not os.fspath(source).strip():
            continue
        try:
            full = Path(source).expanduser().resolve().as_posix()
        except (OSError, RuntimeError, ValueError):
            continue
        if all(full.casefold() != listed.casefold() for listed in paths):
            paths.append(full)
    return paths


def _read_models(path: Path) -> list[dict]:
    """The entries of the list at ``path``: none when it is missing or malformed. Any other read
    failure raises, so a list that is there is never overwritten unread; a list of ours of
    another version (a newer add-on's) raises ValueError, so it is never overwritten either."""
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (FileNotFoundError, UnicodeDecodeError, json.JSONDecodeError):
        return []
    if not isinstance(payload, dict) or payload.get("schema") != EXPORTS_SCHEMA:
        return []
    if payload.get("version") != EXPORTS_VERSION:
        raise ValueError(f"{path.name} is version {payload.get('version')!r}, which this add-on can't update")
    if not isinstance(payload.get("models"), list):
        return []
    return [model for model in payload["models"] if isinstance(model, dict) and isinstance(model.get("file"), str)]


def _write_replacing(path: Path, payload: dict) -> None:
    temporary = path.with_name(path.name + ".tmp")
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with open(temporary, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(payload, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        for attempt in range(REPLACE_ATTEMPTS):
            try:
                os.replace(temporary, path)
                return
            except PermissionError:
                if attempt + 1 == REPLACE_ATTEMPTS:
                    raise
                time.sleep(REPLACE_DELAY_SECONDS)
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
