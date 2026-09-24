import bpy

from bpy.props import BoolProperty, EnumProperty, PointerProperty, StringProperty
from bpy.types import Object, PropertyGroup


MODEL_FLAG_DEFAULTS = {
    "shadow_disabled": False,
    "light_shadow_disabled": False,
    "waving_animation_disabled": True,
    "lighting_reflection_enabled": False,
    "unknown1": False,
    "rain_occlusion_enabled": False,
    "snow_occlusion_enabled": False,
    "dust_occlusion_enabled": False,
    "unknown2": False,
    "edge_geometry_disabled": False,
    "force_lod_range_enabled": False,
    "shadow_mask_enabled": False,
    "extra_lod_enabled": False,
    "enable_force_non_resident": False,
    "bg_uv_scroll_enabled": False,
    "static_mesh": False,
    "unknown3": False,
    "use_crest_change": False,
    "use_material_change": False,
    "unknown4": False,
    "unknown5": False,
    "unknown6": False,
    "unknown7": False,
    "unknown8": False,
}


def _uv2_mode_get(self) -> int:
    # Clear wins over Copy in the exporter, so it wins here too.
    if self.clear_uv2:
        return 2
    return 1 if self.copy_uv1_to_uv2 else 0


def _uv2_mode_set(self, value: int) -> None:
    self.copy_uv1_to_uv2 = value == 1
    self.clear_uv2 = value == 2


def _vertex_color1_mode_get(self) -> int:
    # Clearing the whole color also clears its alpha in the exporter.
    if self.clear_vertex_color1:
        return 2
    return 1 if self.clear_vertex_alpha1 else 0


def _vertex_color1_mode_set(self, value: int) -> None:
    self.clear_vertex_alpha1 = value == 1
    self.clear_vertex_color1 = value == 2


def _import_armature_get(self) -> int:
    return 1 if self.simple_import_use_existing_skeleton else 0


def _import_armature_set(self, value: int) -> None:
    self.simple_import_use_existing_skeleton = value == 1


class XIVIEExportSettings(PropertyGroup):
    backup_models_on_export: BoolProperty(
        name="Back Up Before Overwriting",
        description="Keep timestamped backups before replacing existing MDL or FBX files",
        default=False,
    )  # type: ignore

    export_directory: StringProperty(name="Export Folder", subtype="DIR_PATH", default="")  # type: ignore
    export_name: StringProperty(name="File Name", default="model", maxlen=255)  # type: ignore
    model_format: EnumProperty(
        name="Format",
        items=[("MDL", "MDL", "FFXIV model"), ("FBX", "FBX", "Autodesk FBX"), ("GLTF", "glTF", "glTF")],
        default="MDL",
    )  # type: ignore
    simple_import_use_existing_skeleton: BoolProperty(
        name="Use Existing Skeleton",
        description="Remove the imported armature and bind meshes to an existing Blender armature",
        default=False,
    )  # type: ignore
    # Two-button view of simple_import_use_existing_skeleton.
    simple_import_armature: EnumProperty(
        name="Armature",
        items=[
            ("GENERATED", "Generated", "Create an armature from the imported model's bones", 0),
            ("EXISTING", "Existing", "Remove the imported armature and bind the meshes to an existing one", 1),
        ],
        get=_import_armature_get,
        set=_import_armature_set,
    )  # type: ignore
    simple_import_set_export_directory: BoolProperty(
        name="Use Import Folder",
        description=(
            "After a successful import, use the imported file's folder as the File Export folder. "
            "Model imports from the plugin use the mod's model folder"
        ),
        default=True,
    )  # type: ignore
    resolve_mesh_group_conflicts: BoolProperty(
        name="Offset Clashing IDs",
        description="Offset incoming mesh group IDs when they conflict with existing visible groups",
        default=True,
    )  # type: ignore
    simple_import_skeleton: PointerProperty(
        type=Object,
        name="Skeleton Object",
        description="Existing Blender armature to use for File Import",
        poll=lambda _self, obj: obj.type == "ARMATURE",
    )  # type: ignore
    keep_shapekeys: BoolProperty(
        name="Keep Shape Keys",
        description="Export the meshes' shape keys as FFXIV shapes",
        default=False,
    )  # type: ignore
    check_tris: BoolProperty(
        name="Check Triangulation",
        description=(
            "Treat FBX and glTF exports as triangulated so Create Backfaces can run. "
            "MDL exports always require triangulated meshes"
        ),
        default=True,
    )  # type: ignore
    create_backfaces: BoolProperty(
        name="Create Backfaces",
        description="Duplicate and flip the faces in a BACKFACES vertex group so they render from both sides",
        default=False,
    )  # type: ignore
    reset_scaling_on_export: BoolProperty(
        name="Reset Scaling on Export",
        description="Temporarily reset armature scaling, positioning and rotation to default values for export.",
        default=False,
    )  # type: ignore
    calculate_heels_offset: BoolProperty(
        name="Calculate Heels Offset",
        description=(
            "Measure how far the model reaches below the floor and export it as a "
            "heels_offset attribute on the first mesh part, replacing any manual "
            "heels_offset attribute. Nothing is added when no geometry is below the floor"
        ),
        default=False,
    )  # type: ignore
    remove_yas: EnumProperty(
        name="YAS Groups",
        items=[("KEEP", "Keep", "Keep all groups"), ("NO_GEN", "Remove Genitalia", "Remove genital groups"), ("REMOVE", "Remove All", "Remove iv_/ya_ groups")],
        default="KEEP",
    )  # type: ignore
    use_lods: BoolProperty(
        name="Internal export option",
        default=False,
        options={"HIDDEN"},
    )  # type: ignore

    clear_uv2: BoolProperty(name="Clear UV2", default=False)  # type: ignore
    copy_uv1_to_uv2: BoolProperty(name="Copy UV1 to UV2", default=False)  # type: ignore
    clear_vertex_color1: BoolProperty(name="Clear Vertex Color 1", default=False)  # type: ignore
    clear_vertex_alpha1: BoolProperty(name="Clear Vertex Alpha 1", default=False)  # type: ignore
    clear_vertex_color2: BoolProperty(
        name="Clear Vertex Color 2",
        description="Reset vertex color 2 to black with full alpha",
        default=False,
    )  # type: ignore
    clear_flow_data: BoolProperty(
        name="Clear Flow Data",
        description="Clear the flow data channel",
        default=False,
    )  # type: ignore
    # The exporter treats these boolean pairs as one choice each (a clear
    # overrides the other flag), so the panel edits them through these views.
    uv2_mode: EnumProperty(
        name="UV2",
        items=[
            ("KEEP", "Keep", "Export UV2 as it is", 0),
            ("COPY_UV1", "Copy UV1", "Replace UV2 with a copy of UV1", 1),
            ("CLEAR", "Clear", "Set every UV2 coordinate to zero", 2),
        ],
        get=_uv2_mode_get,
        set=_uv2_mode_set,
    )  # type: ignore
    vertex_color1_mode: EnumProperty(
        name="Vertex Color 1",
        items=[
            ("KEEP", "Keep", "Export vertex color 1 as it is", 0),
            ("CLEAR_ALPHA", "Clear Alpha", "Set the alpha of vertex color 1 to opaque", 1),
            ("CLEAR", "Clear", "Reset vertex color 1 to opaque white", 2),
        ],
        get=_vertex_color1_mode_get,
        set=_vertex_color1_mode_set,
    )  # type: ignore

    def get_mesh_options(self) -> dict[str, bool]:
        return {
            "clear_uv2": self.clear_uv2,
            "copy_uv1_to_uv2": self.copy_uv1_to_uv2,
            "clear_vertex_color1": self.clear_vertex_color1,
            "clear_vertex_alpha1": self.clear_vertex_alpha1,
            "clear_vertex_color2": self.clear_vertex_color2,
            "clear_flow_data": self.clear_flow_data,
        }

    def get_model_flags(self) -> dict[str, bool]:
        return dict(MODEL_FLAG_DEFAULTS)


def get_settings() -> XIVIEExportSettings:
    return bpy.context.scene.xiv_ie_settings


def set_addon_properties() -> None:
    bpy.types.Scene.xiv_ie_settings = PointerProperty(type=XIVIEExportSettings)


def remove_addon_properties() -> None:
    del bpy.types.Scene.xiv_ie_settings
