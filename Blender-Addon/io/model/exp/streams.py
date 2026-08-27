# Modified for XIV Instant Edit, 2026.
import numpy as np

from bpy.types       import Object
from numpy.typing    import NDArray
 
from .accessors      import *
from ..com.schema    import get_array_type
from ..com.helpers   import vector_to_bytes, byte_sign
from ....xivpy.model import VertexDeclaration, VertexUsage, Mesh as XIVMesh


def apply_mesh_options(streams: dict[int, NDArray], mesh_options: dict[str, bool]) -> None:
    tex    = streams[1]
    fields = tex.dtype.names

    # The first two UV channels are packed together in "uv0".
    if "uv0" in fields and tex["uv0"].shape[1] >= 4:
        if mesh_options.get("copy_uv1_to_uv2", False):
            tex["uv0"][:, 2:4] = tex["uv0"][:, 0:2]
        if mesh_options.get("clear_uv2", False):
            tex["uv0"][:, 2:4] = 0.0

    if "colour0" in fields:
        if mesh_options.get("clear_vertex_color1", False):
            tex["colour0"][:] = 255
        elif mesh_options.get("clear_vertex_alpha1", False):
            tex["colour0"][:, 3] = 255

    if "colour1" in fields and mesh_options.get("clear_vertex_color2", False):
        tex["colour1"][:, :3] = 0
        tex["colour1"][:, 3]  = 255

    if "flow" in fields and mesh_options.get("clear_flow_data", False):
        tex["flow"][:, :2] = 0
        tex["flow"][:, 2:] = 255

def get_submesh_streams(obj: Object, vert_decl: VertexDeclaration, mesh_flow: bool, mesh_options: dict[str, bool]=None) -> tuple[NDArray, dict[int, NDArray], dict[str, NDArray], NDArray]:
        loop_count = len(obj.data.loops)
        uv_count   = vert_decl.usage_count(VertexUsage.UV)
        col_count  = vert_decl.usage_count(VertexUsage.COLOUR)

        loop_vertices = get_loop_vertex_indices(obj, loop_count)
        loop_normals  = get_loop_normals(obj, loop_count)
        uv_layers, loop_uvs = get_uvs(obj, loop_count, uv_count)
        loop_colours   = get_col_attributes(obj, loop_vertices, loop_count, col_count)
        loop_bitangents = get_bitangents(obj, loop_count, uv_layers[0].name)
        loop_flow = get_flow_colours(obj, loop_vertices, loop_count) if mesh_flow else None

        # MDL stores these values per vertex, while Blender stores them per face
        # corner. Split only the vertices whose exported corner data differs.
        corner_arrays = [loop_normals, loop_bitangents, *loop_uvs, *loop_colours]
        if loop_flow is not None:
            corner_arrays.append(loop_flow)
        indices, source_vertices, source_loops = create_corner_vertex_map(
            obj, loop_vertices, corner_arrays
        )

        vert_count = len(source_vertices)
        pos, nor   = get_space_data(obj, source_vertices, source_loops, loop_count)
        shapes     = get_shape_co(obj, source_vertices)
        uv_arrays  = [array[source_loops] for array in loop_uvs]
        col_arrays = [array[source_loops] for array in loop_colours]
        bitangents = loop_bitangents[source_loops]

        streams = create_stream_arrays(vert_count, vert_decl)

        streams[0]["position"] = pos
        streams[1]["normal"]   = nor
        streams[1]["tangent"]  = np.c_[vector_to_bytes(bitangents[:, :3].copy()), byte_sign(bitangents[:, 3].copy())]
        if mesh_flow:
            streams[1]["flow"] = get_flow(loop_flow[source_loops], nor, bitangents)

        for col_idx, col in enumerate(col_arrays):
            streams[1][f"colour{col_idx}"] = col

        for uv_idx, uvs in enumerate(uv_arrays):
            if uv_idx < 2:
                start = uv_idx * 2
                stop  = start + 2
                streams[1]["uv0"][:, start: stop] = uvs
            elif uv_idx == 2:
                streams[1]["uv1"] = uvs

        if mesh_options:
            apply_mesh_options(streams, mesh_options)

        return indices, streams, shapes, source_vertices

def update_mesh_streams(mesh: XIVMesh, mesh_streams: dict[int, NDArray], mesh_geo: list[NDArray], mesh_tex: list[NDArray], stream_offset: int, bone_limit: int) -> int:
        
    def update_geo_stream(mesh_geo_stream: NDArray, submesh_geo_stream: NDArray):
        if bone_limit < 5:
            for field in geo_stream.dtype.names:
                if field in ["blend_weights", "blend_indices"]:
                    mesh_geo_stream[field][:] = submesh_geo_stream[field][:, :4]
                else:
                    mesh_geo_stream[field][:] = submesh_geo_stream[field]
        else:
            mesh_geo_stream[:] = geo_stream

    for stream, mesh_arr in mesh_streams.items():
        stride = mesh_arr.dtype.itemsize
        mesh.vertex_buffer_offset[stream] = stream_offset
        mesh.vertex_buffer_stride[stream] = stride
        stream_offset += stride * len(mesh_arr)
        
    offset = 0
    for geo_stream, tex_stream in zip(mesh_geo, mesh_tex):
        update_geo_stream(mesh_streams[0][offset: offset + len(geo_stream)], geo_stream)
        mesh_streams[1][offset: offset + len(tex_stream)] = tex_stream
        offset += len(geo_stream)

    return stream_offset

def create_stream_arrays(vert_count: int, vert_decl: VertexDeclaration) -> dict[int, NDArray]:
    array_types = get_array_type(vert_decl)
    streams     = {}
    for stream, array_type in array_types.items():
        vert_array = np.zeros(vert_count, array_type)
        if "flow" in vert_array.dtype.names:
            vert_array["flow"][:, 2:] = 255
        if "colour0" in vert_array.dtype.names:
            vert_array["colour0"][:]  = 255
        if "colour1" in vert_array.dtype.names:
            vert_array["colour1"][:, 3] = 255
            
        streams[stream] = vert_array

    return streams
