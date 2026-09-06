"""Isolated add-on registration and temporary Blender data for headless tests."""

import importlib
import importlib.util
import sys
import tempfile
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import bpy


@contextmanager
def addon_session(name):
    root = Path(__file__).resolve().parents[1]
    spec = importlib.util.spec_from_file_location(name, root / "__init__.py", submodule_search_locations=[str(root)])
    addon = importlib.util.module_from_spec(spec)
    sys.modules[name] = addon
    spec.loader.exec_module(addon)
    preferences = importlib.import_module(f"{name}.preferences")
    bridge = importlib.import_module(f"{name}.instant_edit")
    with tempfile.TemporaryDirectory(prefix="xiv-ie-tests-") as temporary:
        prefs = SimpleNamespace(
            instant_edit_blender_port=42424, instant_edit_plugin_port=42428,
            instant_edit_cache_directory=temporary, instant_edit_auto_cleanup=True,
        )
        # Network behavior is exercised through explicit transport fixtures; a
        # scene test must never bind the user's listener or use their cache.
        with patch.object(preferences, "get_prefs", return_value=prefs), \
                patch.object(bridge, "start_server", return_value=True):
            bpy.ops.wm.read_factory_settings(use_empty=True)
            try:
                addon.register()
                yield addon
            finally:
                addon.unregister()
                bpy.ops.wm.read_factory_settings(use_empty=True)


@contextmanager
def temporary_scene_data():
    """Remove only datablocks allocated by the enclosed scenario, even on failure."""
    stores = [bpy.data.objects, bpy.data.collections, bpy.data.meshes,
              bpy.data.armatures, bpy.data.materials, bpy.data.images]
    previous = [{item.as_pointer() for item in store} for store in stores]
    try:
        yield
    finally:
        for store, existing in zip(stores, previous):
            for item in tuple(store):
                if item.as_pointer() not in existing:
                    store.remove(item, do_unlink=True)
