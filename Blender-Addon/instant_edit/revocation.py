"""Durable, authenticated revocation of plugin-owned export contexts."""

from __future__ import annotations

import json
import os
import queue
import threading
import urllib.request
import uuid
from urllib.error import HTTPError, URLError

import bpy

from .cache import ensure_cache_root
from .context import _value


SCHEMA = "instant-edit.pending-revocations"
VERSION = 1
REQUEST_TIMEOUT_SECONDS = 2
MAX_RESPONSE_SIZE = 64 * 1024

_lock = threading.RLock()
_results = queue.Queue()
_worker_running = False
_generation = 0


def _queue_path():
    return ensure_cache_root() / "pending-context-revocations.json"


def _load_locked() -> list[dict]:
    path = _queue_path()
    if not path.exists():
        return []
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError("pending context revocations are unreadable") from error
    if not isinstance(payload, dict) or payload.get("schema") != SCHEMA or payload.get("version") != VERSION:
        raise ValueError("pending context revocations have an invalid format")
    records = payload.get("revocations", [])
    if not isinstance(records, list):
        raise ValueError("pending context revocations have an invalid format")
    return [record for record in records if isinstance(record, dict)]


def _save_locked(records: list[dict]) -> None:
    path = _queue_path()
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    payload = {"schema": SCHEMA, "version": VERSION, "revocations": records}
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(payload, stream, ensure_ascii=False, separators=(",", ":"))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass


def _ports_for_collection(collection) -> list[int]:
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


def queue_context_revocations(collections) -> int:
    """Persist tombstones before callers remove the scene's local metadata."""
    additions = []
    for collection in collections:
        if bool(_value(collection, "legacy", False)):
            continue
        context_id = _value(collection, "context_id", "")
        import_id = _value(collection, "import_id", "")
        capability = _value(collection, "capability", "")
        ports = _ports_for_collection(collection)
        if not all(isinstance(value, str) and value for value in (context_id, import_id, capability)) or not ports:
            raise ValueError(
                f"context {context_id or '<unknown>'} has incomplete revocation metadata"
            )
        additions.append({
            "contextId": context_id,
            "importId": import_id,
            "capability": capability,
            "ports": ports,
        })
    if not additions:
        return 0

    with _lock:
        records = _load_locked()
        known = {(item.get("contextId"), item.get("importId")) for item in records}
        for item in additions:
            key = (item["contextId"], item["importId"])
            if key not in known:
                records.append(item)
                known.add(key)
        _save_locked(records)
    return len(additions)


def _send(record: dict) -> bool:
    body = json.dumps({
        "schema": "instant-edit.context-revoke",
        "version": 1,
        "contextId": record.get("contextId"),
        "importId": record.get("importId"),
        "capability": record.get("capability"),
    }).encode("utf-8")
    for port in record.get("ports", []):
        if not isinstance(port, int) or not 1 <= port <= 65535:
            continue
        request = urllib.request.Request(
            f"http://127.0.0.1:{port}/context/revoke",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
                response_body = response.read(MAX_RESPONSE_SIZE + 1)
                status = getattr(response, "status", None) or response.getcode()
            if len(response_body) <= MAX_RESPONSE_SIZE and 200 <= status < 300:
                result = json.loads(response_body.decode("utf-8"))
                if isinstance(result, dict) and result.get("ok"):
                    return True
        except (HTTPError, URLError, TimeoutError, OSError, ValueError, UnicodeError):
            continue
    return False


def _worker(generation: int, records: list[dict]) -> None:
    completed = []
    for record in records:
        if _send(record):
            completed.append((record.get("contextId"), record.get("importId")))
    _results.put((generation, completed))


def _poll_results():
    global _worker_running
    while True:
        try:
            generation, completed = _results.get_nowait()
        except queue.Empty:
            return 0.1
        if generation == _generation:
            break
    _worker_running = False
    if completed:
        completed = set(completed)
        try:
            with _lock:
                records = _load_locked()
                _save_locked([
                    item for item in records
                    if (item.get("contextId"), item.get("importId")) not in completed
                ])
        except Exception as error:
            print(f"Instant Edit: could not update context revocations: {error}")
    return None


def schedule_revocations() -> None:
    global _worker_running, _generation
    if _worker_running:
        return
    try:
        with _lock:
            records = _load_locked()
    except Exception as error:
        print(f"Instant Edit: could not load context revocations: {error}")
        return
    if not records:
        return
    _generation += 1
    generation = _generation
    _worker_running = True
    threading.Thread(
        target=_worker,
        args=(generation, records),
        name="InstantEditContextRevocation",
        daemon=True,
    ).start()
    if not bpy.app.timers.is_registered(_poll_results):
        bpy.app.timers.register(_poll_results, first_interval=0.1)


def cancel_revocations() -> None:
    global _worker_running, _generation
    _generation += 1
    _worker_running = False
    try:
        bpy.app.timers.unregister(_poll_results)
    except Exception:
        pass
