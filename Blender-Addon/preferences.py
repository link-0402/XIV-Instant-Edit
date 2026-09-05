import bpy
import tempfile

from bpy.props import BoolProperty, IntProperty, StringProperty
from bpy.types import AddonPreferences, Operator

from .instant_edit.cache import (
    STALE_SECONDS,
    cache_root,
    clean_cache,
    configure_cache,
)


def _update_listen_port(self, _context) -> None:
    from .instant_edit.server import set_server_port
    set_server_port(self.instant_edit_blender_port)


def _update_cache(self, _context) -> None:
    try:
        configure_cache(
            bpy.path.abspath(self.instant_edit_cache_directory),
            self.instant_edit_auto_cleanup,
        )
        if self.instant_edit_auto_cleanup:
            clean_cache(STALE_SECONDS)
    except Exception as error:
        from .instant_edit.diagnostics import record_failure
        record_failure(
            component="blender_addon",
            operation="addon_settings",
            stage="cache_configuration",
            code="cache_configuration_failed",
            cause="Blender could not apply the XIV Instant Edit cache setting.",
            remedy="Choose a writable cache directory in the add-on preferences, then retry.",
            endpoint="/settings/cache",
            exception=error,
        )
        print(f"XIV Instant Edit: could not configure cache: {error}")


class XIVIE_OT_clean_cache(Operator):
    bl_idname = "xiv_ie.clean_cache"
    bl_label = "Clean Cache Now"
    bl_description = "Remove owned cache jobs and reports, plus managed backups older than 30 days"

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

    instant_edit_cache_directory: StringProperty(
        name="Cache Directory",
        description="Base folder for the add-on-owned XIV-Instant-Edit cache directory",
        subtype="DIR_PATH",
        default=tempfile.gettempdir(),
        update=_update_cache,
    )  # type: ignore

    instant_edit_auto_cleanup: BoolProperty(
        name="Automatic Cache Cleanup",
        description="Remove completed cache jobs and crash leftovers older than 24 hours",
        default=True,
        update=_update_cache,
    )  # type: ignore

    def draw(self, _context) -> None:
        layout = self.layout
        layout.label(text="XIV Instant Edit Connection")
        layout.prop(self, "instant_edit_blender_port")
        layout.prop(self, "instant_edit_plugin_port")
        layout.separator()
        layout.label(text="XIV Instant Edit Cache")
        layout.prop(self, "instant_edit_cache_directory")
        layout.prop(self, "instant_edit_auto_cleanup")
        layout.label(text=f"Managed folder: {cache_root()}")
        layout.operator("xiv_ie.clean_cache", icon="TRASH")


def get_prefs() -> XIVIEPreferences:
    return bpy.context.preferences.addons[__package__].preferences
