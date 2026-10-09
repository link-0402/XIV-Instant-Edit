import re
import bpy
import bmesh

from numpy     import ushort, iinfo
from bpy.types import Object


USHORT_LIMIT = iinfo(ushort).max

def clean_material_path(name: str):
    name = re.sub(r'\.\d{3}$', "", name.strip())
    if not name.endswith(".mtrl"):
        name = name + ".mtrl"
    if not name.startswith("/"):
        name = "/" + name
    return name

def remove_loose_verts(obj: Object) -> None:
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    
    loose_verts = [vert for vert in bm.verts if len(vert.link_faces) == 0]
    if loose_verts:
        bmesh.ops.delete(bm, geom=loose_verts, context='VERTS')
    
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.to_mesh(obj.data)

    bm.free()
    obj.data.update()

def split_seams(obj: Object):
    # Edit Mode takes in every selected mesh: with the other export copies selected, each split
    # redid the work for all of them, and the user's other selected meshes got their hidden faces
    # revealed and seams marked. Only ``obj`` is edited; the selection comes back afterwards.
    others = [other for other in bpy.context.selected_objects if other is not obj]
    for other in others:
        other.select_set(False)
    try:
        _split_seams(obj)
    finally:
        for other in others:
            try:
                other.select_set(True)
            except (ReferenceError, RuntimeError):
                pass

def _split_seams(obj: Object):
    bpy.context.view_layer.objects.active = obj
    if obj.data.shape_keys:
        obj.active_shape_key_index = 0

    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.reveal(select=False)
    bpy.ops.mesh.select_all(action='SELECT')
    
    for uv_layer in obj.data.uv_layers:
        uv_layer.active = True
        bpy.ops.uv.seams_from_islands()

    bpy.ops.mesh.select_all(action='DESELECT')

    bm = bmesh.from_edit_mesh(obj.data)

    for edge in bm.edges:
        if edge.seam or not edge.smooth:
            edge.select = True
    
    bmesh.update_edit_mesh(obj.data, destructive=False, loop_triangles=False)
    bpy.ops.mesh.edge_split()
    bpy.ops.object.mode_set(mode='OBJECT')
    obj.data.update()
