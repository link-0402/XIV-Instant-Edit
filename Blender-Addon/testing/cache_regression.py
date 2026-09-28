"""Standalone safety regression for the Blender add-on's owned cache."""

import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import uuid


def _load_cache():
    path = Path(__file__).resolve().parents[1] / "instant_edit" / "cache.py"
    spec = importlib.util.spec_from_file_location("xiv_instant_edit_cache_regression", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


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

    print("[PASS] cache staging, ownership boundaries, and cleanup")


if __name__ == "__main__":
    run()
