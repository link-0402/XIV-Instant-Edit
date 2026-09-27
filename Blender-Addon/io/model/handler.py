# Modified for XIV Instant Edit, 2026.
import re
import bpy
import numpy as np

from numpy.typing          import NDArray
from bpy.types             import Object, Depsgraph, ShapeKey
from collections           import defaultdict
from collections.abc       import Iterable 
   
from ..logging             import YetAnotherLogger
from ...properties         import get_settings
from .com.space            import lin_to_srgb       
from ...mesh.shapes        import get_shape_mix
from .com.exceptions       import XIVMeshError, XIVMeshParentError
from ...mesh.objects       import visible_meshobj, safe_object_delete, copy_mesh_object, quick_copy
from ...mesh.face_order    import get_original_faces, sequential_faces
from ...xivpy.model.vertex import XIV_COL

# The MDL exporter drops smaller weights (exp/weights.normalise_weights).
WEIGHT_THRESHOLD = 1e-6


def unweighted_vertices(obj: Object) -> tuple[int, int]:
    """Count the vertices faces use, and those among them without a weight above WEIGHT_THRESHOLD."""
    mesh = obj.data
    loop_vertices = np.empty(len(mesh.loops), dtype=np.int32)
    mesh.loops.foreach_get("vertex_index", loop_vertices)
    used = np.zeros(len(mesh.vertices), dtype=bool)
    used[loop_vertices] = True
    weighted = np.fromiter(
        (any(group.weight > WEIGHT_THRESHOLD for group in vertex.groups) for vertex in mesh.vertices),
        dtype=bool, count=len(mesh.vertices),
    )
    return int(np.count_nonzero(used)), int(np.count_nonzero(used & ~weighted))


def colour_layer_correction(obj: Object) -> None:
    '''This function corrects linear colour data that's been wrongly stored as sRGB during FBX import.'''
    verts  = len(obj.data.vertices)
    loops  = len(obj.data.loops)
    layers = [layer.name for layer in obj.data.color_attributes]
    
    layer_data: list[tuple[bool, str, str, str, NDArray]] = []
    for name in layers:
        layer   = obj.data.color_attributes[name]
        domain  = layer.domain
        convert = name.lower().startswith(XIV_COL) and layer.data_type == 'BYTE_COLOR'
        data    = 'FLOAT_COLOR' if convert else layer.data_type

        count = verts if domain == 'POINT' else loops
        rgba = np.ones(count * 4, dtype=np.float32)

        # When we access the data here, Blender converts what it thinks is sRGB to Linear colours.
        # Problem is the data is already Linear.
        layer.data.foreach_get("color", rgba)
        obj.data.color_attributes.remove(layer)
        layer_data.append((convert, name, domain, data, rgba))
        
    for convert, name, domain, data, rgba in layer_data:
        layer = obj.data.color_attributes.new(name, domain=domain, type=data)
        
        # Here we apply the opposite conversion to restore the linear data.
        # Due to the gamma curve we won't get the exact values back, but an approximation.
        # Blender converts only the colour; alpha is stored as is and must stay that way.
        if convert:
            rgba = rgba.reshape(-1, 4)
            rgba[:, :3] = lin_to_srgb(rgba[:, :3])
            rgba = rgba.ravel()

        layer.data.foreach_set("color", rgba)

class SceneHandler:
    """
    This class takes all visible meshes in a Blender scene and runs various logic on them to retain/add properties needed for XIV models. 
    It's designed to work with my export operators to save and restore the Blender scene when the class is done with its operations.
    It works non-destructively by duplicating the initial models, hiding them, then making the destructice edits on the duplicates.
    1. prepare_meshes saves the scene visibility state, checks what process each mesh requires and does an initial sort.
    2. process_meshes are the actual manipulation and finalisation of the meshes.
    3. restore_meshes restores the initial Blender scene from before prepare_meshes.
    Each function should be called separately on the same instance of the class in the listed order.

    """

    def __init__(self, logger: YetAnotherLogger=None, depsgraph: Depsgraph=None, batch=False, source_objects=None):
        props                            = get_settings()
        self.depsgraph : Depsgraph       = depsgraph
        self.shapekeys : bool            = props.keep_shapekeys
        self.xiv_mdl   : bool            = props.model_format == 'MDL'
        self.batch     : bool            = batch
        self.source_objects              = source_objects
        self.delete    : list[Object]    = []
        self.tri_method: tuple[str, str] = ("BEAUTY", "BEAUTY")

        self.logger     : YetAnotherLogger = logger
        self.meshes     : dict[Object, dict[str, list | bool]] = {}
        self.export_objs: list[Object] = []                       
    
    def prepare_scene(self) -> None:
        if self.logger:
            self.logger.log("Preparing meshes...", 2)

        visible_obj = list(self.source_objects) if self.source_objects is not None else visible_meshobj()
        no_skeleton = []

        for obj in visible_obj:
            armature = obj.parent if obj.parent and obj.parent.type == "ARMATURE" else next(
                (
                    modifier.object for modifier in obj.modifiers
                    if modifier.type == "ARMATURE" and modifier.object is not None
                ),
                None,
            )
            if armature is None:
                no_skeleton.append(obj.name)
                continue
            shape_key    = self.sort_shape_keys(obj) if self.shapekeys and obj.data.shape_keys else []
            transparency = ("xiv_transparency" in obj and obj["xiv_transparency"])

            self.meshes[obj] = {
                'shape'       : shape_key,
                'shape_values': [(key.name, key.value) for key in shape_key],
                'transparency': transparency,
                'old_name'    : obj.name,
                'hidden'      : obj.hide_get(),
                # Hiding deselects; restore_meshes selects it again.
                'selected'    : obj.select_get(),
                'armature'    : armature,
                }

            # Export evaluates the basis while retaining every selected shape as
            # a separate key. Snapshotting above must happen before this mutation
            # so restore_meshes can recover even when a later preparation step
            # or the exporter itself raises.
            for key in shape_key:
                key.value = 0
            
            obj.name = "temp_export"
        
        if no_skeleton:
            raise XIVMeshParentError(f"Missing Skeleton Parent: {', '.join(no_skeleton)}.")

    def sort_shape_keys(self, obj: Object) -> list[ShapeKey]:
        shape_keys = []
        for key in obj.data.shape_keys.key_blocks:
            if not key.name.startswith("shp"):
                continue
            if key.name[5:8] == "rue":
                continue
            shape_keys.append(key)

        return shape_keys

    def process_scene(self) -> list[Object]:
        fixed_transp: dict[Object, Object]              = {}
        shape_keys  : list[tuple[Object, Object, list]] = []
        dupes       : list[Object]                      = []
        names       : dict[Object, str]                 = {}

        if not self.depsgraph:
            self.depsgraph = bpy.context.evaluated_depsgraph_get()
        
        transparency = []
        for obj, stats in self.meshes.items():
            if self.logger:
                self.logger.last_item = f"{obj.name}"
            
            if stats["transparency"]:
                transparency.append(obj)

        if transparency:
            fixed_transp = self.handle_transparency(transparency)

        for obj, stats in self.meshes.items():
            if self.logger:
                self.logger.last_item = f"{obj.name}"

            if obj in fixed_transp:
                dupe = fixed_transp[obj]
            else:
                dupe = copy_mesh_object(obj, self.depsgraph)
                self.delete.append(dupe)

                self.rename_object(dupe, stats["old_name"])

            if not dupe.parent or dupe.parent.type != "ARMATURE":
                dupe.parent = stats["armature"]

            if stats["shape"]:
                shape_keys.append((dupe, obj, stats["shape"]))

            dupes.append(dupe)
            names[dupe] = stats["old_name"]

        if shape_keys:
            self.handle_shape_keys(shape_keys)

        self.handle_vertex_groups(dupes)
        self.check_weights(dupes, names)

        for obj in self.meshes:
            obj.hide_set(state=True)
        
        for obj in dupes:
            colour_layer_correction(obj)
        
        self.export_objs = dupes

    def handle_transparency(self, transparency: list[Object]) -> dict[Object, Object]:
        if self.logger:
            self.logger.log("Fixing face order...", 2)

        fixed_transp = {}
        to_process: list[tuple[Object, list]] = []
        for obj in transparency:
            if self.logger:
                self.logger.last_item = f"{obj.name}"
            
            original_faces = get_original_faces(obj)

            dupe = copy_mesh_object(obj, self.depsgraph)
            self.delete.append(dupe)

            self.rename_object(dupe, self.meshes[obj]["old_name"])

            fixed_transp[obj] = dupe
            to_process.append((dupe, original_faces))
        
        tri_graph = bpy.context.evaluated_depsgraph_get()
        for dupe, original_faces in to_process:
            eval_obj  = dupe.evaluated_get(tri_graph)
            previous_mesh = dupe.data
            dupe.data = bpy.data.meshes.new_from_object(
                            eval_obj, 
                            preserve_all_data_layers=True,
                            depsgraph=tri_graph
                            )
            
            if previous_mesh.users == 0:
                bpy.data.meshes.remove(previous_mesh)
            sequential_faces(dupe, original_faces)
        
        return fixed_transp
    
    def rename_object(self, obj: Object, old_name: str) -> None:
        if re.search(r"^\d+.\d+\s", old_name) and not self.xiv_mdl:
            name_parts = old_name.split(" ")
            obj.name = " ".join(name_parts[1:] + name_parts[0:1])
        else:
            obj.name = old_name

    def handle_shape_keys(self, shape_keys: list[tuple[Object, Object, list[ShapeKey]]]) -> None:
        if self.logger:
            self.logger.log("Retaining shape keys...", 2)

        vert_mismatches = []
        for dupe, original, keys in shape_keys:
            if len(original.data.vertices) != len(dupe.data.vertices):
                vert_mismatches.append((dupe, original, keys))
                continue

            for key in keys:
                if self.logger:
                    self.logger.last_item = f"{dupe.name}: Shape {key.name}"
                self._keep_shapes(original, dupe, key.name)

        if vert_mismatches:
            if self.logger:
                self.logger.log("-> Accounting for vert mismatch...", 2)
            self._shape_vert_mismatch(vert_mismatches)

    def _keep_shapes(self, original: Object, dupe: Object, key_name: str) -> None:
        if not dupe.data.shape_keys:
            dupe.shape_key_add(name="Basis")

        new_shape = dupe.shape_key_add(name=key_name)
        
        coords = get_shape_mix(original, key_name)
      
        new_shape.data.foreach_set("co", coords)

    def _shape_vert_mismatch(self, vert_mismatches: list[tuple[Object, Object, list[ShapeKey]]]) -> None:
        """
        We take all meshes with a vert mismatch with its original mesh and do a single depsgraph update to get the evaluated shapes we want.
        """
        temp_copies: dict[Object, dict[str, Object]] = defaultdict(dict)

        if self.logger:
            self.logger.log("-> Creating temp objects...", 2)

        for dupe, original, keys in vert_mismatches:
            for key in keys:
                temp_copy:Object = quick_copy(original, key.name)
                self.delete.append(temp_copy)
                temp_copies[dupe][key.name] = temp_copy

        shape_graph = bpy.context.evaluated_depsgraph_get()

        if self.logger:
            self.logger.log("-> Applying shape keys...", 2)

        for dupe, copies in temp_copies.items():
            for key_name, copy in copies.items():
                if self.logger:
                    self.logger.last_item = f"{dupe.name}: Shape {key_name}"

                mesh = None
                try:
                    eval_obj   = copy.evaluated_get(shape_graph)
                    mesh       = bpy.data.meshes.new_from_object(eval_obj)
                    vert_count = len(mesh.vertices)

                    basis_co = np.zeros(vert_count * 3, dtype=np.float32)
                    mesh.vertices.foreach_get("co", basis_co)

                    if not dupe.data.shape_keys:
                        dupe.shape_key_add(name="Basis")

                    new_shape = dupe.shape_key_add(name=key_name)

                    new_shape.data.foreach_set("co", basis_co)

                except Exception as e:
                    if self.logger:
                        self.logger.last_item = (f"Vertex count mismatch for {dupe.name} shape {key_name}.")
                    raise e
                
                finally:
                    if mesh is not None:
                        bpy.data.meshes.remove(mesh)
                    self.delete.remove(copy)
                    safe_object_delete(copy)

    def handle_vertex_groups(self, dupes: Iterable[Object]):
        if self.logger:
            self.logger.log("Cleaning vertex groups...", 2)

        for dupe in dupes:
            # Collect first: removing while iterating skips the group after each removed one.
            bones = dupe.parent.data.bones
            for name in [group.name for group in dupe.vertex_groups if not bones.get(group.name)]:
                dupe.vertex_groups.remove(dupe.vertex_groups[name])

    def check_weights(self, dupes: Iterable[Object], names: dict[Object, str]) -> None:
        """Refuse meshes with vertices the game would leave behind: each needs a bone weight.

        Runs on the evaluated copies after non-bone groups are gone, so weights from
        modifiers count and weights on groups that are no bone do not.
        """
        unweighted = []
        for dupe in dupes:
            # A bone-parented mesh without groups follows its bone; the MDL writer weights it fully.
            if not dupe.vertex_groups and dupe.parent_type == 'BONE' and dupe.parent_bone:
                continue
            used, count = unweighted_vertices(dupe)
            if count:
                name = names.get(dupe, dupe.name)
                unweighted.append(f"{name} (all)" if count == used else f"{name} ({count:,} of {used:,})")
        if unweighted:
            raise XIVMeshError(
                "Vertices without bone weights: " + ", ".join(unweighted)
                + ". Weight every vertex to the armature's bones before exporting."
            )

    def restore_meshes(self) -> None:
        """We're trying a lot."""
        if self.logger:
            self.logger.log("Restoring scene...", 2)
        
        for obj in reversed(self.delete):
            safe_object_delete(obj)
        self.delete.clear()
    
        for obj in self.meshes:
            try:
                obj.name = self.meshes[obj]["old_name"]
                obj.hide_set(state=self.meshes[obj]["hidden"])
                if self.meshes[obj].get("selected") and not obj.hide_get():
                    obj.select_set(True)
                if obj.data.shape_keys:
                    key_blocks = obj.data.shape_keys.key_blocks
                    for key_name, value in self.meshes[obj].get("shape_values", ()):
                        key = key_blocks.get(key_name)
                        if key is not None:
                            key.value = value
            except Exception as e:
                if self.logger:
                    self.logger.log_exception(f"Error deleting {obj.name}: {e}")
                else:
                    print(f"Error restoring {obj.name}: {e}")
        try:
            bpy.context.view_layer.update()
        except Exception as e:
            if self.logger:
                    self.logger.log_exception(f"Error updating view layer: {e}")
            else:
                print(f"Error updating view layer: {e}")
