import bpy

from bpy.types import Context, Panel

from .instant_edit.context import ContextValidationError, active_context
from .instant_edit.props import get_instant_edit_props
from .materials import material_paths, visible_material_groups
from .properties import get_settings


class XIVIE_PT_main(Panel):
    bl_idname = "XIVIE_PT_main"
    bl_label = "XIV Instant Edit"
    bl_category = "XIV Instant Edit"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"

    def draw(self, context: Context) -> None:
        layout = self.layout
        self._draw_instant_edit(layout, context)
        self._draw_mesh_materials(layout)
        self._draw_simple_export(layout)

    @staticmethod
    def _draw_instant_edit(layout, context: Context) -> None:
        props = get_instant_edit_props()
        box = layout.box()
        box.label(text="Instant Edit", icon="EXPERIMENTAL")
        try:
            ref = active_context(context)
        except ContextValidationError:
            ref = None

        if ref is None:
            box.label(text="Pick a model through the Dalamud plugin.", icon="INFO")
        else:
            box.label(text=f"Model: {props.display_name}")
            box.label(text=f"Game path: {ref.source_game_path}")
            box.operator("xiv_ie.instant_export", text="Quick Export", icon="EXPORT")

        row = box.row(align=True)
        row.prop(props, "save_as_variant")
        name = row.row(align=True)
        name.enabled = props.save_as_variant
        name.prop(props, "variant_name", text="")
        setup = box.row()
        setup.enabled = props.save_as_variant
        setup.prop(props, "auto_setup_penumbra")
        redraw = box.row(align=True)
        redraw.label(text="Redraw:")
        redraw.prop(props, "redraw_mode", expand=True)
        if props.redraw_mode == "GLAM":
            box.label(text="Glamourer refresh does not support face models.", icon="INFO")
        box.label(text=props.last_status, icon="INFO")

    @staticmethod
    def _draw_mesh_materials(layout) -> None:
        groups = visible_material_groups()
        box = layout.box()
        box.label(text="Mesh Materials", icon="MATERIAL")
        box.label(text='Add parts/groups by naming objects "group.part Name".')
        box.label(text="Quick Export includes every visible named mesh.")
        if not groups:
            box.label(text="No visible FFXIV mesh groups.", icon="INFO")
            return

        box.label(text="Material paths are applied to every submesh in a group.")
        for group in groups:
            paths = material_paths(group.objects)
            row = box.row(align=True)
            parts = ", ".join(str(part) for part in group.parts)
            row.label(text=f"Mesh {group.mesh_index} · Parts {parts}")
            if not paths:
                text = "Add Mesh Properties"
                icon = "ERROR"
            elif len(paths) > 1:
                text = "Multiple Materials"
                icon = "ERROR"
            else:
                text = paths[0]
                icon = "MATERIAL"
            operator = row.operator("xiv_ie.mesh_material", text=text, icon=icon)
            operator.mesh_group = group.mesh_index

    @staticmethod
    def _draw_simple_export(layout) -> None:
        settings = get_settings()
        box = layout.box()
        box.label(text="Simple Export", icon="EXPORT")
        box.prop(settings, "export_directory")
        row = box.row(align=True)
        row.prop(settings, "export_name")
        row.prop(settings, "model_format", text="")
        box.operator("xiv_ie.simple_export", icon="EXPORT")

        options = box.box()
        options.label(text="Model Options")
        row = options.row(align=True)
        row.prop(settings, "keep_shapekeys")
        row.prop(settings, "check_tris")
        row = options.row(align=True)
        row.prop(settings, "create_backfaces")
        row.prop(settings, "use_lods")
        options.prop(settings, "remove_yas")
        options.prop(settings, "neck_morph")

        cleanup = box.box()
        cleanup.label(text="Stream Cleanup")
        row = cleanup.row(align=True)
        row.prop(settings, "clear_uv2")
        row.prop(settings, "copy_uv1_to_uv2")
        row = cleanup.row(align=True)
        row.prop(settings, "clear_vertex_color1")
        row.prop(settings, "clear_vertex_alpha1")
        row = cleanup.row(align=True)
        row.prop(settings, "clear_vertex_color2")
        row.prop(settings, "clear_flow_data")
