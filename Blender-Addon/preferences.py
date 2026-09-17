import bpy

from bpy.props import IntProperty
from bpy.types import AddonPreferences, Operator

from .instant_edit.cache import (
    cache_root,
    clean_cache,
    diagnostics_root,
    ensure_cache_root,
    ensure_diagnostics_root,
)


def _update_listen_port(self, _context) -> None:
    from .instant_edit.server import set_server_port
    set_server_port(self.instant_edit_blender_port)


class XIVIE_OT_clean_cache(Operator):
    bl_idname = "xiv_ie.clean_cache"
    bl_label = "Clean Blender Cache Now"
    bl_description = "Remove owned model cache jobs and reports, plus managed backups older than 7 days"

    def execute(self, _context):
        try:
            jobs, byte_count = clean_cache()
        except Exception as error:
            from .instant_edit.diagnostics import record_failure
            record_failure(
                component="blender_addon",
                operation="cache_cleanup",
                stage="cache_cleanup",
                code="cache_cleanup_failed",
                cause="Blender could not clean the XIV Instant Edit cache.",
                remedy="Choose a writable cache directory or remove only the owned cache folder after closing Blender.",
                endpoint="/settings/cache/cleanup",
                exception=error,
            )
            self.report({"ERROR"}, f"Cache cleanup failed: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Removed {jobs} cache item(s), {byte_count / (1024 * 1024):.1f} MiB")
        return {"FINISHED"}


def _open_folder(operator: Operator, folder, label: str):
    try:
        path = folder()
        result = bpy.ops.wm.path_open(filepath=str(path))
    except Exception as error:
        operator.report({"ERROR"}, f"Could not open {label}: {error}")
        return {"CANCELLED"}
    if "FINISHED" not in result:
        operator.report({"ERROR"}, f"Could not open {label}.")
        return {"CANCELLED"}
    return {"FINISHED"}


class XIVIE_OT_open_cache_folder(Operator):
    bl_idname = "xiv_ie.open_cache_folder"
    bl_label = "Open Cache"
    bl_description = "Open the configured XIV Instant Edit cache folder"

    def execute(self, _context):
        return _open_folder(self, ensure_cache_root, "the XIV Instant Edit cache folder")


class XIVIE_OT_open_diagnostics_folder(Operator):
    bl_idname = "xiv_ie.open_diagnostics_folder"
    bl_label = "Open Diagnostics"
    bl_description = "Open the Dalamud plugin diagnostics folder"

    def execute(self, _context):
        return _open_folder(self, ensure_diagnostics_root, "the Dalamud diagnostics folder")


class XIVIEPreferences(AddonPreferences):
    bl_idname = __package__

    instant_edit_blender_port: IntProperty(
        name="Blender Listen Port",
        description="Port used to receive imports from the XIV Instant Edit Dalamud plugin",
        default=42424,
        min=1,
        max=65535,
        update=_update_listen_port,
    )  # type: ignore

    instant_edit_plugin_port: IntProperty(
        name="Plugin Callback Port",
        description="Fallback callback port used to reconnect saved import contexts",
        default=42428,
        min=1,
        max=65535,
    )  # type: ignore

    def draw(self, _context) -> None:
        layout = self.layout
        layout.label(text="XIV Instant Edit Connection")
        layout.prop(self, "instant_edit_blender_port")
        layout.prop(self, "instant_edit_plugin_port")
        layout.separator()
        layout.label(text="XIV Instant Edit Cache")
        layout.label(text="Cache directory and automatic cleanup are configured in the in-game plugin settings.")
        layout.label(text=f"Managed folder: {cache_root()}")
        layout.label(text=f"Diagnostics: {diagnostics_root()}")
        layout.operator("xiv_ie.clean_cache", icon="TRASH")


def get_prefs() -> XIVIEPreferences:
    return bpy.context.preferences.addons[__package__].preferences
