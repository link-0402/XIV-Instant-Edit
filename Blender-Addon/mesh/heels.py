"""SimpleHeels model offsets stored as heels_offset=<value> MDL attributes.

SimpleHeels reads the first heels_offset attribute from the model's attribute
table (any submesh), then scales it by the character's height. The value is in
model units, which match Blender's world units: game Y is Blender Z.
"""

import math

import numpy as np

from bpy.types import Object

from ..instant_edit.context import mesh_ids_from_name


HEELS_OFFSET_ATTRIBUTE = "heels_offset"


def is_heels_offset_attribute(name: str) -> bool:
    """Match both SimpleHeels spellings: heels_offset=0.1 and heels_offset_a_b."""
    return str(name).strip().lower().startswith(HEELS_OFFSET_ATTRIBUTE)


def format_heels_offset(value: float) -> str:
    """Return the attribute for an offset, with 2 to 4 decimals."""
    text = f"{value:.4f}"
    while text.endswith("0") and len(text) - text.index(".") > 3:
        text = text[:-1]
    if text.startswith("-") and float(text) == 0:
        text = text[1:]
    return f"{HEELS_OFFSET_ATTRIBUTE}={text}"


def normalize_heels_offset(text: str) -> str:
    """Validate a typed heels_offset=<number> attribute and format its value."""
    name, _separator, value = str(text).partition("=")
    if name.strip().lower() != HEELS_OFFSET_ATTRIBUTE:
        raise ValueError("Heels offsets must be written as heels_offset=<number>.")
    try:
        # SimpleHeels also accepts a comma as the decimal separator.
        offset = float(value.strip().replace(",", "."))
    except ValueError:
        offset = math.nan
    if not math.isfinite(offset):
        raise ValueError("Heels offsets must be written as heels_offset=<number>, e.g. heels_offset=0.15.")
    return format_heels_offset(offset)


def _lod0_mesh_objects(objects) -> list[tuple[tuple[int, int], Object]]:
    result = []
    for obj in objects:
        if getattr(obj, "type", None) != "MESH" or len(obj.data.vertices) == 0:
            continue
        try:
            group, part, lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if lod == 0:
            result.append(((group, part), obj))
    return result


def calculate_heels_offset(objects) -> float | None:
    """Return how far LOD 0 geometry reaches below the floor, rounded to 0.0001.

    Returns None when nothing is below the floor, so flat footwear and
    non-footwear models never get an offset.
    """
    lowest = None
    for _ids, obj in _lod0_mesh_objects(objects):
        count = len(obj.data.vertices)
        co = np.empty(count * 3, dtype=np.float64)
        obj.data.vertices.foreach_get("co", co)
        # Only world Z is needed: the same matrix_world the exporter applies.
        z_row = np.array(obj.matrix_world, dtype=np.float64)[2]
        z = float((co.reshape(-1, 3) @ z_row[:3]).min() + z_row[3])
        lowest = z if lowest is None else min(lowest, z)
    if lowest is None:
        return None
    offset = round(-lowest, 4)
    return offset if offset > 0 else None


def apply_calculated_heels_offset(objects) -> tuple[str, str] | None:
    """Write the calculated offset onto the first LOD 0 mesh part.

    Meant for the export copies made by SceneHandler, never the user's objects.
    Any manual heels_offset attributes are removed so the model carries exactly
    one. Returns (attribute, object name), or None when no offset applies, in
    which case manual attributes are left alone.
    """
    offset = calculate_heels_offset(objects)
    if offset is None:
        return None
    candidates = _lod0_mesh_objects(objects)
    target = min(candidates, key=lambda item: item[0])[1]
    for obj in objects:
        for key in [key for key in obj.keys() if is_heels_offset_attribute(key)]:
            del obj[key]
    attribute = format_heels_offset(offset)
    target[attribute] = True
    return attribute, target.name
