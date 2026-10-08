"""Exports at every size picked in Magic Fit's Resize panel, through Magic Fit's API (its api.py).

Magic Fit resizes the clothing to each size as a shape key and shows one size at a time; an export
reads the meshes at rest pose, so it writes the size shown. Magic Fit is optional: it is looked up
when needed, never imported when this module loads, and without it exports work as before.
"""

import importlib
import re
import time
from contextlib import contextmanager, nullcontext

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


class Step:
    """Where an export at sizes is: what it's ``doing`` to the size named ``name``, the ``done``-th of
    ``total``."""

    def __init__(self, done: int, total: int, name: str, doing: str):
        self.done, self.total, self.name, self.doing = done, total, name, doing

    def text(self) -> str:
        if self.done >= self.total:
            return f"{self.doing}. Esc cancels."
        return f"{self.doing} {self.name} ({self.done + 1} of {self.total}). Esc cancels."


# Magic Fit's note on how long a resize took: one per size here, replaced by one for all of them.
_RESIZE_TIME_NOTE = re.compile(r"Resized \d+ mesh\(es\) to \d+ size\(s\) in [\d.]+ s")


def export_steps(objects, export, session=None):
    """Resize ``objects``, the meshes an export writes, to each size picked in Magic Fit, and call
    ``export(sized, index)`` while each size shows, a size at a time: a generator yielding a Step
    before each resize and export, so Blender can draw between them (see start). ``session``, when
    given, makes a context manager the exports run in, entered once out of Edit Mode before the
    first size (mesh.export.ShapeExports). Returns Magic Fit's SizedMeshes, in order.

    Raises ValueError with a message for the user.
    """
    api = magic_fit_api()
    if api is None:
        raise ValueError("Magic Fit isn't enabled.")
    picked = list(api.picked_sizes())
    if not picked:
        raise ValueError(NO_SIZES)
    issue = option_name_issue([size.name for size in picked])
    if issue:
        raise ValueError(issue)
    total = len(picked)
    results = []
    resizing = 0.0
    with _object_mode(), (session() if session is not None else nullcontext()):
        for index, size in enumerate(picked):
            yield Step(index, total, size.name, "Resizing to")
            started = time.perf_counter()
            try:
                resized = api.resize_collection(list(objects), [size])
            except api.ResizeError as error:
                raise ValueError(str(error)) from error
            resizing += time.perf_counter() - started
            sized = next((item for item in resized if item.name == size.name), None)
            if sized is None:
                raise ValueError(f"Magic Fit didn't resize to {size.name}.")
            yield Step(index, total, size.name, "Exporting")
            with sized.shown():
                export(sized, index)
            results.append(sized)
    for sized in results:
        sized.notes = [note for note in sized.notes if not _RESIZE_TIME_NOTE.fullmatch(note)]
    results[0].notes.append(
        f"Resized {len(results[0].keys)} mesh(es) to {total} size(s) in {resizing:.1f} s")
    return results


def run(steps):
    """Run ``steps`` (a generator of Step) to its end, showing its progress on the mouse cursor;
    returns what it returns."""
    window_manager = bpy.context.window_manager
    begun = False
    try:
        while True:
            try:
                step = next(steps)
            except StopIteration as stop:
                return stop.value
            if not begun:
                window_manager.progress_begin(0, step.total * 2 + 1)
                begun = True
            window_manager.progress_update(step.done * 2 + (step.doing != "Resizing to"))
    finally:
        if begun:
            window_manager.progress_end()


def export_each(objects, export, session=None) -> list:
    """export_steps run to its end at once."""
    return run(export_steps(objects, export, session))


# A run start hands to RunSizeExports: (steps, done, failed, cancelled).
_pending: list = []
_running = False


def start(context, steps, done, failed, report, cancelled: str = "") -> set[str]:
    """Run ``steps`` (a generator of Step), then call ``done(report, result)`` with what it returns,
    or ``failed(report, error)``; ``report`` is an operator's report. With a window, a modal
    operator takes a step at a time, so Blender keeps drawing and shows the progress, and Esc
    cancels, the status then ending with ``cancelled``; without one (tests, background) it runs
    to its end at once. Returns the calling operator's result."""
    if _running:
        report({"ERROR"}, "Sizes are already being exported.")
        steps.close()
        return {"CANCELLED"}
    if bpy.app.background or getattr(context, "window", None) is None:
        try:
            result = run(steps)
        except Exception as error:
            failed(report, error)
            return {"CANCELLED"}
        done(report, result)
        return {"FINISHED"}
    _pending[:] = [(steps, done, failed, cancelled)]
    bpy.ops.xiv_ie.run_size_exports("INVOKE_DEFAULT")
    return {"FINISHED"}


# Events still handled while sizes export: looking around the viewport changes nothing.
_VIEW_EVENTS = frozenset({
    "MIDDLEMOUSE", "WHEELUPMOUSE", "WHEELDOWNMOUSE", "WHEELINMOUSE", "WHEELOUTMOUSE", "TRACKPADPAN",
    "TRACKPADZOOM", "MOUSEROTATE", "MOUSEMOVE", "INBETWEEN_MOUSEMOVE", "MOUSESMARTZOOM",
})


class RunSizeExports(bpy.types.Operator):
    """Export at Magic Fit's sizes, one size at a time. Esc cancels"""

    bl_idname = "xiv_ie.run_size_exports"
    bl_label = "Export Sizes"
    bl_options = {"INTERNAL", "UNDO"}

    def invoke(self, context, _event):
        global _running
        if not _pending:
            return {"CANCELLED"}
        self._steps, self._done, self._failed, self._cancelled = _pending.pop()
        self._step = None
        self._status = get_instant_edit_props().last_status
        self._shown = ""
        window_manager = context.window_manager
        self._timer = window_manager.event_timer_add(0.01, window=context.window)
        window_manager.modal_handler_add(self)
        self._progress = False
        _running = True
        self._show(context, "Starting the size exports. Esc cancels.")
        return {"RUNNING_MODAL"}

    def modal(self, context, event):
        if event.type == "ESC" and event.value == "PRESS":
            try:
                self._steps.close()
            finally:
                self._end(context)
            step = self._step
            message = (f"Size export cancelled after {step.done} of {step.total} sizes." if step is not None
                       else "Size export cancelled.")
            if self._cancelled:
                message += " " + self._cancelled
            get_instant_edit_props().last_status = message
            self.report({"WARNING"}, message)
            return {"CANCELLED"}
        if event.type != "TIMER":
            return {"PASS_THROUGH"} if event.type in _VIEW_EVENTS else {"RUNNING_MODAL"}
        try:
            step = next(self._steps)
        except StopIteration as stop:
            self._end(context)
            self._done(self.report, stop.value)
            return {"FINISHED"}
        except Exception as error:
            self._end(context)
            self._failed(self.report, error)
            return {"CANCELLED"}
        self._step = step
        window_manager = context.window_manager
        if not self._progress:
            window_manager.progress_begin(0, step.total * 2 + 1)
            self._progress = True
        window_manager.progress_update(step.done * 2 + (step.doing != "Resizing to"))
        self._show(context, step.text())
        return {"RUNNING_MODAL"}

    def _show(self, context, text: str) -> None:
        get_instant_edit_props().last_status = self._shown = text
        if context.workspace is not None:
            context.workspace.status_text_set(text)
        for area in context.screen.areas if context.screen is not None else ():
            area.tag_redraw()

    def _end(self, context) -> None:
        global _running
        _running = False
        window_manager = context.window_manager
        window_manager.event_timer_remove(self._timer)
        if self._progress:
            window_manager.progress_end()
        props = get_instant_edit_props()
        if props.last_status == self._shown:
            # Progress isn't a result: the steps' own status, else the one from before, stays.
            props.last_status = self._status
        if context.workspace is not None:
            context.workspace.status_text_set(None)
        for area in context.screen.areas if context.screen is not None else ():
            area.tag_redraw()


def default_name(results) -> str:
    """The size an option group turns on by default: the one the body shows, else the first."""
    return next((sized.name for sized in results if sized.size is not None and sized.size.now), results[0].name)


def notes(results) -> list[str]:
    """Magic Fit's messages about the resize, each once."""
    return list(dict.fromkeys(note for sized in results for note in sized.notes))
