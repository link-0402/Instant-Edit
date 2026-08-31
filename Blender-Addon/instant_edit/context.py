"""Context records used by the safe XIV Instant Edit v1 bridge.

The collection and object custom properties in this module are deliberately
duplicated (plain and ``instant_edit_*`` names).  The plain names make the
metadata easy to inspect in Blender, while the prefixed names avoid confusing
it with metadata produced by the normal exporter.
"""
# Modified for XIV Instant Edit, 2026.

from dataclasses import dataclass
from typing import Iterable
import re

import bpy


SCHEMA = "instant-edit.context"
VERSION = 2
SUPPORTED_VERSIONS = {1, 2}
COLLECTION_TAG = "instant_edit_context_id"
OBJECT_TAG = "instant_edit_context_id"

CONTEXT_METADATA_FIELDS = (
    "context_id", "schema", "version", "plugin_instance_id", "capability",
    "source_game_path", "managed_destination", "target_file_path",
    "source_mod_directory", "source_mod_name", "source_mod_root_path",
    "target_relative_path", "source_kind", "resolved_game_path", "destination_state",
    "target_collection_id", "target_collection_name",
    "resource_manifest_version", "resource_manifest_status",
    "import_id", "callback_port", "import_file_name", "collection_kind",
)

REQUIRED_OBJECT_FIELDS = (
    "xiv_material",
    "original_material",
    "material_index",
    "mesh_index",
    "submesh_index",
    "context_id",
    "schema",
    "version",
)


class ContextValidationError(ValueError):
    """Raised when an XIV Instant Edit context cannot be used safely."""


@dataclass(frozen=True)
class MeshNameInfo:
    """Parsed YAA mesh identifier and the label/orientation around it."""

    mesh_group: int
    mesh_part: int
    lod: int
    label: str
    prefix: bool


_MESH_ID_PREFIX = re.compile(r"^(\d+)\.(\d+)(?:\s+(.*))?$")
_MESH_ID_SUFFIX = re.compile(r"^(.+?)\s+(\d+)\.(\d+)$")
_LOD_SUFFIX = re.compile(r"\s+LOD(\d+)$", re.IGNORECASE)


def mesh_name_info(obj) -> MeshNameInfo:
    """Parse a mesh name while retaining its ID orientation and display label."""
    name = str(obj.name).strip()
    lod = 0
    lod_match = _LOD_SUFFIX.search(name)
    if lod_match:
        lod = int(lod_match.group(1))
        name = name[:lod_match.start()].rstrip()
    if lod > 2:
        raise ContextValidationError(f"{obj.name}: LOD must be 0, 1, or 2")

    prefix = _MESH_ID_PREFIX.match(name)
    if prefix:
        return MeshNameInfo(
            int(prefix.group(1)),
            int(prefix.group(2)),
            lod,
            (prefix.group(3) or "").strip(),
            True,
        )

    suffix = _MESH_ID_SUFFIX.match(name)
    if suffix:
        return MeshNameInfo(
            int(suffix.group(2)),
            int(suffix.group(3)),
            lod,
            suffix.group(1).strip(),
            False,
        )

    raise ContextValidationError(
        f'{obj.name}: expected a mesh name such as "1.2 Name" or "Name 1.2"'
    )


def is_safe_game_model_path(value: str) -> bool:
    """Accept only normalized, non-rooted game paths for model contexts."""
    if not isinstance(value, str) or not value or len(value) > 4096:
        return False
    if value.startswith(("/", "\\")) or "\\" in value or ":" in value or "\0" in value:
        return False
    parts = value.split("/")
    return all(part not in {"", ".", ".."} for part in parts) and value.casefold().endswith(".mdl")


@dataclass(frozen=True)
class ContextRef:
    collection: object
    objects: tuple
    mesh_objects: tuple
    context_id: str
    import_id: str
    plugin_instance_id: str
    capability: str
    source_game_path: str
    source_kind: str
    resolved_game_path: str
    destination_state: str
    managed_destination: str
    target_file_path: str
    source_mod_directory: str
    source_mod_name: str
    source_mod_root_path: str
    target_relative_path: str
    target_collection_id: str
    target_collection_name: str
    resource_manifest_version: int
    resource_manifest_status: str
    callback_port: int


def _value(obj, name: str, default=None):
    """Read a context property, accepting the prefixed spelling as well."""
    if name in obj:
        return obj[name]
    return obj.get(f"instant_edit_{name}", default)


def _set(obj, name: str, value) -> None:
    obj[name] = value
    obj[f"instant_edit_{name}"] = value


def _check_aliases(obj, names: Iterable[str]) -> None:
    for name in names:
        plain = obj.get(name, None)
        prefixed = obj.get(f"instant_edit_{name}", None)
        if name in obj and f"instant_edit_{name}" in obj and plain != prefixed:
            raise ContextValidationError(f"{getattr(obj, 'name', 'context')}: inconsistent {name} metadata")


def tag_object(obj, metadata: dict) -> None:
    """Attach immutable import metadata to an object."""
    for name, value in metadata.items():
        _set(obj, name, value)


def create_collection(scene, metadata: dict):
    context_id = metadata["context_id"]
    if any(_value(collection, "context_id") == context_id for collection in bpy.data.collections):
        raise ContextValidationError(f"Context {context_id!r} already exists")

    collection = bpy.data.collections.new(f"XIV Instant Edit [{context_id}]")
    scene.collection.children.link(collection)
    for name, value in metadata.items():
        _set(collection, name, value)
    _set(collection, "collection_kind", "instant_edit")
    return collection


def _in_scene(scene, collection) -> bool:
    def walk(parent) -> bool:
        for child in parent.children:
            if child == collection or walk(child):
                return True
        return False

    return walk(scene.collection)


def _context_collections(context_id: str, scene=None) -> list:
    collections = [
        collection for collection in bpy.data.collections
        if _value(collection, "context_id") == context_id
    ]
    if scene is not None:
        collections = [collection for collection in collections if _in_scene(scene, collection)]
    return collections


def context_collections(scene=None) -> list:
    """Return the XIV Instant Edit context collections currently held in a scene."""
    scene = scene or bpy.context.scene
    return [
        collection for collection in bpy.data.collections
        if _value(collection, "collection_kind") == "instant_edit"
        and _value(collection, "context_id", "")
        and _in_scene(scene, collection)
    ]


def collection_visible_in_view_layer(collection, view_layer=None) -> bool:
    """Return whether a collection is enabled through the current layer tree."""
    if getattr(collection, "hide_viewport", False):
        return False
    view_layer = view_layer or getattr(bpy.context, "view_layer", None)
    if view_layer is None:
        return False

    def walk(layer_collection, ancestors_visible=True) -> bool:
        visible = (
            ancestors_visible
            and not getattr(layer_collection, "exclude", False)
            and not getattr(layer_collection, "hide_viewport", False)
        )
        if layer_collection.collection == collection:
            return visible
        return any(walk(child, visible) for child in layer_collection.children)

    return walk(view_layer.layer_collection)


def _remove_metadata(obj, fields=CONTEXT_METADATA_FIELDS) -> None:
    for field in fields:
        obj.pop(field, None)
        obj.pop(f"instant_edit_{field}", None)


def clear_context_metadata(scene=None) -> int:
    """Detach all XIV Instant Edit routing metadata without deleting scene objects."""
    collections = context_collections(scene)
    context_ids = {
        _value(collection, "context_id", "")
        for collection in collections
    }
    for obj in bpy.data.objects:
        if _value(obj, "context_id", "") in context_ids:
            _remove_metadata(obj)
    for collection in collections:
        _remove_metadata(collection)
    return len(collections)


def apply_authoritative_context(collection, payload: dict) -> None:
    """Replace persisted routing metadata with the plugin's reattach response."""
    context_id = _value(collection, "context_id", "")
    import_id = _value(collection, "import_id", "")
    if payload.get("contextId") != context_id:
        raise ContextValidationError("context reattach response changed the context id")
    if import_id and payload.get("importId") != import_id:
        raise ContextValidationError("context reattach response changed the import id")
    if payload.get("schema") != SCHEMA or payload.get("version") not in SUPPORTED_VERSIONS:
        raise ContextValidationError("context reattach response has an invalid schema or version")

    fields = {
        "context_id": payload.get("contextId"),
        "schema": payload.get("schema"),
        "version": payload.get("version"),
        "plugin_instance_id": payload.get("pluginInstanceId"),
        "capability": payload.get("capability"),
        "source_game_path": payload.get("sourceGamePath"),
        "source_kind": payload.get("sourceKind") or "mod",
        "resolved_game_path": payload.get("resolvedGamePath") or payload.get("sourceGamePath"),
        "destination_state": payload.get("destinationState") or "ready",
        "managed_destination": payload.get("managedDestination") or "",
        "target_file_path": payload.get("targetFilePath") or "",
        "source_mod_directory": payload.get("sourceModDirectory") or "",
        "source_mod_name": payload.get("sourceModName") or "",
        "source_mod_root_path": payload.get("sourceModRootPath") or "",
        "target_relative_path": payload.get("targetRelativePath") or "",
        "target_collection_id": payload.get("targetCollectionId") or "",
        "target_collection_name": payload.get("targetCollectionName") or "",
        "resource_manifest_version": payload.get("resourceManifestVersion") or 0,
        "resource_manifest_status": payload.get("resourceManifestStatus") or "capture_failed",
        "import_id": payload.get("importId"),
        "callback_port": payload.get("callbackPort"),
    }
    if not all(value is not None for value in fields.values()):
        raise ContextValidationError("context reattach response is incomplete")
    for name, value in fields.items():
        _set(collection, name, value)


def _require_int(value, name: str, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise ContextValidationError(f"{name} must be a non-negative integer")
    return value


def context_id_for_object(obj) -> str:
    """Resolve an object's context from its tag or XIV Instant Edit collection."""
    ids = set()
    tagged = _value(obj, "context_id", "")
    if isinstance(tagged, str) and tagged:
        ids.add(tagged)
    for collection in obj.users_collection:
        collection_id = _value(collection, "context_id", "")
        if (
            isinstance(collection_id, str)
            and collection_id
            and _value(collection, "collection_kind") == "instant_edit"
        ):
            ids.add(collection_id)
    if len(ids) > 1:
        raise ContextValidationError(f"{obj.name}: object belongs to multiple XIV Instant Edit contexts")
    return next(iter(ids), "")


def mesh_ids_from_name(obj) -> tuple[int, int, int]:
    """Parse YAA-compatible group, part, and LOD identifiers from an object name."""
    info = mesh_name_info(obj)
    return info.mesh_group, info.mesh_part, info.lod


def validate_context(context_id: str, scene=None) -> ContextRef:
    """Validate one complete context and return its authoritative collection."""
    scene = scene or bpy.context.scene
    if not isinstance(context_id, str) or not context_id:
        raise ContextValidationError("context id is missing")

    collections = _context_collections(context_id, scene)
    if len(collections) != 1:
        raise ContextValidationError(
            f"context {context_id!r} must have exactly one scene collection"
        )
    collection = collections[0]
    _check_aliases(collection, (
        "context_id", "schema", "version", "plugin_instance_id", "capability",
        "source_game_path", "managed_destination", "target_file_path",
        "source_mod_directory", "source_mod_name", "source_mod_root_path", "callback_port",
        "target_relative_path", "source_kind", "resolved_game_path", "destination_state",
        "target_collection_id", "target_collection_name",
        "resource_manifest_version", "resource_manifest_status",
    ))

    if _value(collection, "schema") != SCHEMA or _value(collection, "version") not in SUPPORTED_VERSIONS:
        raise ContextValidationError("context collection has an invalid schema or version")

    plugin_instance_id = _value(collection, "plugin_instance_id", "")
    capability = _value(collection, "capability", "")
    source_game_path = _value(collection, "source_game_path", "")
    source_kind = _value(collection, "source_kind", "mod")
    resolved_game_path = _value(collection, "resolved_game_path", source_game_path)
    destination_state = _value(collection, "destination_state", "ready")
    managed_destination = _value(collection, "managed_destination", "")
    target_file_path = _value(collection, "target_file_path", "")
    source_mod_directory = _value(collection, "source_mod_directory", "")
    source_mod_name = _value(collection, "source_mod_name", "")
    source_mod_root_path = _value(collection, "source_mod_root_path", "")
    target_relative_path = _value(collection, "target_relative_path", "")
    target_collection_id = _value(collection, "target_collection_id", "")
    target_collection_name = _value(collection, "target_collection_name", "")
    resource_manifest_version = _value(collection, "resource_manifest_version", 0)
    resource_manifest_status = _value(collection, "resource_manifest_status", "capture_failed")
    import_id = _value(collection, "import_id", "")
    callback_port = _value(collection, "callback_port", 0)
    if not all(isinstance(value, str) and value for value in (
        plugin_instance_id, capability, source_game_path, source_kind,
        resolved_game_path, destination_state
    )):
        raise ContextValidationError("context collection is missing immutable reference data")
    if source_kind not in {"mod", "game"} or destination_state not in {"ready", "new_mod_required"}:
        raise ContextValidationError("context collection has an invalid source or destination state")
    if source_kind == "game" and (
        not is_safe_game_model_path(source_game_path) or
        not is_safe_game_model_path(resolved_game_path)
    ):
        raise ContextValidationError("game context contains an unsafe model path")
    if destination_state == "ready" and not all(isinstance(value, str) and value for value in (
        managed_destination, target_file_path, source_mod_directory, source_mod_name
    )):
        raise ContextValidationError("ready context collection is missing destination data")
    if destination_state == "new_mod_required" and (
        source_kind != "game" or any((managed_destination, target_file_path,
                                      source_mod_directory, source_mod_name,
                                      source_mod_root_path, target_relative_path))
    ):
        raise ContextValidationError("pending game context contains unexpected destination data")
    callback_port = _require_int(callback_port, "callback port", 1)
    resource_manifest_version = _require_int(resource_manifest_version, "resource manifest version")
    if resource_manifest_status not in {"capture_failed", "ready"}:
        raise ContextValidationError("context collection has an invalid resource manifest status")
    if callback_port > 65535:
        raise ContextValidationError("callback port is out of range")

    objects = tuple(collection.objects)
    mesh_objects = []
    ids = set()
    for obj in objects:
        tagged_context_id = _value(obj, "context_id", "")
        if tagged_context_id:
            _check_aliases(obj, REQUIRED_OBJECT_FIELDS + ("plugin_instance_id", "capability"))
            if tagged_context_id != context_id:
                raise ContextValidationError(f"{obj.name}: context id does not match its collection")
            if _value(obj, "schema") != SCHEMA or _value(obj, "version") not in SUPPORTED_VERSIONS:
                raise ContextValidationError(f"{obj.name}: invalid XIV Instant Edit metadata schema")
        if obj.type != "MESH":
            continue

        # Imported and duplicated objects retain the immutable source metadata.
        # Newly-created objects are authorized by being explicitly linked into
        # this context collection and intentionally have no source metadata.
        if tagged_context_id:
            for field in REQUIRED_OBJECT_FIELDS:
                if field not in obj and f"instant_edit_{field}" not in obj:
                    raise ContextValidationError(f"{obj.name}: missing {field} metadata")
            material = _value(obj, "xiv_material")
            original = _value(obj, "original_material")
            if not isinstance(material, str) or not material:
                raise ContextValidationError(f"{obj.name}: invalid xiv_material metadata")
            if not isinstance(original, str) or not original:
                raise ContextValidationError(f"{obj.name}: invalid original material metadata")
            _require_int(_value(obj, "material_index"), "material index")
            _require_int(_value(obj, "mesh_index"), "mesh index")
            _require_int(_value(obj, "submesh_index"), "submesh index")

        key = mesh_ids_from_name(obj)
        if key in ids:
            raise ContextValidationError(
                f"duplicate mesh part {key[0]}.{key[1]} at LOD{key[2]}"
            )
        ids.add(key)
        mesh_objects.append(obj)

    if not mesh_objects:
        raise ContextValidationError("context contains no mesh objects")

    return ContextRef(
        collection=collection,
        objects=objects,
        mesh_objects=tuple(mesh_objects),
        context_id=context_id,
        import_id=import_id if isinstance(import_id, str) else "",
        plugin_instance_id=plugin_instance_id,
        capability=capability,
        source_game_path=source_game_path,
        source_kind=source_kind,
        resolved_game_path=resolved_game_path,
        destination_state=destination_state,
        managed_destination=managed_destination,
        target_file_path=target_file_path,
        source_mod_directory=source_mod_directory,
        source_mod_name=source_mod_name,
        source_mod_root_path=source_mod_root_path if isinstance(source_mod_root_path, str) else "",
        target_relative_path=target_relative_path if isinstance(target_relative_path, str) else "",
        target_collection_id=target_collection_id if isinstance(target_collection_id, str) else "",
        target_collection_name=target_collection_name if isinstance(target_collection_name, str) else "",
        resource_manifest_version=resource_manifest_version,
        resource_manifest_status=resource_manifest_status,
        callback_port=callback_port,
    )


def active_context(context=None) -> ContextRef:
    """Resolve the single context represented by the active/selected objects."""
    context = context or bpy.context
    selected = list(context.selected_objects)
    active = context.active_object
    relevant = selected + ([] if active is None or active in selected else [active])

    resolved = [context_id_for_object(obj) for obj in relevant]
    ids = {context_id for context_id in resolved if context_id}
    if len(ids) > 1:
        raise ContextValidationError("selection contains mixed XIV Instant Edit contexts")

    if not ids:
        # Added meshes may intentionally live outside the imported collection.
        # The cached id selects only the validated export destination; geometry
        # is collected independently from all visible YAA-named meshes.
        scene_props = getattr(context.scene, "xiv_ie_instant_edit_props", None)
        context_id = getattr(scene_props, "context_id", "") if scene_props else ""
        if not context_id:
            raise ContextValidationError("no active XIV Instant Edit context")
    else:
        context_id = next(iter(ids))

    return validate_context(context_id, context.scene)


def metadata_for_export(obj) -> tuple[int, int, int]:
    """Return YAA-compatible name-based ordering within an XIV Instant Edit context."""
    context_id = context_id_for_object(obj)
    if not context_id:
        raise ContextValidationError(f"{obj.name}: object is outside an XIV Instant Edit context")
    return mesh_ids_from_name(obj)
