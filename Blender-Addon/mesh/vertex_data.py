"""One-off vertex data fixes, the counterpart of TexTools' Modify Model Vertices dialog.

Each fix edits the Blender mesh so the MDL exporter writes what TexTools writes
for it, and the result shows in Blender right away:

- UV2 is the second export UV map (see get_xiv_uv_layers). The MDL stores V
  flipped (1 - v), so the file's (0, 0) is Blender's (0, 1).
- Vertex colors 1 and 2 are the first two color attributes named vc*, sorted by
  name, as get_col_attributes reads them. Clearing a color keeps its alpha.
- Flow is the xiv_flow color attribute; its centre color (0.5, 0.5) exports as
  the zero vector, which vanilla models use for vertices without a flow.
"""

import re

import numpy as np

from ..io.model.com.exceptions import XIVMeshError
from ..io.model.com.helpers import ZERO_FLOW_COLOUR
from ..io.model.exp.accessors import get_xiv_uv_layers
from ..xivpy.model import XIV_COL


ACTIONS = (
    ("CLEAR_UV2", "Clear UV2",
     "Set UV2 to zero. Most gear leaves UV2 empty unless it places a decal such as a crest"),
    ("COPY_UV1_TO_UV2", "Copy UV1 to UV2",
     "Copy UV1 into UV2, adding a UV2 map where there is none. Hair reads its opacity through UV2"),
    ("CLEAR_COLOR1", "Clear Vertex Color 1",
     "Set vertex color 1 to white and keep its alpha. Skin and hair use it as mask data; "
     "clearing theirs can cause seams or odd shadows"),
    ("CLEAR_ALPHA1", "Clear Vertex Alpha 1",
     "Set the alpha of vertex color 1 to opaque"),
    ("CLEAR_COLOR2", "Clear Vertex Color 2",
     "Set vertex color 2 to black and keep its alpha. Its red channel is fake wind"),
    ("CLEAR_FLOW", "Clear Flow Data",
     "Remove the hair strand directions, which gives a generic, sharper highlight"),
)
_NAMES = {identifier: name for identifier, name, _description in ACTIONS}
_NUMBERED_UV = re.compile(r"uv[0-2]")


def _mesh_owners(objects) -> list:
    """One object per mesh: linked duplicates share their data."""
    owners = {}
    for obj in objects:
        if obj.type == "MESH" and obj.data is not None:
            owners.setdefault(obj.data.as_pointer(), obj)
    return list(owners.values())


def _uv_layers(obj) -> list:
    try:
        return get_xiv_uv_layers(obj)
    except XIVMeshError as error:
        raise ValueError(str(error)) from error


def _loop_uvs(layer, loop_count: int) -> np.ndarray:
    values = np.empty(loop_count * 2, dtype=np.float32)
    layer.uv.foreach_get("vector", values)
    return values


def _add_uv2(obj, uv1):
    """Add the second export UV map, named so the exporter reads it as UV2."""
    uv_layers = obj.data.uv_layers
    if _NUMBERED_UV.fullmatch(uv1.name.lower()):
        name = "uv1"
    else:
        # Unnumbered maps export in Blender's order; keep this one unnumbered too.
        name = f"{uv1.name} UV2"[:63]
    active = uv_layers.active_index
    layer = uv_layers.new(name=name, do_init=False)
    if layer is None:
        raise ValueError(f"{obj.name} already has the most UV maps Blender allows.")
    uv_layers.active_index = active
    return layer


def _colour_layers(obj) -> list:
    return sorted(
        (layer for layer in obj.data.color_attributes if layer.name.lower().startswith(XIV_COL)),
        key=lambda layer: layer.name.lower(),
    )


def _edit_colour(layer, edit) -> None:
    # Byte colors read as linear; their stored sRGB values round-trip exactly.
    attribute = "color_srgb" if layer.data_type == "BYTE_COLOR" else "color"
    values = np.empty(len(layer.data) * 4, dtype=np.float32)
    layer.data.foreach_get(attribute, values)
    values = values.reshape(-1, 4)
    edit(values)
    layer.data.foreach_set(attribute, values.ravel())


def _apply(action: str, obj) -> bool:
    """Apply one fix to one mesh; return whether it had that data to change."""
    mesh = obj.data
    if action in {"CLEAR_UV2", "COPY_UV1_TO_UV2"}:
        layers = _uv_layers(obj)
        loop_count = len(mesh.loops)
        if action == "CLEAR_UV2":
            if len(layers) < 2:
                return False
            zero = np.tile(np.array((0.0, 1.0), dtype=np.float32), loop_count)
            layers[1].uv.foreach_set("vector", zero)
            return True
        uv1 = _loop_uvs(layers[0], loop_count)
        uv2 = layers[1] if len(layers) > 1 else _add_uv2(obj, layers[0])
        uv2.uv.foreach_set("vector", uv1)
        return True

    if action == "CLEAR_FLOW":
        layer = mesh.color_attributes.get("xiv_flow")
        if layer is None:
            return False
        # Read and written as "color", the way the exporter reads it.
        values = np.empty(len(layer.data) * 4, dtype=np.float32)
        layer.data.foreach_get("color", values)
        values = values.reshape(-1, 4)
        values[:, :2] = ZERO_FLOW_COLOUR
        layer.data.foreach_set("color", values.ravel())
        return True

    layers = _colour_layers(obj)
    index = 1 if action == "CLEAR_COLOR2" else 0
    if len(layers) <= index:
        # The exporter already writes white (color 1) or nothing (color 2) then.
        return False

    def edit(values):
        if action == "CLEAR_COLOR1":
            values[:, :3] = 1.0
        elif action == "CLEAR_ALPHA1":
            values[:, 3] = 1.0
        else:
            values[:, :3] = 0.0

    _edit_colour(layers[index], edit)
    return True


def apply_vertex_data(action: str, objects) -> tuple[list, list]:
    """Apply a fix to every mesh among ``objects``; return the changed and the untouched ones."""
    if action not in _NAMES:
        raise ValueError(f"Unknown vertex data action {action!r}.")
    changed, untouched = [], []
    for obj in _mesh_owners(objects):
        (changed if _apply(action, obj) else untouched).append(obj)
    for obj in changed:
        obj.data.update()
    return changed, untouched


def action_name(action: str) -> str:
    return _NAMES.get(action, action)


def missing_data_label(action: str) -> str:
    """What a mesh lacks when a fix leaves it untouched."""
    return {
        "CLEAR_UV2": "no UV2",
        "CLEAR_COLOR1": "no vertex color 1 (exports white)",
        "CLEAR_ALPHA1": "no vertex color 1 (exports opaque)",
        "CLEAR_COLOR2": "no vertex color 2",
        "CLEAR_FLOW": "no flow data",
    }.get(action, "nothing to change")
