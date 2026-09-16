from .lod    import Lod
from .file   import XIVModel, BoundingBox, BoneTable
from .mesh   import Mesh, Submesh
from .face   import NeckMorph, FACE_DATA_DTYPE
from .enums  import VertexType, VertexUsage, ModelFlags1, ModelFlags2, ModelFlags3
from .shapes import ShapeMesh, SHAPE_VALUE_DTYPE
from .vertex import VertexDeclaration, VertexElement, get_vert_struct, XIV_COL, XIV_UV

XIV_ATTR = ("atr", "heels_offset", "skin_suffix")


def is_model_attribute_name(name: str) -> bool:
    """Return whether an object custom property is an exported mesh attribute.

    Matches Yet Another Addon's own allowlist exactly (see its
    props/getters.py get_xiv_meshes): only the recognized XIV attribute
    prefixes count. Anything else - including unrelated custom properties
    left on an object by other addons or devkits (e.g. "yas", "yakit") -
    is never treated as a model attribute, whether or not it happens to be
    visible in the scene.
    """
    return name.startswith(XIV_ATTR)
