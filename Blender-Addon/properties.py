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

    simple_io_tab: EnumProperty(
        name="Simple Export / Import",
        items=[
            ("IMPORT", "Import", "Import an MDL, FBX or glTF file"),
            ("EXPORT", "Export", "Export mesh objects to a model file"),
        ],
        default="EXPORT",
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
        name="Use Import Folder for Simple Export",
        description=(
            "After a successful import, use the imported file's folder as the Simple Export folder. "
            "Model imports from the plugin use the mod's model folder"
        ),
        default=True,
    )  # type: ignore
    resolve_mesh_group_conflicts: BoolProperty(
        name="Offset Clashing Mesh IDs",
        description="Offset incoming mesh group IDs when they conflict with existing visible groups",
        default=True,
    )  # type: ignore
    simple_import_skeleton: PointerProperty(
        type=Object,
        name="Skeleton Object",
        description="Existing Blender armature to use for Simple Import",
        poll=lambda _self, obj: obj.type == "ARMATURE",
    )  # type: ignore
    keep_shapekeys: BoolProperty(
        name="Keep Shape Keys",
        description="Export the meshes' shape keys as FFXIV shapes",
        default=False,
    )  # type: ignore
    reset_scaling_on_export: BoolProperty(
        name="Reset Armature Scaling",
        description="Temporarily reset armature scaling, positioning and rotation to default values for export",
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
    use_lods: BoolProperty(
        name="Internal export option",
        default=False,
        options={"HIDDEN"},
    )  # type: ignore
    pose_armature: PointerProperty(
        type=Object,
        name="Armature",
        description=(
            "Armature the Pose section shows. Leave empty to use the active object's armature, "
            "or else the armature of the selected Context"
        ),
        poll=lambda _self, obj: obj.type == "ARMATURE",
    )  # type: ignore

    def get_model_flags(self) -> dict[str, bool]:
        return dict(MODEL_FLAG_DEFAULTS)


def get_settings() -> XIVIEExportSettings:
    return bpy.context.scene.xiv_ie_settings


def set_addon_properties() -> None:
    bpy.types.Scene.xiv_ie_settings = PointerProperty(type=XIVIEExportSettings)


def remove_addon_properties() -> None:
    del bpy.types.Scene.xiv_ie_settings
