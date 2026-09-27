# Modified for XIV Instant Edit, 2026.
import numpy as np

from numpy           import float32, uint32
from bpy.types       import VertexGroup
from numpy.typing    import NDArray


def add_to_vgroup(weight_matrix: NDArray, v_group: VertexGroup) -> NDArray:
    indices = np.flatnonzero(weight_matrix[:, v_group.index])
    weights = weight_matrix[:, v_group.index][indices]
    if len(indices) == 0:
        return

    grouped_indices, unique_weights = group_weights(indices, weights)
    
    for array_idx, vert_indices in enumerate(grouped_indices):
        vert_indices = vert_indices.tolist()
        v_group.add(vert_indices, unique_weights[array_idx], type='ADD')

def group_weights(indices: NDArray[uint32], weights: NDArray[float32]) -> tuple[list[NDArray[uint32]], NDArray[float32]]:
    '''Groups vert indices based on unique weight values. 
    This limits the calls to the Blender API vertex_group.add() function.'''
    unique_weights, inverse_indices = np.unique(weights, return_inverse=True)

    sort_order     = np.argsort(inverse_indices)
    sorted_groups  = inverse_indices[sort_order]
    sorted_indices = indices[sort_order]

    split_points    = np.where(np.diff(sorted_groups))[0] + 1
    grouped_indices = np.split(sorted_indices, split_points)

    return grouped_indices, unique_weights
