# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy

from pathlib         import Path
from bpy.types       import Context, UILayout
   
from .objects        import visible_meshobj
from ..io.model      import ModelExport, SceneHandler
from ..io.logging    import YetAnotherLogger
from ..io.model.data import get_neck_morphs
from ..properties    import get_settings



_export_stats: dict[str, list[str]] = {}

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
        
