import bpy
import tempfile

from bpy.props import BoolProperty, IntProperty, StringProperty
from bpy.types import AddonPreferences, Operator

from .instant_edit.cache import (
    STALE_SECONDS,
    cache_root,
    clean_cache,
    configure_cache,
)


def _update_listen_port(self, _context) -> None:
    from .instant_edit.server import set_server_port
    set_server_port(self.instant_edit_blender_port)


def _update_callback_port(self, _context) -> None:
    from .instant_edit.server import set_callback_port
    set_callback_port(self.instant_edit_plugin_port)


def _update_cache(self, _context) -> None:
    try:
        configure_cache(
            bpy.path.abspath(self.instant_edit_cache_directory),
            self.instant_edit_auto_cleanup,
        )
        if self.instant_edit_auto_cleanup:
            clean_cache(STALE_SECONDS)
    except Exception as error:
        print(f"XIV Instant Edit: could not configure cache: {error}")


class XIVIE_OT_clean_cache(Operator):
    bl_idname = "xiv_ie.clean_cache"
    bl_label = "Clean Cache Now"
    bl_description = "Remove all owned XIV Instant Edit import and export cache jobs"

    def execute(self, _context):
        try:
            jobs, byte_count = clean_cache()
        except Exception as error:
            self.report({"ERROR"}, f"Cache cleanup failed: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Removed {jobs} cache job(s), {byte_count / (1024 * 1024):.1f} MiB")
        return {"FINISHED"}


class XIVIEPreferences(AddonPreferences):
    bl_idname = __package__

    instant_edit_blender_port: IntProperty(
        name="Blender Listen Port",
        description="Port used to receive imports from the XIV Instant Edit Dalamud plugin",
        default=42424,
        min=1,
        max=65535,
        update=_update_listen_port,
    )  # type: ignore

    instant_edit_plugin_port: IntProperty(
        name="Plugin Callback Port",
        description="Fallback callback port for legacy import requests",
        default=42428,
        min=1,
        max=65535,
        update=_update_callback_port,
    )  # type: ignore

    instant_edit_cache_directory: StringProperty(
        name="Cache Directory",
        description="Base folder for the add-on-owned XIV-Instant-Edit cache directory",
        subtype="DIR_PATH",
        default=tempfile.gettempdir(),
        update=_update_cache,
    )  # type: ignore

    instant_edit_auto_cleanup: BoolProperty(
        name="Automatic Cache Cleanup",
        description="Remove completed cache jobs and crash leftovers older than 24 hours",
        default=True,
        update=_update_cache,
    )  # type: ignore

    def draw(self, _context) -> None:
        layout = self.layout
        layout.label(text="XIV Instant Edit Connection")
        layout.prop(self, "instant_edit_blender_port")
        layout.prop(self, "instant_edit_plugin_port")
        layout.separator()
        layout.label(text="XIV Instant Edit Cache")
        layout.prop(self, "instant_edit_cache_directory")
        layout.prop(self, "instant_edit_auto_cleanup")
        layout.label(text=f"Managed folder: {cache_root()}")
        layout.operator("xiv_ie.clean_cache", icon="TRASH")


def get_prefs() -> XIVIEPreferences:
    return bpy.context.preferences.addons[__package__].preferences
