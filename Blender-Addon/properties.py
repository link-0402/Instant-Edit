import bpy

from bpy.props import BoolProperty, EnumProperty, PointerProperty, StringProperty
from bpy.types import Object, PropertyGroup


MODEL_FLAG_DEFAULTS = {
    "shadow_disabled": False,
    "light_shadow_disabled": False,
    "waving_animation_disabled": True,
    "lighting_reflection_enabled": False,
    "unknown1": False,
    "rain_occlusion_enabled": False,
    "snow_occlusion_enabled": False,
    "dust_occlusion_enabled": False,
    "unknown2": False,
    "edge_geometry_disabled": False,
    "force_lod_range_enabled": False,
    "shadow_mask_enabled": False,
    "extra_lod_enabled": False,
    "enable_force_non_resident": False,
    "bg_uv_scroll_enabled": False,
    "static_mesh": False,
    "unknown3": False,
    "use_crest_change": False,
    "use_material_change": False,
    "unknown4": False,
    "unknown5": False,
    "unknown6": False,
    "unknown7": False,
    "unknown8": False,
}

NECK_MORPH_ITEMS = [
    ("0", "None", "Do not generate neck morph data"),
    ("0101", "Midlander Male", ""), ("0201", "Midlander Female", ""),
    ("0301", "Highlander Male", ""), ("0401", "Highlander Female", ""),
    ("0501", "Elezen Male", ""), ("0601", "Elezen Female", ""),
    ("0701", "Miqo'te Male", ""), ("0801", "Miqo'te Female", ""),
    ("0901", "Roegadyn Male", ""), ("1001", "Roegadyn Female", ""),
    ("1101", "Lalafell Male", ""), ("1201", "Lalafell Female", ""),
    ("1301", "Au Ra Male", ""), ("1401", "Au Ra Female", ""),
    ("1501", "Hrothgar Male", ""), ("1601", "Hrothgar Female", ""),
    ("1701", "Viera Male", ""), ("1801", "Viera Female", ""),
]


class XIVIEExportSettings(PropertyGroup):
    show_mesh_materials: BoolProperty(name="Mesh Materials", default=True)  # type: ignore
    show_simple_export: BoolProperty(name="Simple Import/Export", default=False)  # type: ignore
    simple_io_tab: EnumProperty(
        name="Simple Import/Export",
        items=[
            ("IMPORT", "Import", "Import an MDL or FBX file"),
            ("EXPORT", "Export", "Export visible mesh objects"),
        ],
        default="EXPORT",
    )  # type: ignore
    show_export_options: BoolProperty(name="Export Options", default=True)  # type: ignore
    backup_models_on_export: BoolProperty(
        name="Backup models on Export",
        description="Keep timestamped backups before replacing existing MDL or FBX files",
        default=False,
    )  # type: ignore
    show_backups: BoolProperty(name="Backup", default=False)  # type: ignore

    export_directory: StringProperty(name="Export Folder", subtype="DIR_PATH", default="")  # type: ignore
    export_name: StringProperty(name="File Name", default="model", maxlen=255)  # type: ignore
    model_format: EnumProperty(
        name="Format",
        items=[("MDL", "MDL", "FFXIV model"), ("FBX", "FBX", "Autodesk FBX"), ("GLTF", "glTF", "glTF")],
        default="MDL",
    )  # type: ignore
    import_format: EnumProperty(
        name="Format",
        items=[("MDL", "MDL", "FFXIV model"), ("FBX", "FBX", "Autodesk FBX")],
        default="MDL",
    )  # type: ignore
    simple_import_use_existing_skeleton: BoolProperty(
        name="Use Existing Skeleton",
        description="Remove the imported armature and bind meshes to an existing Blender armature",
        default=False,
    )  # type: ignore
    simple_import_skeleton: PointerProperty(
        type=Object,
        name="Skeleton Object",
        description="Existing Blender armature to use for Simple Import",
        poll=lambda _self, obj: obj.type == "ARMATURE",
    )  # type: ignore
    keep_shapekeys: BoolProperty(name="Keep Shape Keys", default=True)  # type: ignore
    check_tris: BoolProperty(name="Check Triangulation", default=True)  # type: ignore
    create_backfaces: BoolProperty(name="Create Backfaces", default=False)  # type: ignore
    remove_yas: EnumProperty(
        name="YAS Groups",
        items=[("KEEP", "Keep", "Keep all groups"), ("NO_GEN", "Remove Genitalia", "Remove genital groups"), ("REMOVE", "Remove All", "Remove iv_/ya_ groups")],
        default="KEEP",
    )  # type: ignore
    use_lods: BoolProperty(name="Export LODs", default=False)  # type: ignore
    neck_morph: EnumProperty(
        name="Neck Morph",
        items=NECK_MORPH_ITEMS,
        default="0",
    )  # type: ignore

    clear_uv2: BoolProperty(name="Clear UV2", default=False)  # type: ignore
    copy_uv1_to_uv2: BoolProperty(name="Copy UV1 to UV2", default=False)  # type: ignore
    clear_vertex_color1: BoolProperty(name="Clear Vertex Color 1", default=False)  # type: ignore
    clear_vertex_alpha1: BoolProperty(name="Clear Vertex Alpha 1", default=False)  # type: ignore
    clear_vertex_color2: BoolProperty(name="Clear Vertex Color 2", default=False)  # type: ignore
    clear_flow_data: BoolProperty(name="Clear Flow Data", default=False)  # type: ignore

    def get_mesh_options(self) -> dict[str, bool]:
        return {
            "clear_uv2": self.clear_uv2,
            "copy_uv1_to_uv2": self.copy_uv1_to_uv2,
            "clear_vertex_color1": self.clear_vertex_color1,
            "clear_vertex_alpha1": self.clear_vertex_alpha1,
            "clear_vertex_color2": self.clear_vertex_color2,
            "clear_flow_data": self.clear_flow_data,
        }

    def get_model_flags(self) -> dict[str, bool]:
        return dict(MODEL_FLAG_DEFAULTS)


def get_settings() -> XIVIEExportSettings:
    return bpy.context.scene.xiv_ie_settings


def set_addon_properties() -> None:
    bpy.types.Scene.xiv_ie_settings = PointerProperty(type=XIVIEExportSettings)


def remove_addon_properties() -> None:
    del bpy.types.Scene.xiv_ie_settings
