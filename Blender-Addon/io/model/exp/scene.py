# Modified for XIV Instant Edit, 2026.
import re

from bpy.types           import Object
from collections         import defaultdict

from .validators         import remove_loose_verts, split_seams
from ..com.exceptions    import XIVMeshIDError
from ....xivpy.model     import XIV_ATTR
from ....mesh.transforms import apply_transforms
from ....instant_edit.context import mesh_ids_from_name


def get_attributes(obj: Object) -> list[str]:
    attributes: list[str] = []
    for attr in obj.keys():
        attr: str
        if attr.startswith(XIV_ATTR) and obj[attr]:
            attributes.append(attr.strip())

    return attributes

def get_mesh_ids(obj: Object) -> tuple[int, int]:
    try:
        name_parts = obj.name.strip().split(" ")
        if re.search(r"^\d+\.\d+\s", obj.name):
            group = int(name_parts[0].split(".")[0])
            part  = int(name_parts[0].split(".")[1])
        else:
            group = int(name_parts[-1].split(".")[0])
            part  = int(name_parts[-1].split(".")[1])
    except:
        raise XIVMeshIDError(f"{obj.name}: Couldn't parse Mesh IDs.")
    
    return group, part

def prepare_submeshes(export_obj: list[Object], model_attributes: list[str], lod_level: int) -> list[list[Object]]:
    mesh_dict: dict[int, dict[int, Object]] = defaultdict(dict)
    for obj in export_obj:
        if len(obj.data.vertices) == 0:
            continue
        group, part, object_lod = mesh_ids_from_name(obj)
        if object_lod != lod_level:
            continue
        if part in mesh_dict[group]:
            raise XIVMeshIDError(f'{obj.name}: Submesh already exists as "{mesh_dict[group][part].name}".')
        
        for attr in get_attributes(obj):
            if attr in model_attributes:
                continue
            model_attributes.append(attr)

        apply_transforms(obj)
        remove_loose_verts(obj)
        # Shape-key export follows Yet Another Addon's vertex-domain contract:
        # split UV seams and sharp edges before reading shape-key coordinates.
        # The stream accessor remains corner-aware for meshes passed directly
        # to it, but the normal model-export path must use this canonicalized
        # topology so shape indices and shape vertices stay aligned.
        split_seams(obj)
        mesh_dict[group][part] = obj

    mesh_indices = sorted(mesh_dict.keys())
    final_sort: list[list[Object]] = []
    for mesh_idx in mesh_indices:
        sorted_submeshes = [obj for submesh_idx, obj in sorted(mesh_dict[mesh_idx].items(), key=lambda x: x[0])]
        final_sort.append(sorted_submeshes)
    
    return final_sort
