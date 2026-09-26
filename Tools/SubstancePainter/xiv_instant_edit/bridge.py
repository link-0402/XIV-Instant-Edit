"""Loopback HTTP listener that receives jobs from Instant Edit.

The handler thread never calls Painter's API: it validates the request, reads the status
snapshot the main thread publishes, and queues work that the main thread drains on a timer.
"""

import json
import queue
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

OPEN_SCHEMA = "instant-edit.painter-open"
OPEN_VERSION = 1
MAX_BODY_BYTES = 64 * 1024
REQUEST_TIMEOUT_SECONDS = 5


class Bridge:
    """Owns the listener, the job queue and the status snapshot shared with the handler."""

    def __init__(self):
        self.jobs = queue.Queue(maxsize=4)
        self._lock = threading.Lock()
        self._snapshot = {"ok": True}
        self._server = None
        self._thread = None
        self.port = 0
        self.error = ""

    def publish(self, snapshot: dict) -> None:
        with self._lock:
            self._snapshot = dict(snapshot)

    def snapshot(self) -> dict:
        with self._lock:
            return dict(self._snapshot)

    def start(self, port: int) -> bool:
        self.stop()
        bridge = self

        class Handler(_Handler):
            owner = bridge

        try:
            server = _Server(("127.0.0.1", port), Handler)
        except OSError as error:
            self.port = port
            self.error = str(error)
            return False
        self._server = server
        self.port = port
        self.error = ""
        self._thread = threading.Thread(target=server.serve_forever, name="xiv-instant-edit-bridge", daemon=True)
        self._thread.start()
        return True

    def stop(self) -> None:
        server, self._server = self._server, None
        if server is not None:
            try:
                server.shutdown()
                server.server_close()
            except Exception:
                pass
        self._thread = None


class _Server(ThreadingHTTPServer):
    # Windows lets a second process bind the same port under SO_REUSEADDR; refuse that instead.
    allow_reuse_address = not hasattr(socket, "SO_EXCLUSIVEADDRUSE")
    daemon_threads = True


def validate_open(payload) -> dict:
    """Checks an /open request body; returns the fields the main thread needs."""
    if not isinstance(payload, dict):
        raise ValueError("The request must be a JSON object.")
    if payload.get("schema") != OPEN_SCHEMA or payload.get("version") != OPEN_VERSION:
        raise ValueError("This request came from an incompatible Instant Edit version.")
    job_id = payload.get("jobId")
    manifest = payload.get("manifestPath")
    if not isinstance(job_id, str) or len(job_id) != 32 or any(c not in "0123456789abcdef" for c in job_id):
        raise ValueError("The job id is invalid.")
    if not isinstance(manifest, str) or not manifest.lower().endswith("job.json") or len(manifest) > 1024:
        raise ValueError("The manifest path is invalid.")
    return {"jobId": job_id, "manifestPath": manifest}


class _Handler(BaseHTTPRequestHandler):
    owner: Bridge = None

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(REQUEST_TIMEOUT_SECONDS)

    def log_message(self, format: str, *args) -> None:
        pass

    def _respond(self, status: int, body: dict) -> None:
        data = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _refuse_browser_request(self) -> bool:
        """Refuses what a web page could send (CSRF, DNS rebinding); Instant Edit is a local client."""
        host = (self.headers.get("Host") or "").strip().lower()
        hostname = host.rsplit(":", 1)[0] if ":" in host else host
        if ((not host or hostname in ("127.0.0.1", "localhost"))
                and self.headers.get("Origin") is None and self.headers.get("Sec-Fetch-Site") is None):
            return False
        self._respond(403, {"ok": False, "code": "request_forbidden",
                            "message": "The Painter bridge only accepts requests from local applications."})
        return True

    def do_GET(self) -> None:
        if self._refuse_browser_request():
            return
        if self.path != "/status":
            self._respond(404, {"ok": False, "code": "not_found", "message": "Unknown endpoint."})
            return
        self._respond(200, self.owner.snapshot())

    def do_POST(self) -> None:
        if self._refuse_browser_request():
            return
        if self.path != "/open":
            self._respond(404, {"ok": False, "code": "not_found", "message": "Unknown endpoint."})
            return
        try:
            length = int(self.headers.get("Content-Length") or "0")
        except ValueError:
            length = -1
        if length <= 0 or length > MAX_BODY_BYTES:
            self._respond(413 if length > MAX_BODY_BYTES else 400,
                          {"ok": False, "code": "invalid_body", "message": "The request body is missing or too large."})
            return
        try:
            request = validate_open(json.loads(self.rfile.read(length).decode("utf-8")))
        except (ValueError, UnicodeDecodeError) as error:
            self._respond(400, {"ok": False, "code": "invalid_request", "message": str(error)})
            return
        state = self.owner.snapshot()
        if state.get("settingUp"):
            self._respond(409, {"ok": False, "code": "busy",
                                "message": "Painter is still setting up the previous project."})
            return
        if state.get("projectOpen") and state.get("jobId") != request["jobId"]:
            self._respond(409, {"ok": False, "code": "project_open",
                                "message": "Another project is open in Painter. Save and close it, then send again."})
            return
        try:
            self.owner.jobs.put_nowait(request)
        except queue.Full:
            self._respond(503, {"ok": False, "code": "busy", "message": "Painter has too many pending jobs."})
            return
        self._respond(202, {"ok": True, "queued": True})
