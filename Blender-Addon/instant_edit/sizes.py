"""Exports at every size picked in Magic Fit's Resize panel, through Magic Fit's API (its api.py).

Magic Fit resizes the clothing to each size as a shape key and shows one size at a time; an export
reads the meshes at rest pose, so it writes the size shown. Magic Fit is optional: it is looked up
when needed, never imported when this module loads, and without it exports work as before.
"""

import importlib
from contextlib import contextmanager

import bpy

from .props import get_instant_edit_props

# The api.VERSION of Magic Fit this module was written for.
API_VERSION = 1
NO_SIZES = "Pick sizes in Magic Fit's Resize panel."
_INVALID_FILE_CHARACTERS = frozenset('<>:"/\\|?*')


def magic_fit_api():
    """Magic Fit's api module when an enabled Magic Fit offers it, whichever repository installed it."""
    for name in bpy.context.preferences.addons.keys():
        if name.rsplit(".", 1)[-1] == "magic_fit":
            try:
                api = importlib.import_module(name + ".api")
            except ImportError:
                return None
            return api if getattr(api, "VERSION", 0) >= API_VERSION else None
    return None


def wanted(props=None) -> bool:
    """Whether exports go out at Magic Fit's sizes: the export option is on and Magic Fit can do it."""
    props = props if props is not None else get_instant_edit_props()
    return bool(getattr(props, "export_sizes", False)) and magic_fit_api() is not None


def picked_names(scene=None) -> list[str]:
    """The names of the sizes picked in Magic Fit's Resize panel; none without Magic Fit or sizes."""
    api = magic_fit_api()
    if api is None:
        return []
    try:
        return [size.name for size in api.picked_sizes(scene)]
    except (AttributeError, ReferenceError, RuntimeError, TypeError, ValueError):
        return []


def export_names(props=None) -> list[str] | None:
    """The sizes exports go out at, for display: None when they go out as they are."""
    return picked_names() if wanted(props) else None


def file_part(name: str) -> str:
    """A size's part of a file name: its name, characters file names can't have replaced."""
    cleaned = "".join("-" if char in _INVALID_FILE_CHARACTERS or ord(char) < 32 else char for char in name)
    return cleaned.strip().rstrip(".").strip()


def option_name_issue(names) -> str:
    """Why sizes of these names can't be Penumbra options, or "" when they can."""
    seen = set()
    for name in names:
        if not name or name != name.strip() or len(name) > 120 or any(ord(char) < 32 for char in name) \
                or not file_part(name):
            return f"The Magic Fit size {name!r} can't be an option name."
        if name.casefold() in seen:
            return f"Two Magic Fit sizes are named {name}."
        seen.add(name.casefold())
    return ""


@contextmanager
def _object_mode():
    """Leave Edit Mode for the block, which shows no shape key mix, so it would export no size; then
    make the active object active again (an MDL export leaves its own copy active, then deletes it)
    and come back."""
    view_layer = bpy.context.view_layer
    active = view_layer.objects.active
    edit = bpy.context.mode == "EDIT_MESH"
    if edit:
        bpy.ops.object.mode_set(mode="OBJECT")
    try:
        yield
    finally:
        try:
            if active is not None and active.name in view_layer.objects:
                view_layer.objects.active = active
                if edit:
                    bpy.ops.object.mode_set(mode="EDIT")
        except (ReferenceError, RuntimeError) as error:
            print(f"XIV Instant Edit: could not return to the object being edited: {error}")


def export_each(objects, export) -> list:
    """Resize ``objects``, the meshes an export writes, to each size picked in Magic Fit, and call
    ``export(sized, index)`` while each size shows. Returns Magic Fit's SizedMeshes, in order.

    Raises ValueError with a message for the user.
    """
    api = magic_fit_api()
    if api is None:
        raise ValueError("Magic Fit isn't enabled.")
    count = len(api.picked_sizes())
    if not count:
        raise ValueError(NO_SIZES)
    window_manager = bpy.context.window_manager
    window_manager.progress_begin(0, count + 1)
    try:
        with _object_mode():
            try:
                results = api.resize_collection(list(objects), progress=window_manager.progress_update)
            except api.ResizeError as error:
                raise ValueError(str(error)) from error
            issue = option_name_issue([sized.name for sized in results])
            if issue:
                raise ValueError(issue)
            for index, sized in enumerate(results):
                with sized.shown():
                    export(sized, index)
                window_manager.progress_update(index + 2)
    finally:
        window_manager.progress_end()
    return results


def default_name(results) -> str:
    """The size an option group turns on by default: the one the body shows, else the first."""
    return next((sized.name for sized in results if sized.size is not None and sized.size.now), results[0].name)


def notes(results) -> list[str]:
    """Magic Fit's messages about the resize, each once."""
    return list(dict.fromkeys(note for sized in results for note in sized.notes))
