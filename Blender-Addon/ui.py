from pathlib import Path
import textwrap

import bpy

from bpy.types import Context, Panel

from .instant_edit.context import ContextValidationError, mesh_ids_from_name
from .instant_edit.ops import export_destination_context, normalise_variant_name
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


def _relative_physical_path(file_path: str, root_path: str) -> str:
    """Return a physical file path relative to its source mod root."""
    if not file_path or not root_path:
        return ""
    try:
        return Path(file_path).resolve(strict=False).relative_to(
            Path(root_path).resolve(strict=False)
        ).as_posix()
    except (OSError, ValueError):
        return ""


def _import_file_display(ref) -> str:
    """Prefer the plugin-validated path, with a legacy physical-path fallback."""
    relative = str(getattr(ref, "target_relative_path", "") or "").strip()
    if relative:
        return relative.replace("\\", "/")
    return _relative_physical_path(
        str(getattr(ref, "target_file_path", "") or ""),
        str(getattr(ref, "source_mod_root_path", "") or ""),
    ) or "Unavailable"


def _export_destination_display(ref, props=None) -> str:
    """Return the effective mod-relative Quick Export target and model file."""
    relative = str(getattr(ref, "target_relative_path", "") or "").strip()
    if relative:
        destination = relative.replace("\\", "/")
    else:
        target_file_path = str(getattr(ref, "target_file_path", "") or "").strip()
        destination = _relative_physical_path(
            target_file_path,
            str(getattr(ref, "source_mod_root_path", "") or ""),
        )
        if not destination:
            destination = Path(target_file_path).name if target_file_path else "Unavailable"

    if props is None:
        return destination

    selected_target = next(
        (item for item in props.variant_targets if item.selection_id == props.variant_target), None)
    if (
        selected_target is not None
        and selected_target.kind == "OPTION"
        and selected_target.model_path
    ):
        return selected_target.model_path.replace("\\", "/")

    try:
        variant_name = normalise_variant_name(getattr(props, "variant_name", ""))
    except ValueError:
        return destination
    directory, separator, _ = destination.rpartition("/")
    return f"{directory}/{variant_name}.mdl" if separator else f"{variant_name}.mdl"


def _display_wrap_width(context: Context) -> int:
    """Estimate the sidebar's available text width in characters."""
    region_width = int(getattr(getattr(context, "region", None), "width", 0) or 0)
    if region_width:
        return max(28, min(72, region_width // 8))
    return 42


def _wrap_display_value(value: str, width: int) -> list[str]:
    """Wrap paths at slash boundaries before splitting a long path segment."""
    value = value or "Unavailable"
    if "/" not in value:
        return textwrap.wrap(
            value,
            width=width,
            break_long_words=True,
            break_on_hyphens=False,
        )

    segments = value.split("/")
    tokens = [
        f"{segment}/" if index < len(segments) - 1 else segment
        for index, segment in enumerate(segments)
    ]
    lines = []
    current = ""
    for token in tokens:
        if current and len(current) + len(token) > width:
            lines.append(current)
            current = ""
        if len(token) > width:
            chunks = textwrap.wrap(
                token,
                width=width,
                break_long_words=True,
                break_on_hyphens=False,
            )
            lines.extend(chunks[:-1])
            current = chunks[-1] if chunks else ""
        else:
            current += token
    if current or not lines:
        lines.append(current)
    return lines


def _draw_wrapped_display(
    layout,
    context: Context,
    label: str,
    value: str,
    icon: str = "NONE",
) -> None:
    """Draw a simple multi-line display for a potentially long filesystem path."""
    column = layout.column(align=True)
    column.label(text=f"{label}:", icon=icon)
    wrapped = _wrap_display_value(value, _display_wrap_width(context))
    for line in wrapped or ("Unavailable",):
        column.label(text=line)


def _draw_named_text_input(layout, props, property_name: str, label: str) -> None:
    """Keep the label readable while reserving a compact field for the value."""
    row = layout.row(align=True)
    split = row.split(factor=0.42, align=True)
    split.label(text=label)
    split.prop(props, property_name, text="")


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
        try:
            ref = export_destination_context(context)
        except ContextValidationError:
            ref = None

        if ref is not None:
            source_mod = ref.source_mod_root_path or ref.source_mod_name or ref.source_mod_directory
            box.label(text=f"Source Mod: {source_mod}")
            _draw_wrapped_display(box, context, "Imported File", _import_file_display(ref))
            destination = box.box()
            _draw_wrapped_display(
                destination,
                context,
                "Current Export Destination",
                _export_destination_display(ref, props),
                icon="EXPORT",
            )
        else:
            box.label(text="Pick a model through the Dalamud plugin.", icon="INFO")

        box.prop(props, "export_destination", text="Context")
        box.prop(props, "export_scope")
        if ref is not None:
            targets = box.box()
            header = targets.row(align=True)
            header.label(text="Penumbra Group Target", icon="OUTLINER_COLLECTION")
            header.operator("xiv_ie.refresh_variant_targets", text="", icon="FILE_REFRESH")
            targets.label(
                text="Choose a group to create a new option, or an option to overwrite it.",
                icon="INFO",
            )
            if props.variant_targets_context_id and props.variant_targets_context_id != getattr(ref, "context_id", ""):
                targets.label(text="Refresh targets for this Context.", icon="INFO")
            new_group = targets.row(align=True)
            new_group.operator(
                "xiv_ie.select_variant_target",
                text="New Group",
                depress=props.variant_target == "NEW_GROUP",
                icon="ADD",
            ).selection_id = "NEW_GROUP"
            if not props.variant_targets:
                targets.label(text="Refresh to load compatible groups and options.", icon="INFO")
            group_expanded = True
            for item in props.variant_targets:
                if item.kind == "GROUP":
                    group_expanded = item.expanded
                    group_row = targets.row(align=True)
                    toggle = group_row.operator(
                        "xiv_ie.toggle_variant_target_group",
                        text="",
                        icon="TRIA_DOWN" if item.expanded else "TRIA_RIGHT",
                        emboss=False,
                    )
                    toggle.selection_id = item.selection_id
                    group_row.operator(
                        "xiv_ie.select_variant_target",
                        text=item.group_name,
                        depress=props.variant_target == item.selection_id,
                        icon="OUTLINER_COLLECTION",
                    ).selection_id = item.selection_id
                elif item.kind == "OPTION" and group_expanded:
                    option_row = targets.row(align=True)
                    option_row.label(text="", icon="BLANK1")
                    option_row.operator(
                        "xiv_ie.select_variant_target",
                        text=item.option_name,
                        depress=props.variant_target == item.selection_id,
                        icon="FILE",
                    ).selection_id = item.selection_id
            selected_target = next(
                (item for item in props.variant_targets if item.selection_id == props.variant_target), None)
            if props.variant_target == "NEW_GROUP":
                _draw_named_text_input(box, props, "variant_group_name", "New Group Name")
            if selected_target is None or selected_target.kind != "OPTION":
                _draw_named_text_input(box, props, "variant_name", "New Option Name")
            box.operator("xiv_ie.instant_export", text="Quick Export", icon="EXPORT")
        status_row = box.row(align=True)
        status = status_row.split(factor=0.14, align=True)
        status.label(text="Status:", icon="INFO")
        status_value = status.row(align=True)
        status_value.prop(props, "last_status", text="")
        status_value.operator("xiv_ie.copy_status", text="", icon="COPYDOWN")

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
                    attribute_row.label(text=" ", icon="BLANK1")
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
        options.prop(settings, "simple_import_set_export_directory")

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
