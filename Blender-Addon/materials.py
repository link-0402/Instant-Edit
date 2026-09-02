"""FFXIV material-path helpers shared by the panel and export operators."""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass
import re

import bpy
import numpy as np

from .io.model.com.space import lin_to_srgb
from .io.model.exp.validators import clean_material_path, USHORT_LIMIT
from .instant_edit.context import context_id_for_object, mesh_ids_from_name, mesh_name_info
from .mesh.objects import visible_meshobj
from .xivpy.model import XIV_ATTR


MATERIAL_PRESETS = {
    "gen2/vanilla": "/mt_c0101b0001_a.mtrl",
    "gen3/tbse": "/mt_c0101b0001_b.mtrl",
    "bibo": "/mt_c0101b0001_bibo.mtrl",
    "bibopube": "/mt_c0201b0001_bibopube.mtrl",
    "betterpube": "/mt_c0201b0001_betterpube.mtrl",
    "yet another piercing": "/mt_c0201b0001_piercings.mtrl",
    "yet another fingernail": "/mt_c0201b0001_yafinger.mtrl",
    "yet another toenail": "/mt_c0201b0001_yatoe.mtrl",
}

_CUSTOM_ATTRIBUTE = re.compile(r"^atr_[a-z0-9_]+$")

ATTRIBUTE_NAMES = {
    "nek": "Neck",
    "ude": "Elbow",
    "hij": "Wrist",
    "arm": "Hand",
    "kod": "Waist",
    "hiz": "Knee",
    "sne": "Shin",
    "leg": "Boot",
    "lpd": "Knee Pad",
}

ATTRIBUTE_VARIANTS = {
    "mv": "Head",
    "tv": "Body",
    "gv": "Glove",
    "dv": "Leg",
    "sv": "Shoe",
    "ev": "Earring",
    "nv": "Necklace",
    "wv": "Bracelet",
    "rv": "Ring",
    "fv": "Face",
    "hv": "Hair",
}


@dataclass(frozen=True)
class MaterialGroup:
    mesh_index: int
    objects: tuple

    @property
    def parts(self) -> tuple[int, ...]:
        return tuple(sorted({mesh_ids_from_name(obj)[1] for obj in self.objects}))

    @property
    def part_instances(self) -> tuple["MeshPartInstance", ...]:
        return mesh_part_instances(self.objects, self.mesh_index)


@dataclass(frozen=True)
class MeshPartInstance:
    """One visible part, kept separate from other objects with the same IDs."""

    mesh_index: int
    part_index: int
    instance_key: str
    objects: tuple


def mesh_part_objects(objects, mesh_index: int, part_index: int) -> tuple:
    """Return all visible objects representing one part, including its LODs."""
    result = []
    for obj in objects:
        try:
            group, part, _lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if group == mesh_index and part == part_index:
            result.append(obj)
    return tuple(sorted(result, key=lambda obj: obj.name))


def mesh_part_instance_key(obj) -> str:
    """Return the stable identity used to separate duplicate visible parts.

    Numeric mesh IDs are export coordinates, not object identity.  Imported
    objects carry a per-import key; older context imports fall back to their
    context and display label so their LODs remain together.  Untagged scene
    objects use their collection and label as the least-invasive fallback.
    """
    imported = obj.get("instant_edit_import_instance_id", "")
    if isinstance(imported, str) and imported:
        return f"import:{imported}"

    try:
        context_id = context_id_for_object(obj)
    except Exception:
        context_id = ""
    try:
        label = " ".join(mesh_name_info(obj).label.split()).casefold()
    except Exception:
        label = str(getattr(obj, "name", "")).strip().casefold()

    if context_id:
        return f"context:{context_id}|label:{label}"

    collection_ids = sorted(
        str(collection.as_pointer())
        for collection in getattr(obj, "users_collection", ())
    )
    return f"collection:{','.join(collection_ids)}|label:{label}"


def mesh_part_instances(
    objects,
    mesh_index: int,
    part_index: int | None = None,
) -> tuple[MeshPartInstance, ...]:
    """Group visible objects into independently movable part instances."""
    grouped = defaultdict(list)
    for obj in objects:
        if getattr(obj, "type", None) != "MESH":
            continue
        try:
            group, part, _lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if group != mesh_index or (part_index is not None and part != part_index):
            continue
        grouped[(part, mesh_part_instance_key(obj))].append(obj)

    instances = []
    for (part, instance_key), part_objects in grouped.items():
        instances.append(
            MeshPartInstance(
                mesh_index,
                part,
                instance_key,
                tuple(sorted(part_objects, key=lambda obj: obj.name.casefold())),
            )
        )
    return tuple(
        sorted(
            instances,
            key=lambda item: (
                item.part_index,
                item.objects[0].name.casefold() if item.objects else "",
                item.instance_key,
            ),
        )
    )


def mesh_part_instance_objects(
    objects,
    mesh_index: int,
    part_index: int,
    instance_key: str | None = None,
) -> tuple:
    """Return one duplicate-safe part instance, including all of its LODs."""
    if instance_key is None:
        return mesh_part_objects(objects, mesh_index, part_index)
    for instance in mesh_part_instances(objects, mesh_index, part_index):
        if instance.instance_key == instance_key:
            return instance.objects
    return ()


def mesh_display_name(obj) -> str:
    """Return the editable human label while hiding the exporter mesh ID."""
    try:
        label = mesh_name_info(obj).label
    except Exception:
        label = str(obj.name).strip()
    return label or "Unnamed Part"


def _front_mesh_name(info) -> str:
    label = " ".join(info.label.split())
    identifier = f"{info.mesh_group}.{info.mesh_part}"
    lod_suffix = f" LOD{info.lod}" if info.lod else ""
    return f"{identifier}{f' {label}' if label else ''}{lod_suffix}"


def convert_suffix_mesh_names(objects) -> int:
    """Move suffix-form mesh IDs to the front without allowing name collisions."""
    candidates = []
    for obj in objects:
        if obj.type != "MESH":
            continue
        try:
            info = mesh_name_info(obj)
        except Exception:
            continue
        if info.prefix:
            continue
        candidates.append((obj, _front_mesh_name(info)))

    if not candidates:
        return 0

    candidate_ids = {obj.as_pointer() for obj, _target in candidates}
    existing_names = {
        obj.name
        for obj in objects
        if obj.as_pointer() not in candidate_ids
    }
    targets = defaultdict(list)
    for obj, target in candidates:
        targets[target].append(obj.name)
    conflicts = {
        target: names
        for target, names in targets.items()
        if target in existing_names or len(names) > 1
    }
    if conflicts:
        details = "; ".join(
            f"{target} ({', '.join(names)})"
            for target, names in sorted(conflicts.items())
        )
        raise ValueError(f"Mesh ID conversion would create name collisions: {details}")

    all_names = {obj.name for obj in objects}
    temporary_names = []
    for index, (obj, _target) in enumerate(candidates):
        temporary = f"__xiv_ie_mesh_name_convert_{obj.as_pointer()}_{index}"
        while temporary in all_names or temporary in temporary_names:
            temporary += "_"
        temporary_names.append(temporary)

    originals = [(obj, obj.name) for obj, _target in candidates]
    try:
        for (obj, _target), temporary in zip(candidates, temporary_names):
            obj.name = temporary
        for (obj, target), _temporary in zip(candidates, temporary_names):
            obj.name = target
    except Exception:
        for (obj, _old_name), temporary in zip(originals, temporary_names):
            obj.name = temporary
        for obj, old_name in originals:
            obj.name = old_name
        raise
    return len(candidates)


def mesh_part_tags(objects) -> str:
    """Return the common tag text for a part, or a useful mixed-value marker."""
    values = {
        str(_property(obj, "tags", "")).strip()
        for obj in objects
        if str(_property(obj, "tags", "")).strip()
    }
    if not values:
        return ""
    if len(values) == 1:
        return next(iter(values))
    return "<multiple>"


def mesh_part_attributes(objects) -> tuple[str, ...]:
    """Return the enabled XIV attributes represented by a mesh part."""
    attributes = {
        key
        for obj in objects
        for key, value in obj.items()
        if key.startswith(XIV_ATTR) and value
    }
    return tuple(sorted(attributes))


def attribute_display_name(attribute: str) -> str:
    """Turn common XIV attribute keys into Mesh Studio's compact labels."""
    if attribute.startswith("atr_"):
        parts = attribute.split("_")
        attribute_id = parts[1] if len(parts) > 1 else ""
        if attribute_id in ATTRIBUTE_VARIANTS and len(parts) > 2:
            return f"{ATTRIBUTE_VARIANTS[attribute_id]} {parts[2].upper()}"
        return ATTRIBUTE_NAMES.get(attribute_id, attribute)
    if attribute.startswith("heels_offset="):
        return f"Heels: {attribute.split('=', 1)[1]}"
    if attribute.startswith("skin_suffix="):
        return f"Skin: {attribute.split('=', 1)[1]}"
    return attribute


def normalize_mesh_attribute(value: str) -> str:
    attribute = str(value or "").strip().lower().replace(" ", "_")
    if attribute and not attribute.startswith("atr_"):
        attribute = f"atr_{attribute}"
    if not _CUSTOM_ATTRIBUTE.fullmatch(attribute):
        raise ValueError("Custom attributes must use letters, numbers, or underscores after atr_.")
    return attribute


def set_mesh_part_attribute(
    objects,
    mesh_index: int,
    part_index: int,
    value: str,
    enabled: bool,
    instance_key: str | None = None,
) -> str:
    attribute = normalize_mesh_attribute(value) if enabled else str(value or "").strip()
    if not attribute.startswith(XIV_ATTR):
        raise ValueError("This is not an XIV mesh attribute.")
    for obj in mesh_part_instance_objects(objects, mesh_index, part_index, instance_key):
        if enabled:
            obj[attribute] = True
        elif attribute in obj:
            del obj[attribute]
    return attribute


def flow_data_count(objects) -> int:
    return sum("xiv_flow" in obj.data.color_attributes for obj in objects)


def mesh_flow_enabled(objects) -> bool:
    objects = tuple(objects)
    return bool(objects and _property(objects[0], "xiv_flow", False))


def set_mesh_flow_enabled(objects, enabled: bool) -> None:
    for obj in objects:
        obj["xiv_flow"] = bool(enabled)


def ensure_flow_data(objects) -> int:
    """Create the neutral XIV flow colour channel used by the MDL exporter."""
    updated = 0
    for obj in objects:
        if "xiv_flow" in obj.data.color_attributes:
            continue

        source = obj.data.color_attributes.get("vc2")
        if source is not None:
            if source.data_type == "BYTE_COLOR":
                rgba = np.ones(len(source.data) * 4, dtype=np.float32)
                source.data.foreach_get("color", rgba)
                domain = source.domain
                obj.data.color_attributes.remove(source)
                layer = obj.data.color_attributes.new(
                    "xiv_flow",
                    domain=domain,
                    type="FLOAT_COLOR",
                )
                layer.data.foreach_set("color", lin_to_srgb(rgba))
            else:
                source.name = "xiv_flow"
        else:
            count = len(obj.data.loops)
            rgba = np.tile(np.array((0.5, 0.5, 1.0, 1.0), dtype=np.float32), count)
            layer = obj.data.color_attributes.new(
                "xiv_flow",
                domain="CORNER",
                type="FLOAT_COLOR",
            )
            layer.data.foreach_set("color", rgba)
        updated += 1
    return updated


def normalize_mesh_tags(value: str) -> str:
    """Normalize comma-separated tags while preserving their entered order."""
    tags = []
    seen = set()
    for tag in str(value or "").split(","):
        tag = " ".join(tag.strip().split())
        if tag and tag.casefold() not in seen:
            tags.append(tag)
            seen.add(tag.casefold())
    return ", ".join(tags)


def set_mesh_part_tags(
    objects,
    mesh_index: int,
    part_index: int,
    value: str,
    instance_key: str | None = None,
) -> str:
    tags = normalize_mesh_tags(value)
    for obj in mesh_part_instance_objects(objects, mesh_index, part_index, instance_key):
        if tags:
            obj["instant_edit_tags"] = tags
        elif "instant_edit_tags" in obj:
            del obj["instant_edit_tags"]
    return tags


def _rename_mesh_object(obj, mesh_index: int, part_index: int, label: str, lod: int | None) -> None:
    obj.name = _mesh_object_name(mesh_index, part_index, label, lod)


def _mesh_object_name(mesh_index: int, part_index: int, label: str, lod: int | None) -> str:
    lod_suffix = f" LOD{lod}" if lod else ""
    return f"{mesh_index}.{part_index} {label}{lod_suffix}"


def _rename_mesh_targets(targets) -> int:
    """Apply mesh-ID renames without allowing Blender to suffix collisions."""
    targets = tuple(targets)
    if not targets:
        return 0

    desired = [
        (obj, _mesh_object_name(group, part, label, lod))
        for obj, group, part, lod, label in targets
    ]
    desired_names = [name for _obj, name in desired]
    if len(set(desired_names)) != len(desired_names):
        raise ValueError("Mesh movement would create duplicate object names.")

    target_ids = {obj.as_pointer() for obj, _name in desired}
    existing_names = {
        obj.name for obj in bpy.data.objects if obj.as_pointer() not in target_ids
    }
    conflicts = sorted(set(desired_names) & existing_names)
    if conflicts:
        raise ValueError(
            "Mesh movement would collide with existing objects: " + ", ".join(conflicts)
        )

    originals = [(obj, obj.name) for obj, _name in desired]
    all_names = {obj.name for obj in bpy.data.objects}
    temporary_names = []
    for index, (obj, _name) in enumerate(desired):
        temporary = f"__xiv_ie_move_{obj.as_pointer()}_{index}"
        while temporary in all_names or temporary in temporary_names:
            temporary += "_"
        temporary_names.append(temporary)

    try:
        for (obj, _name), temporary in zip(desired, temporary_names):
            obj.name = temporary
        for obj, name in desired:
            obj.name = name
    except Exception:
        for (obj, _old_name), temporary in zip(originals, temporary_names):
            obj.name = temporary
        for obj, old_name in originals:
            obj.name = old_name
        raise
    return len(desired)


def rename_mesh_part(
    objects,
    mesh_index: int,
    part_index: int,
    value: str,
    instance_key: str | None = None,
) -> str:
    """Rename one part across every visible LOD without changing its export ID."""
    label = " ".join(str(value or "").strip().split())
    if not label:
        raise ValueError("Part name cannot be empty")
    targets = mesh_part_instance_objects(objects, mesh_index, part_index, instance_key)
    if not targets:
        raise ValueError(f"Mesh part {mesh_index}.{part_index} is no longer visible")
    lods = []
    for obj in targets:
        _group, _part, lod = mesh_ids_from_name(obj)
        lods.append((obj, lod))
    for obj, _lod in lods:
        obj.name = f"__xiv_ie_rename_{obj.as_pointer()}"
    for obj, lod in lods:
        _rename_mesh_object(obj, mesh_index, part_index, label, lod)
    return label


def _swap_mesh_ids(objects, first: tuple[int, int], second: tuple[int, int], swap_group: bool) -> int:
    targets = []
    for obj in objects:
        try:
            group, part, lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if (group in {first[0], second[0]} if swap_group else (group, part) in {first, second}):
            targets.append((obj, group, part, lod, mesh_display_name(obj)))
    renames = []
    for obj, group, part, lod, label in targets:
        if swap_group:
            new_group = second[0] if group == first[0] else first[0]
            new_part = part
        else:
            new_group = group
            new_part = second[1] if part == first[1] else first[1]
        renames.append((obj, new_group, new_part, lod, label))
    return _rename_mesh_targets(renames)


def swap_mesh_groups(objects, first_group: int, second_group: int) -> int:
    return _swap_mesh_ids(
        objects,
        (first_group, 0),
        (second_group, 0),
        swap_group=True,
    )


def swap_mesh_parts(objects, mesh_index: int, first_part: int, second_part: int) -> int:
    return _swap_mesh_ids(
        objects,
        (mesh_index, first_part),
        (mesh_index, second_part),
        swap_group=False,
    )


def swap_mesh_part_instances(
    objects,
    mesh_index: int,
    first_part: int,
    first_instance_key: str,
    second_part: int,
    second_instance_key: str,
) -> int:
    """Swap IDs for two selected part instances without touching duplicates."""
    first_objects = mesh_part_instance_objects(
        objects, mesh_index, first_part, first_instance_key
    )
    second_objects = mesh_part_instance_objects(
        objects, mesh_index, second_part, second_instance_key
    )
    if not first_objects or not second_objects:
        return 0

    renames = []
    for obj in first_objects:
        _group, _part, lod = mesh_ids_from_name(obj)
        renames.append((obj, mesh_index, second_part, lod, mesh_display_name(obj)))
    for obj in second_objects:
        _group, _part, lod = mesh_ids_from_name(obj)
        renames.append((obj, mesh_index, first_part, lod, mesh_display_name(obj)))
    return _rename_mesh_targets(renames)


def move_mesh_part_to_group(
    objects,
    source_group: int,
    source_part: int,
    target_group: int,
    instance_key: str | None = None,
) -> int:
    """Move one complete part, including every LOD, to another group."""
    source_objects = mesh_part_instance_objects(
        objects, source_group, source_part, instance_key
    )
    if not source_objects:
        raise ValueError(f"Mesh part {source_group}.{source_part} is no longer visible")

    used_parts = set()
    for obj in objects:
        try:
            group, part, _lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if group == target_group:
            used_parts.add(part)
    target_part = next(index for index in range(len(used_parts) + 1) if index not in used_parts)

    renames = []
    for obj in source_objects:
        _group, _part, lod = mesh_ids_from_name(obj)
        renames.append((obj, target_group, target_part, lod, mesh_display_name(obj)))
    _rename_mesh_targets(renames)
    return target_part


def _property(obj, name: str, default=None):
    if name in obj:
        return obj[name]
    return obj.get(f"instant_edit_{name}", default)


def _mesh_index(obj) -> int:
    return mesh_ids_from_name(obj)[0]


def group_mesh_objects(objects) -> list[MaterialGroup]:
    """Group mesh objects by XIV Instant Edit context and FFXIV mesh index."""
    grouped = defaultdict(list)
    for obj in objects:
        if obj.type != "MESH" or len(obj.data.vertices) == 0:
            continue
        try:
            mesh_index = _mesh_index(obj)
        except Exception:
            continue
        grouped[mesh_index].append(obj)

    return [
        MaterialGroup(mesh_index, tuple(sorted(group, key=lambda obj: obj.name)))
        for mesh_index, group in sorted(grouped.items(), key=lambda item: item[0])
    ]


def visible_material_groups() -> list[MaterialGroup]:
    return group_mesh_objects(visible_meshobj())


def material_group_slots(
    groups: list[MaterialGroup],
    maximum_group: int | None = None,
) -> list[MaterialGroup]:
    """Return every numeric group slot through the current trailing destination."""
    if not groups:
        return []
    occupied = {group.mesh_index: group for group in groups}
    highest_slot = max(occupied) + 1
    if maximum_group is not None:
        highest_slot = max(max(occupied), min(highest_slot, maximum_group))
    return [
        occupied.get(index, MaterialGroup(index, ()))
        for index in range(highest_slot + 1)
    ]


def visible_material_group_slots(maximum_group: int | None = None) -> list[MaterialGroup]:
    return material_group_slots(visible_material_groups(), maximum_group)


def material_paths(objects) -> list[str]:
    """Return the distinct export material paths represented by a mesh group."""
    paths = {}
    for obj in objects:
        path = export_material_path(obj)
        if path:
            paths.setdefault(_material_collapse_key(path), path)
    return sorted(paths.values())


def export_material_path(obj) -> str:
    """Return the normalized path the MDL exporter will read from one object."""
    value = _property(obj, "xiv_material", "")
    if not isinstance(value, str) or not value.strip():
        slots = getattr(obj, "material_slots", ())
        if not slots or slots[0].material is None:
            return ""
        value = slots[0].material.name
    try:
        return clean_material_path(value.strip())
    except (AttributeError, TypeError, ValueError):
        return ""


def _mesh_part_material_path(objects) -> str:
    """Return one material path when every object in a part agrees on it."""
    paths = {}
    for obj in objects:
        path = export_material_path(obj)
        key = _material_collapse_key(path) if path else None
        paths.setdefault(key, path)
    if len(paths) != 1 or None in paths:
        return ""
    return next(iter(paths.values()))


def _material_collapse_key(material: str) -> tuple[str, str]:
    """Return the material identity used when collapsing mesh groups.

    FFXIV's Bibo body material is intentionally shared by several model
    prefixes, so its full path is the one supported exception to exact-path
    matching.  Other materials retain their complete normalized path as the
    identity used for collapsing.
    """
    filename = material.rsplit("/", 1)[-1].casefold()
    if filename.endswith("_bibo.mtrl"):
        return ("bibo", "_bibo.mtrl")
    return ("path", material)


def _collapsible_mesh_parts(objects) -> list[tuple[int, int, str, tuple, str]]:
    """Return material-consistent visible part instances with their material."""
    candidates = []
    for group in group_mesh_objects(objects):
        for instance in mesh_part_instances(group.objects, group.mesh_index):
            material = _mesh_part_material_path(instance.objects)
            if material:
                candidates.append(
                    (
                        group.mesh_index,
                        instance.part_index,
                        instance.instance_key,
                        instance.objects,
                        material,
                    )
                )
    return candidates


def _canonical_material_groups(candidates) -> dict[tuple[str, str], int]:
    canonical = {}
    for group, _part, _instance_key, _objects, material in candidates:
        key = _material_collapse_key(material)
        previous = canonical.get(key)
        if previous is None or group < previous:
            canonical[key] = group
    return canonical


def _occupied_mesh_parts(objects) -> defaultdict[int, set[int]]:
    occupied = defaultdict(set)
    for obj in objects:
        try:
            group, part, _lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if group >= 0 and part >= 0:
            occupied[group].add(part)
    return occupied


def _export_vertex_upper_bound(obj, depsgraph=None) -> int:
    """Return a safe upper bound for the vertices emitted by one submesh."""
    try:
        evaluated = obj.evaluated_get(depsgraph) if depsgraph is not None else obj
        mesh = evaluated.data
        # The corner-aware exporter emits at most one vertex per evaluated loop.
        # Using loops remains safe when UVs, normals, colours, or flow data split
        # a Blender vertex into several MDL vertices.
        return len(mesh.loops)
    except (AttributeError, ReferenceError, RuntimeError):
        return USHORT_LIMIT + 1


def _mesh_lod_vertex_budgets(objects, depsgraph=None) -> defaultdict[tuple[int, int], int]:
    budgets = defaultdict(int)
    for obj in objects:
        try:
            group, _part, lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        budgets[(group, lod)] += _export_vertex_upper_bound(obj, depsgraph)
    return budgets


def _material_collapse_plan(
    candidates,
    target_groups: dict[tuple[str, str], int],
    occupied_objects,
    *,
    only_move_down: bool,
) -> tuple[list[tuple], int]:
    """Build one atomic rename plan for material-based part moves."""
    occupied = _occupied_mesh_parts(occupied_objects)
    try:
        depsgraph = bpy.context.evaluated_depsgraph_get()
    except (AttributeError, RuntimeError):
        depsgraph = None
    vertex_budgets = _mesh_lod_vertex_budgets(occupied_objects, depsgraph)
    moves = []
    ordered = sorted(
        candidates,
        key=lambda item: (
            target_groups.get(_material_collapse_key(item[4]), item[0]),
            item[0],
            item[1],
            item[2],
        ),
    )
    for source_group, source_part, instance_key, objects, material in ordered:
        target_group = target_groups.get(_material_collapse_key(material))
        if target_group is None or source_group == target_group:
            continue
        if only_move_down and source_group < target_group:
            continue

        moving_vertices = defaultdict(int)
        for obj in objects:
            try:
                _group, _part, lod = mesh_ids_from_name(obj)
            except Exception:
                continue
            moving_vertices[lod] += _export_vertex_upper_bound(obj, depsgraph)
        if any(
            vertex_budgets[(target_group, lod)] + count > USHORT_LIMIT
            for lod, count in moving_vertices.items()
        ):
            continue

        target_part = max(occupied[target_group], default=-1) + 1
        occupied[target_group].add(target_part)
        for lod, count in moving_vertices.items():
            source_key = (source_group, lod)
            target_key = (target_group, lod)
            vertex_budgets[source_key] = max(0, vertex_budgets[source_key] - count)
            vertex_budgets[target_key] += count
        moves.append((source_group, source_part, instance_key, objects, target_group, target_part))

    renames = []
    for _source_group, _source_part, _instance_key, objects, target_group, target_part in moves:
        for obj in objects:
            _group, _part, lod = mesh_ids_from_name(obj)
            renames.append(
                (
                    obj,
                    target_group,
                    target_part,
                    lod,
                    mesh_display_name(obj),
                )
            )
    return renames, len(moves)


def auto_collapse_materials(objects) -> int:
    """Move visible matching-material parts into their lowest mesh group."""
    objects = tuple(objects)
    candidates = _collapsible_mesh_parts(objects)
    target_groups = _canonical_material_groups(candidates)
    renames, moved = _material_collapse_plan(
        candidates,
        target_groups,
        objects,
        only_move_down=True,
    )
    _rename_mesh_targets(renames)
    return moved


def collapse_imported_materials(imported_objects, existing_objects) -> int:
    """Move imported matching-material parts into pre-existing visible groups."""
    imported_objects = tuple(imported_objects)
    existing_objects = tuple(existing_objects)
    imported_candidates = _collapsible_mesh_parts(imported_objects)
    existing_candidates = _collapsible_mesh_parts(existing_objects)
    target_groups = _canonical_material_groups(existing_candidates)
    renames, moved = _material_collapse_plan(
        imported_candidates,
        target_groups,
        existing_objects + imported_objects,
        only_move_down=False,
    )
    _rename_mesh_targets(renames)
    return moved


def material_mismatch_parts(objects) -> set[int]:
    """Identify part rows that diverge from the group's authoritative material.

    The exporter builds each LOD mesh from its lowest-numbered part. The lowest
    available LOD/part is therefore the group-wide reference; a part is warned
    when any of its LOD objects is missing that material or exports another one.
    """
    ordered = sorted(
        objects,
        key=lambda obj: (
            mesh_ids_from_name(obj)[2],
            mesh_ids_from_name(obj)[1],
            obj.name.casefold(),
        ),
    )
    if not ordered:
        return set()
    authoritative = export_material_path(ordered[0])
    authoritative_key = _material_collapse_key(authoritative) if authoritative else None
    mismatches = set()
    for obj in ordered:
        _group, part, _lod = mesh_ids_from_name(obj)
        path = export_material_path(obj)
        if not authoritative_key or not path or _material_collapse_key(path) != authoritative_key:
            mismatches.add(part)
    return mismatches


def normalize_material_path(value: str) -> str:
    value = value.strip()
    if not value:
        raise ValueError("Material path cannot be empty")
    return clean_material_path(MATERIAL_PRESETS.get(value.lower(), value))


def find_material_group(context, mesh_index: int) -> MaterialGroup | None:
    return next(
        (group for group in visible_material_groups() if group.mesh_index == mesh_index),
        None,
    )


def assign_material_path(objects, value: str) -> str:
    """Assign one normalized FFXIV material path to every submesh in a group."""
    path = normalize_material_path(value)
    for obj in objects:
        obj["xiv_material"] = path
        if "instant_edit_xiv_material" in obj or context_id_for_object(obj):
            obj["instant_edit_xiv_material"] = path
    return path


def material_suggestions() -> list[str]:
    detected = []
    for group in visible_material_groups():
        detected.extend(material_paths(group.objects))
    presets = [
        "Bibo", "Gen2/Vanilla", "Gen3/TBSE", "Bibopube", "Betterpube",
        "Yet Another Piercing", "Yet Another Toenail", "Yet Another Fingernail",
    ]
    return list(dict.fromkeys(sorted(set(detected)) + presets))
