"""Loopback calls from Painter to Instant Edit's listener (its export server)."""

import json
import urllib.request
from urllib.error import HTTPError, URLError

MAX_RESPONSE_BYTES = 1024 * 1024


class PluginUnavailable(ConnectionError):
    """Instant Edit is not running, or no listener answered on the known ports."""


class PluginRefused(RuntimeError):
    """Instant Edit answered but rejected the request."""

    def __init__(self, status: int, message: str, code: str = ""):
        super().__init__(message)
        self.status = status
        self.code = code


def candidate_ports(stored: int, configured: int) -> list:
    ports = []
    for port in (stored, configured):
        if isinstance(port, int) and 1 <= port <= 65535 and port not in ports:
            ports.append(port)
    return ports


def _request(port: int, endpoint: str, payload, timeout: float):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{endpoint}",
        data=data,
        headers={"Content-Type": "application/json"} if data is not None else {},
        method="GET" if data is None else "POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            status = getattr(response, "status", None) or response.getcode()
            body = response.read(MAX_RESPONSE_BYTES + 1)
    except HTTPError as error:
        with error:
            status = error.code
            body = error.read(MAX_RESPONSE_BYTES + 1)
    if len(body) > MAX_RESPONSE_BYTES:
        raise PluginRefused(status, "Instant Edit's response was too large.")
    try:
        parsed = json.loads(body.decode("utf-8")) if body else {}
    except ValueError:
        parsed = {}
    return status, parsed if isinstance(parsed, dict) else {}


def call(ports: list, endpoint: str, payload=None, timeout: float = 5.0):
    """Sends to the first port that answers; returns (port, status, body).

    Connection failures move on to the next port. An answer with an error status raises
    PluginRefused, with Instant Edit's message.
    """
    last_error = None
    for port in ports:
        try:
            status, body = _request(port, endpoint, payload, timeout)
        except (URLError, ConnectionError, TimeoutError, OSError) as error:
            last_error = error
            continue
        if status >= 400:
            message = body.get("message") or body.get("error") or f"Instant Edit refused the request ({status})."
            raise PluginRefused(status, str(message), str(body.get("code") or ""))
        return port, status, body
    raise PluginUnavailable(
        "Instant Edit is not reachable. Start the game with the plugin loaded."
        + (f" ({last_error})" if last_error else ""))


def is_available(ports: list) -> bool:
    try:
        call(ports, "/status", None, timeout=1.5)
        return True
    except (PluginUnavailable, PluginRefused):
        return False
