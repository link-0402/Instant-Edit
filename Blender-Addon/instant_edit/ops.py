# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy
import json
import hashlib
import uuid
import urllib.request
import time
from urllib.error import HTTPError, URLError

from pathlib   import Path
from bpy.types import Operator, Context

from ..io.model      import ModelImport
from ..materials     import group_mesh_objects
from ..mesh.export   import export_result, get_export_stats, check_triangulation
from ..mesh.objects  import visible_meshobj
from ..properties    import get_settings
from ..xivpy.model   import XIVModel
from .props          import get_instant_edit_props
from .context        import (SCHEMA, VERSION, ContextValidationError,
                             _value, active_context, clear_context_metadata,
                             context_collections, create_collection, tag_object,
                             validate_context)
from .material_preview import (cleanup_preview_bundle, discard_preview_data,
                               load_preview_manifest)
from .cache import create_job, finish_job


MAX_PLUGIN_RESPONSE_SIZE = 64 * 1024
EXPORT_STATUS_POLL_ATTEMPTS = 30
INVALID_VARIANT_CHARS = frozenset('<>:"/\\|?*')
_QUICK_EXPORT_TARGET_ITEMS = []


class PluginResponseError(ValueError):
    def __init__(self, status: int, code: str, message: str):
        self.status = status
        self.code = code
        self.message = message
        super().__init__(f"plugin returned HTTP {status} ({code}): {message}")


def normalise_variant_name(value: str) -> str:
    """Return a safe sibling .mdl file name without accepting a path."""
    name = (value or "").strip()
    if name.lower().endswith(".mdl"):
        name = name[:-4].rstrip()
    if not name or name in {".", ".."}:
        raise ValueError("Enter a variant name.")
    if len(name) > 120:
        raise ValueError("Variant name is too long.")
    if any(char in INVALID_VARIANT_CHARS or ord(char) < 32 for char in name):
        raise ValueError("Variant name contains characters that cannot be used in a file name.")
    return name


def validate_variant_name(source_game_path: str, variant_name: str) -> str:
    """Normalize a variant name and reject the original model's file name."""
    name = normalise_variant_name(variant_name)
    source_name = (source_game_path or "").replace("\\", "/").rsplit("/", 1)[-1]
    if source_name.lower().endswith(".mdl"):
        source_name = source_name[:-4]
    if source_name and name.casefold() == source_name.casefold():
        raise ValueError("Variant name must differ from the originally imported model name.")
    return name


def normalise_variant_group_name(value: str) -> str:
    """Return a safe Penumbra option-group name."""
    name = (value or "").strip()
    if not name:
        raise ValueError("Enter a Penumbra option group name.")
    if len(name) > 120:
        raise ValueError("Penumbra option group name is too long.")
    if any(ord(char) < 32 for char in name):
        raise ValueError("Penumbra option group name contains a control character.")
    return name


def variant_game_path(source_game_path: str, variant_name: str) -> str:
    directory, separator, _ = source_game_path.rpartition("/")
    if not separator:
        raise ValueError("The source model has no parent game directory.")
    return f"{directory}/{variant_name}.mdl"


def _snapshot_object_state(context: Context) -> tuple:
    """Capture the user state that temporary armature setup can disturb."""
    return (
        tuple(context.selected_objects),
        context.view_layer.objects.active,
        context.mode,
    )


def _restore_object_state(context: Context, state: tuple) -> None:
    """Restore selection, active object, and mode without changing scene data."""
    selected, active, mode = state

    if context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")

    for obj in tuple(context.selected_objects):
        obj.select_set(False)
    for obj in selected:
        if obj.name in bpy.data.objects:
            obj.select_set(True)

    context.view_layer.objects.active = (
        active if active is not None and active.name in bpy.data.objects else None
    )
    if mode != "OBJECT" and context.view_layer.objects.active is not None:
        bpy.ops.object.mode_set(mode=mode)


def _remove_staging_objects(objects: list, collection) -> None:
    """Remove only objects recorded as belonging to this failed import."""
    seen = set()
    for obj in reversed(objects):
        if obj is None or obj.as_pointer() in seen:
            continue
        seen.add(obj.as_pointer())
        if obj.name not in bpy.data.objects:
            continue

        data = getattr(obj, "data", None)
        object_type = getattr(obj, "type", "")
        bpy.data.objects.remove(obj, do_unlink=True)
        # Imported mesh/armature datablocks are safe to discard when no other
        # object uses them. Materials are deliberately not removed.
        if data is not None and getattr(data, "users", 1) == 0:
            data_collection = {
                "MESH": bpy.data.meshes,
                "ARMATURE": bpy.data.armatures,
            }.get(object_type)
            if data_collection is not None and data.name in data_collection:
                data_collection.remove(data)

    if collection is not None and collection.name in bpy.data.collections:
        bpy.data.collections.remove(collection, do_unlink=True)


class InstantImport(Operator):
    bl_idname      = "xiv_ie.instant_import"
    bl_label       = "Instant Import"
    bl_description = "Imports a model sent by the Instant Edit plugin into the active scene"
    bl_options     = {"UNDO"}

    file_path: bpy.props.StringProperty(options={'HIDDEN'})  # type: ignore
    game_path: bpy.props.StringProperty(options={'HIDDEN'})  # type: ignore
    object_index: bpy.props.IntProperty(default=-1, options={'HIDDEN'})  # type: ignore
    import_name: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    callback_port: bpy.props.IntProperty(default=0, options={'HIDDEN'})  # type: ignore
    mod_name: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    schema: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    version: bpy.props.IntProperty(default=0, options={'HIDDEN'})  # type: ignore
    plugin_instance_id: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    context_id: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    capability: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    source_game_path: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    managed_destination: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    target_file_path: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    source_mod_directory: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    source_mod_name: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    source_mod_root_path: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    target_relative_path: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    import_id: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    armature_mode: bpy.props.EnumProperty(
        items=[
            ("generated", "Generated", "Create an armature for this import"),
            ("existing", "Existing", "Use an existing scene armature"),
        ],
        default="generated",
        options={'HIDDEN'},
    )  # type: ignore
    armature_target: bpy.props.StringProperty(default="Skeleton", options={'HIDDEN'})  # type: ignore
    apply_textures_and_materials: bpy.props.BoolProperty(default=False, options={'HIDDEN'})  # type: ignore
    preview_manifest_path: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore
    cache_job_directory: bpy.props.StringProperty(default="", options={'HIDDEN'})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        props     = get_instant_edit_props()
        file_path = Path(self.file_path)
        user_state = _snapshot_object_state(context)
        created_objects = []
        collection = None
        preview_package = None
        preview_validation_warning = ""
        if not file_path.is_file():
            props.last_status = "Import failed: file not found."
            self.report({"ERROR"}, "Model file not found.")
            return {"CANCELLED"}

        try:
            is_v1 = self.schema == SCHEMA and self.version == VERSION
            if is_v1:
                context_metadata = {
                    "context_id": self.context_id,
                    "schema": self.schema,
                    "version": self.version,
                }
                collection_metadata = {
                    **context_metadata,
                    "plugin_instance_id": self.plugin_instance_id,
                    "capability": self.capability,
                    "source_game_path": self.source_game_path or self.game_path,
                    "managed_destination": self.managed_destination,
                    "target_file_path": self.target_file_path,
                    "source_mod_directory": self.source_mod_directory,
                    "source_mod_name": self.source_mod_name,
                    "source_mod_root_path": self.source_mod_root_path,
                    "target_relative_path": self.target_relative_path,
                    "import_id": self.import_id,
                    "callback_port": self.callback_port,
                    "import_file_name": file_path.name,
                }
            else:
                # Legacy requests are accepted only inside a generated,
                # tagged context. They must never use the ambient collection.
                legacy_id = f"legacy-{uuid.uuid4().hex}"
                context_metadata = {
                    "context_id": legacy_id,
                    "schema": SCHEMA,
                    "version": VERSION,
                    "legacy": True,
                }
                collection_metadata = dict(context_metadata)

            collection_metadata["import_file_name"] = file_path.name

            collection = create_collection(context.scene, collection_metadata)

            if self.apply_textures_and_materials:
                if self.preview_manifest_path:
                    try:
                        preview_package = load_preview_manifest(self.preview_manifest_path, str(file_path))
                    except Exception as error:
                        preview_validation_warning = f"Material preview unavailable: {error}"
                else:
                    preview_validation_warning = "Material preview unavailable: the plugin did not provide a bundle."

            object_label = self.import_name or file_path.stem
            imported_meshes = ModelImport.from_file(
                str(file_path), object_label,
                collection=collection, context_metadata=context_metadata,
                select_objects=False,
                require_collection=True,
                created_objects=created_objects,
                material_preview=preview_package,
                material_context_key=self.context_id or collection.name,
            )
            if self.armature_mode == "existing":
                self._bind_existing_armature(
                    context,
                    imported_meshes,
                    collection,
                    self.armature_target,
                    created_objects,
                )
            else:
                self._create_armature(
                    context,
                    file_path,
                    imported_meshes,
                    collection,
                    context_metadata,
                    created_objects,
                )

            for obj in imported_meshes:
                tag_object(obj, context_metadata)

            props.game_path    = self.source_game_path or self.game_path
            props.object_index = self.object_index
            props.display_name = file_path.name
            props.context_id = self.context_id if is_v1 else ""
            props.context_schema = self.schema if is_v1 else ""
            props.context_version = self.version if is_v1 else 0
            props.plugin_instance_id = self.plugin_instance_id if is_v1 else ""
            props.capability = self.capability if is_v1 else ""
            props.managed_destination = self.managed_destination if is_v1 else ""
            preview_warnings = [] if preview_package is None else preview_package.warnings
            warning_text = preview_validation_warning
            if preview_warnings:
                warning_text = "; ".join(preview_warnings[:3])
                if len(preview_warnings) > 3:
                    warning_text += f" (+{len(preview_warnings) - 3} more)"
            props.last_status = (
                f"Imported {file_path.name} with preview warnings: {warning_text}"
                if warning_text else f"Imported {file_path.name}"
            )
        except Exception as e:
            _remove_staging_objects(created_objects, collection)
            discard_preview_data(preview_package)
            props.last_status = f"Import failed: {e}"
            self.report({"ERROR"}, f"Import failed: {e}")
            return {"CANCELLED"}

        finally:
            _restore_object_state(context, user_state)
            cleanup_preview_bundle(preview_package)
            if self.cache_job_directory:
                try:
                    finish_job(self.cache_job_directory)
                except OSError as error:
                    print(f"Instant Edit: could not remove import cache job: {error}")

        if preview_validation_warning or (preview_package is not None and preview_package.warnings):
            self.report({"WARNING"}, props.last_status)
        else:
            self.report({"INFO"}, "Model imported!")
        return {"FINISHED"}

    def _bind_existing_armature(
        self,
        context: Context,
        mesh_objects,
        collection,
        target_name: str,
        created_objects=None,
    ) -> None:
        """Bind only this import's meshes to an existing scene armature."""
        target = context.scene.objects.get(target_name.strip())
        if target is None:
            raise ValueError(f'Armature object "{target_name}" was not found in the active scene')
        if target.type != "ARMATURE":
            raise ValueError(f'Blender object "{target.name}" is not an Armature')

        # ModelImport currently returns meshes only. Keep this cleanup scoped to
        # objects recorded by this import for compatibility with importers that
        # may also stage the source armature in the future.
        for obj in tuple(created_objects or ()):
            if obj.type != "ARMATURE" or collection not in obj.users_collection:
                continue
            for mesh in mesh_objects:
                if mesh.parent == obj:
                    mesh.parent = None
            data = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            if data is not None and data.users == 0 and data.name in bpy.data.armatures:
                bpy.data.armatures.remove(data)

        for obj in tuple(mesh_objects):
            if (
                obj.type != "MESH"
                or collection not in obj.users_collection
                or (created_objects is not None and obj not in created_objects)
            ):
                raise ValueError("import returned an object outside its staging collection")

            # Imported meshes are new, but remove any armature modifiers supplied
            # by a future importer before assigning the requested target.
            for modifier in tuple(obj.modifiers):
                if modifier.type == "ARMATURE":
                    obj.modifiers.remove(modifier)
            obj.parent = None
            modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
            modifier.object = target

    def _create_armature(
        self,
        context: Context,
        file_path: Path,
        mesh_objects,
        collection,
        metadata=None,
        created_objects=None,
    ) -> None:
        """Creates an armature containing every bone of the model and parents the
        returned imported mesh to it, so the standard export pipeline can run."""
        model = XIVModel.from_file(str(file_path))

        armature_data = bpy.data.armatures.new("InstantEditArmature")
        armature_obj  = bpy.data.objects.new("InstantEditArmature", armature_data)
        collection.objects.link(armature_obj)
        if created_objects is not None:
            created_objects.append(armature_obj)
        if metadata:
            tag_object(armature_obj, metadata)

        for obj in tuple(context.selected_objects):
            obj.select_set(False)
        context.view_layer.objects.active = armature_obj
        armature_obj.select_set(True)

        bpy.ops.object.mode_set(mode="EDIT")
        try:
            for bone_name in model.bones:
                edit_bone = armature_data.edit_bones.new(bone_name)
                edit_bone.head = (0, 0, 0)
                edit_bone.tail = (0, 0, 0.1)
        finally:
            bpy.ops.object.mode_set(mode="OBJECT")

        for obj in tuple(mesh_objects):
            if (
                obj.type != "MESH"
                or collection not in obj.users_collection
                or (created_objects is not None and obj not in created_objects)
            ):
                raise ValueError("import returned an object outside its staging collection")
            if obj.parent:
                raise ValueError("import returned an already-parented mesh")
            obj.parent = armature_obj
            modifier   = obj.modifiers.new(name="Armature", type="ARMATURE")
            modifier.object = armature_obj


def _valid_export_contexts(context: Context) -> list:
    refs = []
    for collection in context_collections(context.scene):
        context_id = _value(collection, "context_id", "")
        try:
            refs.append(validate_context(context_id, context.scene))
        except ContextValidationError:
            continue
    return refs


def _quick_export_target_items(_self, context):
    global _QUICK_EXPORT_TARGET_ITEMS
    refs = _valid_export_contexts(context or bpy.context)
    items = []
    for ref in refs:
        model_name = ref.source_game_path.replace("\\", "/").rsplit("/", 1)[-1]
        mod_name = ref.source_mod_name or ref.source_mod_directory
        label = f"{model_name} ({mod_name})" if mod_name else model_name
        description = f"Write the exported geometry to {ref.target_file_path}"
        items.append((ref.context_id, label, description))
    _QUICK_EXPORT_TARGET_ITEMS = items
    return _QUICK_EXPORT_TARGET_ITEMS


class QuickExport(Operator):
    bl_idname      = "xiv_ie.instant_export"
    bl_label       = "Quick Export"
    bl_description = "Exports the current model back to the game path it was imported from via Penumbra"
    bl_options     = {"UNDO"}

    target_context_id: bpy.props.EnumProperty(
        name="Export Destination",
        description="Choose the imported model that will receive all exported geometry",
        items=_quick_export_target_items,
        options={"HIDDEN", "SKIP_SAVE"},
    )  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        try:
            export_destination_context(context)
            return True
        except ContextValidationError:
            return False

    def invoke(self, context: Context, _event):
        props = get_instant_edit_props()
        if props.export_destination != "ACTIVE":
            self.target_context_id = props.export_destination
            return self.execute(context)

        refs = _valid_export_contexts(context)
        if len(refs) <= 1:
            self.target_context_id = refs[0].context_id if refs else ""
            return self.execute(context)

        try:
            preferred = active_context(context).context_id
        except ContextValidationError:
            preferred = refs[0].context_id
        self.target_context_id = preferred
        return context.window_manager.invoke_props_dialog(self, width=640)

    def draw(self, context: Context):
        self.layout.label(text="Multiple Instant Edit destinations are available.", icon="QUESTION")
        self.layout.label(text="Choose where all geometry in the selected export scope should be written.")
        self.layout.prop(self, "target_context_id")
        if self.target_context_id:
            try:
                ref = validate_context(self.target_context_id, context.scene)
                self.layout.label(text=f"Game path: {ref.source_game_path}")
                self.layout.label(text=f"Target: {ref.target_file_path}")
            except ContextValidationError:
                self.layout.label(text="The selected destination is no longer valid.", icon="ERROR")

    def execute(self, context: Context):
        try:
            props = get_instant_edit_props()
            destination = self.target_context_id or (
                props.export_destination if props.export_destination != "ACTIVE" else ""
            )
            refs = _valid_export_contexts(context)
            if not destination and len(refs) > 1:
                raise ContextValidationError(
                    "multiple Instant Edit contexts are available; invoke Quick Export and choose a destination"
                )
            perform_instant_export(context, destination or None)
        except Exception as e:
            props = get_instant_edit_props()
            props.last_status = f"Export failed: {e}"
            self.report({"ERROR"}, f"Export failed: {e}")
            return {"CANCELLED"}
        status = get_instant_edit_props().last_status
        self.report({"WARNING"} if " with warnings:" in status else {"INFO"}, status)
        get_export_stats(context)
        return {"FINISHED"}


class ClearInstantEditContexts(Operator):
    bl_idname = "xiv_ie.clear_contexts"
    bl_label = "Clear Contexts"
    bl_description = "Clear all Instant Edit context information without deleting scene objects"

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        collections = context_collections(context.scene)
        from .revocation import queue_context_revocations, schedule_revocations

        try:
            queued = queue_context_revocations(collections)
        except Exception as error:
            self.report({"ERROR"}, f"Contexts were not cleared: could not save revocations: {error}")
            return {"CANCELLED"}
        cleared = clear_context_metadata(context.scene)
        props = get_instant_edit_props()
        for field, value in {
            "game_path": "",
            "display_name": "",
            "object_index": -1,
            "context_id": "",
            "context_schema": "",
            "context_version": 0,
            "plugin_instance_id": "",
            "capability": "",
            "managed_destination": "",
            "last_export_id": "",
            "variant_group_name": "New Group",
            "last_status": f"Instant Edit contexts cleared; {queued} revocation(s) queued.",
        }.items():
            setattr(props, field, value)
        props.export_destination = "ACTIVE"
        schedule_revocations()
        self.report({"INFO"}, f"Cleared {cleared} Instant Edit context(s); revocation queued.")
        return {"FINISHED"}


class CopyInstantEditStatus(Operator):
    bl_idname = "xiv_ie.copy_status"
    bl_label = "Copy Full Import Status"
    bl_description = "Copy the complete Instant Edit status message to the clipboard"

    def execute(self, context):
        context.window_manager.clipboard = get_instant_edit_props().last_status
        self.report({"INFO"}, "Instant Edit status copied")
        return {"FINISHED"}


def build_export_payload(ref, export_id: str, mdl_path: Path, byte_size: int,
                         sha256: str, props, variant_name: str | None,
                         variant_group_name: str | None = None,
                         backup_existing: bool | None = None) -> dict:
    """Build the versioned Dalamud export envelope."""
    payload = {
        "schema": "instant-edit.export",
        "version": VERSION,
        "pluginInstanceId": ref.plugin_instance_id,
        "contextId": ref.context_id,
        "exportId": export_id,
        "capability": ref.capability,
        "filePath": str(mdl_path),
        "size": byte_size,
        "sha256": sha256,
        "redrawMode": props.redraw_mode,
        "backupExisting": bool(
            getattr(get_settings(), "backup_models_on_export", False)
            if backup_existing is None else backup_existing
        ),
    }
    if variant_name is not None:
        payload["variantName"] = variant_name
        if props.auto_setup_penumbra:
            payload["setupInPenumbra"] = True
            payload["variantGroupName"] = variant_group_name
    return payload


def _plugin_error_from_body(body: bytes, status: int) -> PluginResponseError:
    try:
        result = json.loads(body.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError):
        return PluginResponseError(status, "http_error", "plugin returned an invalid response")
    if not isinstance(result, dict):
        return PluginResponseError(status, "http_error", "plugin returned an invalid response")
    return PluginResponseError(
        status,
        str(result.get("code", "http_error")),
        str(result.get("error", result.get("message", "plugin rejected the export"))),
    )


def _decode_plugin_response(body: bytes, status: int) -> dict:
    if len(body) > MAX_PLUGIN_RESPONSE_SIZE:
        raise ValueError("plugin response is too large")
    try:
        result = json.loads(body.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError("plugin returned an invalid response") from error
    if not isinstance(result, dict):
        raise ValueError("plugin returned an invalid response")
    if not result.get("ok"):
        raise PluginResponseError(
            status,
            str(result.get("code", "plugin_error")),
            str(result.get("error", result.get("message", "unknown plugin error"))),
        )
    warnings = result.get("warnings", [])
    if not isinstance(warnings, list) or any(not isinstance(item, str) for item in warnings):
        raise ValueError("plugin returned invalid warnings")
    result["warnings"] = [item[:2048] for item in warnings[:64] if item]
    return result


def plugin_warning_summary(warnings) -> str:
    items = [item[:512] for item in warnings if isinstance(item, str) and item]
    summary = "; ".join(items[:3])
    if len(items) > 3:
        summary += f" (+{len(items) - 3} more)"
    return summary


def _request_export_status(ref, export_id: str) -> tuple[dict | None, bool]:
    """Return (receipt, pending); missing/unreachable receipts return (None, False)."""
    payload = {
        "schema": "instant-edit.export-status",
        "version": VERSION,
        "pluginInstanceId": ref.plugin_instance_id,
        "contextId": ref.context_id,
        "exportId": export_id,
        "capability": ref.capability,
    }
    request = urllib.request.Request(
        f"http://127.0.0.1:{ref.callback_port}/export/status",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=2) as response:
            status = getattr(response, "status", None) or response.getcode()
            body = response.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
        if status == 202:
            result = _decode_plugin_response(body, status)
            return None, result.get("code") == "export_pending"
        if 200 <= status < 300:
            return _decode_plugin_response(body, status), False
    except HTTPError as error:
        # 404 means the original request never registered. Other response
        # errors are authoritative and should be surfaced when possible.
        if error.code != 404:
            body = error.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
            raise _plugin_error_from_body(body, error.code) from error
    except (URLError, TimeoutError, OSError, ValueError, UnicodeError):
        pass
    return None, False


def _recover_export_receipt(ref, export_id: str) -> dict | None:
    for attempt in range(EXPORT_STATUS_POLL_ATTEMPTS):
        receipt, pending = _request_export_status(ref, export_id)
        if receipt is not None:
            return receipt
        if not pending:
            return None
        if attempt + 1 < EXPORT_STATUS_POLL_ATTEMPTS:
            time.sleep(0.5)
    return None


def _send_plugin_export(ref, payload: dict) -> dict:
    request = urllib.request.Request(
        f"http://127.0.0.1:{ref.callback_port}/export",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            status = getattr(response, "status", None) or response.getcode()
            body = response.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
        if not 200 <= status < 300:
            raise _plugin_error_from_body(body, status)
        return _decode_plugin_response(body, status)
    except HTTPError as error:
        body = error.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
        raise _plugin_error_from_body(body, error.code) from error
    except (URLError, TimeoutError, OSError) as error:
        receipt = _recover_export_receipt(ref, str(payload.get("exportId", "")))
        if receipt is not None:
            return receipt
        raise ValueError(
            f"plugin response was lost and no receipt was available for export "
            f"{payload.get('exportId', '')}: {str(error) or 'connection failed'}"
        ) from error


def _send_plugin_restore(ref, backup_name: str, redraw_mode: str) -> dict:
    payload = {
        "schema": "instant-edit.backup-restore",
        "version": 1,
        "pluginInstanceId": ref.plugin_instance_id,
        "contextId": ref.context_id,
        "capability": ref.capability,
        "backupName": backup_name,
        "redrawMode": redraw_mode,
    }
    request = urllib.request.Request(
        f"http://127.0.0.1:{ref.callback_port}/backup/restore",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            status = getattr(response, "status", None) or response.getcode()
            body = response.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
        if len(body) > MAX_PLUGIN_RESPONSE_SIZE:
            raise ValueError("plugin response is too large")
        if not 200 <= status < 300:
            raise _plugin_error_from_body(body, status)
        return _decode_plugin_response(body, status)
    except HTTPError as error:
        body = error.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
        raise _plugin_error_from_body(body, error.code) from error
    except (URLError, TimeoutError, OSError) as error:
        raise ValueError(str(error) or "invalid plugin response") from error


def restore_quick_backup(context: Context, backup_name: str) -> dict:
    """Restore an authorized MDL backup through the plugin and reload Penumbra."""
    ref = export_destination_context(context)
    props = get_instant_edit_props()
    try:
        result = _send_plugin_restore(ref, backup_name, props.redraw_mode)
    except PluginResponseError as error:
        if error.status != 410 and not (error.status == 401 and error.code == "plugin_instance_mismatch"):
            raise
        from .recovery import reattach_collection

        if not reattach_collection(ref.collection, context.scene):
            raise ValueError(f"plugin returned HTTP {error.status} ({error.code}); context recovery failed") from error
        ref = export_destination_context(context)
        result = _send_plugin_restore(ref, backup_name, props.redraw_mode)
    warnings = result.get("warnings", [])
    target = result.get("targetFilePath") or ref.target_file_path
    props.last_status = (
        f"Restored {target} with warnings: {plugin_warning_summary(warnings)}"
        if warnings else f"Restored {target}"
    )
    return result


def export_destination_context(context: Context, destination: str | None = None):
    props = get_instant_edit_props()
    destination = destination or getattr(props, "export_destination", "ACTIVE")
    if destination == "ACTIVE":
        return active_context(context)
    return validate_context(destination, context.scene)


def export_objects_for_scope(ref, scope: str) -> list:
    """Return the mesh objects selected by the Instant Edit export scope."""
    objects = visible_meshobj()
    if scope == "VISIBLE_NO_MANNEQUIN":
        return [obj for obj in objects if obj.name != "Mannequin"]
    if scope == "CURRENT_COLLECTION":
        return [obj for obj in objects if obj in ref.collection.objects]
    return objects


def perform_instant_export(context: Context, destination: str | None = None) -> Path:
    """Export one validated context and send only the v1 secure envelope."""
    ref = export_destination_context(context, destination)
    props = get_instant_edit_props()
    variant_name = validate_variant_name(ref.source_game_path, props.variant_name) if props.save_as_variant else None
    variant_group_name = (
        normalise_variant_group_name(props.variant_group_name)
        if variant_name is not None and props.auto_setup_penumbra
        else None
    )
    export_objects = export_objects_for_scope(ref, getattr(props, "export_scope", "VISIBLE"))
    if not export_objects:
        raise ValueError("No visible mesh objects to export.")
    export_groups = group_mesh_objects(export_objects)
    recognized = {obj for group in export_groups for obj in group.objects}
    unrecognized = [obj.name for obj in export_objects if obj not in recognized]
    if unrecognized:
        raise ValueError(
            "Visible mesh names must use 'group.part Name' or 'Name group.part': "
            + ", ".join(unrecognized)
        )
    not_triangulated = check_triangulation(export_objects)
    if not_triangulated:
        raise ValueError("Not Triangulated: " + ", ".join(not_triangulated) + ".")

    export_id = uuid.uuid4().hex
    temp_dir = create_job("exports", export_id)
    mdl_path = temp_dir / f"model_{export_id}.mdl"
    try:
        get_settings().model_format = "MDL"
        export_result(mdl_path.with_suffix(""), "MDL", export_objects=export_objects)
        if not mdl_path.is_file():
            raise ValueError("Export produced no .mdl file.")

        digest = hashlib.sha256()
        byte_size = 0
        with mdl_path.open("rb") as exported:
            for chunk in iter(lambda: exported.read(1024 * 1024), b""):
                byte_size += len(chunk)
                digest.update(chunk)

        # These are the only fields sent in the body. Port is routing only.
        payload = build_export_payload(
            ref,
            export_id,
            mdl_path,
            byte_size,
            digest.hexdigest(),
            props,
            variant_name,
            variant_group_name,
        )
        try:
            result = _send_plugin_export(ref, payload)
        except PluginResponseError as error:
            if error.status != 410 and not (
                error.status == 401 and error.code == "plugin_instance_mismatch"
            ):
                raise

            # A saved scene may outlive the runtime context or the plugin instance.
            # Reattach once and rebuild the envelope with the plugin-issued
            # capability and instance id.
            from .recovery import reattach_collection

            if not reattach_collection(ref.collection, context.scene):
                raise ValueError(
                    f"plugin returned HTTP {error.status} ({error.code}); context recovery failed"
                ) from error
            ref = validate_context(ref.context_id, context.scene)
            payload = build_export_payload(
                ref,
                export_id,
                mdl_path,
                byte_size,
                digest.hexdigest(),
                props,
                variant_name,
                variant_group_name,
            )
            result = _send_plugin_export(ref, payload)

        props.last_export_id = export_id
        target_file_path = Path(result.get("targetFilePath") or ref.target_file_path)
        if variant_name is not None and not result.get("targetFilePath"):
            target_file_path = target_file_path.with_name(f"{variant_name}.mdl")
        setup_status = " and set up in Penumbra" if variant_name is not None and props.auto_setup_penumbra else ""
        group_status = "; ".join(
            f"{group.mesh_index}.({','.join(str(part) for part in group.parts)})"
            for group in export_groups
        )
        warnings = result.get("warnings", [])
        props.last_status = (
            f"Exported {group_status} to {target_file_path}{setup_status} with warnings: "
            f"{plugin_warning_summary(warnings)}"
            if warnings else f"Exported {group_status} to {target_file_path}{setup_status}"
        )
        return mdl_path
    finally:
        try:
            finish_job(temp_dir)
        except OSError as error:
            print(f"Instant Edit: could not remove export cache job: {error}")


class ApplyInstantEdit(Operator):
    bl_idname = "xiv_ie.instant_apply"
    bl_label = "Apply Instant Edit"
    bl_description = "Apply the active Instant Edit context"

    @classmethod
    def poll(cls, context):
        return QuickExport.poll(context)

    def execute(self, context):
        try:
            perform_instant_export(context)
        except Exception as e:
            get_instant_edit_props().last_status = f"Apply failed: {e}"
            self.report({"ERROR"}, f"Apply failed: {e}")
            return {"CANCELLED"}
        status = get_instant_edit_props().last_status
        self.report({"WARNING"} if " with warnings:" in status else {"INFO"}, status)
        get_export_stats(context)
        return {"FINISHED"}


CLASSES = [
    InstantImport,
    QuickExport,
    ClearInstantEditContexts,
    CopyInstantEditStatus,
    ApplyInstantEdit,
]
