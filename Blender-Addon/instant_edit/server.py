# Modified for XIV Instant Edit, 2026.
import json
import socket
import threading
import uuid

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from queue       import Empty, Full, Queue

import bpy

from .context import is_safe_game_model_path


MAX_IMPORT_BODY_SIZE = 1024 * 1024
MAX_IMPORT_QUEUE_SIZE = 32
REQUEST_TIMEOUT_SECONDS = 5
IMPORT_OPTIONS_CAPABILITY = "instant-edit.import-options.v1"
MATERIAL_PREVIEW_CAPABILITY = "instant-edit.material-preview.v1"
CACHE_HANDOFF_CAPABILITY = "instant-edit.cache-handoff.v1"
VANILLA_CONTEXT_CAPABILITY = "instant-edit.vanilla-context.v1"

_import_queue: Queue = Queue(maxsize=MAX_IMPORT_QUEUE_SIZE)
_server              = None
_thread               = None
_port                 = 42424
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
                    VANILLA_CONTEXT_CAPABILITY,
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
            from .cache import stage_import

            data = stage_import(data)
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
        self._respond(200, {"ok": True, "queued": True, "cached": True})

    @staticmethod
    def _validate_import(data) -> dict:
        if not isinstance(data, dict):
            raise ValueError("JSON body must be an object")

        import_options = _normalise_import_options(data.get("importOptions"))
        if data.get("schema") != "instant-edit.context":
            raise ValueError("schema must be instant-edit.context")
        version = data.get("version")
        if isinstance(version, bool) or version not in {1, 2}:
            raise ValueError("version must be 1 or 2")

        plugin_instance_id = _string(data, "pluginInstanceId", required=True, max_length=256)
        context_id = _string(data, "contextId", required=True, max_length=256)
        import_id = _string(data, "importId", required=True, max_length=256)
        capability = _string(data, "capability", required=True, max_length=1024)
        file_path = _string(data, "filePath", required=True, max_length=4096)
        source_game_path = _string(data, "sourceGamePath", required=True, max_length=4096)
        source_kind = _string(data, "sourceKind", max_length=16) or "mod"
        resolved_game_path = _string(data, "resolvedGamePath", max_length=4096) or source_game_path
        destination_state = _string(data, "destinationState", max_length=32) or "ready"
        managed_destination = _string(data, "managedDestination", max_length=4096)
        target_file_path = _string(data, "targetFilePath", max_length=4096)
        source_mod_directory = _string(data, "sourceModDirectory", max_length=256)
        source_mod_name = _string(data, "sourceModName", max_length=512)
        source_mod_root_path = _string(data, "sourceModRootPath", max_length=4096)
        target_relative_path = _string(data, "targetRelativePath", max_length=4096)
        target_collection_id = _string(data, "targetCollectionId", max_length=64)
        target_collection_name = _string(data, "targetCollectionName", max_length=512)
        preview_manifest_path = _string(data, "previewManifestPath", max_length=4096)
        display_name = _string(data, "displayName", max_length=255)
        if source_kind not in {"mod", "game"} or destination_state not in {"ready", "new_mod_required"}:
            raise ValueError("sourceKind or destinationState is invalid")
        if version == 1 and (source_kind != "mod" or destination_state != "ready"):
            raise ValueError("version 1 contexts must be ready mod contexts")
        if source_kind == "game" and (
            not is_safe_game_model_path(source_game_path) or
            not is_safe_game_model_path(resolved_game_path)
        ):
            raise ValueError("game context contains an unsafe model path")
        if destination_state == "ready" and not all((managed_destination, target_file_path,
                                                       source_mod_directory, source_mod_name)):
            raise ValueError("ready context is missing Penumbra destination data")
        if destination_state == "new_mod_required" and (
            source_kind != "game" or any((managed_destination, target_file_path,
                                          source_mod_directory, source_mod_name,
                                          source_mod_root_path, target_relative_path))
        ):
            raise ValueError("pending game context contains unexpected destination data")
        if target_collection_id:
            try:
                if uuid.UUID(target_collection_id).int == 0:
                    raise ValueError
            except (ValueError, AttributeError) as error:
                raise ValueError("targetCollectionId must be a non-empty UUID") from error

        callback_port = data.get("callbackPort")
        if isinstance(callback_port, bool) or not isinstance(callback_port, int) or not 1 <= callback_port <= 65535:
            raise ValueError("callbackPort must be between 1 and 65535")
        object_index = data.get("objectIndex")
        if isinstance(object_index, bool) or not isinstance(object_index, int) or not 0 <= object_index <= 65535:
            raise ValueError("objectIndex must be between 0 and 65535")
        resource_manifest_version = data.get("resourceManifestVersion")
        if isinstance(resource_manifest_version, bool) or not isinstance(resource_manifest_version, int):
            raise ValueError("resourceManifestVersion must be an integer")
        resource_manifest_status = data.get("resourceManifestStatus")
        if resource_manifest_status not in {"capture_failed", "ready"}:
            raise ValueError("resourceManifestStatus must be capture_failed or ready")
        expected_manifest_version = 2 if resource_manifest_status == "ready" else 0
        if resource_manifest_version != expected_manifest_version:
            raise ValueError("resource manifest version and status do not agree")

        return {
            **data,
            "pluginInstanceId": plugin_instance_id,
            "contextId": context_id,
            "importId": import_id,
            "capability": capability,
            "filePath": file_path,
            "sourceGamePath": source_game_path,
            "sourceKind": source_kind,
            "resolvedGamePath": resolved_game_path,
            "destinationState": destination_state,
            "managedDestination": managed_destination,
            "targetFilePath": target_file_path,
            "sourceModDirectory": source_mod_directory,
            "sourceModName": source_mod_name,
            "sourceModRootPath": source_mod_root_path,
            "targetRelativePath": target_relative_path,
            "targetCollectionId": target_collection_id,
            "targetCollectionName": target_collection_name,
            "resourceManifestVersion": resource_manifest_version,
            "resourceManifestStatus": resource_manifest_status,
            "previewManifestPath": preview_manifest_path,
            "callbackPort": callback_port,
            "objectIndex": object_index,
            "name": display_name,
            "importOptions": import_options,
        }


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
                    object_index=int(data.get("objectIndex", -1)),
                    import_name=data.get("name", ""),
                    callback_port=data.get("callbackPort", 0),
                    schema=data.get("schema", ""),
                    version=int(data.get("version", 0)),
                    plugin_instance_id=data.get("pluginInstanceId", ""),
                    context_id=data.get("contextId", ""),
                    capability=data.get("capability", ""),
                    source_game_path=data.get("sourceGamePath", ""),
                    source_kind=data.get("sourceKind", "mod"),
                    resolved_game_path=data.get("resolvedGamePath", data.get("sourceGamePath", "")),
                    destination_state=data.get("destinationState", "ready"),
                    managed_destination=data.get("managedDestination", ""),
                    target_file_path=data.get("targetFilePath", ""),
                    source_mod_directory=data.get("sourceModDirectory", ""),
                    source_mod_name=data.get("sourceModName", ""),
                    source_mod_root_path=data.get("sourceModRootPath", ""),
                    target_relative_path=data.get("targetRelativePath", ""),
                    target_collection_id=data.get("targetCollectionId", ""),
                    target_collection_name=data.get("targetCollectionName", ""),
                    resource_manifest_version=int(data.get("resourceManifestVersion", 0)),
                    resource_manifest_status=data.get("resourceManifestStatus", "capture_failed"),
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
