import bpy

from bpy.types import Context, Panel

from .instant_edit.context import ContextValidationError, _value, mesh_ids_from_name
from .instant_edit.ops import export_destination_context
from .instant_edit.props import get_instant_edit_props
from .materials import (
    attribute_display_name,
    material_paths,
    mesh_display_name,
    mesh_part_attributes,
    mesh_part_objects,
    visible_material_groups,
)
from .properties import get_settings
from .backups import list_backups, target_folder


def draw_status_context_menu(menu, context) -> None:
    """Add full-status copying when the status text field is right-clicked."""
    button_prop = getattr(context, "button_prop", None)
    if button_prop is None or getattr(button_prop, "identifier", "") != "last_status":
        return
    menu.layout.separator()
    menu.layout.operator("xiv_ie.copy_status", text="Copy Full Import Status", icon="COPYDOWN")


class XIVIE_PT_main(Panel):
    bl_idname = "XIVIE_PT_main"
    bl_label = "XIV Instant Edit"
    bl_category = "XIV Instant Edit"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"

    def draw(self, context: Context) -> None:
        layout = self.layout
        self._draw_instant_edit(layout, context)
        self._draw_mesh_materials(layout, context)
        self._draw_simple_export(layout)
        self._draw_export_options(layout)
        self._draw_backups(layout, context)
        self._draw_utilities(layout)

    @staticmethod
    def _draw_instant_edit(layout, context: Context) -> None:
        props = get_instant_edit_props()
        box = layout.box()
        box.label(text="Instant Edit", icon="EXPERIMENTAL")
        try:
            ref = export_destination_context(context)
        except ContextValidationError:
            ref = None

        box.prop(props, "export_destination", text="Export Destination")
        if ref is None:
            box.label(text="Pick a model through the Dalamud plugin.", icon="INFO")
        else:
            model_path = (ref.source_game_path or "").replace("\\", "/")
            model_name = str(_value(ref.collection, "import_file_name", "") or props.display_name).strip()
            if not model_name:
                model_name = model_path.rsplit("/", 1)[-1]
            model_directory = model_path.rsplit("/", 1)[0] if "/" in model_path else ""
            box.label(text=f"Model Name: {model_name}")
            box.label(text=f"Model Path: {model_directory}")
            source_mod = ref.source_mod_root_path or ref.source_mod_name or ref.source_mod_directory
            box.label(text=f"Source Mod: {source_mod}")
            box.prop(props, "export_scope")
            box.operator("xiv_ie.instant_export", text="Quick Export", icon="EXPORT")

        row = box.row(align=True)
        row.prop(props, "save_as_variant")
        name = row.row(align=True)
        name.enabled = props.save_as_variant
        name.prop(props, "variant_name", text="")
        setup = box.row()
        setup.enabled = props.save_as_variant
        setup.prop(props, "auto_setup_penumbra")
        group_name = setup.row(align=True)
        group_name.enabled = props.auto_setup_penumbra
        group_name.prop(props, "variant_group_name", text="")
        redraw = box.row(align=True)
        redraw.label(text="Redraw:")
        redraw.prop(props, "redraw_mode", expand=True)
        if props.redraw_mode == "GLAM":
            box.label(text="Glamourer refresh does not support face models.", icon="INFO")
        status_row = box.row(align=True)
        status_row.label(text="Status:", icon="INFO")
        status_row.prop(props, "last_status", text="")
        status_row.operator("xiv_ie.copy_status", text="", icon="COPYDOWN")

    @staticmethod
    def _draw_mesh_materials(layout, context: Context) -> None:
        """Draw the compact Mesh Studio overview adapted from Yet Another Addon."""
        def triangle_count(obj) -> int:
            obj.data.calc_loop_triangles()
            return len(obj.data.loop_triangles)

        def lod_zero_objects(objects) -> tuple:
            lod_zero = tuple(obj for obj in objects if mesh_ids_from_name(obj)[2] == 0)
            if lod_zero:
                return lod_zero
            lowest_lod = min(mesh_ids_from_name(obj)[2] for obj in objects)
            return tuple(obj for obj in objects if mesh_ids_from_name(obj)[2] == lowest_lod)

        def aligned_control(layout, label: str):
            row = layout.row(align=True).split(factor=0.25, align=True)
            label_row = row.row(align=True)
            label_row.alignment = "RIGHT"
            label_row.label(text=label)
            return row.row(align=True)

        settings = get_settings()
        box = layout.box()
        expanded = settings.show_mesh_materials
        header = box.row(align=True)
        header.prop(
            settings,
            "show_mesh_materials",
            text="",
            icon="TRIA_DOWN" if expanded else "TRIA_RIGHT",
            emboss=False,
        )
        header.label(text="Mesh Materials", icon="MATERIAL")
        if not expanded:
            return

        groups = visible_material_groups()
        if not groups:
            box.label(text="No visible FFXIV mesh groups.", icon="INFO")
            return

        columns = box.row(align=True).split(factor=0.4, align=True)
        for title in ("OBJECT", "PART", "ATTR"):
            column = columns.row(align=True)
            column.alignment = "CENTER"
            column.label(text=title)

        total_triangles = 0
        for group in groups:
            paths = material_paths(group.objects)
            lod_zero = lod_zero_objects(group.objects)
            vertices = sum(len(obj.data.vertices) for obj in lod_zero)
            total_triangles += sum(triangle_count(obj) for obj in lod_zero)

            mesh_box = box.box()
            mesh_header = mesh_box.row(align=True).split(factor=0.4, align=True)
            mesh_id_row = mesh_header.row(align=True)
            mesh_id_row.label(text=f"Mesh #{group.mesh_index}")
            drag = mesh_id_row.operator(
                "xiv_ie.drag_mesh_order",
                text="",
                icon="GRIP_V",
                emboss=False,
            )
            drag.scope = "GROUP"
            drag.mesh_group = group.mesh_index
            vertex_row = mesh_header.row(align=True)
            vertex_row.alignment = "RIGHT"
            if vertices > 65536:
                vertex_row.label(text="", icon="ERROR")
            elif vertices > 58982:
                vertex_row.label(text="", icon="INFO")
            vertex_row.label(text=f"Vertices: {vertices:,}")

            mesh_box.separator(type="LINE", factor=0.2)
            mesh_column = mesh_box.column(align=True)
            for part in group.parts:
                part_objects = mesh_part_objects(group.objects, group.mesh_index, part)
                display_objects = lod_zero_objects(part_objects)
                representative = display_objects[0]
                duplicate_ids = len(display_objects) > 1

                object_row = mesh_column.row(align=True).split(factor=0.4, align=True)

                name_row = object_row.row(align=True)
                rename = name_row.operator(
                    "xiv_ie.rename_mesh_part",
                    text=mesh_display_name(representative),
                    emboss=False,
                )
                rename.mesh_group = group.mesh_index
                rename.mesh_part = part

                part_row = object_row.row(align=True)
                part_row.label(text="", icon="ERROR" if duplicate_ids else "BLANK1")
                part_row.label(text=str(part))
                drag = part_row.operator(
                    "xiv_ie.drag_mesh_order",
                    text="",
                    icon="GRIP_V",
                    emboss=False,
                )
                drag.scope = "PART"
                drag.mesh_group = group.mesh_index
                drag.mesh_part = part

                attribute_row = object_row.row(align=True)
                attribute_row.alignment = "EXPAND"
                attributes = mesh_part_attributes(display_objects)
                if not attributes:
                    attribute_row.label(text="", icon="BLANK1")
                for attribute in attributes:
                    remove = attribute_row.operator(
                        "xiv_ie.mesh_attribute",
                        text=attribute_display_name(attribute),
                    )
                    remove.mesh_group = group.mesh_index
                    remove.mesh_part = part
                    remove.attribute = attribute
                add = attribute_row.operator("xiv_ie.mesh_attribute", text="", icon="ADD")
                add.mesh_group = group.mesh_index
                add.mesh_part = part
                add.attribute = "NEW"

            mesh_box.separator(type="LINE", factor=0.5)
            if not paths:
                text = "Add Mesh Properties"
            elif len(paths) > 1:
                text = "Multiple Materials"
            else:
                text = paths[0]
            material_row = aligned_control(mesh_box.column(align=True), "Material:")
            operator = material_row.operator("xiv_ie.mesh_material", text=text)
            operator.mesh_group = group.mesh_index

        visible_lod_zero = {
            obj
            for group in groups
            for obj in lod_zero_objects(group.objects)
        }
        selected_triangles = sum(
            triangle_count(obj)
            for obj in context.selected_objects
            if obj.type == "MESH" and obj in visible_lod_zero
        )
        summary = box.row(align=True)
        summary.alignment = "RIGHT"
        count = f"{selected_triangles:,} / {total_triangles:,}" if selected_triangles else f"{total_triangles:,}"
        summary.label(text=f"Triangles: {count}")

    @staticmethod
    def _draw_simple_export(layout) -> None:
        settings = get_settings()
        box = layout.box()
        expanded = settings.show_simple_export
        header = box.row(align=True)
        header.prop(
            settings,
            "show_simple_export",
            text="",
            icon="TRIA_DOWN" if expanded else "TRIA_RIGHT",
            emboss=False,
        )
        header.label(text="Simple Import/Export", icon="EXPORT")
        if not expanded:
            return

        tabs = box.row(align=True)
        tabs.prop(settings, "simple_io_tab", expand=True)

        if settings.simple_io_tab == "IMPORT":
            box.prop(settings, "import_format", expand=True)
            box.prop(
                settings,
                "simple_import_use_existing_skeleton",
                text="Remove imported armature and use existing skeleton",
            )
            if settings.simple_import_use_existing_skeleton:
                box.prop(settings, "simple_import_skeleton", text="Skeleton Object")
            box.operator("xiv_ie.simple_import", text="Import", icon="IMPORT")
            return

        box.prop(settings, "export_directory")
        row = box.row(align=True)
        row.prop(settings, "export_name")
        row.prop(settings, "model_format", text="")
        box.operator("xiv_ie.simple_export", icon="EXPORT")

    @staticmethod
    def _draw_export_options(layout) -> None:
        settings = get_settings()
        box = layout.box()
        expanded = settings.show_export_options
        header = box.row(align=True)
        header.prop(
            settings,
            "show_export_options",
            text="",
            icon="TRIA_DOWN" if expanded else "TRIA_RIGHT",
            emboss=False,
        )
        header.label(text="Export Options", icon="EXPORT")
        if not expanded:
            return

        options = box.box()
        options.label(text="Model Options")
        row = options.row(align=True)
        row.prop(settings, "keep_shapekeys")
        row.prop(settings, "check_tris")
        row = options.row(align=True)
        row.prop(settings, "create_backfaces")
        options.prop(settings, "remove_yas")
        options.prop(settings, "backup_models_on_export")

        cleanup = box.box()
        cleanup.label(text="Vertex Data Fixes")
        row = cleanup.row(align=True)
        row.prop(settings, "clear_uv2")
        row.prop(settings, "copy_uv1_to_uv2")
        row = cleanup.row(align=True)
        row.prop(settings, "clear_vertex_color1")
        row.prop(settings, "clear_vertex_alpha1")
        row = cleanup.row(align=True)
        row.prop(settings, "clear_vertex_color2")
        row.prop(settings, "clear_flow_data")

    @staticmethod
    def _draw_backups(layout, context: Context) -> None:
        settings = get_settings()
        if not settings.backup_models_on_export:
            return
        box = layout.box()
        expanded = settings.show_backups
        header = box.row(align=True)
        header.prop(
            settings,
            "show_backups",
            text="",
            icon="TRIA_DOWN" if expanded else "TRIA_RIGHT",
            emboss=False,
        )
        header.label(text="Backup", icon="FILE_BACKUP")
        if not expanded:
            return

        folder, source = target_folder(settings, context)
        if folder is None:
            box.label(text=f"{source} is unavailable.", icon="INFO")
            return
        box.label(text=f"Folder: {folder}")
        entries = list_backups(folder)
        if not entries:
            box.label(text="No model backups found.", icon="INFO")
        for entry in entries:
            row = box.row(align=True)
            row.label(text=entry.original_name, icon="FILE")
            row.label(text=entry.created.astimezone().strftime("%Y-%m-%d %H:%M:%S"))
            restore = row.operator("xiv_ie.restore_backup", text="", icon="FILE_REFRESH")
            restore.backup_name = entry.path.name
            import_op = row.operator("xiv_ie.import_backup", text="", icon="IMPORT")
            import_op.backup_name = entry.path.name
        clear = box.row()
        clear.enabled = bool(entries)
        clear.operator("xiv_ie.clear_backups", text="Clear All Backups", icon="TRASH")

    @staticmethod
    def _draw_utilities(layout) -> None:
        props = get_instant_edit_props()
        box = layout.box()
        expanded = props.show_utilities
        header = box.row(align=True)
        header.prop(
            props,
            "show_utilities",
            text="",
            icon="TRIA_DOWN" if expanded else "TRIA_RIGHT",
            emboss=False,
        )
        header.label(text="Toolbox", icon="TOOL_SETTINGS")
        if expanded:
            box.operator("xiv_ie.clear_contexts", text="Clear Contexts", icon="TRASH")
