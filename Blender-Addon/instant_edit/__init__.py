# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy

from .props  import set_addon_properties, remove_addon_properties, get_instant_edit_props
from .server import get_server_error, set_callback_port, start_server, stop_server, poll_import_queue


def register() -> None:
    set_addon_properties()

    port = 42424
    try:
        from ..preferences import get_prefs
        prefs = get_prefs()
        port = prefs.instant_edit_blender_port
        set_callback_port(prefs.instant_edit_plugin_port)
    except Exception:
        pass

    if not start_server(port):
        error = get_server_error() or "the port may already be in use"
        props = get_instant_edit_props()
        props.last_status = f"Instant Edit listener unavailable on port {port}: {error}"
        print(props.last_status)
    bpy.app.timers.register(poll_import_queue, first_interval=1.0, persistent=True)


def unregister() -> None:
    stop_server()
    try:
        bpy.app.timers.unregister(poll_import_queue)
    except Exception:
        pass
    try:
        remove_addon_properties()
    except (AttributeError, RuntimeError):
        pass
