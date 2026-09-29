# Modified for XIV Instant Edit, 2026.
import bpy
import numpy as np

from contextlib import contextmanager
from pathlib         import Path
from bpy.types       import Context, UILayout
from mathutils       import Matrix

from .heels          import apply_calculated_heels_offset
from .objects        import visible_meshobj, armature_for_object
from ..io.model      import ModelExport, SceneHandler
from ..io.logging    import YetAnotherLogger
from ..properties    import get_settings
from ..backups       import create_backup
from ..instant_edit.character import is_hung_weapon



_export_stats: dict[str, list[str]] = {}


@contextmanager
def _clean_export_state(export_objects, reset_scaling=False):
    """Temporarily export armatures at rest pose and optionally neutral scale.

    Blender users may pose or scale an imported model for display. FFXIV MDL
    exports must be evaluated from the armature rest pose. Scale neutralization
    is optional; every affected value is restored even if export fails. A
    weapon armature hung from a character's bone exports at the origin, where
    its own skeleton puts the model, not in the character's hand.
    """
    objects = tuple(dict.fromkeys(export_objects or ()))
    armatures = tuple(
        dict.fromkeys(
            armature
            for obj in objects
            if (armature := armature_for_object(obj)) is not None
        )
    )
    scales = (
        [(obj, obj.scale.copy()) for obj in (*objects, *armatures)]
        if reset_scaling else []
    )
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
    hung = [(armature, armature.matrix_basis.copy()) for armature in armatures if is_hung_weapon(armature)]

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
        if hung:
            # Setting the world matrix reads the parent bone's current place.
            bpy.context.view_layer.update()
            for armature, _basis in hung:
                armature.matrix_world = Matrix.Identity(4)
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
        for armature, basis in hung:
            if armature.name in bpy.data.objects:
                armature.matrix_basis = basis
        bpy.context.view_layer.update()

def check_triangulation(objects=None) -> list[str]:
    """Names of meshes with faces of more than three corners and no Triangulate modifier."""
    visible = list(objects) if objects is not None else visible_meshobj()
    not_triangulated = []

    for obj in visible:
        if any(modifier.type == "TRIANGULATE" and modifier.show_viewport for modifier in obj.modifiers):
            continue
        polygons = obj.data.polygons
        corners  = np.empty(len(polygons), dtype=np.int32)
        polygons.foreach_get("loop_total", corners)
        if np.any(corners > 3):
            not_triangulated.append(obj.name)

    return not_triangulated

def check_weights(objects=None) -> list[str]:
    """Names of meshes without a vertex group for any bone of their armature.

    A quick check for the sidebar and before an export starts; the export itself
    then refuses every vertex without a bone weight (SceneHandler.check_weights).
    """
    visible = list(objects) if objects is not None else visible_meshobj()
    unweighted = []

    for obj in visible:
        # A bone-parented mesh without groups follows its bone; the MDL writer weights it fully.
        if not obj.vertex_groups and obj.parent_type == "BONE" and obj.parent_bone:
            continue
        armature = armature_for_object(obj)
        bones    = armature.data.bones if armature is not None else ()
        if not any(group.name in bones for group in obj.vertex_groups):
            unweighted.append(obj.name)

    return unweighted

def flush_edit_mode(objects) -> None:
    """Write Edit Mode changes into the meshes, which exports and checks read."""
    for obj in objects or ():
        if obj.mode == "EDIT":
            obj.update_from_editmode()

def get_export_path(directory: Path, file_name: str, subfolder: bool, body_slot:str ="") -> str:
    if subfolder:
        export_path = directory / body_slot / file_name
    else:
        export_path = directory / file_name

    return export_path

def export_result(file_path: Path, file_format: str, logger: YetAnotherLogger=None, batch=False, export_objects=None) -> None:
    settings = get_settings()
    flush_edit_mode(export_objects)
    with _clean_export_state(export_objects, settings.reset_scaling_on_export):
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

            heels_offset = None
            if get_settings().calculate_heels_offset:
                # Measured on the prepared export copies: rest pose, modifiers
                # and shape-key basis match what the exporter writes.
                heels_offset = apply_calculated_heels_offset(scene_handler.export_objs)

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
                settings = get_settings()
                if settings.backup_models_on_export:
                    create_backup(self.file_path.parent, self.file_path.name + ".fbx")
                bpy.ops.export_scene.fbx(
                                    filepath=str(self.file_path) + ".fbx", 
                                    **get_export_settings('FBX')
                                )
                
            else:
                if self.logger:
                    self.logger.log(f"Converting to MDL...", 2)
                settings = get_settings()
                if settings.backup_models_on_export:
                    create_backup(self.file_path.parent, self.file_path.name + ".mdl")
                _export_stats = ModelExport.export_scene(
                                                scene_handler.export_objs, 
                                                str(self.file_path) + ".mdl",
                                                settings.use_lods,
                                                [],
                                                logger=self.logger,
                                                **settings.get_model_flags()
                                            )

            if heels_offset is not None:
                attribute, obj_name = heels_offset
                _export_stats.setdefault(obj_name, []).append(f"Calculated {attribute}.")

        finally:
            if scene_handler is not None:
                scene_handler.restore_meshes()
        
