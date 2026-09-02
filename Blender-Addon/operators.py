from pathlib import Path
import uuid

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty, StringProperty
from bpy.types import Context, Operator

from .instant_edit.context import ContextValidationError, mesh_ids_from_name
from .materials import (
    assign_material_path,
    auto_collapse_materials,
    ensure_flow_data,
    find_material_group,
    mesh_flow_enabled,
    mesh_display_name,
    mesh_part_instance_objects,
    mesh_part_instances,
    mesh_part_tags,
    material_paths,
    material_suggestions,
    move_mesh_part_to_group,
    rename_mesh_part,
    set_mesh_flow_enabled,
    set_mesh_part_attribute,
    set_mesh_part_tags,
    swap_mesh_groups,
    swap_mesh_part_instances,
    swap_mesh_parts,
    convert_suffix_mesh_names,
    visible_material_group_slots,
    visible_material_groups,
)
from .mesh.export import check_triangulation, export_result, get_export_stats
from .mesh.objects import visible_meshobj
from .properties import get_settings
from .xivpy.model import XIVModel
from .backups import clear_backups, list_backups, restore_local, target_folder


_ACTIVE_MESH_DRAG: tuple[str, int, int, int, str] | None = None


def active_mesh_drag_state() -> tuple[str, int, int, int, str] | None:
    """Return scope, current group/part, ceiling, and optional instance key."""
    return _ACTIVE_MESH_DRAG


def _redraw(context: Context) -> None:
    if context.screen:
        for area in context.screen.areas:
            area.tag_redraw()


def _move_mesh_group_once(
    mesh_group: int,
    direction: str,
    maximum_group: int | None = None,
) -> int | None:
    groups = visible_material_group_slots(maximum_group)
    position = next(
        (index for index, group in enumerate(groups) if group.mesh_index == mesh_group),
        -1,
    )
    neighbor = position + (-1 if direction == "UP" else 1)
    if position < 0 or neighbor < 0 or neighbor >= len(groups):
        return None
    new_group = groups[neighbor].mesh_index
    swap_mesh_groups(visible_meshobj(), mesh_group, new_group)
    return new_group


def _move_mesh_part_to_adjacent_group(
    mesh_group: int,
    mesh_part: int,
    direction: str,
    maximum_group: int | None = None,
    part_instance_key: str | None = None,
) -> int | None:
    step = -1 if direction == "UP" else 1
    target_group = mesh_group + step
    if target_group < 0:
        return None
    highest_group = max(item.mesh_index for item in visible_material_groups())
    if target_group > highest_group + 1:
        return None
    if maximum_group is not None and target_group > maximum_group:
        return None
    return move_mesh_part_to_group(
        visible_meshobj(),
        mesh_group,
        mesh_part,
        target_group,
        part_instance_key,
    )


def _move_mesh_part_once(
    mesh_group: int,
    mesh_part: int,
    direction: str,
    maximum_group: int | None = None,
    part_instance_key: str | None = None,
    cross_group_only: bool = False,
) -> int | None:
    group = next(
        (item for item in visible_material_groups() if item.mesh_index == mesh_group),
        None,
    )
    if group is None or mesh_part not in group.parts:
        return None

    if part_instance_key is not None:
        if cross_group_only:
            return _move_mesh_part_to_adjacent_group(
                mesh_group,
                mesh_part,
                direction,
                maximum_group,
                part_instance_key,
            )

        instances = list(mesh_part_instances(group.objects, mesh_group))
        position = next(
            (
                index
                for index, item in enumerate(instances)
                if item.part_index == mesh_part
                and item.instance_key == part_instance_key
            ),
            -1,
        )
        if position < 0:
            return None

        step = -1 if direction == "UP" else 1
        neighbor = position + step
        # Duplicate rows occupy the same export slot. Skip them until the
        # drag crosses an actual part ID; this lets either duplicate move
        # independently without renaming the other duplicate.
        while 0 <= neighbor < len(instances):
            target = instances[neighbor]
            if target.part_index != mesh_part:
                changed = swap_mesh_part_instances(
                    visible_meshobj(),
                    mesh_group,
                    mesh_part,
                    part_instance_key,
                    target.part_index,
                    target.instance_key,
                )
                if changed:
                    return target.part_index
                return None
            neighbor += step

        # There is no distinct part ID in this direction, so the selected
        # instance can still be moved into an adjacent material group.
        return _move_mesh_part_to_adjacent_group(
            mesh_group,
            mesh_part,
            direction,
            maximum_group,
            part_instance_key,
        )

    parts = list(group.parts)
    position = parts.index(mesh_part)
    neighbor = position + (-1 if direction == "UP" else 1)
    if not cross_group_only and 0 <= neighbor < len(parts):
        new_part = parts[neighbor]
        swap_mesh_parts(visible_meshobj(), mesh_group, mesh_part, new_part)
        return new_part

    return _move_mesh_part_to_adjacent_group(
        mesh_group,
        mesh_part,
        direction,
        maximum_group,
    )


def _simple_import_bind_existing_skeleton(imported_objects: list, skeleton) -> list:
    """Rebind this import's meshes and remove only its imported armatures."""
    mesh_objects = [obj for obj in imported_objects if obj.type == "MESH"]
    for obj in mesh_objects:
        for modifier in tuple(obj.modifiers):
            if modifier.type == "ARMATURE":
                obj.modifiers.remove(modifier)
        obj.parent = None
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = skeleton

    remaining = []
    for obj in imported_objects:
        if obj.type != "ARMATURE":
            remaining.append(obj)
            continue
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if data is not None and data.users == 0 and data.name in bpy.data.armatures:
            bpy.data.armatures.remove(data)
    return remaining


def _simple_import_create_armature(
    context: Context,
    file_path: Path,
    mesh_objects: list,
    imported_objects: list | None = None,
):
    """Create the generated armature used by plugin-driven MDL imports."""
    model = XIVModel.from_file(str(file_path))
    armature_data = bpy.data.armatures.new("InstantEditArmature")
    armature_obj = bpy.data.objects.new("InstantEditArmature", armature_data)
    target_collection = context.collection or context.scene.collection
    target_collection.objects.link(armature_obj)
    if imported_objects is not None:
        imported_objects.append(armature_obj)

    previous_selection = tuple(context.selected_objects)
    previous_active = context.view_layer.objects.active
    for obj in previous_selection:
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

    for obj in mesh_objects:
        obj.parent = armature_obj
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = armature_obj

    for obj in previous_selection:
        if obj.name in bpy.data.objects:
            obj.select_set(True)
    context.view_layer.objects.active = (
        previous_active
        if previous_active is not None and previous_active.name in bpy.data.objects
        else armature_obj
    )
    return armature_obj


def _simple_import_remove_objects(objects: list) -> None:
    """Remove only objects created by a failed simple import."""
    for obj in reversed(objects):
        if obj.name not in bpy.data.objects:
            continue
        data = getattr(obj, "data", None)
        object_type = getattr(obj, "type", "")
        bpy.data.objects.remove(obj, do_unlink=True)
        data_collection = {"MESH": bpy.data.meshes, "ARMATURE": bpy.data.armatures}.get(object_type)
        if data is not None and data_collection is not None and data.users == 0 and data.name in data_collection:
            data_collection.remove(data)


def _simple_import_select_objects(objects: list) -> None:
    for obj in tuple(bpy.context.selected_objects):
        obj.select_set(False)
    valid_objects = [obj for obj in objects if obj.name in bpy.data.objects]
    for obj in valid_objects:
        obj.select_set(True)
    if valid_objects:
        bpy.context.view_layer.objects.active = valid_objects[-1]


class XIVIE_OT_simple_export(Operator):
    bl_idname = "xiv_ie.simple_export"
    bl_label = "Simple Export"
    bl_description = "Export all visible mesh objects using the selected format"
    bl_options = {"REGISTER"}

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT" and bool(visible_meshobj())

    def execute(self, context: Context):
        settings = get_settings()
        directory = Path(bpy.path.abspath(settings.export_directory)).resolve()
        name = (settings.export_name or "").strip()
        if not directory.is_dir():
            self.report({"ERROR"}, "Choose an existing export folder.")
            return {"CANCELLED"}
        if not name or name in {".", ".."} or Path(name).name != name:
            self.report({"ERROR"}, "Enter a valid file name without a path.")
            return {"CANCELLED"}

        suffix = {"MDL": ".mdl", "FBX": ".fbx", "GLTF": ".gltf"}[settings.model_format]
        if name.lower().endswith(suffix):
            name = name[:-len(suffix)]
        from .instant_edit.ops import export_destination_context, export_objects_for_scope

        scope = getattr(context.scene.xiv_ie_instant_edit_props, "export_scope", "VISIBLE")
        try:
            ref = export_destination_context(context) if scope == "CURRENT_COLLECTION" else None
            objects = export_objects_for_scope(ref, scope)
        except ContextValidationError as error:
            message = (
                "Select a Context before exporting the XIV Instant Edit Collection."
                if scope == "CURRENT_COLLECTION" else str(error)
            )
            self.report({"ERROR"}, message)
            return {"CANCELLED"}
        if not objects:
            self.report({"ERROR"}, "No visible mesh objects match Export Parts.")
            return {"CANCELLED"}
        if settings.model_format == "MDL":
            not_triangulated = check_triangulation(objects)
            if not_triangulated:
                self.report({"ERROR"}, "Not Triangulated: " + ", ".join(not_triangulated))
                return {"CANCELLED"}

        try:
            export_result(directory / name, settings.model_format, export_objects=objects)
            get_export_stats(context)
        except Exception as error:
            self.report({"ERROR"}, f"Export failed: {error}")
            return {"CANCELLED"}

        self.report({"INFO"}, f"Exported {name}{suffix}")
        return {"FINISHED"}


class XIVIE_OT_simple_import(Operator):
    bl_idname = "xiv_ie.simple_import"
    bl_label = "Simple Import"
    bl_description = "Import an MDL or FBX file into the current scene"
    bl_options = {"REGISTER", "UNDO"}

    filepath: StringProperty(options={"HIDDEN"})  # type: ignore
    filter_glob: StringProperty(subtype="FILE_PATH", options={"HIDDEN"})  # type: ignore
    import_format: EnumProperty(
        items=[
            ("MDL", "MDL", "FFXIV model"),
            ("FBX", "FBX", "Autodesk FBX"),
        ],
        default="MDL",
        options={"HIDDEN", "SKIP_SAVE"},
    )  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def invoke(self, context: Context, event):
        settings = get_settings()
        self.import_format = settings.import_format
        self.filter_glob = {"MDL": "*.mdl", "FBX": "*.fbx"}[self.import_format]
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context: Context):
        file_path = Path(bpy.path.abspath(self.filepath)).resolve()
        import_format = self.import_format
        expected_suffix = {"MDL": ".mdl", "FBX": ".fbx"}[import_format]
        settings = get_settings()
        skeleton = settings.simple_import_skeleton
        use_existing_skeleton = settings.simple_import_use_existing_skeleton

        if not file_path.is_file() or file_path.suffix.casefold() != expected_suffix:
            self.report({"ERROR"}, f"Choose a valid {expected_suffix[1:].upper()} file.")
            return {"CANCELLED"}
        if use_existing_skeleton and (skeleton is None or skeleton.type != "ARMATURE"):
            self.report({"ERROR"}, "Choose an existing Blender Armature for the imported meshes.")
            return {"CANCELLED"}

        imported_objects = []
        try:
            if import_format == "MDL":
                from .io.model import ModelImport

                created_objects = []
                imported = ModelImport.from_file(
                    str(file_path),
                    file_path.stem,
                    select_objects=False,
                    created_objects=created_objects,
                )
                imported_objects = list(created_objects or imported)
                mesh_objects = [obj for obj in imported_objects if obj.type == "MESH"]
                if use_existing_skeleton:
                    imported_objects = _simple_import_bind_existing_skeleton(imported_objects, skeleton)
                elif mesh_objects:
                    _simple_import_create_armature(
                        context,
                        file_path,
                        mesh_objects,
                        imported_objects,
                    )
            else:
                existing_objects = {obj.as_pointer() for obj in bpy.data.objects}
                result = bpy.ops.import_scene.fbx(
                    filepath=str(file_path),
                    colors_type="LINEAR",
                )
                if "FINISHED" not in result:
                    return set(result)
                imported_objects = [
                    obj for obj in bpy.data.objects if obj.as_pointer() not in existing_objects
                ]
                if use_existing_skeleton:
                    imported_objects = _simple_import_bind_existing_skeleton(imported_objects, skeleton)

                import_instance_id = uuid.uuid4().hex
                for obj in imported_objects:
                    if obj.type == "MESH":
                        obj["instant_edit_import_instance_id"] = import_instance_id

            _simple_import_select_objects(imported_objects)
            imported_count = sum(obj.type == "MESH" for obj in imported_objects)
            if settings.simple_import_set_export_directory:
                settings.export_directory = str(file_path.parent)
        except Exception as error:
            _simple_import_remove_objects(imported_objects)
            self.report({"ERROR"}, f"Import failed: {error}")
            return {"CANCELLED"}

        count_text = f" ({imported_count} mesh object{'s' if imported_count != 1 else ''})" if imported_count else ""
        self.report({"INFO"}, f"Imported {file_path.name}{count_text}")
        return {"FINISHED"}


class XIVIE_OT_restore_backup(Operator):
    bl_idname = "xiv_ie.restore_backup"
    bl_label = "Restore Backup"
    bl_description = "Restore this model backup and preserve the current model first"
    bl_options = {"REGISTER", "UNDO"}

    backup_name: StringProperty(options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def execute(self, context: Context):
        settings = get_settings()
        folder, _source = target_folder(settings, context)
        if folder is None:
            self.report({"ERROR"}, "The current target export folder is unavailable.")
            return {"CANCELLED"}
        entry = next((item for item in list_backups(folder) if item.path.name == self.backup_name), None)
        if entry is None:
            self.report({"ERROR"}, "That backup no longer exists.")
            return {"CANCELLED"}
        try:
            quick = False
            try:
                from .instant_edit.context import ContextValidationError
                from .instant_edit.ops import (export_destination_context,
                                               plugin_warning_summary,
                                               restore_quick_backup)

                ref = export_destination_context(context)
                quick = (
                    Path(ref.target_file_path).resolve().parent == folder
                    and entry.original_name.lower().endswith(".mdl")
                )
                if quick:
                    result = restore_quick_backup(context, entry.path.name)
            except (ContextValidationError, ImportError):
                pass
            if not quick:
                restore_local(folder, entry)
        except Exception as error:
            self.report({"ERROR"}, f"Restore failed: {error}")
            return {"CANCELLED"}
        warnings = result.get("warnings", []) if quick else []
        message = (
            f"Restored {entry.original_name} with warnings: {plugin_warning_summary(warnings)}"
            if warnings else f"Restored {entry.original_name}"
        )
        self.report({"WARNING"} if warnings else {"INFO"}, message)
        return {"FINISHED"}


class XIVIE_OT_import_backup(Operator):
    bl_idname = "xiv_ie.import_backup"
    bl_label = "Import Backup"
    bl_description = "Import this model backup into a new collection"
    bl_options = {"REGISTER", "UNDO"}

    backup_name: StringProperty(options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        folder, _source = target_folder(get_settings(), context)
        if folder is None:
            self.report({"ERROR"}, "The current target export folder is unavailable.")
            return {"CANCELLED"}
        entry = next((item for item in list_backups(folder) if item.path.name == self.backup_name), None)
        if entry is None:
            self.report({"ERROR"}, "That backup no longer exists.")
            return {"CANCELLED"}
        collection = bpy.data.collections.new(f"Backup - {Path(entry.original_name).stem}")
        context.scene.collection.children.link(collection)
        before = set(bpy.data.objects)
        try:
            if entry.original_name.lower().endswith(".mdl"):
                from .io.model import ModelImport

                imported = ModelImport.from_file(
                    str(entry.path), Path(entry.original_name).stem,
                    collection=collection, require_collection=True,
                )
                count = len(imported)
            else:
                result = bpy.ops.import_scene.fbx(filepath=str(entry.path), colors_type="LINEAR")
                if "FINISHED" not in result:
                    raise RuntimeError("Blender FBX importer did not finish")
                new_objects = [obj for obj in bpy.data.objects if obj not in before]
                for obj in new_objects:
                    if collection not in obj.users_collection:
                        collection.objects.link(obj)
                    for old_collection in tuple(obj.users_collection):
                        if old_collection != collection:
                            old_collection.objects.unlink(obj)
                count = len(new_objects)
        except Exception as error:
            for obj in tuple(bpy.data.objects):
                if obj not in before:
                    bpy.data.objects.remove(obj, do_unlink=True)
            if collection.name in bpy.data.collections:
                bpy.data.collections.remove(collection)
            self.report({"ERROR"}, f"Import failed: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Imported {entry.original_name} into {collection.name} ({count} object{'s' if count != 1 else ''})")
        return {"FINISHED"}


class XIVIE_OT_clear_backups(Operator):
    bl_idname = "xiv_ie.clear_backups"
    bl_label = "Clear All Backups"
    bl_description = "Delete all recognized model backups in the current target folder"
    bl_options = {"REGISTER", "UNDO"}

    folder_label: StringProperty(options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    backup_count: IntProperty(default=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def invoke(self, context: Context, event):
        folder, _source = target_folder(get_settings(), context)
        self.folder_label = str(folder) if folder is not None else "Unavailable folder"
        self.backup_count = len(list_backups(folder))
        return context.window_manager.invoke_confirm(self, event)

    def draw(self, context: Context):
        self.layout.label(text=f"Delete {self.backup_count} backup(s) from:")
        self.layout.label(text=self.folder_label)

    def execute(self, context: Context):
        folder, _source = target_folder(get_settings(), context)
        removed = clear_backups(folder)
        self.report({"INFO"}, f"Cleared {removed} backup{'s' if removed != 1 else ''}.")
        return {"FINISHED"}


class XIVIE_OT_drag_mesh_order(Operator):
    bl_idname = "xiv_ie.drag_mesh_order"
    bl_label = "Drag to Reorder"
    bl_description = "Hold and drag vertically to reorder this mesh group or part"
    bl_options = {"REGISTER", "UNDO", "BLOCKING"}

    scope: EnumProperty(
        items=(
            ("GROUP", "Mesh Group", "Reorder the complete mesh group"),
            ("PART", "Mesh Part", "Reorder this part inside its mesh group"),
        ),
        default="GROUP",
        options={"HIDDEN", "SKIP_SAVE"},
    )  # type: ignore
    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    @classmethod
    def description(cls, context, properties):
        item = "mesh group" if properties.scope == "GROUP" else "mesh part"
        return f"Hold and drag vertically to reorder this {item}; release to drop; Esc or right-click cancels"

    def invoke(self, context: Context, event):
        global _ACTIVE_MESH_DRAG
        if self.scope == "GROUP":
            group = find_material_group(context, self.mesh_group)
            targets = group.objects if group is not None else ()
        else:
            targets = mesh_part_instance_objects(
                visible_meshobj(),
                self.mesh_group,
                self.mesh_part,
                self.mesh_part_instance or None,
            )
        if not targets:
            self.report({"ERROR"}, "The mesh item is no longer visible.")
            return {"CANCELLED"}

        self._dragged_objects = tuple(targets)
        self._original_names = tuple((obj, obj.name) for obj in visible_meshobj())
        self._last_mouse_y = event.mouse_y
        self._drag_distance = 0.0
        self._part_drag_group_lock = None
        self._step = max(18.0, 22.0 * context.preferences.system.ui_scale)
        self._maximum_group = max(
            group.mesh_index for group in visible_material_groups()
        ) + 1
        _ACTIVE_MESH_DRAG = (
            self.scope,
            self.mesh_group,
            self.mesh_part,
            self._maximum_group,
            self.mesh_part_instance,
        )

        context.window.cursor_modal_set("MOVE_Y")
        context.workspace.status_text_set(
            "Hold and drag vertically to reorder; release to drop; Esc or right-click to cancel"
        )
        context.window_manager.modal_handler_add(self)
        _redraw(context)
        return {"RUNNING_MODAL"}

    def modal(self, context: Context, event):
        if event.type == "MOUSEMOVE":
            self._drag_distance += event.mouse_y - self._last_mouse_y
            self._last_mouse_y = event.mouse_y
            while abs(self._drag_distance) >= self._step:
                direction = "UP" if self._drag_distance > 0 else "DOWN"
                if not self._move_once(context, direction):
                    self._drag_distance = 0.0
                    break
                self._drag_distance += -self._step if direction == "UP" else self._step
            return {"RUNNING_MODAL"}

        if event.type in {"ESC", "RIGHTMOUSE", "WINDOW_DEACTIVATE"}:
            if event.type == "WINDOW_DEACTIVATE" or event.value == "PRESS":
                self._restore_names()
                return self._finish(context, cancelled=True)

        if event.type in {"RET", "NUMPAD_ENTER", "SPACE"} and event.value == "PRESS":
            return self._finish(context, cancelled=False)

        if event.type == "LEFTMOUSE":
            if event.value == "RELEASE":
                return self._finish(context, cancelled=False)

        return {"RUNNING_MODAL"}

    def _move_once(self, context: Context, direction: str) -> bool:
        global _ACTIVE_MESH_DRAG
        try:
            mesh_group, mesh_part, _lod = mesh_ids_from_name(self._dragged_objects[0])
        except Exception:
            return False
        try:
            cross_group_only = (
                self.scope == "PART"
                and self._part_drag_group_lock == mesh_group
            )
            if self.scope == "GROUP":
                moved = _move_mesh_group_once(
                    mesh_group,
                    direction,
                    self._maximum_group,
                )
            else:
                moved = _move_mesh_part_once(
                    mesh_group,
                    mesh_part,
                    direction,
                    self._maximum_group,
                    self.mesh_part_instance or None,
                    cross_group_only=cross_group_only,
                )
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return False
        if moved is None:
            return False
        new_group, new_part, _lod = mesh_ids_from_name(self._dragged_objects[0])
        if self.scope == "PART" and new_group != mesh_group:
            # Keep the newly entered group locked for this drag. Further
            # movement crosses groups instead of reordering inside it.
            self._part_drag_group_lock = new_group
        _ACTIVE_MESH_DRAG = (
            self.scope,
            new_group,
            new_part,
            self._maximum_group,
            self.mesh_part_instance,
        )
        _redraw(context)
        return True

    def _restore_names(self) -> None:
        existing = [
            (obj, name)
            for obj, name in self._original_names
            if obj.name in bpy.data.objects and bpy.data.objects[obj.name] == obj
        ]
        for index, (obj, _name) in enumerate(existing):
            obj.name = f"__xiv_ie_drag_restore_{index}_{obj.as_pointer()}"
        for obj, name in existing:
            obj.name = name

    @staticmethod
    def _finish(context: Context, cancelled: bool):
        global _ACTIVE_MESH_DRAG
        _ACTIVE_MESH_DRAG = None
        context.window.cursor_modal_restore()
        context.workspace.status_text_set(None)
        _redraw(context)
        return {"CANCELLED"} if cancelled else {"FINISHED"}


class XIVIE_OT_rename_mesh_part(Operator):
    bl_idname = "xiv_ie.rename_mesh_part"
    bl_label = "Rename Mesh Part"
    bl_description = "Rename this part without changing its export ID"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    new_name: StringProperty(name="Part Name", maxlen=128)  # type: ignore

    def invoke(self, context: Context, event):
        objects = mesh_part_instance_objects(
            visible_meshobj(),
            self.mesh_group,
            self.mesh_part,
            self.mesh_part_instance or None,
        )
        if not objects:
            self.report({"ERROR"}, f"Mesh part {self.mesh_group}.{self.mesh_part} is no longer visible.")
            return {"CANCELLED"}
        self.new_name = mesh_display_name(objects[0])
        return context.window_manager.invoke_props_dialog(self, width=420)

    def draw(self, context: Context):
        self.layout.label(text=f"Rename mesh part {self.mesh_group}.{self.mesh_part}")
        self.layout.prop(self, "new_name", text="Name")

    def execute(self, context: Context):
        try:
            rename_mesh_part(
                visible_meshobj(),
                self.mesh_group,
                self.mesh_part,
                self.new_name,
                self.mesh_part_instance or None,
            )
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_convert_mesh_names(Operator):
    bl_idname = "xiv_ie.convert_mesh_names"
    bl_label = "Move Mesh IDs to Front"
    bl_description = "Convert suffix-form mesh IDs in every scene mesh to the prefix naming convention"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        try:
            converted = convert_suffix_mesh_names(bpy.context.scene.objects)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        if converted:
            self.report({"INFO"}, f"Moved mesh IDs to the front on {converted} object{'s' if converted != 1 else ''}.")
        else:
            self.report({"INFO"}, "No suffix-form mesh IDs found.")
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_mesh_tags(Operator):
    bl_idname = "xiv_ie.mesh_tags"
    bl_label = "Mesh Part Tags"
    bl_description = "Set comma-separated tags on this mesh part"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    tags: StringProperty(name="Tags", description="Comma-separated tags attached to this model part", maxlen=512)  # type: ignore

    def invoke(self, context: Context, event):
        objects = mesh_part_instance_objects(
            visible_meshobj(),
            self.mesh_group,
            self.mesh_part,
            self.mesh_part_instance or None,
        )
        if not objects:
            self.report({"ERROR"}, f"Mesh part {self.mesh_group}.{self.mesh_part} is no longer visible.")
            return {"CANCELLED"}
        self.tags = mesh_part_tags(objects)
        if self.tags == "<multiple>":
            self.tags = ""
        return context.window_manager.invoke_props_dialog(self, width=520)

    def draw(self, context: Context):
        self.layout.label(text=f"Tags for mesh part {self.mesh_group}.{self.mesh_part}")
        self.layout.prop(self, "tags", text="")

    def execute(self, context: Context):
        set_mesh_part_tags(
            visible_meshobj(),
            self.mesh_group,
            self.mesh_part,
            self.tags,
            self.mesh_part_instance or None,
        )
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_mesh_attribute(Operator):
    bl_idname = "xiv_ie.mesh_attribute"
    bl_label = "Mesh Part Attribute"
    bl_description = "Add or remove an XIV attribute on this mesh part"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    attribute: StringProperty(default="NEW", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    custom: BoolProperty(name="Custom", default=False)  # type: ignore
    custom_attribute: StringProperty(name="", default="atr_", maxlen=128)  # type: ignore
    selection: EnumProperty(
        name="",
        items=(
            ("atr_nek", "Neck", ""),
            ("atr_ude", "Elbow", ""),
            ("atr_hij", "Wrist", ""),
            ("atr_arm", "Glove", ""),
            ("atr_kod", "Waist", ""),
            ("atr_hiz", "Knee", ""),
            ("atr_sne", "Shin", ""),
            ("atr_leg", "Boot", ""),
            ("atr_lpd", "Knee Pad", ""),
        ),
        default="atr_nek",
    )  # type: ignore

    @classmethod
    def description(cls, context, properties):
        if properties.attribute == "NEW":
            return "Add an XIV attribute to this mesh part"
        return f"Remove {properties.attribute} from this mesh part"

    def invoke(self, context: Context, event):
        if self.attribute != "NEW":
            return self.execute(context)
        return context.window_manager.invoke_props_dialog(self, width=320)

    def draw(self, context: Context):
        layout = self.layout
        layout.prop(self, "custom", icon="FILE_TEXT")
        layout.prop(self, "custom_attribute" if self.custom else "selection")

    def execute(self, context: Context):
        value = self.custom_attribute if self.custom else self.selection
        enabled = self.attribute == "NEW"
        if not enabled:
            value = self.attribute
        try:
            attribute = set_mesh_part_attribute(
                visible_meshobj(),
                self.mesh_group,
                self.mesh_part,
                value,
                enabled,
                self.mesh_part_instance or None,
            )
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _redraw(context)
        verb = "Added" if enabled else "Removed"
        self.report({"INFO"}, f"{verb} {attribute} on mesh part {self.mesh_group}.{self.mesh_part}")
        return {"FINISHED"}


class XIVIE_OT_mesh_flow(Operator):
    bl_idname = "xiv_ie.mesh_flow"
    bl_label = "Mesh Flow Data"
    bl_description = "Create or toggle XIV flow data for this mesh group"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    action: StringProperty(default="ADD", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    @classmethod
    def description(cls, context, properties):
        if properties.action == "TOGGLE":
            return "Toggle whether the MDL exporter includes this mesh group's flow data"
        return "Add a neutral XIV flow colour channel to every part in this mesh group"

    def execute(self, context: Context):
        group = find_material_group(context, self.mesh_group)
        if group is None:
            self.report({"ERROR"}, f"Mesh group {self.mesh_group} is no longer available.")
            return {"CANCELLED"}
        if self.action == "TOGGLE":
            enabled = not mesh_flow_enabled(group.objects)
            set_mesh_flow_enabled(group.objects, enabled)
            self.report({"INFO"}, f"Mesh group {self.mesh_group}: flow export {'enabled' if enabled else 'disabled'}")
        else:
            updated = ensure_flow_data(group.objects)
            self.report({"INFO"}, "Flow colour channels updated." if updated else "All flow colour channels already exist.")
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_auto_collapse_materials(Operator):
    bl_idname = "xiv_ie.auto_collapse_materials"
    bl_label = "Auto-collapse materials"
    bl_description = "Move matching visible mesh parts into their lowest-numbered mesh group"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        try:
            moved = auto_collapse_materials(visible_meshobj())
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}

        _redraw(context)
        if moved:
            self.report(
                {"INFO"},
                f"Auto-collapsed {moved} mesh part{'s' if moved != 1 else ''}.",
            )
        else:
            self.report({"INFO"}, "No matching mesh parts needed collapsing.")
        return {"FINISHED"}


class XIVIE_OT_mesh_material(Operator):
    bl_idname = "xiv_ie.mesh_material"
    bl_label = "Mesh Material"
    bl_description = "View or change the FFXIV material path for this mesh group"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def _material_search(self, context, edit_text):
        return material_suggestions()

    material: StringProperty(
        name="Material Path",
        description="FFXIV .mtrl path used when exporting this mesh group",
        search=_material_search,
        search_options={"SUGGESTION"},
    )  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def invoke(self, context: Context, event):
        group = find_material_group(context, self.mesh_group)
        if group is None:
            self.report({"ERROR"}, f"Mesh group {self.mesh_group} is no longer available.")
            return {"CANCELLED"}
        paths = material_paths(group.objects)
        self.material = paths[0] if len(paths) == 1 else ""
        return context.window_manager.invoke_props_dialog(
            self,
            width=520,
            confirm_text="Assign Material",
        )

    def draw(self, context: Context):
        self.layout.label(text=f"Mesh Group {self.mesh_group}")
        self.layout.prop(self, "material", text="")

    def execute(self, context: Context):
        group = find_material_group(context, self.mesh_group)
        if group is None:
            self.report({"ERROR"}, f"Mesh group {self.mesh_group} is no longer available.")
            return {"CANCELLED"}
        try:
            path = assign_material_path(group.objects, self.material)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}

        if context.screen:
            for area in context.screen.areas:
                area.tag_redraw()
        self.report({"INFO"}, f"Mesh group {self.mesh_group}: {path}")
        return {"FINISHED"}
