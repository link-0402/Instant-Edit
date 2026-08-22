from pathlib import Path

import bpy
from bpy.props import IntProperty, StringProperty
from bpy.types import Context, Operator

from .materials import (
    assign_material_path,
    find_material_group,
    material_paths,
    material_suggestions,
)
from .mesh.export import check_triangulation, export_result, get_export_stats
from .mesh.objects import visible_meshobj
from .properties import get_settings


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
