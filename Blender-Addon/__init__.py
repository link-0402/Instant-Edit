"""XIV Instant Edit Blender extension."""

import bpy

from . import instant_edit
from .instant_edit import ops as instant_ops
from .instant_edit import props as instant_props
from .operators import XIVIE_OT_mesh_material, XIVIE_OT_simple_export
from .preferences import XIVIEPreferences
from .properties import XIVIEExportSettings, set_addon_properties, remove_addon_properties
from .ui import XIVIE_PT_main


CLASSES = [
    XIVIEPreferences,
    XIVIEExportSettings,
    *instant_props.CLASSES,
    *instant_ops.CLASSES,
    XIVIE_OT_mesh_material,
    XIVIE_OT_simple_export,
    XIVIE_PT_main,
]


def register() -> None:
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    set_addon_properties()
    instant_edit.register()


def unregister() -> None:
    instant_edit.unregister()
    remove_addon_properties()
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
