# Modified for XIV Instant Edit, 2026.
import json
import socket
import threading

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from queue       import Empty, Full, Queue

import bpy


MAX_IMPORT_BODY_SIZE = 1024 * 1024
MAX_IMPORT_QUEUE_SIZE = 32
REQUEST_TIMEOUT_SECONDS = 5
IMPORT_OPTIONS_CAPABILITY = "instant-edit.import-options.v1"
MATERIAL_PREVIEW_CAPABILITY = "instant-edit.material-preview.v1"
CACHE_HANDOFF_CAPABILITY = "instant-edit.cache-handoff.v1"

_import_queue: Queue = Queue(maxsize=MAX_IMPORT_QUEUE_SIZE)
_server              = None
_thread               = None
_port                 = 42424
_callback_port        = 42428
_server_error         = ""


def _string(data: dict, *names: str, required: bool = False, max_length: int = 1024) -> str:
    value = next((data.get(name) for name in names if name in data), "")
    if value is None and not required:
        return ""
    if not isinstance(value, str) or len(value) > max_length or (required and not value):
        label = names[0]
        raise ValueError(f"{label} must be a non-empty string" if required else f"{label} must be a string")
    return value


def _normalise_import_options(value) -> dict:
    """Validate and normalize optional scene setup requested by the plugin."""
    if value is None:
        return {
            "armatureMode": "generated",
            "targetObject": "Skeleton",
            "applyTexturesAndMaterials": False,
            "excludeBodyAndGeneralMaterials": False,
        }
    if not isinstance(value, dict):
        raise ValueError("importOptions must be an object")

    mode = value.get("armatureMode", "generated")
    target = value.get("targetObject", "Skeleton")
    if not isinstance(mode, str) or mode not in {"generated", "existing"}:
        raise ValueError("importOptions.armatureMode is invalid")
    if not isinstance(target, str) or not target.strip() or len(target) > 128:
        raise ValueError("importOptions.targetObject must be a non-empty string of at most 128 characters")
    apply_preview = value.get("applyTexturesAndMaterials", False)
    if not isinstance(apply_preview, bool):
        raise ValueError("importOptions.applyTexturesAndMaterials must be a boolean")
    exclude_body = value.get("excludeBodyAndGeneralMaterials", False)
    if not isinstance(exclude_body, bool):
        raise ValueError("importOptions.excludeBodyAndGeneralMaterials must be a boolean")
    if exclude_body and not apply_preview:
        raise ValueError("importOptions.excludeBodyAndGeneralMaterials requires material previews")
    return {
        "armatureMode": mode,
        "targetObject": target.strip(),
        "applyTexturesAndMaterials": apply_preview,
        "excludeBodyAndGeneralMaterials": exclude_body,
    }


class _ImportHandler(BaseHTTPRequestHandler):

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(REQUEST_TIMEOUT_SECONDS)

    def do_GET(self) -> None:
        if self.path.rstrip("/") == "/status":
            self._respond(200, {
                "ok": True,
                "ready": True,
                "addon": "XIV Instant Edit",
                "addonId": "xiv_instant_edit",
                "capabilities": [
                    IMPORT_OPTIONS_CAPABILITY,
                    MATERIAL_PREVIEW_CAPABILITY,
                    CACHE_HANDOFF_CAPABILITY,
                ],
            })
        else:
            self._respond(404, {"ok": False, "error": "not found"})

    def do_POST(self) -> None:
        if self.path.rstrip("/") != "/import":
            self._respond(404, {"ok": False, "error": "not found"})
            return

        try:
            length_header = self.headers.get("Content-Length")
            if length_header is None:
                self._respond(411, {"ok": False, "error": "Content-Length is required"})
                return

            length = int(length_header)
            if length < 0:
                raise ValueError("Content-Length must not be negative")
            if length > MAX_IMPORT_BODY_SIZE:
                self._respond(413, {"ok": False, "error": "request body is too large"})
                return

            body = self.rfile.read(length)
            if len(body) != length:
                self._respond(400, {"ok": False, "error": "incomplete request body"})
                return

            data = json.loads(body)
            data = self._validate_import(data)
            cached = False
            if data.get("schema") == "instant-edit.context" and data.get("version") == 1:
                from .cache import stage_import

                data = stage_import(data)
                cached = True
        except (socket.timeout, TimeoutError):
            self._respond(408, {"ok": False, "error": "request body timed out"})
            return
        except Exception as e:
            self._respond(400, {"ok": False, "error": f"invalid request: {e}"})
            return

        try:
            _import_queue.put_nowait(data)
        except Full:
            cache_job = data.get("cacheJobDirectory", "")
            if cache_job:
                from .cache import remove_job
                remove_job(cache_job)
            self._respond(503, {"ok": False, "error": "import queue is full"})
            return
        self._respond(200, {"ok": True, "queued": True, "cached": cached})

    @staticmethod
    def _validate_import(data) -> dict:
        if not isinstance(data, dict):
            raise ValueError("JSON body must be an object")

        # Transitional compatibility for the first v1 plugin build, which
        # wrapped the context in an instant-edit.import command envelope.
        import_options = _normalise_import_options(data.get("importOptions"))
        if data.get("schema") == "instant-edit.import" and isinstance(data.get("context"), dict):
            nested = data["context"]
            if nested.get("schema") != "instant-edit.context" or nested.get("version") != 1:
                raise ValueError("nested context must use instant-edit.context v1")
            data = {
                **nested,
                "schema": "instant-edit.context",
                "version": 1,
                "filePath": data.get("filePath", ""),
                "displayName": data.get("name", ""),
                "targetFilePath": nested.get("targetFilePath", ""),
                "targetRelativePath": nested.get("targetRelativePath", ""),
                "managedDestination": nested.get(
                    "managedDestination", nested.get("targetFolder", nested.get("modName", ""))
                ),
                "sourceModDirectory": nested.get("sourceModDirectory", nested.get("modName", "")),
                "sourceModName": nested.get("sourceModName", nested.get("modName", "")),
                "resourceManifestVersion": nested.get("resourceManifestVersion", 0),
                "resourceManifestStatus": nested.get("resourceManifestStatus", "legacy"),
                "previewManifestPath": data.get("previewManifestPath", ""),
                "importOptions": import_options,
            }
        elif data.get("schema") == "instant-edit.import" and data.get("context") is None:
            # Older plugin builds used the command envelope without a context.
            data = {key: value for key, value in data.items()
                    if key not in {"schema", "version", "command", "context"}}
            data["importOptions"] = import_options

        # v1 is the authoritative protocol. Legacy is intentionally limited
        # to the old flat fields below and is never treated as a safe context.
        versioned = any(field in data for field in ("schema", "version", "contextId", "pluginInstanceId"))
        if versioned:
            if data.get("schema") != "instant-edit.context":
                raise ValueError("schema must be instant-edit.context")
            if isinstance(data.get("version"), bool) or data.get("version") != 1:
                raise ValueError("version must be 1")

            plugin_instance_id = _string(data, "pluginInstanceId", required=True, max_length=256)
            context_id = _string(data, "contextId", required=True, max_length=256)
            capability = _string(data, "capability", required=True, max_length=1024)
            file_path = _string(data, "filePath", required=True, max_length=4096)
            source_game_path = _string(data, "sourceGamePath", "gamePath", required=True, max_length=4096)
            managed_destination = _string(
                data, "managedDestination", "targetFolder", required=True, max_length=4096
            )
            target_file_path = _string(data, "targetFilePath", required=True, max_length=4096)
            source_mod_directory = _string(
                data, "sourceModDirectory", "modName", required=True, max_length=256
            )
            source_mod_name = _string(
                data, "sourceModName", "modName", required=True, max_length=512
            )
            source_mod_root_path = _string(
                data, "sourceModRootPath", max_length=4096
            )
            target_relative_path = _string(
                data, "targetRelativePath", max_length=4096
            )
            preview_manifest_path = _string(
                data, "previewManifestPath", max_length=4096
            )
            callback_port = data.get("callbackPort", data.get("pluginPort", 0)) or _callback_port
            if isinstance(callback_port, bool) or not isinstance(callback_port, int) or not 1 <= callback_port <= 65535:
                raise ValueError("callbackPort must be between 1 and 65535")
            object_index = data.get("objectIndex", -1)
            if isinstance(object_index, bool) or not isinstance(object_index, int):
                raise ValueError("objectIndex must be an integer")
            resource_manifest_version = data.get("resourceManifestVersion", 0)
            if isinstance(resource_manifest_version, bool) or not isinstance(resource_manifest_version, int) or resource_manifest_version < 0:
                raise ValueError("resourceManifestVersion must be a non-negative integer")
            resource_manifest_status = data.get("resourceManifestStatus", "legacy")
            if resource_manifest_status not in {"legacy", "capture_failed", "ready"}:
                raise ValueError("resourceManifestStatus must be legacy, capture_failed, or ready")

            return {
                **data,
                "schema": "instant-edit.context",
                "version": 1,
                "pluginInstanceId": plugin_instance_id,
                "contextId": context_id,
                "capability": capability,
                "filePath": file_path,
                "sourceGamePath": source_game_path,
                "managedDestination": managed_destination,
                "targetFilePath": target_file_path,
                "sourceModDirectory": source_mod_directory,
                "sourceModName": source_mod_name,
                "sourceModRootPath": source_mod_root_path,
                "targetRelativePath": target_relative_path,
                "resourceManifestVersion": resource_manifest_version,
                "resourceManifestStatus": resource_manifest_status,
                "previewManifestPath": preview_manifest_path,
                "callbackPort": callback_port,
                "name": _string(data, "displayName", "name", max_length=255),
                "importOptions": import_options,
            }

        # Legacy /import compatibility. This branch deliberately produces no
        # context reference and cannot be used by the safe exporter.
        for field in ("filePath", "gamePath", "name", "modName"):
            if field in data and not isinstance(data[field], str):
                raise ValueError(f"{field} must be a string")

        if "filePath" in data and not data["filePath"]:
            raise ValueError("filePath must not be empty")

        for field in ("objectIndex", "callbackPort"):
            value = data.get(field)
            if field in data and (
                value is None or isinstance(value, bool) or not isinstance(value, int)
            ):
                raise ValueError(f"{field} must be an integer")

        callback_port = data.get("callbackPort")
        if callback_port is not None and not 1 <= callback_port <= 65535:
            raise ValueError("callbackPort must be between 1 and 65535")

        if len(data.get("modName", "")) > 64:
            raise ValueError("modName is too long")

        data["importOptions"] = import_options
        return data


    def _respond(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args) -> None:
        pass


def start_server(port: int = 42424) -> bool:
    """Starts the HTTP listener that receives import commands from the XIV Instant Edit plugin."""
    global _server, _thread, _port, _server_error

    if _server is not None:
        if port == _port:
            return True
        stop_server()

    try:
        server = ThreadingHTTPServer(("127.0.0.1", port), _ImportHandler)
    except OSError as e:
        _server = None
        _thread = None
        _server_error = str(e)
        print(f"XIV Instant Edit: could not listen on port {port}: {_server_error}")
        return False

    _port   = port
    _server = server
    _server_error = ""
    _thread = threading.Thread(target=_server.serve_forever, daemon=True)
    _thread.start()
    print(f"XIV Instant Edit: listening on port {port}")
    return True


def set_server_port(port: int) -> bool:
    return start_server(port)


def set_callback_port(port: int) -> None:
    global _callback_port
    if not 1 <= port <= 65535:
        raise ValueError("callback port must be between 1 and 65535")
    _callback_port = port


def get_server_error() -> str:
    return _server_error


def stop_server() -> None:
    global _server, _thread, _port

    if _server is not None:
        try:
            _server.shutdown()
            _server.server_close()
        except Exception:
            pass
        _server = None
        _thread = None
        _port   = 42424


def poll_import_queue() -> float:
    """Timer callback that runs pending imports on Blender's main thread."""
    try:
        while True:
            try:
                data = _import_queue.get_nowait()
            except Empty:
                break
            try:
                if bpy.context.mode != "OBJECT":
                    bpy.ops.object.mode_set(mode="OBJECT")

                bpy.ops.xiv_ie.instant_import(
                    "EXEC_DEFAULT",
                    file_path=data.get("filePath", ""),
                    game_path=data.get("gamePath", ""),
                    object_index=int(data.get("objectIndex", -1)),
                    import_name=data.get("name", ""),
                    callback_port=data.get("callbackPort", 0),
                    mod_name=data.get("modName", ""),
                    schema=data.get("schema", ""),
                    version=int(data.get("version", 0)),
                    plugin_instance_id=data.get("pluginInstanceId", ""),
                    context_id=data.get("contextId", ""),
                    capability=data.get("capability", ""),
                    source_game_path=data.get("sourceGamePath", data.get("gamePath", "")),
                    managed_destination=data.get("managedDestination", ""),
                    target_file_path=data.get("targetFilePath", ""),
                    source_mod_directory=data.get("sourceModDirectory", ""),
                    source_mod_name=data.get("sourceModName", ""),
                    source_mod_root_path=data.get("sourceModRootPath", ""),
                    target_relative_path=data.get("targetRelativePath", ""),
                    resource_manifest_version=int(data.get("resourceManifestVersion", 0)),
                    resource_manifest_status=data.get("resourceManifestStatus", "legacy"),
                    import_id=data.get("importId", ""),
                    armature_mode=data.get("importOptions", {}).get("armatureMode", "generated"),
                    armature_target=data.get("importOptions", {}).get("targetObject", "Skeleton"),
                    apply_textures_and_materials=data.get("importOptions", {}).get("applyTexturesAndMaterials", False),
                    preview_manifest_path=data.get("previewManifestPath", ""),
                    cache_job_directory=data.get("cacheJobDirectory", ""),
                )
            except Exception as e:
                print(f"XIV Instant Edit: import failed: {e}")
    except Exception as e:
        print(f"XIV Instant Edit: queue error: {e}")

    return 0.5
