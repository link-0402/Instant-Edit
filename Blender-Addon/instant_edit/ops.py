# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy
import json
import hashlib
import tempfile
import uuid
import urllib.request
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
                             active_context, create_collection, tag_object)


MAX_PLUGIN_RESPONSE_SIZE = 64 * 1024
INVALID_VARIANT_CHARS = frozenset('<>:"/\\|?*')


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

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        props     = get_instant_edit_props()
        file_path = Path(self.file_path)
        user_state = _snapshot_object_state(context)
        created_objects = []
        collection = None
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
                    "import_id": self.import_id,
                    "callback_port": self.callback_port,
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

            collection = create_collection(context.scene, collection_metadata)

            imported_meshes = ModelImport.from_file(
                str(file_path), self.import_name or file_path.stem,
                collection=collection, context_metadata=context_metadata,
                select_objects=False,
                require_collection=True,
                created_objects=created_objects,
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
            props.display_name = self.import_name or file_path.stem
            props.context_id = self.context_id if is_v1 else ""
            props.context_schema = self.schema if is_v1 else ""
            props.context_version = self.version if is_v1 else 0
            props.plugin_instance_id = self.plugin_instance_id if is_v1 else ""
            props.capability = self.capability if is_v1 else ""
            props.managed_destination = self.managed_destination if is_v1 else ""
            props.last_status  = f"Imported {file_path.name}"
        except Exception as e:
            _remove_staging_objects(created_objects, collection)
            props.last_status = f"Import failed: {e}"
            self.report({"ERROR"}, f"Import failed: {e}")
            return {"CANCELLED"}

        finally:
            _restore_object_state(context, user_state)

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


class QuickExport(Operator):
    bl_idname      = "xiv_ie.instant_export"
    bl_label       = "Quick Export"
    bl_description = "Exports the current model back to the game path it was imported from via Penumbra"
    bl_options     = {"UNDO"}

    @classmethod
    def poll(cls, context: Context):
        try:
            active_context(context)
            return True
        except ContextValidationError:
            return False

    def execute(self, context: Context):
        try:
            perform_instant_export(context)
        except Exception as e:
            props = get_instant_edit_props()
            props.last_status = f"Export failed: {e}"
            self.report({"ERROR"}, f"Export failed: {e}")
            return {"CANCELLED"}
        self.report({"INFO"}, "Exported back to the game!")
        get_export_stats(context)
        return {"FINISHED"}


def build_export_payload(ref, export_id: str, mdl_path: Path, byte_size: int,
                         sha256: str, props, variant_name: str | None) -> dict:
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
    }
    if variant_name is not None:
        payload["variantName"] = variant_name
        if props.auto_setup_penumbra:
            payload["setupInPenumbra"] = True
    return payload


def perform_instant_export(context: Context) -> Path:
    """Export one validated context and send only the v1 secure envelope."""
    ref = active_context(context)
    props = get_instant_edit_props()
    variant_name = normalise_variant_name(props.variant_name) if props.save_as_variant else None
    export_objects = visible_meshobj()
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
    temp_dir = Path(tempfile.gettempdir()) / "InstantEdit" / "export" / export_id
    temp_dir.mkdir(parents=True, exist_ok=True)
    mdl_path = temp_dir / f"model_{export_id}.mdl"
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
    )
    request = urllib.request.Request(
        f"http://127.0.0.1:{ref.callback_port}/export",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            status = getattr(response, "status", None) or response.getcode()
            if not 200 <= status < 300:
                raise ValueError(f"plugin returned HTTP {status}")
            body = response.read(MAX_PLUGIN_RESPONSE_SIZE + 1)
            if len(body) > MAX_PLUGIN_RESPONSE_SIZE:
                raise ValueError("plugin response is too large")
            result = json.loads(body.decode("utf-8"))
            if not isinstance(result, dict) or not result.get("ok"):
                raise ValueError((result or {}).get("error", "unknown plugin error"))
    except HTTPError as e:
        raise ValueError(f"plugin returned HTTP {e.code}") from e
    except (URLError, TimeoutError, OSError, json.JSONDecodeError) as e:
        raise ValueError(str(e) or "invalid plugin response") from e

    props.last_export_id = export_id
    target_file_path = Path(ref.target_file_path)
    if variant_name is not None:
        target_file_path = target_file_path.with_name(f"{variant_name}.mdl")
    setup_status = " and set up in Penumbra" if variant_name is not None and props.auto_setup_penumbra else ""
    group_status = "; ".join(
        f"{group.mesh_index}.({','.join(str(part) for part in group.parts)})"
        for group in export_groups
    )
    props.last_status = f"Exported {group_status} to {target_file_path}{setup_status}"
    return mdl_path


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
        self.report({"INFO"}, "Applied Instant Edit context")
        get_export_stats(context)
        return {"FINISHED"}


CLASSES = [
    InstantImport,
    QuickExport,
    ApplyInstantEdit,
]
