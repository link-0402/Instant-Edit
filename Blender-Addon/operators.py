from pathlib import Path

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty, StringProperty
from bpy.types import Context, Operator

from .materials import (
    assign_material_path,
    ensure_flow_data,
    find_material_group,
    mesh_flow_enabled,
    mesh_display_name,
    mesh_part_objects,
    mesh_part_tags,
    material_paths,
    material_suggestions,
    rename_mesh_part,
    set_mesh_flow_enabled,
    set_mesh_part_attribute,
    set_mesh_part_tags,
    swap_mesh_groups,
    swap_mesh_parts,
    visible_material_groups,
)
from .mesh.export import check_triangulation, export_result, get_export_stats
from .mesh.objects import visible_meshobj
from .properties import get_settings


def _redraw(context: Context) -> None:
    if context.screen:
        for area in context.screen.areas:
            area.tag_redraw()


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
        objects = visible_meshobj()
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


class XIVIE_OT_move_mesh_group(Operator):
    bl_idname = "xiv_ie.move_mesh_group"
    bl_label = "Move Mesh Group"
    bl_description = "Change the priority of this mesh group"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    direction: StringProperty(default="UP", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def execute(self, context: Context):
        groups = visible_material_groups()
        position = next((index for index, group in enumerate(groups) if group.mesh_index == self.mesh_group), -1)
        if position < 0:
            self.report({"ERROR"}, f"Mesh group {self.mesh_group} is no longer visible.")
            return {"CANCELLED"}
        offset = -1 if self.direction == "UP" else 1
        neighbor = position + offset
        if neighbor < 0 or neighbor >= len(groups):
            return {"CANCELLED"}
        swap_mesh_groups(visible_meshobj(), self.mesh_group, groups[neighbor].mesh_index)
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_move_mesh_part(Operator):
    bl_idname = "xiv_ie.move_mesh_part"
    bl_label = "Move Mesh Part"
    bl_description = "Change the priority of this mesh part"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    direction: StringProperty(default="UP", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def execute(self, context: Context):
        group = find_material_group(context, self.mesh_group)
        if group is None or self.mesh_part not in group.parts:
            self.report({"ERROR"}, f"Mesh part {self.mesh_group}.{self.mesh_part} is no longer visible.")
            return {"CANCELLED"}
        parts = list(group.parts)
        position = parts.index(self.mesh_part)
        offset = -1 if self.direction == "UP" else 1
        neighbor = position + offset
        if neighbor < 0 or neighbor >= len(parts):
            return {"CANCELLED"}
        swap_mesh_parts(visible_meshobj(), self.mesh_group, self.mesh_part, parts[neighbor])
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_rename_mesh_part(Operator):
    bl_idname = "xiv_ie.rename_mesh_part"
    bl_label = "Rename Mesh Part"
    bl_description = "Rename this part across all of its LODs without changing its export ID"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    new_name: StringProperty(name="Part Name", maxlen=128)  # type: ignore

    def invoke(self, context: Context, event):
        objects = mesh_part_objects(visible_meshobj(), self.mesh_group, self.mesh_part)
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
            rename_mesh_part(visible_meshobj(), self.mesh_group, self.mesh_part, self.new_name)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_mesh_tags(Operator):
    bl_idname = "xiv_ie.mesh_tags"
    bl_label = "Mesh Part Tags"
    bl_description = "Set comma-separated tags on every LOD of this mesh part"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    tags: StringProperty(name="Tags", description="Comma-separated tags attached to this model part", maxlen=512)  # type: ignore

    def invoke(self, context: Context, event):
        objects = mesh_part_objects(visible_meshobj(), self.mesh_group, self.mesh_part)
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
        set_mesh_part_tags(visible_meshobj(), self.mesh_group, self.mesh_part, self.tags)
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_mesh_attribute(Operator):
    bl_idname = "xiv_ie.mesh_attribute"
    bl_label = "Mesh Part Attribute"
    bl_description = "Add or remove an XIV attribute on this mesh part"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
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
        return "Add a neutral XIV flow colour channel to every part and LOD in this mesh group"

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
