"""XIV Instant Edit Blender extension."""

import bpy

from . import instant_edit
from .instant_edit import ops as instant_ops
from .instant_edit import props as instant_props
from .operators import (
    XIVIE_OT_drag_mesh_order,
    XIVIE_OT_mesh_attribute,
    XIVIE_OT_mesh_flow,
    XIVIE_OT_mesh_material,
    XIVIE_OT_mesh_tags,
    XIVIE_OT_rename_mesh_part,
    XIVIE_OT_simple_export,
)
from .preferences import XIVIEPreferences
from .properties import XIVIEExportSettings, set_addon_properties, remove_addon_properties
from .ui import XIVIE_PT_main


CLASSES = [
    XIVIEPreferences,
    XIVIEExportSettings,
    *instant_props.CLASSES,
    *instant_ops.CLASSES,
    XIVIE_OT_drag_mesh_order,
    XIVIE_OT_mesh_attribute,
    XIVIE_OT_mesh_flow,
    XIVIE_OT_mesh_material,
    XIVIE_OT_mesh_tags,
    XIVIE_OT_rename_mesh_part,
    XIVIE_OT_simple_export,
    XIVIE_PT_main,
]


def _registered_class(cls):
    """Return a stale or current Blender class registered under this name."""
    registered = getattr(bpy.types, cls.__name__, None)
    return registered if registered is not None else None


def register() -> None:
    # A failed registration can leave the classes processed before the failure
    # behind. Clean those up so Blender can retry without a restart.
    if any(_registered_class(cls) is not None for cls in CLASSES):
        unregister()
    try:
        for cls in CLASSES:
            bpy.utils.register_class(cls)
        set_addon_properties()
        instant_edit.register()
    except Exception:
        unregister()
        raise


def unregister() -> None:
    instant_edit.unregister()
    try:
        remove_addon_properties()
    except (AttributeError, RuntimeError):
        pass
    for cls in reversed(CLASSES):
        registered = _registered_class(cls)
        if registered is not None:
            try:
                bpy.utils.unregister_class(registered)
            except (AttributeError, RuntimeError):
                pass
