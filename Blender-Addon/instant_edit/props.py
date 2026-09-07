# Modified for XIV Instant Edit, 2026.
import bpy

from bpy.types import Object, PropertyGroup
from bpy.props import (
    StringProperty,
    IntProperty,
    BoolProperty,
    EnumProperty,
    CollectionProperty,
    PointerProperty,
)


_EXPORT_DESTINATION_ITEMS = []
NO_EXPORT_CONTEXT = "NONE"
IN_PLACE_TARGET = "IN_PLACE"
_LAST_EXPORT_DESTINATION = NO_EXPORT_CONTEXT


def _export_destination_items(_self, context):
    global _EXPORT_DESTINATION_ITEMS
    from .context import context_collections, _value

    scene = getattr(context, "scene", None) or getattr(bpy.context, "scene", None)
    sentinel = (
        NO_EXPORT_CONTEXT,
        "Select Context",
        "Choose the imported model destination for Quick Export",
    )
    if scene is None:
        _EXPORT_DESTINATION_ITEMS = [sentinel]
        return _EXPORT_DESTINATION_ITEMS

    items = [sentinel]
    for collection in sorted(
        context_collections(scene),
        key=lambda value: str(_value(value, "source_game_path", "")).casefold(),
    ):
        context_id = str(_value(collection, "context_id", ""))
        game_path = str(_value(collection, "source_game_path", ""))
        model_name = game_path.replace("\\", "/").rsplit("/", 1)[-1] or context_id
        mod_name = str(_value(collection, "source_mod_name", ""))
        label = f"{model_name} ({mod_name})" if mod_name else model_name
        items.append((context_id, label, f"Overwrite the imported model at {game_path}"))
    identifiers = {item[0] for item in items}
    if _LAST_EXPORT_DESTINATION not in identifiers:
        items.append((
            _LAST_EXPORT_DESTINATION,
            "Removed Context",
            "This Context was removed and will be deselected automatically",
        ))
    # Blender requires dynamically generated enum strings to remain alive for
    # as long as the enum is in use.
    _EXPORT_DESTINATION_ITEMS = items
    return _EXPORT_DESTINATION_ITEMS


def _export_destination_changed(self, context) -> None:
    """Refresh the authenticated Penumbra target tree for the selected context."""
    global _LAST_EXPORT_DESTINATION
    _LAST_EXPORT_DESTINATION = self.export_destination or NO_EXPORT_CONTEXT
    self.variant_target = "NEW_GROUP"
    self.variant_targets.clear()
    self.variant_targets_context_id = ""
    if context is None or self.export_destination == NO_EXPORT_CONTEXT:
        return
    try:
        # Import locally to keep property registration independent from the
        # operator module's Blender imports.
        from .context import validate_context
        from .ops import refresh_variant_targets

        if validate_context(self.export_destination, context.scene).destination_state != "ready":
            return
        refresh_variant_targets(context)
    except Exception as error:
        self.last_status = f"Could not load Penumbra targets: {error}"


class XIVIEVariantTarget(PropertyGroup):
    """One selectable Penumbra group or option returned by the plugin."""

    selection_id: StringProperty(default="", maxlen=256)  # type: ignore
    kind: StringProperty(default="", maxlen=16)  # type: ignore
    group_name: StringProperty(default="", maxlen=120)  # type: ignore
    option_name: StringProperty(default="", maxlen=120)  # type: ignore
    model_path: StringProperty(default="", maxlen=4096)  # type: ignore
    backup_target_id: StringProperty(default="", maxlen=128)  # type: ignore
    backup_directory: StringProperty(default="", maxlen=4096)  # type: ignore
    expanded: BoolProperty(default=True)  # type: ignore


class XIVIEInstantEditProps(PropertyGroup):

    show_utilities: BoolProperty(
        name="Toolbox",
        description="Show maintenance actions for XIV Instant Edit context data",
        default=False,
    )  # type: ignore

    export_destination: EnumProperty(
        name="Context",
        description="Choose which imported model destination Quick Export uses",
        items=_export_destination_items,
        update=_export_destination_changed,
    )  # type: ignore

    export_scope: EnumProperty(
        name="Export Parts",
        description="Choose which visible mesh objects Quick Export and Simple Export include",
        default="VISIBLE",
        items=[
            ("VISIBLE", "All Visible", "Export every visible mesh object"),
            (
                "VISIBLE_NO_MANNEQUIN",
                "All except...",
                "Export every visible mesh object except the explicitly selected mesh",
            ),
            (
                "CURRENT_COLLECTION",
                "XIV Instant Edit Collection",
                "Export only visible mesh objects in the selected Context's XIV Instant Edit collection",
            ),
        ],
    )  # type: ignore

    export_excluded_mesh: PointerProperty(
        type=Object,
        name="Excluded Mesh",
        description="Mesh object to exclude when Export Parts is set to All except...",
        poll=lambda _self, obj: obj.type == "MESH",
    )  # type: ignore

    game_path: StringProperty(
        name="",
        description="Game path of the model loaded via XIV Instant Edit",
        default="",
        maxlen=255,
    )  # type: ignore

    display_name: StringProperty(
        name="",
        description="Display name of the loaded model",
        default="",
        maxlen=255,
    )  # type: ignore

    object_index: IntProperty(
        name="",
        description="Index of the game object this model was picked from",
        default=-1,
    )  # type: ignore

    # These are a display/cache of the immutable collection reference. Safe
    # export reads the collection and tagged objects, not these scene values.
    context_id: StringProperty(
        name="",
        description="Active XIV Instant Edit context identifier",
        default="",
        maxlen=256,
    )  # type: ignore

    context_schema: StringProperty(
        name="",
        description="XIV Instant Edit context schema",
        default="",
        maxlen=128,
    )  # type: ignore

    context_version: IntProperty(
        name="",
        description="XIV Instant Edit context schema version",
        default=0,
    )  # type: ignore

    plugin_instance_id: StringProperty(name="", default="", maxlen=256)  # type: ignore
    capability: StringProperty(name="", default="", maxlen=1024)  # type: ignore
    managed_destination: StringProperty(name="", default="", maxlen=4096)  # type: ignore
    last_export_id: StringProperty(name="", default="", maxlen=128)  # type: ignore

    last_status: StringProperty(
        name="",
        description="Status of the last XIV Instant Edit action",
        default="Pick a model in-game via the XIV Instant Edit plugin to get started.",
        maxlen=4096,
    )  # type: ignore

    variant_name: StringProperty(
        name="New Option Name",
        description="File name and Penumbra option name; .mdl is added automatically",
        default="",
        maxlen=128,
    )  # type: ignore

    variant_group_name: StringProperty(
        name="Penumbra Option Group",
        description="Existing Penumbra option group to reuse, or name for a new group",
        default="New Group",
        maxlen=120,
    )  # type: ignore

    variant_target: StringProperty(
        name="Penumbra Group Target",
        description="The Penumbra group or option to receive the Quick Export",
        default="NEW_GROUP",
        maxlen=256,
    )  # type: ignore

    variant_targets_context_id: StringProperty(default="", maxlen=256)  # type: ignore
    variant_targets: CollectionProperty(type=XIVIEVariantTarget)  # type: ignore

def _ensure_scene_property() -> None:
    """Restore the Scene pointer after a Blender extension hot-reload if possible."""
    if hasattr(bpy.types.Scene, "xiv_ie_instant_edit_props"):
        return

    registered = getattr(bpy.types, XIVIEInstantEditProps.__name__, None)
    if registered is None:
        raise RuntimeError(
            "XIV Instant Edit is not fully registered; disable and re-enable the add-on "
            "or restart Blender."
        )
    bpy.types.Scene.xiv_ie_instant_edit_props = PointerProperty(type=registered)


def get_instant_edit_props() -> XIVIEInstantEditProps:
    scene = getattr(bpy.context, "scene", None)
    if scene is None:
        raise RuntimeError("XIV Instant Edit requires an active Blender Scene.")
    _ensure_scene_property()
    return scene.xiv_ie_instant_edit_props


def set_addon_properties() -> None:
    bpy.types.Scene.xiv_ie_instant_edit_props = bpy.props.PointerProperty(
        type=XIVIEInstantEditProps)


def remove_addon_properties() -> None:
    del bpy.types.Scene.xiv_ie_instant_edit_props


CLASSES = [
    XIVIEVariantTarget,
    XIVIEInstantEditProps,
]
