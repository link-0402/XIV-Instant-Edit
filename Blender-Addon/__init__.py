"""XIV Instant Edit Blender extension."""

import bpy

from . import instant_edit
from .instant_edit import ops as instant_ops
from .instant_edit import props as instant_props
from .operators import (
    XIVIE_OT_drag_mesh_order,
    XIVIE_OT_mesh_attribute,
    XIVIE_OT_mesh_flow,
    XIVIE_OT_mesh_material,
    XIVIE_OT_mesh_tags,
    XIVIE_OT_rename_mesh_part,
    XIVIE_OT_convert_mesh_names,
    XIVIE_OT_combine_armatures,
    XIVIE_OT_simple_import,
    XIVIE_OT_simple_export,
    XIVIE_OT_restore_backup,
    XIVIE_OT_import_backup,
    XIVIE_OT_clear_backups,
)
from .preferences import (
    XIVIEPreferences,
    XIVIE_OT_clean_cache,
    XIVIE_OT_open_cache_folder,
    XIVIE_OT_open_diagnostics_folder,
)
from .properties import XIVIEExportSettings, set_addon_properties, remove_addon_properties
from .ui import (
    XIVIE_PT_main,
    XIVIE_PT_export_target_status_popover,
    XIVIE_PT_last_status_popover,
)


CLASSES = [
    XIVIEPreferences,
    XIVIE_OT_clean_cache,
    XIVIE_OT_open_cache_folder,
    XIVIE_OT_open_diagnostics_folder,
    XIVIEExportSettings,
    *instant_props.CLASSES,
    *instant_ops.CLASSES,
    XIVIE_OT_drag_mesh_order,
    XIVIE_OT_mesh_attribute,
    XIVIE_OT_mesh_flow,
    XIVIE_OT_mesh_material,
    XIVIE_OT_mesh_tags,
    XIVIE_OT_rename_mesh_part,
    XIVIE_OT_convert_mesh_names,
    XIVIE_OT_combine_armatures,
    XIVIE_OT_simple_import,
    XIVIE_OT_simple_export,
    XIVIE_OT_restore_backup,
    XIVIE_OT_import_backup,
    XIVIE_OT_clear_backups,
    XIVIE_PT_export_target_status_popover,
    XIVIE_PT_last_status_popover,
    XIVIE_PT_main,
]


def register() -> None:
    # register() always tears down first instead of trying to detect whether
    # a previous session left something registered. Detection turned out to
    # be unreliable for this add-on's own classes: bpy.types.<cls.__name__>
    # is None for AddonPreferences even while it's genuinely registered, and
    # for every Operator here it's *also* wrong, because Blender exposes
    # Operators under bpy.types by an identifier derived from bl_idname
    # (e.g. "xiv_ie.clean_cache" -> "XIV_IE_OT_clean_cache"), not by the
    # Python class name ("XIVIE_OT_clean_cache" - note the missing
    # underscore). That combination is what let classes stay registered
    # forever after a disable: unregister() never actually saw them as
    # registered, so unregister_class() was never called, and the next
    # register() always hit "already registered as a subclass". Class
    # objects are stable across a plain enable/disable/enable cycle within
    # one Blender session (proven empirically - Blender does not reload the
    # module), so unregister() below just always attempts every class
    # directly and relies on unregister_class()'s RuntimeError for classes
    # that were never registered, which it already treats as a no-op. A
    # Reload Scripts (F8) cycle is the one case this can't fully clean up,
    # since that creates new, never-registered class objects while the old
    # ones remain registered under Blender's RNA system - restarting Blender
    # remains the recovery path for that specific case.
    unregister()
    try:
        for cls in CLASSES:
            bpy.utils.register_class(cls)
        set_addon_properties()
        instant_edit.register()
    except Exception:
        unregister()
        raise


def unregister() -> None:
    instant_edit.unregister()
    try:
        remove_addon_properties()
    except (AttributeError, RuntimeError):
        pass
    for cls in reversed(CLASSES):
        try:
            bpy.utils.unregister_class(cls)
        except (AttributeError, RuntimeError):
            pass
