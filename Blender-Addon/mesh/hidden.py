"""Find the vertices that other meshes cover, like skin under a top, and delete them.

A vertex counts as covered when rays leaving it within 45 degrees of its
normal all hit a surface that hides what lies behind it. Looking at a covered
vertex from anywhere in that cone shows the cover instead; from further to the
side, a viewer sees at most a sliver through the gap between skin and gear.
The rules follow how the game draws a model:

- Faces with the vertex's own material never cover it, so skin parts don't
  hide each other's folds. Only Itself instead tests a mesh against its own
  faces alone, for layers inside one mesh.
- A face whose material culls backfaces covers only with its front, so a ray
  passes through it from behind. Faces without a material cover with their
  front too. A vertex of a face drawn from both sides (no culling, or no
  material) must be covered on both sides.
- A face covers where its material is at least 90% opaque. When an image's
  alpha drives the material's Alpha, directly or through math nodes like the
  material preview's alpha test, a ray that hits the face reads that image
  where it hits, so a top's opaque parts cover the skin and its sheer parts
  don't. Gear the game draws solid because its material has no alpha
  threshold covers everywhere, whatever an older import drew. A mesh marked
  for transparency sorting, a Transparent BSDF or an alpha the test cannot
  follow covers nothing.
- Surfaces closer to a vertex than a small tolerance do not cover it, so a
  mesh lying on a copy of itself (a mannequin, a Backfaces duplicate) survives.
- The test uses each mesh's own vertices in the rest pose, without modifiers.
  Each shp_ shape key is another state of the scene, with the key applied to
  every mesh that has it; a vertex must be covered in every state.

A covered vertex is deleted with the faces that use it, unless one of those
faces has an uncovered corner: that face stays whole, so the edge of a covered
area never opens a gap. A margin of rings of covered vertices around the
uncovered ones stays too, as animations move the cover against what it covers.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import math

import bmesh
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform

from ..instant_edit.context import mesh_ids_from_name
from ..instant_edit.material_preview import drawn_solid


CONE_DEGREES = 45.0
# The rays of the cone: one along the normal and rings of 6 and 12 around it.
_RINGS = 2
# A ray that passes through this many culled backfaces counts as escaped.
_MAX_HOPS = 64
# Relative to the size of the scene: the step past a surface a ray continues
# from, and the distance within which a surface does not cover a vertex.
_STEP = 1e-6
_TOLERANCE = 1e-4
# What a face hides: nothing, what lies behind its front, or both sides.
_BLOCKS_NOTHING, _BLOCKS_FRONT, _BLOCKS_BOTH = 0, 1, 2
# Behind a 90% opaque texel the skin shows at a tenth of its strength at most.
_MIN_OPACITY = 0.9


def _divide(a, b):
    """Blender's Math node divide: zero where the divisor is zero."""
    a, b = np.broadcast_arrays(np.asarray(a, dtype=np.float32), np.asarray(b, dtype=np.float32))
    return np.divide(a, b, out=np.zeros(a.shape, dtype=np.float32), where=b != 0)


# The Math nodes an opacity image may pass through on its way to Alpha.
_MATH = {
    "GREATER_THAN": lambda a, b: np.greater(a, b).astype(np.float32),
    "LESS_THAN": lambda a, b: np.less(a, b).astype(np.float32),
    "MULTIPLY": np.multiply,
    "DIVIDE": _divide,
    "ADD": np.add,
    "SUBTRACT": np.subtract,
    "MINIMUM": np.minimum,
    "MAXIMUM": np.maximum,
}


def _cone(degrees: float, rings: int) -> np.ndarray:
    """Unit vectors around +Z out to ``degrees`` from it: +Z, then ring r of 6r vectors."""
    vectors = [(0.0, 0.0, 1.0)]
    for ring in range(1, rings + 1):
        tilt = math.radians(degrees) * ring / rings
        count = 6 * ring
        for index in range(count):
            # Offset each ring by half a step so its rays fall between the last ring's.
            turn = 2.0 * math.pi * (index + 0.5 * (ring - 1)) / count
            vectors.append((math.sin(tilt) * math.cos(turn), math.sin(tilt) * math.sin(turn), math.cos(tilt)))
    return np.array(vectors)


def shape_key_states(obj) -> dict[str, np.ndarray]:
    """The object's shp_ shape keys, which the game applies, as local vertex positions."""
    keys = obj.data.shape_keys
    if keys is None:
        return {}
    count = len(obj.data.vertices)
    states = {}
    for key in keys.key_blocks[1:]:
        if not key.name.startswith("shp"):
            continue
        co = np.empty(count * 3, dtype=np.float64)
        key.data.foreach_get("co", co)
        states[key.name] = co.reshape(-1, 3)
    return states


@dataclass
class _Opacity:
    """Where a material's opacity image hides what lies behind it."""

    # One byte per texel, rows from the bottom like Blender's pixels: 1 hides.
    hides_at: bytes
    width: int
    height: int
    extension: str
    # The UV map the image node reads; empty for the mesh's active render map.
    uv_map: str

    def hides(self, u: float, v: float) -> bool:
        if not (math.isfinite(u) and math.isfinite(v)):
            return False
        if self.extension == "REPEAT":
            u, v = u % 1.0, v % 1.0
        elif self.extension == "CLIP" and not (0.0 <= u <= 1.0 and 0.0 <= v <= 1.0):
            return False
        x = min(max(int(u * self.width), 0), self.width - 1)
        y = min(max(int(v * self.height), 0), self.height - 1)
        return bool(self.hides_at[y * self.width + x])


def _opacity_source(alpha):
    """The image node whose alpha feeds the ``alpha`` socket and the Math steps
    on the way (nearest Alpha first), or None for anything else."""
    steps = []
    socket = alpha
    for _depth in range(32):
        if not socket.is_linked:
            return None
        link = socket.links[0]
        node = link.from_node
        if node.type == "REROUTE":
            socket = node.inputs[0]
        elif node.type == "TEX_IMAGE":
            return (node, steps) if link.from_socket.name == "Alpha" else None
        elif node.type == "MATH" and node.operation in _MATH:
            operands = (node.inputs[0], node.inputs[1])
            linked = [index for index, operand in enumerate(operands) if operand.is_linked]
            if len(linked) != 1:
                return None
            index = linked[0]
            steps.append((node.operation, index, float(operands[1 - index].default_value), node.use_clamp))
            socket = operands[index]
        else:
            return None
    return None


def _read_opacity(node, steps) -> _Opacity | None:
    image = node.image
    vector = node.inputs["Vector"]
    uv_map = ""
    if vector.is_linked:
        source = vector.links[0].from_node
        if source.type != "UVMAP":
            return None
        uv_map = source.uv_map
    width, height = image.size if image is not None else (0, 0)
    if not width or not height:
        return None
    channels = image.channels
    if channels == 4:
        pixels = np.empty(width * height * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
        values = pixels[3::4].copy()
        del pixels
    else:
        values = np.ones(width * height, dtype=np.float32)
    for operation, index, constant, clamp in reversed(steps):
        values = _MATH[operation](*((values, constant) if index == 0 else (constant, values)))
        if clamp:
            values = np.clip(values, 0.0, 1.0)
    opaque = (np.asarray(values) >= _MIN_OPACITY).reshape(height, width)
    # Filtering blends a texel with its neighbours, so one next to a
    # see-through texel does not count.
    hides_at = opaque.copy()
    for axis in (0, 1):
        for shift in (-1, 1):
            hides_at &= np.roll(opaque, shift, axis=axis)
    return _Opacity(hides_at.tobytes(), width, height, node.extension, uv_map)


def material_cover(material) -> bool | _Opacity:
    """Where a material hides what lies behind it: everywhere (True), nowhere
    (False), or where an opacity image says."""
    if material is None:
        return True
    shader_package = str(material.get("xiv_shader_package", ""))
    # The eye occlusion shell only shades the eyes, whatever older imports drew.
    if shader_package.casefold() == "characterocclusion.shpk":
        return False
    # Older imports drew gear without an alpha threshold see-through.
    try:
        constants = json.loads(str(material.get("xiv_shader_constants", "[]")))
        if shader_package and isinstance(constants, list) and drawn_solid(
                shader_package, [constant for constant in constants if isinstance(constant, dict)]):
            return True
    except (TypeError, ValueError):
        pass
    tree = material.node_tree
    nodes = list(tree.nodes) if tree is not None else []
    if any(node.type == "BSDF_TRANSPARENT" for node in nodes):
        return False
    principled = [node for node in nodes if node.type == "BSDF_PRINCIPLED"]
    if not principled:
        return getattr(material, "surface_render_method", "") != "BLENDED"
    cover = True
    for node in principled:
        alpha = node.inputs.get("Alpha")
        if alpha is None:
            continue
        if not alpha.is_linked:
            if alpha.default_value < _MIN_OPACITY:
                return False
            continue
        source = _opacity_source(alpha)
        opacity = _read_opacity(*source) if source is not None else None
        if opacity is None or cover is not True:
            return False
        cover = opacity
    return cover


def material_key(material) -> str | None:
    """The material's name without the .001 Blender adds to a second import of it."""
    if material is None:
        return None
    base, dot, suffix = material.name.rpartition(".")
    return base if dot and len(suffix) == 3 and suffix.isdigit() else material.name


def mesh_lod(obj) -> int | None:
    """The LOD an Instant Edit part name gives, or None for other meshes."""
    try:
        return mesh_ids_from_name(obj)[2]
    except Exception:
        return None


@dataclass
class _Mesh:
    """One object's triangles, normals and positions in world space."""

    obj: object
    matrix: np.ndarray
    basis: np.ndarray
    states: dict[str, np.ndarray]
    # Per vertex: the average of its corner normals in world space, and
    # whether a face using it is drawn from both sides.
    normals: np.ndarray
    two_sided: np.ndarray
    triangles: np.ndarray
    # The vertex of each face corner, the first corner of each face and its corner count.
    loop_vertices: np.ndarray
    polygon_starts: np.ndarray
    polygon_sizes: np.ndarray
    edges: np.ndarray
    # Per triangle: what it hides (_BLOCKS_*) and the material_key of its material.
    blocks: np.ndarray
    materials: list
    # Per triangle: the index into ``opacities`` of the image that says where
    # it hides, or -1 where all of it does. Each entry has its triangles' UVs.
    opacity_index: np.ndarray
    opacities: list[tuple[_Opacity, np.ndarray]]

    @classmethod
    def read(cls, obj, covers: dict) -> "_Mesh":
        """``covers`` caches material_cover per material across meshes."""
        mesh = obj.data
        mesh.calc_loop_triangles()
        count = len(mesh.vertices)
        basis = np.empty(count * 3, dtype=np.float64)
        mesh.vertices.foreach_get("co", basis)
        triangles = np.empty(len(mesh.loop_triangles) * 3, dtype=np.int64)
        mesh.loop_triangles.foreach_get("vertices", triangles)
        triangles = triangles.reshape(-1, 3)
        triangle_loops = np.empty(len(mesh.loop_triangles) * 3, dtype=np.int64)
        mesh.loop_triangles.foreach_get("loops", triangle_loops)
        triangle_loops = triangle_loops.reshape(-1, 3)
        polygons = np.empty(len(mesh.loop_triangles), dtype=np.int64)
        mesh.loop_triangles.foreach_get("polygon_index", polygons)
        loop_vertices = np.empty(len(mesh.loops), dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", loop_vertices)
        polygon_starts = np.empty(len(mesh.polygons), dtype=np.int64)
        mesh.polygons.foreach_get("loop_start", polygon_starts)
        polygon_sizes = np.empty(len(mesh.polygons), dtype=np.int64)
        mesh.polygons.foreach_get("loop_total", polygon_sizes)
        edges = np.empty(len(mesh.edges) * 2, dtype=np.int64)
        mesh.edges.foreach_get("vertices", edges)
        matrix = np.array(obj.matrix_world, dtype=np.float64)
        corner_normals = np.empty(len(mesh.loops) * 3, dtype=np.float64)
        mesh.corner_normals.foreach_get("vector", corner_normals)
        normals = np.zeros((count, 3))
        np.add.at(normals, loop_vertices, corner_normals.reshape(-1, 3))
        # Normals follow the inverse transpose, which keeps them pointing out
        # of a scaled or mirrored mesh.
        normals = normals @ np.linalg.inv(matrix[:3, :3])
        if np.linalg.det(matrix[:3, :3]) < 0:
            # Blender draws a mirrored object with its faces turned back
            # around; do the same so the world-space normals point outwards.
            triangles = triangles[:, (0, 2, 1)]
            triangle_loops = triangle_loops[:, (0, 2, 1)]
        # A material index past the last slot draws with no material.
        slots = [slot.material for slot in obj.material_slots] + [None]
        transparent = bool(obj.get("xiv_transparency", False))
        slot_covers = []
        uv_corners: dict[str, np.ndarray | None] = {}
        opacities = []
        slot_opacity = []
        for material in slots:
            key = material.as_pointer() if material is not None else None
            if key not in covers:
                covers[key] = material_cover(material)
            cover = covers[key]
            index = -1
            if isinstance(cover, _Opacity):
                layer = mesh.uv_layers.get(cover.uv_map) if cover.uv_map else next(
                    (layer for layer in mesh.uv_layers if layer.active_render), None)
                if layer is not None and layer.name not in uv_corners:
                    uv = np.empty(len(mesh.loops) * 2, dtype=np.float64)
                    layer.data.foreach_get("uv", uv)
                    uv_corners[layer.name] = uv.reshape(-1, 2)[triangle_loops]
                if layer is None:
                    cover = False
                else:
                    index = len(opacities)
                    opacities.append((cover, uv_corners[layer.name]))
            slot_covers.append(cover)
            slot_opacity.append(index)
        slot_blocks = np.array([
            _BLOCKS_NOTHING if transparent or cover is False
            else _BLOCKS_FRONT if material is None or material.use_backface_culling
            else _BLOCKS_BOTH
            for material, cover in zip(slots, slot_covers)
        ], dtype=np.int8)
        polygon_material = np.zeros(len(mesh.polygons), dtype=np.int64)
        mesh.polygons.foreach_get("material_index", polygon_material)
        polygon_material = np.minimum(polygon_material, len(slots) - 1)
        material_index = polygon_material[polygons]
        slot_keys = [material_key(material) for material in slots]
        # Faces without a material are drawn from both sides too.
        slot_culled = np.array([material is not None and material.use_backface_culling for material in slots])
        two_sided = np.zeros(count, dtype=bool)
        two_sided[loop_vertices[~slot_culled[np.repeat(polygon_material, polygon_sizes)]]] = True
        return cls(obj, matrix, basis.reshape(-1, 3), shape_key_states(obj), normals, two_sided, triangles,
                   loop_vertices, polygon_starts, polygon_sizes, edges.reshape(-1, 2),
                   slot_blocks[material_index], [slot_keys[index] for index in material_index.tolist()],
                   np.array(slot_opacity, dtype=np.int64)[material_index], opacities)

    def world(self, local: np.ndarray) -> np.ndarray:
        return local @ self.matrix[:3, :3].T + self.matrix[:3, 3]

    def own_materials(self) -> frozenset:
        """The materials its faces use."""
        return frozenset(self.materials)


class _Scene:
    """The covering triangles of one scene state in a BVH tree."""

    def __init__(self, meshes: list[_Mesh], positions: list[np.ndarray], extent: float, ignored: frozenset):
        """Faces whose material_key is in ``ignored`` cover nothing."""
        points = []
        triangles = []
        self.blocks = []
        # Per triangle: None where all of it hides, else its opacity image,
        # corners and UVs for reading the image where a ray hits.
        self.textures = []
        offset = 0
        for mesh, world in zip(meshes, positions):
            used = np.array([material not in ignored for material in mesh.materials], dtype=bool)
            used &= mesh.blocks != _BLOCKS_NOTHING
            chosen = np.flatnonzero(used)
            points.append(world)
            triangles.append(mesh.triangles[chosen] + offset)
            self.blocks += mesh.blocks[chosen].tolist()
            for triangle in chosen.tolist():
                index = mesh.opacity_index[triangle]
                if index < 0:
                    self.textures.append(None)
                    continue
                opacity, uvs = mesh.opacities[index]
                corners = [Vector(point) for point in world[mesh.triangles[triangle]].tolist()]
                uv = [Vector((u, v, 0.0)) for u, v in uvs[triangle].tolist()]
                self.textures.append((opacity, *corners, *uv))
            offset += len(world)
        co = np.concatenate(points) if points else np.zeros((0, 3))
        faces = np.concatenate(triangles) if triangles else np.zeros((0, 3), dtype=np.int64)
        self.tree = BVHTree.FromPolygons(co.tolist(), faces.tolist(), all_triangles=True)
        self.step = max(extent * _STEP, 1e-7)
        self.tolerance = max(extent * _TOLERANCE, self.step * 2)

    def escapes(self, point: Vector, direction: Vector) -> bool:
        """Whether nothing covers ``point`` from a viewer far away along ``direction``."""
        ray_cast = self.tree.ray_cast
        blocks = self.blocks
        textures = self.textures
        step = direction * self.step
        origin = point + step
        travelled = self.step
        for _hop in range(_MAX_HOPS):
            location, normal, index, distance = ray_cast(origin, direction)
            if location is None:
                return True
            travelled += distance
            # The viewer looks back along the ray, so it sees the front of a
            # surface whose normal points along the ray.
            if travelled > self.tolerance and (blocks[index] == _BLOCKS_BOTH or normal.dot(direction) > 0.0):
                texture = textures[index]
                if texture is None:
                    return False
                uv = barycentric_transform(location, *texture[1:])
                if texture[0].hides(uv.x, uv.y):
                    return False
            origin = location + step
            travelled += self.step
        return True

    def covered(self, point: np.ndarray, normal: np.ndarray, cone: np.ndarray, two_sided: bool) -> bool:
        """Whether every ray of ``cone``, turned to point along ``normal`` (and
        against it for a vertex drawn from both sides), is covered."""
        length = float(np.linalg.norm(normal))
        if length == 0.0:
            return False
        normal = normal / length
        side = np.cross(normal, (1.0, 0.0, 0.0) if abs(normal[0]) < 0.9 else (0.0, 1.0, 0.0))
        side /= np.linalg.norm(side)
        directions = cone @ np.stack((side, np.cross(normal, side), normal))
        if two_sided:
            directions = np.concatenate((directions, -directions))
        origin = Vector(point.tolist())
        return not any(self.escapes(origin, Vector(direction)) for direction in directions.tolist())


class _Target:
    """One mesh whose covered vertices may go, with its vertices welded at seams."""

    def __init__(self, mesh: _Mesh):
        self.mesh = mesh
        points, first, weld = np.unique(mesh.basis, axis=0, return_index=True, return_inverse=True)
        self.weld = weld.ravel()
        # One vertex per welded position, and the welded normal and sides there.
        self.first = first
        self.normals = np.zeros((len(points), 3))
        np.add.at(self.normals, self.weld, mesh.normals)
        self.two_sided = np.zeros(len(points), dtype=bool)
        np.logical_or.at(self.two_sided, self.weld, mesh.two_sided)
        self.uncovered = np.zeros(len(points), dtype=bool)

    def hidden_vertices(self, margin: int) -> np.ndarray:
        """Covered vertices with faces, none of whose faces has an uncovered corner,
        more than ``margin`` edges from those faces."""
        mesh = self.mesh
        if not len(mesh.basis) or not len(mesh.polygon_starts):
            return np.zeros(0, dtype=np.int64)
        weld = self.weld
        kept = self.uncovered.copy()
        corners = weld[mesh.loop_vertices]
        loop_polygons = np.repeat(np.arange(len(mesh.polygon_starts)), mesh.polygon_sizes)
        touched = np.zeros(len(mesh.polygon_starts), dtype=bool)
        np.logical_or.at(touched, loop_polygons, kept[corners])
        kept[corners[touched[loop_polygons]]] = True
        first, second = weld[mesh.edges[:, 0]], weld[mesh.edges[:, 1]]
        for _ring in range(margin):
            grown = kept.copy()
            grown[first[kept[second]]] = True
            grown[second[kept[first]]] = True
            if np.array_equal(grown, kept):
                break
            kept = grown
        has_face = np.zeros(len(mesh.basis), dtype=bool)
        has_face[mesh.loop_vertices] = True
        return np.flatnonzero(has_face & ~kept[weld])


def _state_positions(mesh: _Mesh, state: str | None) -> np.ndarray:
    local = mesh.states.get(state, mesh.basis) if state else mesh.basis
    return mesh.world(local)


def _state_moves(meshes: list[_Mesh], state: str) -> bool:
    return any(state in mesh.states and not np.array_equal(mesh.states[state], mesh.basis) for mesh in meshes)


def hidden_vertices(targets, occluders=None, *, margin: int = 1, cone_degrees: float = CONE_DEGREES,
                    progress=None) -> dict:
    """Map each target's mesh to the indices of its vertices that other meshes cover.

    ``occluders`` are the meshes that can cover the targets' vertices besides
    the targets themselves, and faces with a target's own material never
    cover it; None means each target is tested against its own faces alone. A
    target only counts meshes of its own LOD among the occluders, and meshes
    without an Instant Edit name as every LOD (and as LOD 0 when they are the
    targets). Covered vertices up to ``margin`` edges from an uncovered one
    stay. ``progress`` is called with the fraction done.

    The result has one entry per mesh datablock, keyed by one of the objects
    using it: a vertex of a mesh several objects show must be covered in each.
    """
    targets = list(dict.fromkeys(obj for obj in targets if obj.type == "MESH"))
    if occluders is not None:
        occluders = [obj for obj in dict.fromkeys(occluders) if obj.type == "MESH"]
        shared = {obj.data.as_pointer() for obj in targets}
        targets += [obj for obj in occluders if obj.data.as_pointer() in shared and obj not in targets]
    read = {}
    covers = {}

    def mesh_of(obj) -> _Mesh:
        if obj not in read:
            read[obj] = _Mesh.read(obj, covers)
        return read[obj]

    # Each test: the targets, the meshes that may cover them, and the
    # materials that cover nothing in it.
    tests = []
    if occluders is None:
        tests = [([obj], [obj], frozenset()) for obj in targets]
    else:
        by_scene: dict = {}
        for obj in targets:
            lod = mesh_lod(obj) or 0
            by_scene.setdefault((lod, mesh_of(obj).own_materials()), []).append(obj)
        for (lod, materials), group in by_scene.items():
            others = [obj for obj in occluders if obj not in group and mesh_lod(obj) in {lod, None}]
            tests.append((group, group + others, materials))

    plan = []
    for group, members, ignored in tests:
        meshes = [mesh_of(obj) for obj in members]
        states = [None] + sorted({name for mesh in meshes for name in mesh.states if _state_moves(meshes, name)})
        plan.append((group, meshes, ignored, states))
    search_targets = {obj: _Target(read[obj]) for group, *_rest in plan for obj in group}
    total = sum(len(search_targets[obj].first) * len(states) for group, _m, _i, states in plan for obj in group) or 1
    done = 0
    cone = _cone(cone_degrees, _RINGS)

    for group, meshes, ignored, states in plan:
        corners = np.concatenate([mesh.world(mesh.basis) for mesh in meshes if len(mesh.basis)] or [np.zeros((1, 3))])
        extent = float(np.linalg.norm(corners.max(axis=0) - corners.min(axis=0)))
        index = {id(mesh): position for position, mesh in enumerate(meshes)}
        for state in states:
            positions = [_state_positions(mesh, state) for mesh in meshes]
            scene = _Scene(meshes, positions, extent, ignored)
            for obj in group:
                target = search_targets[obj]
                world = positions[index[id(target.mesh)]]
                for count, position in enumerate(np.flatnonzero(~target.uncovered).tolist()):
                    if not scene.covered(world[target.first[position]], target.normals[position], cone,
                                         target.two_sided[position]):
                        target.uncovered[position] = True
                    if progress is not None and count % 256 == 255:
                        progress(min((done + count) / total, 1.0))
                done += len(target.first)

    result = {}
    for obj, target in search_targets.items():
        owner, hidden = result.get(obj.data.as_pointer(), (obj, None))
        found = target.hidden_vertices(margin)
        result[obj.data.as_pointer()] = (owner, found if hidden is None else np.intersect1d(hidden, found))
    if progress is not None:
        progress(1.0)
    return dict(result.values())


def remove_vertices(obj, indices) -> int:
    """Delete the vertices ``indices`` of ``obj`` and the faces that use them."""
    indices = sorted(set(int(index) for index in indices))
    if not indices:
        return 0
    mesh = obj.data
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[bm.verts[index] for index in indices], context="VERTS")
        bm.to_mesh(mesh)
    finally:
        bm.free()
    mesh.update()
    return len(indices)
