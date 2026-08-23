# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy

from contextlib import contextmanager
from pathlib         import Path
from bpy.types       import Context, UILayout

from .objects        import visible_meshobj
from ..io.model      import ModelExport, SceneHandler
from ..io.logging    import YetAnotherLogger
from ..io.model.data import get_neck_morphs
from ..properties    import get_settings



_export_stats: dict[str, list[str]] = {}


def _armature_for_object(obj):
    """Return the armature that drives an exported mesh, if any."""
    if obj.parent and obj.parent.type == "ARMATURE":
        return obj.parent
    return next(
        (
            modifier.object
            for modifier in obj.modifiers
            if modifier.type == "ARMATURE"
            and modifier.object is not None
            and modifier.object.type == "ARMATURE"
        ),
        None,
    )


@contextmanager
def _clean_export_state(export_objects):
    """Temporarily export meshes at neutral scale and armature rest pose.

    Blender users may pose or scale an imported model for display. FFXIV MDL
    exports must instead be evaluated from the armature rest pose at neutral
    scale, so capture all affected state and restore it even if export fails.
    """
    objects = tuple(dict.fromkeys(export_objects or ()))
    armatures = tuple(
        dict.fromkeys(
            armature
            for obj in objects
            if (armature := _armature_for_object(obj)) is not None
        )
    )
    scales = [(obj, obj.scale.copy()) for obj in (*objects, *armatures)]
    poses = [
        (
            armature,
            armature.data.pose_position,
            [
                (
                    bone,
                    tuple(bone.location),
                    bone.rotation_mode,
                    tuple(bone.rotation_quaternion),
                    tuple(bone.rotation_euler),
                    tuple(bone.rotation_axis_angle),
                    tuple(bone.scale),
                )
                for bone in armature.pose.bones
            ],
        )
        for armature in armatures
    ]

    try:
        for obj, _scale in scales:
            obj.scale = (1.0, 1.0, 1.0)
        for armature, _pose_position, bones in poses:
            armature.data.pose_position = "REST"
            for bone, _location, _rotation_mode, _quaternion, _euler, _axis_angle, _scale in bones:
                bone.location = (0.0, 0.0, 0.0)
                bone.rotation_mode = "QUATERNION"
                bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
                bone.scale = (1.0, 1.0, 1.0)
        bpy.context.view_layer.update()
        yield
    finally:
        for armature, pose_position, bones in poses:
            armature.data.pose_position = pose_position
            for (
                bone,
                location,
                rotation_mode,
                quaternion,
                euler,
                axis_angle,
                scale,
            ) in bones:
                bone.location = location
                bone.rotation_mode = rotation_mode
                if rotation_mode == "QUATERNION":
                    bone.rotation_quaternion = quaternion
                elif rotation_mode == "AXIS_ANGLE":
                    bone.rotation_axis_angle = axis_angle
                else:
                    bone.rotation_euler = euler
                bone.scale = scale
        for obj, scale in scales:
            if obj.name in bpy.data.objects:
                obj.scale = scale
        bpy.context.view_layer.update()

def check_triangulation(objects=None) -> list[str]:
    visible = list(objects) if objects is not None else visible_meshobj()
    not_triangulated = []

    for obj in visible:
        tri_modifier = False
        for modifier in reversed(obj.modifiers):
            if modifier.type == "TRIANGULATE" and modifier.show_viewport:
                tri_modifier = True
                break

        if not tri_modifier:
            triangulated = True
            for poly in obj.data.polygons:
                verts = len(poly.vertices)
                if verts > 3:
                    triangulated = False
                    break

            if not triangulated:
                not_triangulated.append(obj.name)

    return not_triangulated
   
def get_export_path(directory: Path, file_name: str, subfolder: bool, body_slot:str ="") -> str:
    if subfolder:
        export_path = directory / body_slot / file_name
    else:
        export_path = directory / file_name

    return export_path

def export_result(file_path: Path, file_format: str, logger: YetAnotherLogger=None, batch=False, export_objects=None) -> None:
    with _clean_export_state(export_objects):
        bpy.context.evaluated_depsgraph_get().update()
        export = FileExport(file_path, file_format, logger=logger, batch=batch, export_objects=export_objects)
        export.export_template()

def get_export_stats(context: Context) -> None:
    global _export_stats

    def draw_popup(self, context: Context):
            layout: UILayout = self.layout
            for obj_name, messages in export_stats.items():
                layout.label(text=obj_name, icon='OUTLINER_OB_MESH')
                layout.separator(type='LINE')
                for message in messages:
                    layout.label(text=message, icon='INFO')
                layout.separator(type='SPACE', factor=2)

    if _export_stats:
        export_stats  = _export_stats.copy()
        _export_stats = {}
        context.window_manager.popup_menu(draw_popup, title=f"Model created succesfully!", icon='CHECKMARK')

def get_export_settings(format: str) -> dict[str, str | int | bool]:
    if format == 'GLTF':
        return {
            "export_format": "GLTF_SEPARATE", 
            "export_texture_dir": "GLTF Textures",
            "use_selection": False,
            "use_active_collection": False,
            "export_animations": False,
            "export_extras": True,
            "export_leaf_bone": False,
            "export_apply": True,
            "use_visible": True,
            "export_morph_normal": False,
            "export_try_sparse_sk": False,
            "export_attributes": True,
            "export_normals": True,
            "export_tangents": True,
            "export_skins": True,
            "export_influence_nb": 8,
            "export_active_vertex_color_when_no_material": True,
            "export_all_vertex_colors": True,
            "export_image_format": "NONE"
        }
    
    elif format == 'FBX':
        return {
            "use_selection": False,
            "use_active_collection": False,
            "bake_anim": False,
            "use_custom_props": True,
            "use_triangles": False,
            "add_leaf_bones": False,
            "use_mesh_modifiers": False,
            "use_visible": True,
            "colors_type": 'LINEAR'
        }
    

class FileExport:
    def __init__(self, file_path: Path, file_format: str, logger: YetAnotherLogger=None, batch=False, export_objects=None):
        self.logger      = logger
        self.file_format = file_format
        self.file_path   = file_path
        self.batch       = batch
        self.export_objects = export_objects
 
    def export_template(self):
        global _export_stats

        scene_handler = None
        try:
            scene_handler = SceneHandler(
                logger=self.logger, batch=self.batch, source_objects=self.export_objects
            )
            scene_handler.prepare_scene()
            scene_handler.process_scene()

            if self.logger:
                self.logger.log_separator()
                self.logger.log(f"Exporting {self.file_path.stem}")
                self.logger.log_separator()
                self.logger.last_item = None

            if self.file_format == 'GLTF':
                bpy.ops.export_scene.gltf(
                                    filepath=str(self.file_path) + ".gltf", 
                                    **get_export_settings('GLTF')
                                )

            elif self.file_format == 'FBX':
                bpy.ops.export_scene.fbx(
                                    filepath=str(self.file_path) + ".fbx", 
                                    **get_export_settings('FBX')
                                )
                
            else:
                if self.logger:
                    self.logger.log(f"Converting to MDL...", 2)
                settings = get_settings()
                _export_stats = ModelExport.export_scene(
                                                scene_handler.export_objs, 
                                                str(self.file_path) + ".mdl",
                                                settings.use_lods,
                                                get_neck_morphs(settings.neck_morph),
                                                logger=self.logger,
                                                **settings.get_model_flags(),
                                                **settings.get_mesh_options()
                                            )
        
        except Exception as e:
            raise e

        finally:
            if scene_handler is not None:
                scene_handler.restore_meshes()
        
