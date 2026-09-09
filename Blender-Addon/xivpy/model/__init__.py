import re

from .lod    import Lod
from .file   import XIVModel, BoundingBox, BoneTable
from .mesh   import Mesh, Submesh
from .face   import NeckMorph, FACE_DATA_DTYPE
from .enums  import VertexType, VertexUsage, ModelFlags1, ModelFlags2, ModelFlags3
from .shapes import ShapeMesh, SHAPE_VALUE_DTYPE
from .vertex import VertexDeclaration, VertexElement, get_vert_struct, XIV_COL, XIV_UV

XIV_ATTR = ("atr", "heels_offset", "skin_suffix")

_MODEL_ATTRIBUTE_NAME = re.compile(r"^[a-z0-9_]+$")
_INTERNAL_OBJECT_PROPERTIES = frozenset(
    {
        "xiv_material",
        "original_material",
        "material_index",
        "mesh_index",
        "submesh_index",
        "instant_edit_import_instance_id",
        "xiv_flow",
        "xiv_transparency",
        "context_id",
        "schema",
        "version",
        "plugin_instance_id",
        "capability",
        "source_game_path",
        "managed_destination",
        "target_file_path",
        "source_mod_directory",
        "source_mod_name",
        "source_mod_root_path",
        "target_relative_path",
        "source_kind",
        "resolved_game_path",
        "destination_state",
        "target_collection_id",
        "target_collection_name",
        "resource_manifest_version",
        "resource_manifest_status",
        "backup_target_id",
        "backup_directory",
        "import_id",
        "callback_port",
        "import_file_name",
        "collection_kind",
    }
)


def is_model_attribute_name(name: str) -> bool:
    """Return whether an object custom property is an exported mesh attribute."""
    if name in _INTERNAL_OBJECT_PROPERTIES or name.startswith("instant_edit_"):
        return False
    if name.startswith(XIV_ATTR):
        return True
    return bool(_MODEL_ATTRIBUTE_NAME.fullmatch(name))
