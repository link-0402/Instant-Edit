# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy
from bpy.app.handlers import persistent

from .props  import set_addon_properties, remove_addon_properties, get_instant_edit_props
from .server import get_server_error, set_callback_port, start_server, stop_server, poll_import_queue
from .recovery import cancel_recovery, schedule_recovery


@persistent
def _recover_after_scene_load(_dummy) -> None:
    schedule_recovery()


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
    if _recover_after_scene_load not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_recover_after_scene_load)
    schedule_recovery()


def unregister() -> None:
    if _recover_after_scene_load in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_recover_after_scene_load)
    cancel_recovery()
    stop_server()
    try:
        bpy.app.timers.unregister(poll_import_queue)
    except Exception:
        pass
    try:
        remove_addon_properties()
    except (AttributeError, RuntimeError):
        pass
