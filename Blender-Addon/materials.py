"""FFXIV material-path helpers shared by the panel and export operators."""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass
import re

import bpy
import numpy as np

from .io.model.com.space import lin_to_srgb
from .io.model.exp.validators import clean_material_path
from .instant_edit.context import context_id_for_object, mesh_ids_from_name
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

_MESH_ID_PREFIX = re.compile(r"^(?P<group>\d+)\.(?P<part>\d+)(?:\s+|$)(?P<label>.*)$")
_MESH_ID_SUFFIX = re.compile(r"^(?P<label>.*?)(?:\s+|^)(?P<group>\d+)\.(?P<part>\d+)$")
_LOD_SUFFIX = re.compile(r"\s+LOD(?P<lod>\d+)$", re.IGNORECASE)
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


def mesh_display_name(obj) -> str:
    """Return the editable human label while hiding the exporter mesh ID."""
    name = str(obj.name).strip()
    match = _MESH_ID_PREFIX.match(name)
    if match:
        label = match.group("label").strip()
    else:
        match = _MESH_ID_SUFFIX.match(name)
        label = match.group("label").strip() if match else name
    label = _LOD_SUFFIX.sub("", label).strip()
    return label or "Unnamed Part"


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
) -> str:
    attribute = normalize_mesh_attribute(value) if enabled else str(value or "").strip()
    if not attribute.startswith(XIV_ATTR):
        raise ValueError("This is not an XIV mesh attribute.")
    for obj in mesh_part_objects(objects, mesh_index, part_index):
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


def set_mesh_part_tags(objects, mesh_index: int, part_index: int, value: str) -> str:
    tags = normalize_mesh_tags(value)
    for obj in mesh_part_objects(objects, mesh_index, part_index):
        if tags:
            obj["instant_edit_tags"] = tags
        elif "instant_edit_tags" in obj:
            del obj["instant_edit_tags"]
    return tags


def _rename_mesh_object(obj, mesh_index: int, part_index: int, label: str, lod: int | None) -> None:
    lod_suffix = f" LOD{lod}" if lod else ""
    obj.name = f"{mesh_index}.{part_index} {label}{lod_suffix}"


def rename_mesh_part(objects, mesh_index: int, part_index: int, value: str) -> str:
    """Rename one part across every visible LOD without changing its export ID."""
    label = " ".join(str(value or "").strip().split())
    if not label:
        raise ValueError("Part name cannot be empty")
    targets = mesh_part_objects(objects, mesh_index, part_index)
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
    if not targets:
        return 0
    for obj, _group, _part, _lod, _label in targets:
        obj.name = f"__xiv_ie_move_{obj.as_pointer()}"
    changed = 0
    for obj, group, part, lod, label in targets:
        if swap_group:
            new_group = second[0] if group == first[0] else first[0]
            new_part = part
        else:
            new_group = group
            new_part = second[1] if part == first[1] else first[1]
        _rename_mesh_object(obj, new_group, new_part, label, lod)
        changed += 1
    return changed


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


def material_paths(objects) -> list[str]:
    """Return the distinct export material paths represented by a mesh group."""
    paths = set()
    for obj in objects:
        value = _property(obj, "xiv_material", "")
        if isinstance(value, str) and value.strip():
            paths.add(value.strip())
            continue
        for slot in obj.material_slots:
            material = slot.material
            if material is not None and material.name.lower().endswith(".mtrl"):
                paths.add(material.name)
                break
    return sorted(paths)


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
