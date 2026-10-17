"""Exports as steps, so Blender keeps drawing while they run.

An export written as a generator yields a Step before each slow part. With a window, a modal
operator takes one step at a time: Blender redraws between them, the status bar shows the step and
Esc cancels. Waiting on the plugin runs on a worker thread meanwhile (in_background), so a slow
Penumbra reload doesn't freeze Blender. Without a window (tests, background) the steps run to their
end at once.
"""

import threading

import bpy

from .props import get_instant_edit_props


# Esc while a step waits on a worker: work that can give up waiting (the receipt of a lost export
# reply) does so and ends as still pending. The work itself, already sent, can't be taken back.
stop_waiting = threading.Event()

# Whether the steps run to their end at once on Blender's main thread (no window, see start), so a
# wait blocks Blender itself and is kept short.
_inline = False


def inline_run() -> bool:
    return _inline


class Step:
    """Where an export is: ``text`` for the status bar, ``done`` of ``total`` for the progress bar.
    ``wait``, when set, is work on a worker thread the export waits for; Esc can't stop it, only
    the waiting that can give up (see stop_waiting)."""

    def __init__(self, text: str, done: int = 0, total: int = 1, wait=None):
        self.text, self.done, self.total, self.wait = text, done, total, wait

    def status(self) -> str:
        return self.text if self.wait is not None else f"{self.text} Esc cancels."

    def cancelled(self) -> str:
        """The status after Esc stopped the export at this step."""
        return "Export cancelled."


class _Background:
    """Work running on its own worker thread: done() says whether it ended, result() what it returned."""

    def __init__(self, work):
        self._ended = threading.Event()
        self._result = None
        self._error = None
        threading.Thread(target=self._run, args=(work,), name="XIV IE export", daemon=True).start()

    def _run(self, work) -> None:
        try:
            self._result = work()
        except BaseException as error:
            self._error = error
        finally:
            self._ended.set()

    def done(self) -> bool:
        return self._ended.is_set()

    def result(self):
        self._ended.wait()
        if self._error is not None:
            raise self._error
        return self._result


def in_background(work, step: Step):
    """Run ``work``, which must not touch bpy, on a worker thread while the export shows ``step``;
    returns what it returns (``result = yield from in_background(...)``)."""
    step.wait = _Background(work)
    yield step
    return step.wait.result()


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
                window_manager.progress_begin(0, step.total)
                begun = True
            window_manager.progress_update(step.done)
    finally:
        if begun:
            window_manager.progress_end()


# A run start hands to RunExportSteps: (steps, done, failed, cancelled).
_pending: list = []
_running = False


def start(context, steps, done, failed, report, cancelled: str = "") -> set[str]:
    """Run ``steps`` (a generator of Step), then call ``done(report, result)`` with what it returns,
    or ``failed(report, error)``; ``report`` is an operator's report. With a window, a modal
    operator takes a step at a time, so Blender keeps drawing and shows the progress, and Esc
    cancels, the status then ending with ``cancelled``; without one (tests, background) it runs
    to its end at once. Returns the calling operator's result."""
    global _inline
    if _running:
        report({"ERROR"}, "An export is already running.")
        steps.close()
        return {"CANCELLED"}
    if bpy.app.background or getattr(context, "window", None) is None:
        _inline = True
        try:
            result = run(steps)
        except Exception as error:
            failed(report, error)
            return {"CANCELLED"}
        finally:
            _inline = False
        done(report, result)
        return {"FINISHED"}
    _pending[:] = [(steps, done, failed, cancelled)]
    bpy.ops.xiv_ie.run_export_steps("INVOKE_DEFAULT")
    return {"FINISHED"}


# Events still handled while an export runs: looking around the viewport changes nothing.
_VIEW_EVENTS = frozenset({
    "MIDDLEMOUSE", "WHEELUPMOUSE", "WHEELDOWNMOUSE", "WHEELINMOUSE", "WHEELOUTMOUSE", "TRACKPADPAN",
    "TRACKPADZOOM", "MOUSEROTATE", "MOUSEMOVE", "INBETWEEN_MOUSEMOVE", "MOUSESMARTZOOM",
})


class RunExportSteps(bpy.types.Operator):
    """Run an export a step at a time. Esc cancels"""

    bl_idname = "xiv_ie.run_export_steps"
    bl_label = "Export"
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
        stop_waiting.clear()
        self._show(context, "Starting the export. Esc cancels.")
        return {"RUNNING_MODAL"}

    def modal(self, context, event):
        step = self._step
        waiting = step is not None and step.wait is not None and not step.wait.done()
        # Not even once the work ended: the plugin may have written the export already.
        if event.type == "ESC" and event.value == "PRESS" and (step is None or step.wait is None):
            try:
                self._steps.close()
            finally:
                self._end(context)
            message = step.cancelled() if step is not None else "Export cancelled."
            if self._cancelled:
                message += " " + self._cancelled
            get_instant_edit_props().last_status = message
            self.report({"WARNING"}, message)
            return {"CANCELLED"}
        if event.type == "ESC" and event.value == "PRESS" and waiting and not stop_waiting.is_set():
            # What was sent stays sent; only waiting for the plugin's answer stops.
            stop_waiting.set()
            self._show(context, f"{step.text} Stopping the wait; the plugin may still finish it.")
            return {"RUNNING_MODAL"}
        if event.type != "TIMER":
            return {"PASS_THROUGH"} if event.type in _VIEW_EVENTS else {"RUNNING_MODAL"}
        if waiting:
            return {"RUNNING_MODAL"}
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
            window_manager.progress_begin(0, step.total)
            self._progress = True
        window_manager.progress_update(step.done)
        self._show(context, step.status())
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
