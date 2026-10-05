"""Reshape meshes made for one race into another's shape, the way the game's racial deformer does.

Each vertex moves by its bones' deformer matrices blended by its weights, as the game's vertex
shader blends them: the base shape and every shape key, and custom normals turn with it. The
matrices are in game space (Y up), so the mesh is taken there through its world transform, which
is also what the exporter writes. A scaling applied earlier can be taken off in the same pass:
each vertex's blended matrix is inverted exactly, so another race's scaling replaces it.
"""

import numpy as np

from bpy.types import Object

# Blender (x, y, z) is game (x, z, -y); see io/model/com/space.py.
BLENDER_TO_GAME = np.array([
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, -1.0, 0.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
])
_IDENTITY = np.eye(4)


def _skin(obj: Object) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """(vertex, group, weight) of every positive weight; every vertex group counts as a bone, as on export."""
    vertices, groups, weights = [], [], []
    for vertex in obj.data.vertices:
        for element in vertex.groups:
            if element.weight > 1e-6:
                vertices.append(vertex.index)
                groups.append(element.group)
                weights.append(element.weight)
    return np.array(vertices, dtype=np.int64), np.array(groups, dtype=np.int64), np.array(weights, dtype=np.float64)


def _blend(obj: Object, skin, matrices: dict[str, np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
    """Each vertex's blended 4×4 deformer matrix in game space, and which vertices it moves.

    A bone the deformer doesn't list stays put; vertices without weight keep the identity."""
    vertices, groups, weights = skin
    count = len(obj.data.vertices)
    table = np.repeat(_IDENTITY[np.newaxis, :3], max(len(obj.vertex_groups), 1), axis=0)
    listed = np.zeros(len(table), dtype=bool)
    for group in obj.vertex_groups:
        matrix = matrices.get(group.name)
        if matrix is not None:
            table[group.index] = matrix
            listed[group.index] = True
    known = groups < len(table)
    vertices, groups, weights = vertices[known], groups[known], weights[known]
    blended = np.zeros((count, 3, 4))
    np.add.at(blended, vertices, weights[:, np.newaxis, np.newaxis] * table[groups])
    total = np.zeros(count)
    np.add.at(total, vertices, weights)
    moved = np.zeros(count, dtype=bool)
    moved[vertices[listed[groups]]] = True
    weighted = total > 1e-6
    blended[weighted] /= total[weighted, np.newaxis, np.newaxis]
    blended[~weighted] = _IDENTITY[:3]
    full = np.repeat(_IDENTITY[np.newaxis], count, axis=0)
    full[:, :3] = blended
    return full, moved & weighted


def apply_deformer(obj: Object, matrices: dict[str, np.ndarray], previous: dict[str, np.ndarray] | None = None) -> int:
    """Move a mesh's vertices, shape keys and custom normals through a racial deformer, after
    taking off the ``previous`` one (the scaling the mesh has now) when given.

    Returns how many vertices change (0 leaves the mesh untouched)."""
    mesh = obj.data
    skin = _skin(obj)
    blended, moved = _blend(obj, skin, matrices)
    if previous:
        undo, undone = _blend(obj, skin, previous)
        blended = blended @ np.linalg.inv(undo)
        moved |= undone
    if not moved.any():
        return 0
    to_game = BLENDER_TO_GAME @ np.array(obj.matrix_world, dtype=np.float64)
    from_game = np.linalg.inv(to_game)
    linear = blended[:, :3, :3]

    def move(co: np.ndarray) -> np.ndarray:
        game = co @ to_game[:3, :3].T + to_game[:3, 3]
        game = np.einsum("vij,vj->vi", linear, game) + blended[:, :3, 3]
        return game @ from_game[:3, :3].T + from_game[:3, 3]

    normals = None
    if mesh.has_custom_normals:
        loop_count = len(mesh.loops)
        normals = np.empty(loop_count * 3, dtype=np.float32)
        mesh.corner_normals.foreach_get("vector", normals)
        loop_vertices = np.empty(loop_count, dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", loop_vertices)
        # Directions go through the inverse transpose of the transform, and back through its transpose.
        normal_to_game = np.linalg.inv(to_game[:3, :3]).T
        game = normals.reshape(-1, 3).astype(np.float64) @ normal_to_game.T
        game = np.einsum("lij,lj->li", linear[loop_vertices], game)
        normals = game @ to_game[:3, :3]
        lengths = np.linalg.norm(normals, axis=1, keepdims=True)
        normals = np.divide(normals, lengths, out=np.zeros_like(normals), where=lengths > 1e-9)

    count = len(mesh.vertices)
    co = np.empty(count * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", co)
    mesh.vertices.foreach_set("co", move(co.reshape(-1, 3).astype(np.float64)).astype(np.float32).ravel())
    if mesh.shape_keys:
        for key in mesh.shape_keys.key_blocks:
            key.data.foreach_get("co", co)
            key.data.foreach_set("co", move(co.reshape(-1, 3).astype(np.float64)).astype(np.float32).ravel())
    mesh.update()
    if normals is not None:
        mesh.normals_split_custom_set(normals.astype(np.float32))
        mesh.update()
    return int(np.count_nonzero(moved))
