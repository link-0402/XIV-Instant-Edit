"""XIV Instant Edit Blender extension."""

import bpy

from . import instant_edit
from .instant_edit import ops as instant_ops
from .instant_edit import props as instant_props
from .keymaps import register_keymaps, unregister_keymaps
from .operators import (
    XIVIE_OT_copy_text,
    XIVIE_OT_drag_mesh_order,
    XIVIE_OT_duplicate_backfaces,
    XIVIE_OT_mesh_attribute,
    XIVIE_OT_mesh_material,
    XIVIE_OT_remove_hidden_vertices,
    XIVIE_OT_select_mesh_part,
    XIVIE_OT_convert_mesh_names,
    XIVIE_OT_combine_armatures,
    XIVIE_OT_simple_import,
    XIVIE_OT_simple_export,
    XIVIE_OT_restore_backup,
    XIVIE_OT_import_backup,
    XIVIE_OT_clear_backups,
    XIVIE_OT_vertex_data,
    XIVIE_OT_show_pose_action,
    XIVIE_OT_delete_pose_action,
    XIVIE_OT_toggle_rest_pose,
)
from .preferences import (
    XIVIEPreferences,
    XIVIE_OT_clean_cache,
    XIVIE_OT_open_cache_folder,
    XIVIE_OT_open_diagnostics_folder,
)
from .properties import XIVIEExportSettings, set_addon_properties, remove_addon_properties
from .ui import (
    XIVIE_MT_export_targets,
    XIVIE_MT_links,
    XIVIE_MT_pose_actions,
    XIVIE_PT_backups,
    XIVIE_PT_connection_popover,
    XIVIE_PT_context_details_popover,
    XIVIE_PT_export_scope_popover,
    XIVIE_PT_file_io,
    XIVIE_PT_last_status_popover,
    XIVIE_PT_mesh_groups,
    XIVIE_PT_options,
    XIVIE_PT_options_export,
    XIVIE_PT_options_import,
    XIVIE_PT_pose,
    XIVIE_PT_session,
    XIVIE_PT_tools,
    XIVIE_PT_vertex_data_popover,
    draw_mesh_part_context_menu,
)


CLASSES = [
    XIVIEPreferences,
    XIVIE_OT_clean_cache,
    XIVIE_OT_open_cache_folder,
    XIVIE_OT_open_diagnostics_folder,
    XIVIEExportSettings,
    *instant_props.CLASSES,
    *instant_ops.CLASSES,
    XIVIE_OT_copy_text,
    XIVIE_OT_drag_mesh_order,
    XIVIE_OT_duplicate_backfaces,
    XIVIE_OT_mesh_attribute,
    XIVIE_OT_mesh_material,
    XIVIE_OT_remove_hidden_vertices,
    XIVIE_OT_select_mesh_part,
    XIVIE_OT_convert_mesh_names,
    XIVIE_OT_combine_armatures,
    XIVIE_OT_simple_import,
    XIVIE_OT_simple_export,
    XIVIE_OT_restore_backup,
    XIVIE_OT_import_backup,
    XIVIE_OT_clear_backups,
    XIVIE_OT_vertex_data,
    XIVIE_OT_show_pose_action,
    XIVIE_OT_delete_pose_action,
    XIVIE_OT_toggle_rest_pose,
    XIVIE_MT_export_targets,
    XIVIE_MT_links,
    XIVIE_MT_pose_actions,
    XIVIE_PT_last_status_popover,
    XIVIE_PT_connection_popover,
    XIVIE_PT_context_details_popover,
    XIVIE_PT_export_scope_popover,
    XIVIE_PT_vertex_data_popover,
    # Top-level panels keep their bl_order; sub-panels follow their parent.
    XIVIE_PT_session,
    XIVIE_PT_mesh_groups,
    XIVIE_PT_pose,
    XIVIE_PT_file_io,
    XIVIE_PT_options,
    XIVIE_PT_options_import,
    XIVIE_PT_options_export,
    XIVIE_PT_backups,
    XIVIE_PT_tools,
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
        bpy.types.UI_MT_button_context_menu.append(draw_mesh_part_context_menu)
        register_keymaps()
    except Exception:
        unregister()
        raise


def unregister() -> None:
    unregister_keymaps()
    bpy.types.UI_MT_button_context_menu.remove(draw_mesh_part_context_menu)
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
