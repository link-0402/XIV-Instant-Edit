"""Find the vertices that no camera angle can see, and delete them.

A face counts as seen when a ray from one of its sample points escapes to
infinity. The samples are the face's corners and its centre, and the rays go
towards directions spread evenly over the sphere, each sample with its own
rotation of that set so neighbouring samples cover the gaps between each
other's directions. The rules follow how the game draws a model:

- A face whose material culls backfaces is seen only from its front, and a ray
  passes through it from behind. Faces without a material count as visible
  from both sides but never hide anything from behind.
- A see-through face (alpha from a texture or below 1, blended, or a mesh
  marked for transparency sorting) hides nothing.
- Surfaces closer to a sample than a small tolerance do not hide it, so a mesh
  lying on a copy of itself (a mannequin, a Backfaces duplicate) survives.
- The test uses each mesh's own vertices in the rest pose, without modifiers.
  Each shp_ shape key is another state of the scene, with the key applied to
  every mesh that has it; a face seen in any state is kept.

A vertex is deleted when no face that uses it is seen, which also deletes
those faces. A hidden face whose corners all belong to seen faces stays, so
the edge of a covered area never opens a gap, and a margin of hidden vertices
around the seen ones stays too: a band just under the edge of a covering mesh
shows only at grazing angles the sampled directions can miss, and animations
move the covering mesh against what it covers.
"""

from __future__ import annotations

from dataclasses import dataclass

import bmesh
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

from ..instant_edit.context import mesh_ids_from_name


DIRECTIONS = 256
_ROTATIONS = 16
# Directions closer than about 3 degrees to a face's plane do not count: a
# face seen that edge-on covers no pixels.
_MIN_DOT = 0.05
# A face's centre is tested within 60 degrees of its normal, which is where a
# hole in whatever covers it shows its middle.
_CENTRE_MIN_DOT = 0.5
# A ray that passes through this many culled backfaces counts as escaped.
_MAX_HOPS = 64
# Relative to the size of the scene: the step past a surface a ray continues
# from, and the distance within which a surface does not hide a sample.
_STEP = 1e-6
_TOLERANCE = 1e-4
# What a face hides: nothing, what lies behind its front, or both sides.
_BLOCKS_NOTHING, _BLOCKS_FRONT, _BLOCKS_BOTH = 0, 1, 2


def _sphere_directions(count: int) -> np.ndarray:
    """``count`` unit vectors spread evenly over the sphere (a Fibonacci lattice)."""
    index = np.arange(count) + 0.5
    z = 1.0 - 2.0 * index / count
    radius = np.sqrt(1.0 - z * z)
    angle = np.pi * (1.0 + 5.0 ** 0.5) * index
    return np.stack((radius * np.cos(angle), radius * np.sin(angle), z), axis=1)


def _rotations(count: int) -> list[np.ndarray]:
    """Fixed random rotations, so the result does not change between runs."""
    rng = np.random.default_rng(0x5EE)
    matrices = []
    for _ in range(count):
        w, x, y, z = rng.normal(size=4)
        norm = (w * w + x * x + y * y + z * z) ** 0.5
        w, x, y, z = w / norm, x / norm, y / norm, z / norm
        matrices.append(np.array((
            (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
        )))
    return matrices


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


def see_through(material) -> bool:
    """Whether a material lets what lies behind it show."""
    if material is None:
        return False
    if getattr(material, "surface_render_method", "") == "BLENDED":
        return True
    tree = material.node_tree
    for node in tree.nodes if tree is not None else ():
        alpha = node.inputs.get("Alpha") if node.type == "BSDF_PRINCIPLED" else None
        if alpha is not None and (alpha.is_linked or alpha.default_value < 1.0):
            return True
    return False


def mesh_lod(obj) -> int | None:
    """The LOD an Instant Edit part name gives, or None for other meshes."""
    try:
        return mesh_ids_from_name(obj)[2]
    except Exception:
        return None


@dataclass
class _Mesh:
    """One object's triangles and positions in world space."""

    obj: object
    matrix: np.ndarray
    basis: np.ndarray
    states: dict[str, np.ndarray]
    triangles: np.ndarray
    triangle_polygons: np.ndarray
    # Each polygon's vertices: polygon_vertices[polygon_starts[i]:][:polygon_sizes[i]].
    polygon_vertices: np.ndarray
    polygon_starts: np.ndarray
    polygon_sizes: np.ndarray
    edges: np.ndarray
    # Per triangle: seen from the front only, and what it hides (_BLOCKS_*).
    culled: np.ndarray
    blocks: np.ndarray

    @classmethod
    def read(cls, obj) -> "_Mesh":
        mesh = obj.data
        mesh.calc_loop_triangles()
        count = len(mesh.vertices)
        basis = np.empty(count * 3, dtype=np.float64)
        mesh.vertices.foreach_get("co", basis)
        triangles = np.empty(len(mesh.loop_triangles) * 3, dtype=np.int64)
        mesh.loop_triangles.foreach_get("vertices", triangles)
        triangles = triangles.reshape(-1, 3)
        polygons = np.empty(len(mesh.loop_triangles), dtype=np.int64)
        mesh.loop_triangles.foreach_get("polygon_index", polygons)
        polygon_vertices = np.empty(len(mesh.loops), dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", polygon_vertices)
        polygon_starts = np.empty(len(mesh.polygons), dtype=np.int64)
        mesh.polygons.foreach_get("loop_start", polygon_starts)
        polygon_sizes = np.empty(len(mesh.polygons), dtype=np.int64)
        mesh.polygons.foreach_get("loop_total", polygon_sizes)
        edges = np.empty(len(mesh.edges) * 2, dtype=np.int64)
        mesh.edges.foreach_get("vertices", edges)
        matrix = np.array(obj.matrix_world, dtype=np.float64)
        if np.linalg.det(matrix[:3, :3]) < 0:
            # Blender draws a mirrored object with its faces turned back
            # around; do the same so the world-space normals point outwards.
            triangles = triangles[:, (0, 2, 1)]
        # A material index past the last slot draws with no material.
        slots = [slot.material for slot in obj.material_slots] + [None]
        transparent = bool(obj.get("xiv_transparency", False))
        slot_culled = np.array([material is not None and material.use_backface_culling for material in slots])
        slot_blocks = np.array([
            _BLOCKS_NOTHING if transparent or see_through(material)
            else _BLOCKS_FRONT if material is None or material.use_backface_culling
            else _BLOCKS_BOTH
            for material in slots
        ], dtype=np.int8)
        material_index = np.zeros(len(mesh.polygons), dtype=np.int64)
        mesh.polygons.foreach_get("material_index", material_index)
        material_index = np.minimum(material_index[polygons], len(slots) - 1)
        return cls(obj, matrix, basis.reshape(-1, 3), shape_key_states(obj), triangles, polygons,
                   polygon_vertices, polygon_starts, polygon_sizes, edges.reshape(-1, 2),
                   slot_culled[material_index], slot_blocks[material_index])

    def world(self, local: np.ndarray) -> np.ndarray:
        return local @ self.matrix[:3, :3].T + self.matrix[:3, 3]


def _rows(keys: np.ndarray, values: np.ndarray, count: int) -> tuple[np.ndarray, np.ndarray]:
    """Group ``values`` by ``keys`` in 0..count-1: row i is values[starts[i]:starts[i + 1]]."""
    order = np.argsort(keys, kind="stable")
    return np.searchsorted(keys[order], np.arange(count + 1)), values[order]


class _Target:
    """What the search knows about one mesh whose vertices may go.

    A polygon is open while it is not seen and one of its vertices is not kept
    yet: only then can a test of it still change which vertices go.
    """

    def __init__(self, mesh: _Mesh):
        self.mesh = mesh
        vertex_count = len(mesh.basis)
        polygon_count = len(mesh.polygon_starts)
        self.polygon_seen = np.zeros(polygon_count, dtype=bool)
        self.vertex_kept = np.zeros(vertex_count, dtype=bool)
        # How many of each polygon's vertices are not kept yet.
        self.polygon_unkept = mesh.polygon_sizes.copy()
        loop_polygons = np.zeros(len(mesh.polygon_vertices), dtype=np.int64)
        for polygon, (start, size) in enumerate(zip(mesh.polygon_starts.tolist(), mesh.polygon_sizes.tolist())):
            loop_polygons[start:start + size] = polygon
        self.vertex_polygon_starts, self.vertex_polygons = _rows(mesh.polygon_vertices, loop_polygons, vertex_count)
        corners = mesh.triangles.ravel()
        self.vertex_triangle_starts, self.vertex_triangles = _rows(
            corners, np.arange(len(corners)) // 3, vertex_count)

    def polygon_vertices(self, polygon: int) -> np.ndarray:
        mesh = self.mesh
        start = mesh.polygon_starts[polygon]
        return mesh.polygon_vertices[start:start + mesh.polygon_sizes[polygon]]

    def mark_seen(self, triangles) -> None:
        polygons = np.unique(self.mesh.triangle_polygons[triangles])
        new = polygons[~self.polygon_seen[polygons]]
        if not len(new):
            return
        self.polygon_seen[new] = True
        vertices = np.unique(np.concatenate([self.polygon_vertices(polygon) for polygon in new.tolist()]))
        vertices = vertices[~self.vertex_kept[vertices]]
        self.vertex_kept[vertices] = True
        starts = self.vertex_polygon_starts
        rows = [self.vertex_polygons[starts[vertex]:starts[vertex + 1]] for vertex in vertices.tolist()]
        if rows:
            np.subtract.at(self.polygon_unkept, np.concatenate(rows), 1)

    def triangle_open(self, triangles: np.ndarray) -> np.ndarray:
        polygons = self.mesh.triangle_polygons[triangles]
        return ~self.polygon_seen[polygons] & (self.polygon_unkept[polygons] > 0)

    def open_triangles(self) -> np.ndarray:
        return np.flatnonzero(self.triangle_open(np.arange(len(self.mesh.triangles))))

    def triangles_at(self, vertex: int) -> np.ndarray:
        starts = self.vertex_triangle_starts
        return self.vertex_triangles[starts[vertex]:starts[vertex + 1]]

    def hidden_vertices(self, margin: int) -> np.ndarray:
        """Vertices with faces, none of them seen, more than ``margin`` edges from a kept vertex.

        Vertices split at a seam count as one vertex, so they are kept together.
        """
        mesh = self.mesh
        if not len(mesh.basis):
            return np.zeros(0, dtype=np.int64)
        _points, weld = np.unique(mesh.basis, axis=0, return_inverse=True)
        weld = weld.ravel()
        kept = np.zeros(weld.max() + 1, dtype=bool)
        kept[weld[self.vertex_kept]] = True
        first, second = weld[mesh.edges[:, 0]], weld[mesh.edges[:, 1]]
        for _ring in range(margin):
            grown = kept.copy()
            grown[first[kept[second]]] = True
            grown[second[kept[first]]] = True
            if np.array_equal(grown, kept):
                break
            kept = grown
        has_face = np.diff(self.vertex_polygon_starts) > 0
        return np.flatnonzero(has_face & ~kept[weld])


class _Scene:
    """Every occluding triangle of one scene state in a BVH tree."""

    def __init__(self, meshes: list[_Mesh], positions: list[np.ndarray], extent: float):
        offsets = np.cumsum([0] + [len(co) for co in positions])
        co =np.concatenate(positions) if positions else np.zeros((0, 3))
        triangles = np.concatenate([mesh.triangles + offset for mesh, offset in zip(meshes, offsets)]) \
            if meshes else np.zeros((0, 3), dtype=np.int64)
        self.tree = BVHTree.FromPolygons(co.tolist(), triangles.tolist(), all_triangles=True)
        self.blocks = np.concatenate([mesh.blocks for mesh in meshes]).tolist() if meshes else []
        self.step = max(extent * _STEP, 1e-7)
        self.tolerance = max(extent * _TOLERANCE, self.step * 2)


class _Search:
    """Casts the rays for the targets of one scene state."""

    def __init__(self, scene: _Scene, directions: int):
        self.scene = scene
        base = _sphere_directions(directions)
        self.sets = [base @ rotation.T for rotation in _rotations(_ROTATIONS)]
        self.vectors = [[Vector(d) for d in directions.tolist()] for directions in self.sets]
        self.steps = [[d * scene.step for d in vectors] for vectors in self.vectors]
        self.samples = 0

    def escapes(self, point: Vector, direction: Vector, step: Vector) -> bool:
        """Whether nothing hides ``point`` from a viewer far away along ``direction``."""
        scene = self.scene
        ray_cast = scene.tree.ray_cast
        blocks = scene.blocks
        tolerance = scene.tolerance
        origin = point + step
        travelled = scene.step
        for _hop in range(_MAX_HOPS):
            location, normal, index, distance = ray_cast(origin, direction)
            if location is None:
                return True
            travelled += distance
            # The viewer looks back along the ray, so it sees the front of a
            # surface whose normal points along the ray.
            kind = blocks[index]
            if travelled > tolerance and kind and (kind == _BLOCKS_BOTH or normal.dot(direction) > 0.0):
                return False
            origin = location + step
            travelled += scene.step
        return True

    def test(self, target: _Target, point: np.ndarray, triangles: np.ndarray, normals: np.ndarray,
             min_dot: float = _MIN_DOT) -> None:
        """Mark the ``triangles`` that ``point``, a point on each of them, shows to some viewer."""
        rotation = self.samples % _ROTATIONS
        self.samples += 1
        mesh = target.mesh
        dots = self.sets[rotation] @ normals[triangles].T
        both_sides = ~mesh.culled[triangles]
        allowed = (dots > min_dot) | (both_sides & (dots < -min_dot))
        wanted = allowed.any(axis=1)
        if not wanted.any():
            return
        # Try the directions the faces point to first; most seen faces show on the first ray.
        score = np.where(both_sides, np.abs(dots), dots).max(axis=1)
        order = np.flatnonzero(wanted)
        order = order[np.argsort(-score[order], kind="stable")].tolist()
        pending = np.ones(len(triangles), dtype=bool)
        vectors = self.vectors[rotation]
        steps = self.steps[rotation]
        origin = Vector(point.tolist())
        position = 0
        while position < len(order):
            row = order[position]
            position += 1
            if not self.escapes(origin, vectors[row], steps[row]):
                continue
            target.mark_seen(triangles[pending & allowed[row]])
            # Faces whose vertices are all kept by now no longer matter either.
            pending &= ~allowed[row] & target.triangle_open(triangles)
            if not pending.any():
                return
            wanted = allowed[:, pending].any(axis=1)
            order = [later for later in order[position:] if wanted[later]]
            position = 0


def _state_positions(mesh: _Mesh, state: str | None) -> np.ndarray:
    local = mesh.states.get(state, mesh.basis) if state else mesh.basis
    return mesh.world(local)


def _search_state(targets: list[_Target], meshes: list[_Mesh], state: str | None, extent: float,
                  directions: int, progress) -> None:
    positions = [_state_positions(mesh, state) for mesh in meshes]
    scene = _Scene(meshes, positions, extent)
    search = _Search(scene, directions)
    index = {id(mesh): position for position, mesh in enumerate(meshes)}
    for target in targets:
        mesh = target.mesh
        co = positions[index[id(mesh)]]
        corners = co[mesh.triangles]
        normals = np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0])
        lengths = np.linalg.norm(normals, axis=1)
        normals = np.divide(normals, lengths[:, None], out=np.zeros_like(normals), where=lengths[:, None] > 0)

        open_triangles = target.open_triangles()
        if not len(open_triangles):
            continue
        # Corner samples: one per position, shared by every face that meets there,
        # including faces of vertices the import split at a seam.
        open_vertices = np.unique(mesh.triangles[open_triangles])
        points, group = np.unique(co[open_vertices], axis=0, return_inverse=True)
        group = group.ravel()
        members = [[] for _ in range(len(points))]
        for vertex, position in zip(open_vertices.tolist(), group.tolist()):
            members[position].append(vertex)
        for position, vertices in enumerate(members):
            progress()
            triangles = np.unique(np.concatenate([target.triangles_at(vertex) for vertex in vertices]))
            triangles = triangles[target.triangle_open(triangles)]
            if len(triangles):
                search.test(target, points[position], triangles, normals)

        # Centre samples catch a face whose middle shows between covered corners.
        for triangle in target.open_triangles().tolist():
            progress()
            triangle = np.array((triangle,))
            if target.triangle_open(triangle)[0]:
                search.test(target, corners[triangle[0]].mean(axis=0), triangle, normals, _CENTRE_MIN_DOT)


def hidden_vertices(targets, occluders=None, *, margin: int = 2, directions: int = DIRECTIONS,
                    progress=None) -> dict:
    """Map each target's mesh to the indices of its vertices no viewer can see.

    ``occluders`` are the meshes that can hide the targets' vertices besides
    the targets themselves; None means each target is tested on its own. A
    target only counts meshes of its own LOD among the occluders, and meshes
    without an Instant Edit name as every LOD (and as LOD 0 when they are the
    targets). Hidden vertices up to
    ``margin`` edges from a seen one stay. ``progress`` is called with the
    fraction done.

    The result has one entry per mesh datablock, keyed by one of the objects
    using it: a vertex of a mesh several objects show must be hidden in each.
    """
    targets = list(dict.fromkeys(obj for obj in targets if obj.type == "MESH"))
    if occluders is not None:
        occluders = [obj for obj in dict.fromkeys(occluders) if obj.type == "MESH"]
        shared = {obj.data.as_pointer() for obj in targets}
        targets += [obj for obj in occluders if obj.data.as_pointer() in shared and obj not in targets]
    read = {}

    def mesh_of(obj) -> _Mesh:
        if obj not in read:
            read[obj] = _Mesh.read(obj)
        return read[obj]

    scenes = []
    if occluders is None:
        scenes = [([obj], [obj]) for obj in targets]
    else:
        by_lod: dict = {}
        for obj in targets:
            by_lod.setdefault(mesh_lod(obj) or 0, []).append(obj)
        for lod, group in by_lod.items():
            others = [obj for obj in occluders if obj not in group and mesh_lod(obj) in {lod, None}]
            scenes.append((group, group + others))

    plan = []
    for group, members in scenes:
        meshes = [mesh_of(obj) for obj in members]
        states = sorted({name for mesh in meshes for name in mesh.states})
        plan.append((group, meshes, [None] + states))
    total = sum(
        (len(read[obj].basis) + len(read[obj].triangles)) * len(states)
        for group, _meshes, states in plan for obj in group
    ) or 1
    done = 0

    def tick():
        nonlocal done
        done += 1
        if progress is not None and done % 512 == 0:
            progress(min(done / total, 1.0))

    result = {}
    for group, meshes, states in plan:
        corners = np.concatenate([mesh.world(mesh.basis) for mesh in meshes if len(mesh.basis)] or [np.zeros((1, 3))])
        extent = float(np.linalg.norm(corners.max(axis=0) - corners.min(axis=0)))
        search_targets = [_Target(read[obj]) for obj in group]
        for state in states:
            live = [target for target in search_targets if len(target.open_triangles())]
            if not live:
                break
            if state is not None and not _state_moves(meshes, state):
                continue
            _search_state(live, meshes, state, extent, directions, tick)
        for target in search_targets:
            obj = target.mesh.obj
            owner, hidden = result.get(obj.data.as_pointer(), (obj, None))
            found = target.hidden_vertices(margin)
            result[obj.data.as_pointer()] = (owner, found if hidden is None else np.intersect1d(hidden, found))
    if progress is not None:
        progress(1.0)
    return dict(result.values())


def _state_moves(meshes: list[_Mesh], state: str) -> bool:
    return any(state in mesh.states and not np.array_equal(mesh.states[state], mesh.basis) for mesh in meshes)


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
