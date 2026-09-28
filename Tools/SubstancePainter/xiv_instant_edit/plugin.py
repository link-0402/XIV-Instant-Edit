"""Wires the listener, the dock and Painter's events together. Runs on Painter's main thread.

Calls to Instant Edit run on worker threads, since each waits up to its timeout for an answer;
their results come back to the main thread through the timer.
"""

import os
import queue
import threading
import time
import traceback
import uuid

from PySide6 import QtCore

import substance_painter.application as application
import substance_painter.event as event
import substance_painter.logging as logging
import substance_painter.project as project

from . import client, painter_job, settings
from . import manifest as manifest_module
from .bridge import Bridge
from .ui import Panel

SETUP_TIMEOUT_SECONDS = 600
SEND_TIMEOUT_SECONDS = 900
POLL_INTERVAL_SECONDS = 1.0


def _painter_version() -> str:
    try:
        return str(application.version())
    except Exception:
        return ""


class Plugin:
    def __init__(self):
        self.settings = settings.load()
        self.bridge = Bridge()
        self.panel = None
        self.timer = None
        self.job = None
        self.pending = None
        self.pending_deadline = 0.0
        self.send = None
        # A Send between the button and Instant Edit taking the textures (linking, exporting).
        self.sending = False
        # (callback, result, error) of finished worker calls, run by the timer.
        self.results = queue.Queue()

    # Lifecycle -----------------------------------------------------------------------------

    def _handlers(self):
        return (
            (event.ProjectOpened, self._on_project_opened),
            (event.ProjectEditionEntered, self._on_edition_entered),
            (event.ProjectAboutToClose, self._on_project_closing),
        )

    def start(self) -> None:
        self.panel = Panel(self.send_to_game)
        if self.bridge.start(self.settings.painter_port):
            self.panel.set_status(f"Listening for Instant Edit on port {self.settings.painter_port}.")
        else:
            message = f"Could not listen on port {self.settings.painter_port}: {self.bridge.error}"
            self.panel.set_status(message)
            logging.error("XIV Instant Edit: " + message)
        for event_type, handler in self._handlers():
            event.DISPATCHER.connect_strong(event_type, handler)
        self.timer = QtCore.QTimer()
        self.timer.setInterval(250)
        self.timer.timeout.connect(self._tick)
        self.timer.start()
        if project.is_open():
            self._adopt_open_project()
        self._publish()

    def close(self) -> None:
        if self.timer is not None:
            self.timer.stop()
            self.timer = None
        for event_type, handler in self._handlers():
            try:
                event.DISPATCHER.disconnect(event_type, handler)
            except Exception:
                pass
        self.bridge.stop()
        if self.panel is not None:
            self.panel.close()
            self.panel = None

    # Shared helpers ------------------------------------------------------------------------

    def _ports(self, job) -> list:
        return client.candidate_ports(job.callback_port, self.settings.plugin_port)

    def _in_background(self, call, done) -> None:
        """Runs call() on a worker thread, then done(result, error) on the main thread."""
        def work():
            try:
                result, error = call(), None
            except Exception as caught:
                result, error = None, caught
            self.results.put((done, result, error))

        threading.Thread(target=work, name="xiv-instant-edit-call", daemon=True).start()

    def _log(self, text: str, error: bool = False) -> None:
        (logging.error if error else logging.info)("XIV Instant Edit: " + text)
        if self.panel is not None:
            self.panel.add_log(text)

    def _publish(self) -> None:
        try:
            is_open = project.is_open()
        except Exception:
            is_open = False
        self.bridge.publish({
            "ok": True,
            "addonVersion": self.settings.version,
            "painterVersion": _painter_version(),
            "projectOpen": is_open,
            "jobId": self.job.job_id if self.job is not None else "",
            "settingUp": self.pending is not None,
            "sending": self.sending or self.send is not None,
        })

    def _tick(self) -> None:
        try:
            while True:
                try:
                    done, result, error = self.results.get_nowait()
                except queue.Empty:
                    break
                done(result, error)
            if self.pending is not None and time.monotonic() > self.pending_deadline:
                job, self.pending = self.pending, None
                self._report_setup(job, False, "Painter did not finish loading the mesh in time.")
            if self.pending is None:
                try:
                    request = self.bridge.jobs.get_nowait()
                except queue.Empty:
                    request = None
                if request is not None:
                    self._open(request)
            if self.send is not None and time.monotonic() >= self.send["next_poll"]:
                self._poll_send()
        except Exception:
            logging.error("XIV Instant Edit: " + traceback.format_exc())
        finally:
            self._publish()

    # Opening a job -------------------------------------------------------------------------

    def _open(self, request: dict) -> None:
        try:
            job = manifest_module.load(request["manifestPath"])
        except manifest_module.ManifestError as error:
            self._log(f"Could not open the job: {error}", error=True)
            return
        if job.job_id != request["jobId"]:
            self._log("The job file does not match the request.", error=True)
            return
        if project.is_open():
            current = self._current_job()
            if current is not None and current.job_id == job.job_id:
                self.panel.show()
                self._log("This project is already open.")
                return
            self._report_setup(job, False, "Another project is open in Painter. Save and close it, then send again.")
            return
        try:
            painter_job.create_project(job)
        except Exception as error:
            self._report_setup(job, False, f"Painter could not create the project: {error}")
            return
        self.pending = job
        self.pending_deadline = time.monotonic() + SETUP_TIMEOUT_SECONDS
        self._log(f"Loading {job.display_name}…")

    def _on_edition_entered(self, _event) -> None:
        job, self.pending = self.pending, None
        if job is None:
            return
        try:
            layers = painter_job.setup_texture_sets(job)
            state = painter_job.JobState.from_manifest(job, layers)
            painter_job.store(state)
        except Exception as error:
            logging.error("XIV Instant Edit: " + traceback.format_exc())
            self._report_setup(job, False, f"Setting up the project failed: {error}")
            return
        try:
            warning = painter_job.apply_display(job)
        except Exception as error:
            logging.error("XIV Instant Edit: " + traceback.format_exc())
            warning = f"Transparency isn't shown: {error}"
        self.job = state
        self.panel.set_job(state.display_name)
        self.panel.show()
        if warning:
            self._log(warning, error=True)
        self._log("Project ready. Paint, then press Send to game.")
        project.execute_when_not_busy(lambda: self._baseline(state))

    def _baseline(self, state) -> None:
        try:
            files = painter_job.run_export(state, os.path.join(state.job_dir, "baseline"))
        except Exception as error:
            self._report_setup(state, False, f"The first export failed: {error}")
            return
        self._report_setup(state, True, "", files)

    def _report_setup(self, job, ok: bool, message: str, files=None) -> None:
        if not ok:
            self._log(message, error=True)
        payload = {
            "schema": "instant-edit.painter-baseline", "version": 1,
            "jobId": job.job_id, "capability": job.capability, "ok": ok, "message": message,
            "files": [{"key": key, "path": path} for key, path in (files or [])],
            "painterVersion": _painter_version(), "pluginVersion": self.settings.version,
        }
        ports = self._ports(job)

        def done(_result, error) -> None:
            if error is not None:
                self._log(f"Could not reach Instant Edit: {error}", error=True)

        self._in_background(lambda: client.call(ports, "/painter/baseline", payload), done)

    # Existing projects ---------------------------------------------------------------------

    def _current_job(self):
        try:
            return painter_job.load()
        except Exception as error:
            self._log(f"This project's Instant Edit data can't be read: {error}", error=True)
            return None

    def _on_project_opened(self, _event) -> None:
        self._adopt_open_project()

    def _on_project_closing(self, _event) -> None:
        self.job = None
        self.send = None
        self.sending = False
        if self.panel is not None:
            self.panel.set_job(None)
            self.panel.set_busy(False)

    def _adopt_open_project(self) -> None:
        state = self._current_job()
        self.job = state
        self.panel.set_job(state.display_name if state is not None else None)
        if state is not None:
            self._attach(state, lambda error: self._log(str(error), error=True) if error is not None
                         else self._log("Linked to Instant Edit."))

    def _attach(self, state, then) -> None:
        """Links the project to Instant Edit in the background; then(error or None) runs afterwards."""
        payload = {
            "schema": "instant-edit.painter-attach", "version": 1,
            "jobId": state.job_id, "capability": state.capability,
            "projectPath": project.file_path() or "",
            "painterVersion": _painter_version(), "pluginVersion": self.settings.version,
        }
        ports = self._ports(state)

        def done(result, error) -> None:
            if error is None:
                job_dir = result[2].get("jobDir")
                if isinstance(job_dir, str) and job_dir:
                    state.job_dir = job_dir
            then(error)

        self._in_background(lambda: client.call(ports, "/painter/attach", payload, timeout=3.0), done)

    # Sending -------------------------------------------------------------------------------

    def send_to_game(self) -> None:
        if self.sending or self.send is not None:
            self._log("A send is still being applied.")
            return
        state = self.job or self._current_job()
        if state is None:
            self._log("This project was not opened from Instant Edit.", error=True)
            return
        self.job = state
        self.sending = True
        self.panel.set_busy(True)
        self.panel.set_status("Linking to Instant Edit…")
        self._attach(state, lambda error: self._confirm_and_export(state, error))

    def _confirm_and_export(self, state, error) -> None:
        if error is not None or not self.sending or state is not self.job:
            # Unreachable, or the project closed while linking.
            self._stop_sending(str(error) if error is not None else "")
            return
        missing = painter_job.missing_base_layers(state)
        if missing and not self.panel.confirm(
                "XIV Instant Edit",
                "The \"XIV original\" layer was deleted in: " + ", ".join(missing) + ".\n\n"
                "Those textures will be replaced by only what is painted now. Send anyway?"):
            self._stop_sending("")
            return
        self.panel.set_status("Exporting textures…")
        project.execute_when_not_busy(lambda: self._export_and_send(state))

    def _stop_sending(self, error: str) -> None:
        self.sending = False
        if self.panel is not None:
            self.panel.set_busy(False)
            self.panel.set_status("")
        if error:
            self._log(error, error=True)

    def _export_and_send(self, state) -> None:
        send_id = uuid.uuid4().hex
        try:
            files = painter_job.run_export(state, os.path.join(state.job_dir, "export", send_id))
        except Exception as error:
            self._stop_sending(f"Send failed: {error}")
            return
        payload = {
            "schema": "instant-edit.painter-send", "version": 1,
            "jobId": state.job_id, "capability": state.capability, "sendId": send_id,
            "files": [{"key": key, "path": path} for key, path in files],
        }
        ports = self._ports(state)

        def done(result, error) -> None:
            if error is not None:
                self._stop_sending(f"Send failed: {error}")
                return
            self.sending = False
            now = time.monotonic()
            self.send = {"id": send_id, "job": state, "ports": [result[0]], "next_poll": now + POLL_INTERVAL_SECONDS,
                         "deadline": now + SEND_TIMEOUT_SECONDS, "polling": False}
            self.panel.set_busy(True)
            self.panel.set_status("Applying in game…")

        self._in_background(lambda: client.call(ports, "/painter/send", payload, timeout=10.0), done)

    def _poll_send(self) -> None:
        send = self.send
        if send["polling"]:
            return
        send["polling"] = True
        state = send["job"]
        payload = {"schema": "instant-edit.painter-send-status", "version": 1,
                   "jobId": state.job_id, "capability": state.capability, "sendId": send["id"]}
        ports = send["ports"]

        def done(result, error) -> None:
            send["polling"] = False
            send["next_poll"] = time.monotonic() + POLL_INTERVAL_SECONDS
            if self.send is not send:
                return  # The project closed meanwhile.
            if error is not None:
                if time.monotonic() > send["deadline"]:
                    self._finish_send(f"Lost contact with Instant Edit: {error}", [], failed=True)
                return
            body = result[2]
            if body.get("state") == "pending":
                if time.monotonic() > send["deadline"]:
                    self._finish_send("Instant Edit is still applying the textures; check its Sessions tab.", [], failed=True)
                return
            self._finish_send(str(body.get("message") or ""), body.get("results") or [],
                              failed=body.get("state") != "done")

        self._in_background(lambda: client.call(ports, "/painter/send/status", payload, timeout=3.0), done)

    def _finish_send(self, message: str, results: list, failed: bool) -> None:
        self.send = None
        self.panel.set_busy(False)
        self.panel.set_status("")
        lines = [message] if message else []
        for result in results:
            if isinstance(result, dict):
                detail = f" — {result['message']}" if result.get("message") else ""
                lines.append(f"{result.get('label') or result.get('key')}: {result.get('outcome')}{detail}")
        self._log("\n".join(lines) or ("Send failed." if failed else "Sent."), error=failed)
