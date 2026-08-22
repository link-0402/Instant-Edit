import bpy

from bpy.props import IntProperty
from bpy.types import AddonPreferences


def _update_listen_port(self, _context) -> None:
    from .instant_edit.server import set_server_port
    set_server_port(self.instant_edit_blender_port)


def _update_callback_port(self, _context) -> None:
    from .instant_edit.server import set_callback_port
    set_callback_port(self.instant_edit_plugin_port)


class XIVIEPreferences(AddonPreferences):
    bl_idname = __package__

    instant_edit_blender_port: IntProperty(
        name="Blender Listen Port",
        description="Port used to receive imports from the Instant Edit Dalamud plugin",
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

    def draw(self, _context) -> None:
        layout = self.layout
        layout.label(text="Instant Edit Connection")
        layout.prop(self, "instant_edit_blender_port")
        layout.prop(self, "instant_edit_plugin_port")


def get_prefs() -> XIVIEPreferences:
    return bpy.context.preferences.addons[__package__].preferences

