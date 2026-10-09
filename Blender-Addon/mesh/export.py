# Modified for XIV Instant Edit, 2026.
import bpy
import numpy as np

from contextlib import ExitStack, contextmanager
from pathlib         import Path
from bpy.types       import Context, UILayout
from mathutils       import Matrix

from .heels          import apply_calculated_heels_offset, is_heels_offset_attribute
from .objects        import visible_meshobj, armature_for_object
from .shapes         import get_shape_mix
from ..io.model      import ModelExport, SceneHandler, exportable_shape_keys
from ..io.logging    import YetAnotherLogger
from ..properties    import get_settings
from ..backups       import create_backup
from ..instant_edit.character import is_hung_weapon
from ..instant_edit.context import SIZE_COPY_NAME



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

def check_uv_layers(objects=None) -> list[str]:
    """Problems with the UV maps meshes would export, one sentence per mesh (see get_xiv_uv_layers)."""
    from ..io.model.com.exceptions import XIVMeshError
    from ..io.model.exp.accessors import get_xiv_uv_layers

    visible = list(objects) if objects is not None else visible_meshobj()
    problems = []
    for obj in visible:
        try:
            get_xiv_uv_layers(obj)
        except XIVMeshError as error:
            problems.append(str(error))
    return problems

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

def check_dropped_shape_keys(objects=None) -> list[str]:
    """Names of meshes whose shape keys an export leaves out because Keep Shape Keys is off.

    Meshes on a body material are skipped: devkit body parts carry shape keys
    that are meant to be left out.
    """
    if get_settings().keep_shapekeys:
        return []
    from ..materials import export_material_path, is_body_material

    visible = list(objects) if objects is not None else visible_meshobj()
    return [
        obj.name for obj in visible
        if exportable_shape_keys(obj) and not is_body_material(export_material_path(obj))
    ]

def dropped_shape_keys_warning(objects=None) -> str:
    """One sentence naming the meshes check_dropped_shape_keys finds, or "" when there are none."""
    names = check_dropped_shape_keys(objects)
    if not names:
        return ""
    shown = ", ".join(names[:5])
    if len(names) > 5:
        shown += f", +{len(names) - 5} more"
    return f"Keep Shape Keys is off, so the export leaves out the shape keys of {shown}."

def draw_dropped_shape_keys(layout: UILayout, names: list[str]) -> None:
    """Draw check_dropped_shape_keys' meshes into an export confirmation dialog."""
    if not names:
        return
    layout.label(text="Keep Shape Keys is off, so these meshes export without their shape keys:", icon="ERROR")
    column = layout.column(align=True)
    for name in names[:12]:
        column.label(text=name, icon="MESH_DATA")
    if len(names) > 12:
        column.label(text=f"+{len(names) - 12} more meshes", icon="BLANK1")
    layout.label(text="Turn on Keep Shape Keys in Options > Export to keep them.", icon="BLANK1")

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

@contextmanager
def _showing(export_objects):
    """Show hidden meshes an export was given for its duration: parts a character send imported
    hidden because the game didn't draw them still belong to their model."""
    view_layer = bpy.context.view_layer
    hidden = [
        obj for obj in dict.fromkeys(export_objects or ())
        if obj.name in view_layer.objects and obj.hide_get()
    ]
    for obj in hidden:
        obj.hide_set(False)
    try:
        yield
    finally:
        for obj in hidden:
            if obj.name in view_layer.objects:
                obj.hide_set(True)


def export_result(file_path: Path, file_format: str, logger: YetAnotherLogger=None, batch=False, export_objects=None) -> None:
    settings = get_settings()
    flush_edit_mode(export_objects)
    with _showing(export_objects), _clean_export_state(export_objects, settings.reset_scaling_on_export):
        bpy.context.evaluated_depsgraph_get().update()
        export = FileExport(file_path, file_format, logger=logger, batch=batch, export_objects=export_objects)
        export.export_template()

class ShapeExports:
    """Export the same meshes several times while their shape keys change, like a model at each of
    Magic Fit's sizes, with ``write`` inside the ``with`` block.

    For an MDL, the export copies are made and prepared once (split along UV seams and sharp edges,
    the slow part of an export), and each write only moves their vertices, shape keys and custom
    normals to the shape the meshes show then. That writes what a fresh export of the shape would,
    but for custom normals: set again on the split copy, they come out up to about 0.0005 off
    (0.03 degrees), and a tangent byte may round the other way. Other formats, meshes with
    transparency (their faces are reordered) and meshes whose copies can't be followed back to
    their vertices are exported afresh each time.

    The meshes keep their names, visibility and shape key values while the block runs, so they can
    change between writes (Magic Fit adds a key per size).
    """

    _SOURCE_VERTEX = "xiv_ie_source_vertex"
    _SOURCE_CORNER = "xiv_ie_source_corner"
    _COPY_NAME     = SIZE_COPY_NAME

    def __init__(self, export_objects, file_format: str):
        self.export_objects = list(export_objects)
        self.file_format    = file_format
        self.copies         = None
        self._handler       = None
        self._contexts      = None

    def __enter__(self) -> "ShapeExports":
        if self.file_format != 'MDL':
            return self
        flush_edit_mode(self.export_objects)
        self._contexts = ExitStack()
        try:
            settings = get_settings()
            self._contexts.enter_context(_showing(self.export_objects))
            self._contexts.enter_context(
                _clean_export_state(self.export_objects, settings.reset_scaling_on_export))
            bpy.context.evaluated_depsgraph_get().update()
            self._make_copies()
        except BaseException:
            self._close()
            raise
        return self

    def __exit__(self, *_exc) -> bool:
        self._close()
        return False

    def write(self, file_path: Path) -> None:
        """Export the meshes as they show now to ``file_path`` (without its extension)."""
        global _export_stats

        if self.copies is None:
            export_result(file_path, self.file_format, export_objects=self.export_objects)
            return
        self._move_copies()
        copies = [source["copy"] for source in self.copies]
        settings = get_settings()
        with self._named_copies():
            self._reset_heels_offsets()
            heels_offset = apply_calculated_heels_offset(copies) if settings.calculate_heels_offset else None
            if settings.backup_models_on_export:
                create_backup(file_path.parent, file_path.name + ".mdl")
            _export_stats = ModelExport.export_scene(
                copies, str(file_path) + ".mdl", settings.use_lods, [], **settings.get_model_flags())
        if heels_offset is not None:
            attribute, obj_name = heels_offset
            _export_stats.setdefault(obj_name, []).append(f"Calculated {attribute}.")

    def _make_copies(self) -> None:
        view_layer = bpy.context.view_layer
        active = view_layer.objects.active
        try:
            self._copy_meshes()
        finally:
            # Preparing a copy makes it active; the meshes stay in front of the user meanwhile.
            if active is not None and active.name in view_layer.objects:
                view_layer.objects.active = active

    def _copy_meshes(self) -> None:
        handler = SceneHandler(source_objects=self.export_objects)
        self._handler = handler
        handler.prepare_scene()
        handler.process_scene()
        sources = []
        for (original, stats), copy in zip(handler.meshes.items(), handler.export_objs):
            sources.append({
                "original": original,
                "copy": copy,
                "name": stats["old_name"],
                # Kept and game shape keys are off in an export; Magic Fit's sizes turn others on.
                "off_keys": [name for name, _value in stats["shape_values"]],
                "kept_keys": [key.name for key in stats["shape"]],
                "transparency": stats["transparency"],
            })
        if any(source["transparency"] for source in sources) or any(
                source["kept_keys"] and len(source["copy"].data.vertices) != len(source["original"].data.vertices)
                for source in sources):
            # Exported afresh each time: drop the copies.
            handler.restore_meshes()
            self._handler = None
            return

        from ..io.model.exp.scene import prepare_export_mesh, PREPARED_PROPERTY

        # While the meshes are still hidden: preparing a copy enters Edit Mode, which takes in
        # every selected mesh.
        for source in sources:
            copy = source["copy"]
            mesh = copy.data
            source["vertex_count"] = len(mesh.vertices)
            source["corner_count"] = len(mesh.loops)
            # The world matrix prepare_export_mesh applies, before it makes it the identity.
            source["matrix"] = copy.matrix_world.copy()
            for name, domain, count in ((self._SOURCE_VERTEX, 'POINT', len(mesh.vertices)),
                                        (self._SOURCE_CORNER, 'CORNER', len(mesh.loops))):
                attribute = mesh.attributes.new(name, 'INT', domain)
                attribute.data.foreach_set("value", np.arange(count, dtype=np.int32))
            prepare_export_mesh(copy)
            copy[PREPARED_PROPERTY] = True
            source["heels"] = {key: copy[key] for key in copy.keys() if is_heels_offset_attribute(key)}
            for key, name, count in (("vertices", self._SOURCE_VERTEX, len(copy.data.vertices)),
                                     ("corners", self._SOURCE_CORNER, len(copy.data.loops))):
                values = np.empty(count, dtype=np.int32)
                copy.data.attributes[name].data.foreach_get("value", values)
                source[key] = values
        for source in sources:
            source["copy"].hide_set(True)
        self._release_sources()
        self.copies = sources

    def _release_sources(self) -> None:
        """Give the meshes back their names, visibility and key values, which SceneHandler changed
        while making the copies; the copies take other names until they're written."""
        handler = self._handler
        for copy in handler.export_objs:
            copy.name = self._COPY_NAME
        for obj, stats in handler.meshes.items():
            obj.name = stats["old_name"]
            obj.hide_set(stats["hidden"])
            if stats.get("selected") and not obj.hide_get():
                obj.select_set(True)
            keys = obj.data.shape_keys
            if keys is not None:
                for key_name, value in stats["shape_values"]:
                    block = keys.key_blocks.get(key_name)
                    if block is not None:
                        block.value = value

    @contextmanager
    def _named_copies(self):
        """The copies under the meshes' names while they're written, as the writer reads parts from names."""
        sources = self.copies
        for source in sources:
            source["original"].name = "temp_export"
        for source in sources:
            source["copy"].name = source["name"]
        try:
            yield
        finally:
            for source in sources:
                source["copy"].name = self._COPY_NAME
            for source in sources:
                source["original"].name = source["name"]

    def _reset_heels_offsets(self) -> None:
        """Give the copies back the heels_offset attributes they were made with: an offset calculated
        for an earlier shape would stay on them, though a shape with nothing below the floor gets none."""
        for source in self.copies:
            copy = source["copy"]
            for key in [key for key in copy.keys() if is_heels_offset_attribute(key)]:
                del copy[key]
            for key, value in source["heels"].items():
                copy[key] = value

    def _move_copies(self) -> None:
        """Give the copies the shape the meshes show now, with their kept and game shape keys off,
        as SceneHandler copies them."""
        turned_off = []
        for source in self.copies:
            keys = source["original"].data.shape_keys
            if keys is None:
                continue
            for name in source["off_keys"]:
                block = keys.key_blocks.get(name)
                if block is not None and block.value != 0:
                    turned_off.append((block, block.value))
                    block.value = 0
        try:
            depsgraph = bpy.context.evaluated_depsgraph_get()
            for source in self.copies:
                self._move_copy(source, depsgraph)
        finally:
            for block, value in reversed(turned_off):
                block.value = value

    def _move_copy(self, source: dict, depsgraph) -> None:
        original = source["original"]
        evaluated = original.evaluated_get(depsgraph)
        # Only the positions and normals are read.
        mesh = bpy.data.meshes.new_from_object(evaluated, depsgraph=depsgraph)
        try:
            if len(mesh.vertices) != source["vertex_count"] or len(mesh.loops) != source["corner_count"]:
                raise ValueError(f"{source['name']}: its vertex count changed between exports.")
            co = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
            mesh.vertices.foreach_get("co", co)
            normals = None
            if mesh.has_custom_normals:
                normals = np.empty(len(mesh.loops) * 3, dtype=np.float32)
                mesh.corner_normals.foreach_get("vector", normals)
        finally:
            bpy.data.meshes.remove(mesh)

        copy = source["copy"]
        data = copy.data
        moved = co.reshape(-1, 3)[source["vertices"]].ravel()
        data.vertices.foreach_set("co", moved)
        keys = data.shape_keys
        if keys is not None:
            keys.key_blocks[0].data.foreach_set("co", moved)
            for block in keys.key_blocks[1:]:
                # As SceneHandler keeps a shape key: the mesh's mix with this key fully on.
                mix = get_shape_mix(original, block.name).reshape(-1, 3)[source["vertices"]]
                block.data.foreach_set("co", mix.astype(np.float32).ravel())
        if normals is not None:
            data.normals_split_custom_set(normals.reshape(-1, 3)[source["corners"]])
        self._transform(data, source["matrix"])
        data.update()

    @staticmethod
    def _transform(data, matrix: Matrix) -> None:
        """Move ``data`` by ``matrix`` as apply_transforms moves a copy into world space."""
        if matrix == Matrix.Identity(4):
            return
        if not data.shape_keys:
            data.transform(matrix)
            return
        count = len(data.vertices)
        transform_matrix = np.array(matrix).astype(np.float32)
        for points in (data.vertices, *(block.data for block in data.shape_keys.key_blocks)):
            pos = np.zeros(count * 3, dtype=np.float32)
            points.foreach_get("co", pos)
            pos = np.c_[pos.reshape(-1, 3), np.ones(count)]
            points.foreach_set("co", (transform_matrix @ pos.T).T[:, :3].flatten())

    def _close(self) -> None:
        handler, self._handler, self.copies = self._handler, None, None
        try:
            if handler is not None:
                handler.restore_meshes()
        finally:
            if self._contexts is not None:
                contexts, self._contexts = self._contexts, None
                contexts.close()


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
        
