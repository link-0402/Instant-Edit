"""Small shared HTTP client for loopback calls to the Dalamud plugin."""

import json
import urllib.request
from urllib.error import HTTPError

from .context import _value


def candidate_ports(collection) -> list[int]:
    ports = []
    stored = _value(collection, "callback_port", 0)
    if isinstance(stored, int) and 1 <= stored <= 65535:
        ports.append(stored)
    try:
        from ..preferences import get_prefs

        configured = get_prefs().instant_edit_plugin_port
        if isinstance(configured, int) and 1 <= configured <= 65535 and configured not in ports:
            ports.append(configured)
    except Exception:
        pass
    return ports


def post_json(
    port: int,
    endpoint: str,
    payload: dict,
    *,
    timeout: float,
    max_response_size: int,
) -> tuple[int, bytes]:
    """POST JSON and return status/body for both success and HTTP error responses."""
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{endpoint}",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            status = getattr(response, "status", None) or response.getcode()
            body = response.read(max_response_size + 1)
    except HTTPError as error:
        status = error.code
        body = error.read(max_response_size + 1)
    if len(body) > max_response_size:
        raise ValueError("plugin response is too large")
    return status, body
