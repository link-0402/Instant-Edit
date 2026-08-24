import re

import numpy as np

from numpy           import single, byte, ubyte
from bpy.types       import Object
from numpy.typing    import NDArray

from ..com.space     import blend_to_xiv_space, world_to_tangent_space
from ..com.helpers   import calc_tangents_with_bitangent, vector_to_bytes, quantise_flow, normalise_vectors
from ..com.exceptions import XIVMeshError

from ....xivpy.model import XIV_COL, XIV_UV


def get_xiv_uv_layers(obj: Object, expected_count: int | None = None) -> list:
    """Return export UV layers in a deterministic channel order."""
    candidates = [
        layer for layer in obj.data.uv_layers
        if layer.name.lower().startswith(XIV_UV)
    ]
    if not candidates:
        raise XIVMeshError(f'{obj.name}: No export UV layer was found.')

    numbered: dict[int, object] = {}
    unnumbered = []
    for layer in candidates:
        match = re.fullmatch(r"uv([0-2])", layer.name.lower())
        if match:
            channel = int(match.group(1))
            if channel in numbered:
                raise XIVMeshError(f'{obj.name}: Duplicate UV channel uv{channel}.')
            numbered[channel] = layer
        else:
            unnumbered.append(layer)

    if numbered:
        if unnumbered:
            names = ", ".join(layer.name for layer in candidates)
            raise XIVMeshError(
                f'{obj.name}: Ambiguous UV layer names ({names}). '
                'Use uv0, uv1, and uv2 exclusively.'
            )
        highest = max(numbered)
        missing = [f"uv{idx}" for idx in range(highest + 1) if idx not in numbered]
        if missing:
            raise XIVMeshError(
                f'{obj.name}: Missing UV channel(s): {", ".join(missing)}.'
            )
        layers = [numbered[idx] for idx in range(highest + 1)]
    else:
        # Legacy names such as UVMap have no channel number; preserve Blender order.
        layers = candidates

    if len(layers) > 3:
        raise XIVMeshError(f'{obj.name}: More than three export UV layers were found.')
    if expected_count is not None and len(layers) > expected_count:
        raise XIVMeshError(
            f'{obj.name}: Found {len(layers)} UV layers, but the vertex declaration '
            f'only has {expected_count} channel(s).'
        )
    return layers


def get_loop_vertex_indices(obj: Object, loop_count: int) -> NDArray:
    indices = np.zeros(loop_count, np.uint32)
    obj.data.loops.foreach_get("vertex_index", indices)
    return indices


def get_space_data(obj: Object, source_vertices: NDArray, source_loops: NDArray, loop_count: int) -> tuple[NDArray, NDArray]:
    vert_count = len(obj.data.vertices)
    pos = np.zeros(vert_count * 3, single)
    obj.data.vertices.foreach_get("co", pos)
    pos = blend_to_xiv_space(pos.reshape(-1, 3))[source_vertices]

    nor = np.zeros(loop_count * 3, single)
    obj.data.loops.foreach_get("normal", nor)
    loop_nor = blend_to_xiv_space(nor.reshape(-1, 3))

    return pos, loop_nor[source_loops]


def get_loop_normals(obj: Object, loop_count: int) -> NDArray:
    nor = np.zeros(loop_count * 3, single)
    obj.data.loops.foreach_get("normal", nor)
    return blend_to_xiv_space(nor.reshape(-1, 3))


def get_shape_co(obj: Object, source_vertices: NDArray) -> dict[str, NDArray]:
    shapes: dict[str, NDArray] = {}
    if obj.data.shape_keys:
        source_count = len(obj.data.vertices)
        for shape_key in obj.data.shape_keys.key_blocks[1:] or []:
            shape_pos = np.zeros(source_count * 3, single)
            shape_key.data.foreach_get("co", shape_pos)
            shape_pos = blend_to_xiv_space(shape_pos.reshape(-1, 3))
            shapes[shape_key.name] = shape_pos[source_vertices]

    return shapes


def get_uvs(obj: Object, loop_count: int, uv_count: int) -> tuple[list, list[NDArray]]:
    layers = get_xiv_uv_layers(obj, uv_count)
    uv_arrays: list[NDArray] = []
    for uv_layer in layers:
        loop_uvs = np.zeros(loop_count * 2, single)
        uv_layer.uv.foreach_get("vector", loop_uvs)
        loop_uvs = loop_uvs.reshape(-1, 2)
        loop_uvs[:, 1] = 1 - loop_uvs[:, 1]
        _validate_corner_array(obj, f'UV layer "{uv_layer.name}"', loop_uvs, loop_count)
        uv_arrays.append(loop_uvs)

    return layers, uv_arrays


def get_col_attributes(obj: Object, loop_vertices: NDArray, loop_count: int, col_count: int) -> list[NDArray]:
    layers = [
        layer for layer in obj.data.color_attributes
        if layer.name.lower().startswith(XIV_COL)
    ][:col_count]

    col_arrays: list[NDArray] = []
    for layer in layers:
        source_count = loop_count if layer.domain == 'CORNER' else len(obj.data.vertices)
        col_arr = np.zeros(source_count * 4, single)
        layer.data.foreach_get("color", col_arr)
        col_arr = col_arr.reshape(-1, 4)

        if layer.domain == 'POINT':
            col_arr = col_arr[loop_vertices]
        elif layer.domain != 'CORNER':
            raise XIVMeshError(
                f'{obj.name}: Colour layer "{layer.name}" uses unsupported domain {layer.domain}.'
            )

        _validate_corner_array(obj, f'colour layer "{layer.name}"', col_arr, loop_count)
        col_arr = col_arr.clip(0.0, 1.0) * 255.0
        col_arrays.append(col_arr.round().astype(byte))

    return col_arrays


def get_bitangents(obj: Object, loop_count: int, uv_layer: str) -> NDArray:
    obj.data.calc_tangents(uvmap=uv_layer)

    loop_bitan = np.zeros(loop_count * 3, single)
    obj.data.loops.foreach_get("bitangent", loop_bitan)
    loop_bitan = blend_to_xiv_space(loop_bitan.reshape(-1, 3))

    loop_bi_sign = np.zeros(loop_count, single)
    obj.data.loops.foreach_get("bitangent_sign", loop_bi_sign)
    bitangents = np.c_[loop_bitan, loop_bi_sign]
    _validate_corner_array(obj, "tangent data", bitangents, loop_count)
    return bitangents


def get_weights(obj: Object, vert_count: int, group_count: int) -> NDArray:
    weight_matrix = np.zeros((vert_count, group_count), dtype=np.float32)
    for vertex_idx, vertex in enumerate(obj.data.vertices):
        for group in vertex.groups:
            weight_matrix[vertex_idx, group.group] = group.weight

    return weight_matrix


def get_flow_colours(obj: Object, loop_vertices: NDArray, loop_count: int) -> NDArray:
    if "xiv_flow" not in obj.data.color_attributes:
        return np.full((loop_count, 2), 0.5, single)

    flow_layer = obj.data.color_attributes["xiv_flow"]
    source_count = loop_count if flow_layer.domain == 'CORNER' else len(obj.data.vertices)
    flow_colour = np.zeros(source_count * 4, single)
    flow_layer.data.foreach_get("color", flow_colour)
    flow_colour = flow_colour.reshape(-1, 4)
    if flow_layer.domain == 'POINT':
        flow_colour = flow_colour[loop_vertices]
    elif flow_layer.domain != 'CORNER':
        raise XIVMeshError(
            f'{obj.name}: Flow layer uses unsupported domain {flow_layer.domain}.'
        )
    _validate_corner_array(obj, "flow data", flow_colour, loop_count)
    return flow_colour[:, :2]


def create_corner_vertex_map(obj: Object, loop_vertices: NDArray, corner_arrays: list[NDArray]) -> tuple[NDArray, NDArray, NDArray]:
    """Expand Blender loop-domain attributes into MDL vertex-domain attributes."""
    loop_count = len(loop_vertices)
    for idx, arr in enumerate(corner_arrays):
        _validate_corner_array(obj, f"corner attribute {idx}", arr, loop_count)

    exported_indices = np.empty(loop_count, dtype=np.uint32)
    source_vertices: list[int] = []
    source_loops: list[int] = []
    vertex_map: dict[tuple, int] = {}

    for loop_idx, source_vertex in enumerate(loop_vertices):
        key = (int(source_vertex),) + tuple(
            np.ascontiguousarray(arr[loop_idx]).tobytes() for arr in corner_arrays
        )
        export_vertex = vertex_map.get(key)
        if export_vertex is None:
            export_vertex = len(source_vertices)
            vertex_map[key] = export_vertex
            source_vertices.append(int(source_vertex))
            source_loops.append(loop_idx)
        exported_indices[loop_idx] = export_vertex

    return (
        exported_indices,
        np.asarray(source_vertices, dtype=np.uint32),
        np.asarray(source_loops, dtype=np.uint32),
    )


def get_flow(flow_colour: NDArray, normals: NDArray, bitangents: NDArray) -> NDArray:
    signs         = bitangents[:, 3]
    bitangent_xyz = bitangents[:, :3]
    tangents      = calc_tangents_with_bitangent(normals, bitangent_xyz, signs)
    world_flow    = _flow_vectors(flow_colour)
    tangent_flow  = world_to_tangent_space(world_flow, tangents, bitangent_xyz, normals)

    return np.c_[vector_to_bytes(tangent_flow), np.full(len(tangent_flow), 255, dtype=ubyte)]


def _validate_corner_array(obj: Object, label: str, array: NDArray, loop_count: int) -> None:
    if len(array) != loop_count:
        raise XIVMeshError(
            f'{obj.name}: {label} has {len(array)} values for {loop_count} mesh corners.'
        )
    if not np.all(np.isfinite(array)):
        raise XIVMeshError(f'{obj.name}: {label} contains non-finite values.')


def _flow_vectors(flow_colour: NDArray) -> NDArray:
    flow_vectors = normalise_vectors((flow_colour * 2) - 1)
    quantised    = quantise_flow(flow_vectors)
    return np.c_[quantised, np.zeros(len(flow_vectors))]
