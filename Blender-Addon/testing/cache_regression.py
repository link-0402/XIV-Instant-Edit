"""Standalone safety regression for the Blender add-on's owned cache."""

from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import time
import types
import uuid


def _load_cache():
    path = Path(__file__).resolve().parents[1] / "instant_edit" / "cache.py"
    spec = importlib.util.spec_from_file_location("xiv_instant_edit_cache_regression", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def _load_stash():
    """cache.py and stash.py as one package, so stash reads the cache this test configures."""
    root = Path(__file__).resolve().parents[1] / "instant_edit"
    package_name = "xiv_instant_edit_stash_regression"
    package = types.ModuleType(package_name)
    package.__path__ = [str(root)]
    sys.modules[package_name] = package
    loaded = []
    for name in ("cache", "stash"):
        spec = importlib.util.spec_from_file_location(f"{package_name}.{name}", root / f"{name}.py")
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        assert spec.loader is not None
        spec.loader.exec_module(module)
        loaded.append(module)
    return loaded


def _expect_value_error(action, what: str) -> None:
    try:
        action()
    except ValueError:
        pass
    else:
        raise AssertionError(f"{what} was accepted")


def assert_stash(base: Path) -> None:
    """Blender's side of the stash: its folders, where Save to Stash writes, and the list of exports."""
    cache, stash = _load_stash()
    root = cache.configure_cache(base, True)
    stash_root = stash.stash_root()
    assert stash_root == root / "package-imports", stash_root
    assert stash_root.is_relative_to(base.resolve()), "stash_root() did not read the configured cache"

    # A folder counts when it directly holds an extraction's or Blender's own file; newest first.
    stash_root.mkdir(exist_ok=True)
    markers = {"Unity": "stash.json", "Older": "extract-report.txt", "Sims": "extract-files.txt",
               "From Blender": "blender-exports.json"}
    for folder, marker in markers.items():
        (stash_root / folder).mkdir()
        (stash_root / folder / marker).write_text("{}", encoding="utf-8")
    (stash_root / "Nested" / "inner").mkdir(parents=True)
    (stash_root / "Nested" / "inner" / "stash.json").write_text("{}", encoding="utf-8")
    (stash_root / "Folder Marker" / "stash.json").mkdir(parents=True)
    (stash_root / "stash.json").write_text("{}", encoding="utf-8")
    now = time.time()
    for age, folder in enumerate(("Sims", "From Blender", "Unity", "Older")):
        os.utime(stash_root / folder, (now - age * 60, now - age * 60))
    listed = [folder.name for folder in stash.stash_folders()]
    assert listed == ["Sims", "From Blender", "Unity", "Older"], listed
    assert all(folder.parent == stash_root for folder in stash.stash_folders())
    cache.configure_cache(base / "fresh base", True)
    assert stash.stash_folders() == [], "a cache without package-imports listed stash folders"
    cache.configure_cache(base, True)

    kaihua = stash_root / "Kaihua"
    (kaihua / "Models").mkdir(parents=True)
    (kaihua / "stash.json").write_text("{}", encoding="utf-8")
    source = kaihua / "Models" / "Kaihua.fbx"
    source.write_bytes(b"fbx")
    outside = base / "elsewhere" / "Outfit.fbx"

    # 1. The folder the user chose.
    assert stash.target_folder([str(source)], "Unity", "Top") == stash_root / "Unity"
    # 2. The stash folder holding a source file, matched ignoring case. Sources outside the stash,
    # sitting loose in its root, or climbing out of it don't count; the first that does wins.
    found = stash.target_folder(
        ["", None, str(outside), str(stash_root / "loose.fbx"), str(kaihua / ".." / ".." / "climbed.fbx"),
         str(stash_root).swapcase() + "/KAIHUA/models/kaihua.fbx", str(stash_root / "Unity" / "Pants.fbx")],
        "", "Top")
    assert found.parent == stash_root and found.name.casefold() == "kaihua", found
    assert stash.target_folder([stash_root / "Unity" / "Pants.fbx", source], None, "Top") == stash_root / "Unity"
    # 3. Otherwise a new folder named after the file; nothing is created on disk.
    assert stash.target_folder([str(outside)], "", "Top") == stash_root / "Top"
    assert stash.target_folder([], None, "Top") == stash_root / "Top"
    assert not (stash_root / "Top").exists()
    for chosen, name in (("../escape", "Top"), ("", "a/b"), ("", "a\\b"), ("", ""), ("", "..")):
        _expect_value_error(lambda: stash.target_folder([], chosen, name), f"stash folder {chosen!r}/{name!r}")

    # record_export: creates the list, the entry's file is relative with '/', sources are full paths, once each.
    sidecar = kaihua / "blender-exports.json"
    temporary = kaihua / "blender-exports.json.tmp"
    (kaihua / "Blender").mkdir()
    top = kaihua / "Blender" / "Top.mdl"
    top.write_bytes(b"mdl")
    before = datetime.now(timezone.utc).replace(microsecond=0)
    assert stash.record_export(kaihua, top, [str(source), str(source), ""]) == sidecar
    written = json.loads(sidecar.read_text("utf-8"))
    assert set(written) == {"schema", "version", "models"}, written
    assert written["schema"] == "instant-edit.blender-exports" and written["version"] == 1, written
    [entry] = written["models"]
    assert set(entry) == {"file", "exportedUtc", "sources"}, entry
    assert entry["file"] == "Blender/Top.mdl", entry
    assert re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", entry["exportedUtc"]), entry
    exported = datetime.strptime(entry["exportedUtc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    assert before <= exported <= datetime.now(timezone.utc), entry
    assert entry["sources"] == [source.resolve().as_posix()], entry
    assert not temporary.exists(), "record_export left its .tmp behind"

    # Another file is added; the same file again (any case or separator) replaces its entry in place.
    pants = stash_root / "Unity" / "Pants.fbx"
    stash.record_export(kaihua, "Blender/Bottom.mdl", [pants])
    bottom = json.loads(sidecar.read_text("utf-8"))["models"][1]
    stash.record_export(kaihua, "blender\\TOP.mdl", [pants])
    models = json.loads(sidecar.read_text("utf-8"))["models"]
    assert [model["file"] for model in models] == ["blender/TOP.mdl", "Blender/Bottom.mdl"], models
    assert models[0]["sources"] == [pants.resolve().as_posix()] and models[1] == bottom, models
    assert not temporary.exists()

    # A file that isn't inside the folder is refused before the list is touched.
    kept = sidecar.read_bytes()
    for bad in (base / "elsewhere" / "Top.mdl", "../Top.mdl", "Blender//Top.mdl", "C:Top.mdl", "", kaihua):
        _expect_value_error(lambda: stash.record_export(kaihua, bad, [source]), f"exported file {bad!r}")
    assert sidecar.read_bytes() == kept

    # A list that isn't ours, or can't be decoded, starts over; its broken entries are dropped.
    for broken in (b"{not json", b"[]", b"\xff\xfe\xfa",
                   b'{"schema": "someone-else", "version": 1, "models": []}'):
        sidecar.write_bytes(broken)
        stash.record_export(kaihua, top, [source])
        models = json.loads(sidecar.read_text("utf-8"))["models"]
        assert [model["file"] for model in models] == ["Blender/Top.mdl"], (broken, models)
    assert b"\r\n" not in sidecar.read_bytes(), "the list is written with '\\n' line ends"

    # A list of ours of another version (a newer add-on's) is refused and left as it was.
    newer = b'{"schema": "instant-edit.blender-exports", "version": 2, "models": []}'
    sidecar.write_bytes(newer)
    _expect_value_error(lambda: stash.record_export(kaihua, top, [source]), "a version 2 list")
    assert sidecar.read_bytes() == newer and not temporary.exists()
    sidecar.write_text(json.dumps({"schema": "instant-edit.blender-exports", "version": 1, "models": [
        7, {"sources": []}, {"file": "Blender/Kept.mdl", "exportedUtc": "2026-10-27T12:00:00Z", "sources": []}]}),
        encoding="utf-8")
    stash.record_export(kaihua, top, [source])
    models = json.loads(sidecar.read_text("utf-8"))["models"]
    assert [model["file"] for model in models] == ["Blender/Kept.mdl", "Blender/Top.mdl"], models

    # A list that is there but can't be read now is left alone rather than overwritten.
    kept = sidecar.read_bytes()
    own_read = "read_text" in Path.__dict__
    original_read = Path.read_text

    def busy_read(self, *args, **kwargs):
        if self.name == "blender-exports.json":
            raise PermissionError("in use")
        return original_read(self, *args, **kwargs)

    Path.read_text = busy_read
    try:
        stash.record_export(kaihua, top, [pants])
    except PermissionError:
        pass
    else:
        raise AssertionError("record_export overwrote a list it could not read")
    finally:
        if own_read:
            Path.read_text = original_read
        else:
            del Path.read_text
    assert sidecar.read_bytes() == kept and not temporary.exists()

    # The plugin may hold the list open as Blender replaces it: a busy replace is retried, and one
    # that never succeeds leaves the list as it was and no .tmp.
    original_replace = os.replace
    original_delay = stash.REPLACE_DELAY_SECONDS
    attempts = []

    def busy_replace(source_path, target_path):
        attempts.append(target_path)
        if len(attempts) < 3:
            raise PermissionError("in use")
        original_replace(source_path, target_path)

    stash.REPLACE_DELAY_SECONDS = 0
    os.replace = busy_replace
    try:
        stash.record_export(kaihua, "Blender/Retried.mdl", [pants])
        assert len(attempts) == 3, attempts
        assert json.loads(sidecar.read_text("utf-8"))["models"][-1]["file"] == "Blender/Retried.mdl"
        kept = sidecar.read_bytes()

        def never(_source_path, _target_path):
            raise PermissionError("in use")

        os.replace = never
        try:
            stash.record_export(kaihua, "Blender/Never.mdl", [pants])
        except PermissionError:
            pass
        else:
            raise AssertionError("a replace that never succeeds was reported as saved")
    finally:
        os.replace = original_replace
        stash.REPLACE_DELAY_SECONDS = original_delay
    assert sidecar.read_bytes() == kept and not temporary.exists()

    # A folder that isn't there yet is made.
    assert stash.record_export(stash_root / "Top", "Blender/Top.mdl", []) == stash_root / "Top" / "blender-exports.json"
    assert json.loads((stash_root / "Top" / "blender-exports.json").read_text("utf-8"))["models"][0]["sources"] == []
    assert "Top" in [folder.name for folder in stash.stash_folders()]
    print("[PASS] stash folders, Save to Stash's target folder, and blender-exports.json")


def assert_stash_sources(base: Path) -> None:
    """The sources Save to Stash records: a model it saved, imported back, stands for its own sources."""
    cache, stash = _load_stash()
    cache.configure_cache(base / "sources", True)
    stash_root = stash.stash_root()
    kaihua = stash_root / "Kaihua"
    (kaihua / "Models").mkdir(parents=True)
    (kaihua / "Blender").mkdir()
    fbx = kaihua / "Models" / "Kaihua.fbx"
    fbx.write_bytes(b"fbx")
    pants = stash_root / "Unity" / "Pants.fbx"
    outside = base / "elsewhere" / "Outfit.fbx"
    # Each saved model as its list spells it, and the files it was made from.
    exports = {"Blender/Top.mdl": [fbx.resolve().as_posix()], "Blender\\Mixed.mdl": [pants.as_posix(), fbx.as_posix()],
               "Blender/Bare.mdl": []}
    for name in ("Top", "Mixed", "Bare", "Unlisted"):
        (kaihua / "Blender" / f"{name}.mdl").write_bytes(b"mdl")
    sidecar = kaihua / "blender-exports.json"
    sidecar.write_text(json.dumps({"schema": "instant-edit.blender-exports", "version": 1, "models": [
        {"file": file, "exportedUtc": "2026-10-27T12:00:00Z", "sources": sources}
        for file, sources in exports.items()]}), encoding="utf-8")

    def full(path) -> str:
        return Path(path).resolve().as_posix()

    # Files that aren't models Save to Stash wrote are kept as they are, once each and in order.
    assert stash.export_sources([str(outside), "", None, str(fbx), fbx, str(outside)]) \
        == [full(outside), full(fbx)], stash.export_sources([str(outside), str(fbx)])
    # A saved model stands for its own sources, found ignoring case and separators.
    top = kaihua / "Blender" / "Top.mdl"
    assert stash.export_sources([str(top)]) == [full(fbx)], stash.export_sources([str(top)])
    assert stash.export_sources([str(kaihua / "BLENDER" / "top.MDL")]) == [full(fbx)]
    assert stash.export_sources([str(outside), str(top), str(fbx)]) == [full(outside), full(fbx)]
    assert stash.export_sources([str(kaihua / "Blender" / "Mixed.mdl")]) == [full(pants), full(fbx)]
    # One saved without sources stands for none; one the list doesn't name stays itself.
    assert stash.export_sources([str(kaihua / "Blender" / "Bare.mdl"), str(outside)]) == [full(outside)]
    unlisted = kaihua / "Blender" / "Unlisted.mdl"
    assert stash.export_sources([str(unlisted)]) == [full(unlisted)]
    assert stash.export_sources([]) == [] and stash.export_sources(None) == []

    # A list that is broken, or can't be read now, names nothing.
    kept = sidecar.read_bytes()
    sidecar.write_bytes(b"{not json")
    assert stash.export_sources([str(top)]) == [full(top)]
    sidecar.write_bytes(kept)
    own_read = "read_text" in Path.__dict__
    original_read = Path.read_text

    def busy_read(self, *args, **kwargs):
        if self.name == "blender-exports.json":
            raise PermissionError("in use")
        return original_read(self, *args, **kwargs)

    Path.read_text = busy_read
    try:
        assert stash.export_sources([str(top)]) == [full(top)]
    finally:
        if own_read:
            Path.read_text = original_read
        else:
            del Path.read_text
    assert stash.export_sources([str(top)]) == [full(fbx)]
    print("[PASS] Save to Stash records the files a model saved to the stash was made from")


def run() -> None:
    cache = _load_cache()
    with tempfile.TemporaryDirectory(prefix="xiv-ie-cache-test-") as temporary:
        base = Path(temporary)
        os.environ["APPDATA"] = str(base / "appdata")
        expected_diagnostics = (
            base / "appdata" / "XIVLauncher" / "pluginConfigs" / "InstantEdit" / "Diagnostics"
        ).resolve()
        assert cache.diagnostics_root() == expected_diagnostics
        assert cache.ensure_diagnostics_root() == expected_diagnostics
        root = cache.configure_cache(base, True)
        restored = _load_cache()
        assert restored.restore_cache_configuration()
        assert restored.cache_base_directory() == base.resolve()
        texture_session = root / "texture-edits" / uuid.uuid4().hex
        texture_session.mkdir(parents=True)
        working_texture = texture_session / "c0101e0001_top_d.tga"
        working_texture.write_bytes(b"artist working image")
        assert root == (base / cache.CACHE_FOLDER).resolve()
        assert json.loads((root / ".instant-edit-cache.json").read_text("utf-8"))["schema"] == cache.CACHE_SCHEMA

        # Temp cleaners delete the old marker while newer cache files survive; a
        # cache holding only its own entries re-adopts itself, a foreign entry is refused.
        (root / ".instant-edit-cache.json").unlink()
        (root / "Contexts").mkdir()
        (root / "Contexts" / f"{uuid.uuid4().hex}.json").write_text("{}", encoding="utf-8")
        (root / "game-exports" / "chara").mkdir(parents=True)
        (root / "texture-backups").mkdir()
        (root / "texture-backups" / f"{'A' * 64}.tex").write_bytes(b"original")
        (root / "mod-backups").mkdir()
        (root / "mod-backups" / ("B" * 64)).write_bytes(b"original")
        (root / "package-imports" / "Some Package").mkdir(parents=True)
        (root / "TextureSessions.json").write_text("[]", encoding="utf-8")
        (root / f".pending-context-revocations.json.{uuid.uuid4().hex}.tmp").write_text("{", encoding="utf-8")
        assert cache.ensure_cache_root() == root
        assert (root / ".instant-edit-cache.json").is_file()
        (root / ".instant-edit-cache.json").unlink()
        foreign_entry = root / "notes.txt"
        foreign_entry.write_text("user file", encoding="utf-8")
        try:
            cache.ensure_cache_root()
        except ValueError:
            assert not (root / ".instant-edit-cache.json").exists()
        else:
            raise AssertionError("an unmarked cache with a foreign entry was adopted")
        foreign_entry.unlink()
        assert cache.ensure_cache_root() == root

        handoff = base / "handoff" / uuid.uuid4().hex
        preview = handoff / "preview"
        preview.mkdir(parents=True)
        model = handoff / "source.mdl"
        model.write_bytes(b"\x06\x00\x00\x01model")
        (preview / "materials.json").write_text('{"materials":[]}', encoding="utf-8")
        (preview / "texture.rgba").write_bytes(b"rgba")

        staged = cache.stage_import({
            "filePath": str(model),
            "previewManifestPath": str(preview / "materials.json"),
        })
        staged_model = Path(staged["filePath"])
        job = Path(staged["cacheJobDirectory"])
        assert staged_model.read_bytes() == model.read_bytes()
        assert Path(staged["previewManifestPath"]).is_file()
        assert job.parent == root / "imports"

        try:
            cache.stage_import({"filePath": str(base / "missing.mdl")})
        except cache.CacheStagingError as error:
            assert error.code == "model_file_unavailable" and error.remedy
        else:
            raise AssertionError("a missing model did not produce a specific staging failure")

        empty_model = handoff / "empty.mdl"
        empty_model.write_bytes(b"")
        try:
            cache.stage_import({"filePath": str(empty_model)})
        except cache.CacheStagingError as error:
            assert error.code == "model_size_unsupported" and error.remedy
        else:
            raise AssertionError("an empty model did not produce a specific staging failure")

        unsupported_preview = preview / "unsupported.png"
        unsupported_preview.write_bytes(b"png")
        try:
            cache.stage_import({
                "filePath": str(model),
                "previewManifestPath": str(preview / "materials.json"),
            })
        except cache.CacheStagingError as error:
            assert error.code == "preview_file_unsupported" and error.remedy
        else:
            raise AssertionError("an unsupported preview file did not produce a specific failure")
        unsupported_preview.unlink()

        foreign = base / "foreign"
        foreign.mkdir()
        (foreign / "keep.txt").write_text("keep", encoding="utf-8")
        assert not cache.remove_job(foreign)
        assert (foreign / "keep.txt").is_file()

        export_job = cache.create_job("exports")
        (export_job / "result.mdl").write_bytes(b"result")
        cache.configure_cache(base, False)
        assert cache.finish_job(job) and cache.finish_job(export_job)
        removed, removed_bytes = cache.clean_cache()
        assert removed == 2
        assert removed_bytes >= len(model.read_bytes()) + len(b"result")
        assert not job.exists() and not export_job.exists()
        assert working_texture.read_bytes() == b"artist working image"
        cache.clean_cache(cache.STALE_SECONDS)
        assert working_texture.is_file()
        assert (foreign / "keep.txt").is_file()

        # A job still running when the plugin moves the cache folder is finished in the folder it lives in.
        cache.configure_cache(base, True)
        moved_job = cache.create_job("imports")
        (moved_job / "model.mdl").write_bytes(b"model")
        moved_root = base / "moved cache base"
        cache.configure_cache(moved_root, True)
        assert moved_job.resolve() in cache._active_jobs
        assert cache.finish_job(moved_job)
        assert moved_job.resolve() not in cache._active_jobs and not moved_job.exists()
        cache.configure_cache(base, False)
        kept_job = cache.create_job("exports")
        cache.configure_cache(moved_root, False)
        assert cache.finish_job(kept_job) and kept_job.is_dir() and kept_job.resolve() not in cache._active_jobs
        cache.configure_cache(base, False)
        assert cache.clean_cache()[0] >= 1 and not kept_job.exists()
        # Only a job of a cache that carries its marker is finished: the same layout elsewhere is left alone.
        impostor = base / "impostor" / "imports" / uuid.uuid4().hex
        impostor.mkdir(parents=True)
        assert not cache.finish_job(impostor) and impostor.is_dir()
        print("[PASS] a job outlives a change of the cache folder and is still finished in the folder it lives in")
        cache.configure_cache(base, True)

        # The plugin keeps a hair model backup's EST entries beside it; they expire together.
        history = root / "backups" / ("e" * 64)
        history.mkdir(parents=True, exist_ok=True)
        expired = history / "c0801h0108_hir.mdl.20200101T000000.000000Z.bak"
        fresh = history / f"c0801h0108_hir.mdl.{time.strftime('%Y%m%dT%H%M%S', time.gmtime())}.000000Z.bak"
        for backup in (expired, fresh):
            backup.write_bytes(b"model")
            (history / f"{backup.name}.est.json").write_text("{}", encoding="utf-8")
        cache.clean_cache()
        assert not expired.exists() and not (history / f"{expired.name}.est.json").exists()
        assert fresh.is_file() and (history / f"{fresh.name}.est.json").is_file()

        # Backups last as long as the plugin's Settings say; a plugin that sends nothing keeps the last value.
        ten_days = history / f"c0801h0108_hir.mdl.{time.strftime('%Y%m%dT%H%M%S', time.gmtime(time.time() - 10 * 86400))}.000000Z.bak"
        ten_days.write_bytes(b"model")
        cache.configure_cache(base, False, 30)
        cache.configure_cache(base, False)
        assert cache.backup_retention_days() == 30
        cache.clean_cache()
        assert ten_days.is_file(), "a 10-day-old backup stays while the plugin keeps backups 30 days"
        cache.configure_cache(base, False, 7)
        cache.clean_cache()
        assert not ten_days.exists(), "a 10-day-old backup goes once the plugin keeps backups 7 days"
        for bad in (0, 366, True, 7.5):
            try:
                cache.configure_cache(base, False, bad)
            except ValueError:
                pass
            else:
                raise AssertionError(f"backup retention {bad!r} was accepted")
        assert cache.backup_retention_days() == 7

        assert_stash(base)
        assert_stash_sources(base)

    print("[PASS] cache staging, ownership boundaries, and cleanup")


if __name__ == "__main__":
    run()
