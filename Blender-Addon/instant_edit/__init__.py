# Modified for XIV Instant Edit, 2026.
import bpy
from bpy.app.handlers import persistent

from .props  import set_addon_properties, remove_addon_properties, get_instant_edit_props
from .server import get_server_error, start_server, stop_server, poll_import_queue
from .recovery import cancel_recovery, schedule_recovery
from .revocation import cancel_revocations, schedule_revocations
from .cache import STALE_SECONDS, clean_cache, configure_cache


_visibility_check_pending = False


@persistent
def _recover_after_scene_load(_dummy) -> None:
    schedule_revocations()
    schedule_recovery()


def _switch_hidden_export_context() -> None:
    """Move the selector away from a context hidden in the current view layer."""
    from .context import collection_visible_in_view_layer, context_collections, context_id_for_object, _value
    from .props import NO_EXPORT_CONTEXT

    scene = getattr(bpy.context, "scene", None)
    view_layer = getattr(bpy.context, "view_layer", None)
    if scene is None or view_layer is None:
        return
    collections = sorted(
        context_collections(scene),
        key=lambda value: (
            str(_value(value, "source_game_path", "")).casefold(),
            str(_value(value, "context_id", "")).casefold(),
        ),
    )
    if len(collections) < 2:
        return
    props = get_instant_edit_props()
    selected_id = props.export_destination
    selected_index = next(
        (index for index, value in enumerate(collections)
         if str(_value(value, "context_id", "")) == selected_id),
        None,
    )
    if selected_index is None or collection_visible_in_view_layer(
        collections[selected_index], view_layer
    ):
        return

    visible = [
        value for value in collections
        if collection_visible_in_view_layer(value, view_layer)
    ]
    target_id = NO_EXPORT_CONTEXT
    visible_ids = {
        str(_value(value, "context_id", "")) for value in visible
    }
    preferred_objects = []
    active = getattr(view_layer.objects, "active", None)
    if active is not None:
        preferred_objects.append(active)
    preferred_objects.extend(
        obj for obj in getattr(bpy.context, "selected_objects", ())
        if obj is not active
    )
    for obj in preferred_objects:
        context_id = context_id_for_object(obj)
        if context_id in visible_ids:
            target_id = context_id
            break
    else:
        for offset in range(1, len(collections) + 1):
            candidate = collections[(selected_index + offset) % len(collections)]
            candidate_id = str(_value(candidate, "context_id", ""))
            if candidate_id in visible_ids:
                target_id = candidate_id
                break

    if props.export_destination != target_id:
        # Enum assignment deliberately reuses _export_destination_changed so
        # cached Penumbra target data is cleared and rebuilt for the new Context.
        props.export_destination = target_id


def _run_visibility_check():
    global _visibility_check_pending
    _visibility_check_pending = False
    try:
        _switch_hidden_export_context()
    except (AttributeError, ReferenceError, RuntimeError):
        pass
    return None


@persistent
def _context_visibility_changed(_scene, _depsgraph) -> None:
    global _visibility_check_pending
    if _visibility_check_pending:
        return
    _visibility_check_pending = True
    bpy.app.timers.register(_run_visibility_check, first_interval=0.05)


def register() -> None:
    set_addon_properties()

    port = 42424
    try:
        from ..preferences import get_prefs
        prefs = get_prefs()
        port = prefs.instant_edit_blender_port
        configure_cache(
            bpy.path.abspath(prefs.instant_edit_cache_directory),
            prefs.instant_edit_auto_cleanup,
        )
        if prefs.instant_edit_auto_cleanup:
            clean_cache(STALE_SECONDS)
    except Exception:
        pass

    if not start_server(port):
        error = get_server_error() or "the port may already be in use"
        props = get_instant_edit_props()
        props.last_status = f"XIV Instant Edit listener unavailable on port {port}: {error}"
        print(props.last_status)
    bpy.app.timers.register(poll_import_queue, first_interval=1.0, persistent=True)
    if _recover_after_scene_load not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_recover_after_scene_load)
    if _context_visibility_changed not in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.append(_context_visibility_changed)
    schedule_recovery()
    schedule_revocations()


def unregister() -> None:
    global _visibility_check_pending
    if _context_visibility_changed in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.remove(_context_visibility_changed)
    if _recover_after_scene_load in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_recover_after_scene_load)
    cancel_recovery()
    cancel_revocations()
    stop_server()
    try:
        bpy.app.timers.unregister(poll_import_queue)
    except Exception:
        pass
    try:
        bpy.app.timers.unregister(_run_visibility_check)
    except Exception:
        pass
    _visibility_check_pending = False
    try:
        remove_addon_properties()
    except (AttributeError, RuntimeError):
        pass
