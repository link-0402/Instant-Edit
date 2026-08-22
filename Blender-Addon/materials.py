"""FFXIV material-path helpers shared by the panel and export operators."""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass

import bpy

from .io.model.exp.validators import clean_material_path
from .instant_edit.context import context_id_for_object, mesh_ids_from_name
from .mesh.objects import visible_meshobj


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


@dataclass(frozen=True)
class MaterialGroup:
    mesh_index: int
    objects: tuple

    @property
    def parts(self) -> tuple[int, ...]:
        return tuple(sorted(mesh_ids_from_name(obj)[1] for obj in self.objects))


def _property(obj, name: str, default=None):
    if name in obj:
        return obj[name]
    return obj.get(f"instant_edit_{name}", default)


def _mesh_index(obj) -> int:
    return mesh_ids_from_name(obj)[0]


def group_mesh_objects(objects) -> list[MaterialGroup]:
    """Group mesh objects by Instant Edit context and FFXIV mesh index."""
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
