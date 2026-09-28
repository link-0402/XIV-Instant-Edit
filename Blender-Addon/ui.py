import ntpath
from pathlib import Path, PurePosixPath
import re
import textwrap
from collections import Counter, defaultdict
from typing import NamedTuple

from bpy.types import Context, Menu, Panel

from .instant_edit.context import (
    ContextValidationError,
    _value,
    context_collections,
    planned_mesh_ids,
    validate_context,
)
from .instant_edit.ops import (MASHUP_TARGET, SAVE_NEW_MOD_TARGET,
                               export_destination_context, mashup_target_state,
                               cached_export_readiness, cached_hair_skeleton,
                               normalise_variant_name, save_new_mod_target_state)
from .instant_edit.props import DEFAULT_STATUS, IN_PLACE_TARGET, get_instant_edit_props
from .instant_edit.server import server_status
from .materials import (
    attribute_display_name,
    material_mismatch_parts,
    material_paths,
    mesh_display_name,
    mesh_part_attributes,
)
from .keymaps import draw_shortcut
from .mesh_list import layout_rows, lod_zero_objects, scene_parts
from .operators import active_mesh_drag_plan, active_mesh_drag_state
from .pose import pose_actions, pose_armature, shown_action
from .properties import get_settings
from .backups import list_backups, target_folder


# "XI" collides with Yet Another Addon's sidebar tab; leading "IE" keeps the
# abbreviated tab label distinct from it.
CATEGORY = "IE - Instant Edit"

# Blender's report icons: warnings use the ERROR triangle and errors the CANCEL
# circle. STATUS_WARNING only exists from Blender 5.0 on.
_SEVERITY_ICONS = {"ERROR": "CANCEL", "WARNING": "ERROR"}
_MAX_SHOWN_ISSUES = 4
_VERTEX_LIMIT = 65536
_VERTEX_WARNING = 58982

# Model file suffixes and the equipment slots they belong to.
_SLOT_LABELS = {
    "met": "Head", "top": "Body", "glv": "Hands", "dwn": "Legs", "sho": "Feet",
    "ear": "Earrings", "nek": "Necklace", "wrs": "Bracelets", "rir": "Right Ring",
    "ril": "Left Ring", "hir": "Hair", "fac": "Face", "til": "Tail", "zer": "Ears",
}


def _relative_physical_path(file_path: str, root_path: str) -> str:
    """Return a physical file path relative to its source mod root."""
    if not file_path or not root_path:
        return ""
    # Bridge payloads carry Windows paths even when the regression suite runs
    # on Linux.  pathlib follows the host OS, so use ntpath for Windows-style
    # paths instead of treating ``D:\\...`` as a relative POSIX filename.
    windows_style = (
        "\\" in file_path
        or "\\" in root_path
        or bool(ntpath.splitdrive(file_path)[0])
        or bool(ntpath.splitdrive(root_path)[0])
    )
    if windows_style:
        file_path = ntpath.normpath(file_path)
        root_path = ntpath.normpath(root_path)
        try:
            if ntpath.normcase(ntpath.commonpath((file_path, root_path))) != ntpath.normcase(root_path):
                return ""
            return ntpath.relpath(file_path, root_path).replace("\\", "/")
        except ValueError:
            return ""
    try:
        return Path(file_path).resolve(strict=False).relative_to(
            Path(root_path).resolve(strict=False)
        ).as_posix()
    except (OSError, ValueError):
        return ""


def _import_file_display(ref) -> str:
    """Prefer the plugin-validated path, with a legacy physical-path fallback."""
    if getattr(ref, "source_kind", "mod") == "game":
        return str(getattr(ref, "resolved_game_path", "") or "Unavailable").replace("\\", "/")
    relative = str(getattr(ref, "target_relative_path", "") or "").strip()
    if relative:
        return relative.replace("\\", "/")
    return _relative_physical_path(
        str(getattr(ref, "target_file_path", "") or ""),
        str(getattr(ref, "source_mod_root_path", "") or ""),
    ) or "Unavailable"


def _export_destination_display(ref, props=None) -> str:
    """Return the effective mod-relative Quick Export target and model file."""
    relative = str(getattr(ref, "target_relative_path", "") or "").strip()
    if relative:
        destination = relative.replace("\\", "/")
    else:
        target_file_path = str(getattr(ref, "target_file_path", "") or "").strip()
        destination = _relative_physical_path(
            target_file_path,
            str(getattr(ref, "source_mod_root_path", "") or ""),
        )
        if not destination:
            destination = Path(target_file_path).name if target_file_path else "Unavailable"

    if props is None:
        return destination

    if getattr(props, "variant_target", "") in {
        IN_PLACE_TARGET, MASHUP_TARGET, SAVE_NEW_MOD_TARGET,
    }:
        return destination

    selected_target = _selected_target(props)
    if (
        selected_target is not None
        and selected_target.kind == "OPTION"
        and selected_target.model_path
    ):
        return selected_target.model_path.replace("\\", "/")

    try:
        variant_name = normalise_variant_name(getattr(props, "variant_name", ""))
    except ValueError:
        return destination
    directory, separator, _ = destination.rpartition("/")
    return f"{directory}/{variant_name}.mdl" if separator else f"{variant_name}.mdl"


# Label text at 100% interface scale: 5.25 pixels per character measured, plus room for wide
# letters. A sidebar line loses the tab column and panel padding; a popover line its padding.
_CHARACTER_WIDTH = 5.8
_SIDEBAR_OVERHEAD = 36
_POPOVER_PADDING = 20


def _display_wrap_width(context: Context, units: int = 0) -> int:
    """Estimate how many characters fit on a line of the sidebar, or of a popover ``units`` wide.

    A popover's units scale with the interface, so its width in characters does not.
    """
    if units:
        return max(24, int((units * 20 - _POPOVER_PADDING) / _CHARACTER_WIDTH))
    region_width = int(getattr(getattr(context, "region", None), "width", 0) or 0)
    if region_width:
        try:
            scale = float(context.preferences.system.ui_scale) or 1.0
        except (AttributeError, TypeError, ValueError):
            scale = 1.0
        return max(24, min(72, int((region_width / scale - _SIDEBAR_OVERHEAD) / _CHARACTER_WIDTH)))
    return 42


def _wrap_display_value(value: str, width: int) -> list[str]:
    """Wrap paths at slash boundaries before splitting a long path segment."""
    value = value or "Unavailable"
    if "/" not in value:
        return textwrap.wrap(
            value,
            width=width,
            break_long_words=True,
            break_on_hyphens=False,
        )

    segments = value.split("/")
    tokens = [
        f"{segment}/" if index < len(segments) - 1 else segment
        for index, segment in enumerate(segments)
    ]
    lines = []
    current = ""
    for token in tokens:
        if current and len(current) + len(token) > width:
            lines.append(current)
            current = ""
        if len(token) > width:
            chunks = textwrap.wrap(
                token,
                width=width,
                break_long_words=True,
                break_on_hyphens=False,
            )
            lines.extend(chunks[:-1])
            current = chunks[-1] if chunks else ""
        else:
            current += token
    if current or not lines:
        lines.append(current)
    return lines


def _draw_wrapped_label(layout, context: Context, text: str, icon: str = "NONE", indent: int = 4,
                        units: int = 0) -> None:
    """Draw a sentence across as many labels as the sidebar (or a popover ``units`` wide) needs."""
    column = layout.column(align=True)
    width = max(20, _display_wrap_width(context, units) - indent)
    for index, line in enumerate(_wrap_display_value(text, width) or (text,)):
        column.label(text=line, icon=icon if index == 0 else ("BLANK1" if icon != "NONE" else "NONE"))


def _split_row(layout, label: str):
    """A row laid out like use_property_split: its label right-aligned in the first 40%."""
    split = layout.split(factor=0.4, align=True)
    left = split.row(align=True)
    left.alignment = "RIGHT"
    left.label(text=label)
    return split.row(align=True)


def _draw_hint(layout, context: Context, text: str, icon: str = "INFO") -> None:
    column = layout.column(align=True)
    column.active = False
    _draw_wrapped_label(column, context, text, icon)


_MATERIAL_WARNING_RE = re.compile(
    r"^(?P<prefix>Warning: missing files for materials?:) (?P<materials>.+)\. "
    r"(?P<guidance>Use Create Mashup to include them\.)$"
)


def _issue_display_lines(message: str) -> list[str]:
    """Split a known issue message into explicit display lines.

    The material-coverage warning packs a comma-joined material list into one
    sentence, which word-wraps awkwardly (e.g. a lone ``/`` stranded at the end
    of a line). When the message matches that shape, break it into a label
    line, one material per line, and the guidance sentence on its own line.
    """
    match = _MATERIAL_WARNING_RE.match(message)
    if match is None:
        return [message]
    materials = [name.strip() for name in match.group("materials").split(", ") if name.strip()]
    if not materials:
        return [message]
    return [match.group("prefix"), *materials, match.group("guidance")]


def _status_icon(message: str) -> str:
    """Pick a report icon for the free-text status of the last action."""
    lowered = message.casefold()
    if "failed" in lowered or lowered.startswith("could not") or "unavailable" in lowered:
        return "CANCEL"
    if "warning" in lowered or "could not refresh" in lowered:
        return "ERROR"
    return "INFO"


def _draw_status_popover_body(layout, context: Context, message: str, icon: str = "NONE", units: int = 0) -> None:
    """Word-wrap a status message across multiple labels inside a popover."""
    column = layout.column(align=True)
    width = _display_wrap_width(context, units)
    first = True
    for segment in _issue_display_lines(message):
        for line in _wrap_display_value(segment, width) or (segment,):
            column.label(text=line, icon=icon if first else "NONE")
            first = False


# ---- Contexts and export targets --------------------------------------------------


class _ContextEntry(NamedTuple):
    context_id: str
    collection: object
    label: str
    ref: object
    error: str


def _model_label(game_path: str) -> str:
    stem = PurePosixPath(str(game_path or "").replace("\\", "/")).stem
    suffix = stem.rsplit("_", 1)[-1].casefold() if "_" in stem else ""
    return _SLOT_LABELS.get(suffix, stem or "Model")


def _context_entries(context: Context) -> list[_ContextEntry]:
    """Every Context collection in the scene, valid or not, in selector order."""
    rows = []
    for collection in context_collections(context.scene):
        context_id = str(_value(collection, "context_id", ""))
        try:
            ref, error = validate_context(context_id, context.scene), ""
        except ContextValidationError as exc:
            ref, error = None, str(exc)
        game_path = str(_value(collection, "source_game_path", "")).replace("\\", "/")
        source = str(_value(collection, "source_mod_name", "") or "")
        if not source and _value(collection, "source_kind", "mod") == "game":
            source = "Game Data"
        rows.append((game_path.casefold(), context_id, collection, game_path, source, ref, error))
    rows.sort(key=lambda row: (row[0], row[1]))

    def label_for(game_path, source, use_file_name):
        model = PurePosixPath(game_path).stem if use_file_name else _model_label(game_path)
        return f"{model} · {source}" if source else model or "Model"

    labels = [label_for(row[3], row[4], False) for row in rows]
    # Two Contexts for the same slot of one mod fall back to the model file name.
    labels = [
        label_for(row[3], row[4], True) if labels.count(label) > 1 else label
        for row, label in zip(rows, labels)
    ]
    entries = []
    for row, label in zip(rows, labels):
        repeat = labels[:len(entries)].count(label)
        entries.append(_ContextEntry(
            row[1], row[2], f"{label} ({repeat + 1})" if repeat else label, row[5], row[6]))
    return entries


def _layer_collection(layer_collection, collection):
    if layer_collection.collection == collection:
        return layer_collection
    for child in layer_collection.children:
        found = _layer_collection(child, collection)
        if found is not None:
            return found
    return None


def _selected_target(props):
    return next(
        (item for item in props.variant_targets if item.selection_id == props.variant_target), None)


def _target_groups(props) -> list:
    """Return the cached target tree as (group, options) pairs."""
    groups = []
    for item in props.variant_targets:
        if item.kind == "GROUP":
            groups.append((item, []))
        elif item.kind == "OPTION" and groups:
            groups[-1][1].append(item)
    return groups


def _target_summary(props) -> tuple[str, str]:
    """Text and icon for the export target dropdown."""
    selection = props.variant_target
    if selection == IN_PLACE_TARGET:
        return "In Place", "FILE_TICK"
    if selection == "NEW_GROUP":
        return "New Group", "ADD"
    if selection == MASHUP_TARGET:
        return "Create Mashup", "EXPERIMENTAL"
    if selection == SAVE_NEW_MOD_TARGET:
        return "Save as New Mod", "NEWFOLDER"
    target = _selected_target(props)
    if target is None:
        return "Choose a Target", "QUESTION"
    if target.kind == "GROUP":
        return f"{target.group_name} › New Option", "ADD"
    return f"{target.group_name} › {target.option_name}", "FILE"


class XIVIE_MT_export_targets(Menu):
    bl_idname = "XIVIE_MT_export_targets"
    bl_label = "Export Target"

    def draw(self, context: Context) -> None:
        layout = self.layout
        props = get_instant_edit_props()
        try:
            ref = export_destination_context(context, persist=False)
        except ContextValidationError as error:
            layout.label(text=str(error), icon="INFO")
            return

        def target(parent, selection_id: str, text: str) -> None:
            selected = props.variant_target == selection_id
            parent.operator(
                "xiv_ie.select_variant_target",
                text=text,
                icon="RADIOBUT_ON" if selected else "RADIOBUT_OFF",
            ).selection_id = selection_id

        target(layout, IN_PLACE_TARGET, "In Place")
        if props.variant_targets_context_id == ref.context_id:
            for group, options in _target_groups(props):
                layout.separator()
                layout.label(text=group.group_name, icon="OUTLINER_COLLECTION")
                for option in options:
                    target(layout, option.selection_id, option.option_name)
                target(layout, group.selection_id, "New Option...")
        else:
            layout.separator()
            layout.operator("xiv_ie.refresh_variant_targets", text="Load Mod Options", icon="FILE_REFRESH")
        layout.separator()
        target(layout, "NEW_GROUP", "New Group...")

        show_mashup, mashup_enabled, mashup_message = mashup_target_state(context, ref)
        if show_mashup:
            row = layout.row()
            row.enabled = mashup_enabled
            target(row, MASHUP_TARGET, "Create Mashup...")
            if not mashup_enabled and mashup_message:
                layout.label(text=mashup_message, icon="ERROR")
            return
        show_new_mod, new_mod_enabled, new_mod_message = save_new_mod_target_state(context, ref)
        if show_new_mod:
            row = layout.row()
            row.enabled = new_mod_enabled
            target(row, SAVE_NEW_MOD_TARGET, "Save as New Mod...")
            if not new_mod_enabled and new_mod_message:
                layout.label(text=new_mod_message, icon="ERROR")


# ---- Popovers -----------------------------------------------------------------------
# HEADER (not UI) keeps these out of the sidebar's tab list; they are only ever
# shown anchored via layout.popover(), never as their own N-panel tab.


class XIVIE_PT_last_status_popover(Panel):
    """Pop out full last status message."""

    bl_idname = "XIVIE_PT_last_status_popover"
    bl_label = "XIV Instant Edit Status"
    bl_space_type = "VIEW_3D"
    bl_region_type = "HEADER"
    bl_ui_units_x = 20

    def draw(self, context: Context) -> None:
        layout = self.layout
        message = get_instant_edit_props().last_status or "No status yet."
        _draw_status_popover_body(layout, context, message, units=self.bl_ui_units_x)
        layout.separator()
        layout.operator("xiv_ie.copy_status", text="Copy to Clipboard", icon="COPYDOWN")


class XIVIE_PT_connection_popover(Panel):
    bl_idname = "XIVIE_PT_connection_popover"
    bl_label = "Plugin Connection"
    bl_space_type = "VIEW_3D"
    bl_region_type = "HEADER"
    bl_ui_units_x = 16

    def draw(self, context: Context) -> None:
        layout = self.layout
        running, port, error = server_status()
        if running:
            layout.label(text=f"Listening for the plugin on port {port}", icon="CHECKMARK")
        else:
            row = layout.row()
            row.alert = True
            row.label(text=f"Not listening on port {port}", icon="CANCEL")
            units = self.bl_ui_units_x
            _draw_wrapped_label(layout, context, error or "The listener could not start.", "BLANK1", units=units)
            _draw_wrapped_label(
                layout, context,
                "Blender keeps trying and connects once the port is free, such as when the other "
                "Blender closes. Or choose another port in the preferences.",
                "BLANK1", units=units,
            )
        layout.separator()
        row = layout.row(align=True)
        row.operator("preferences.addon_show", text="Preferences", icon="PREFERENCES").module = __package__
        row.operator("xiv_ie.open_diagnostics_folder", text="Diagnostics", icon="FILE_FOLDER")


class XIVIE_PT_context_details_popover(Panel):
    bl_idname = "XIVIE_PT_context_details_popover"
    bl_label = "Context Details"
    bl_space_type = "VIEW_3D"
    bl_region_type = "HEADER"
    bl_ui_units_x = 22

    def draw(self, context: Context) -> None:
        layout = self.layout
        try:
            ref = export_destination_context(context, persist=False)
        except ContextValidationError as error:
            layout.label(text=str(error), icon="INFO")
            return
        if ref.source_kind == "game":
            source = ("Game Data", "", "WORLD")
        else:
            source = (
                ref.source_mod_name or "Source Mod",
                ref.source_mod_root_path or ref.source_mod_directory,
                "FILE_FOLDER",
            )
        destination = (
            "Not created yet" if ref.destination_state == "new_mod_required"
            else _export_destination_display(ref, get_instant_edit_props())
        )
        width = max(24, _display_wrap_width(context, self.bl_ui_units_x) - 6)
        for title, value, icon in (
            source,
            ("Imported File", _import_file_display(ref), "IMPORT"),
            ("Export Destination", destination, "EXPORT"),
        ):
            header = layout.row(align=True)
            header.label(text=title, icon=icon)
            if value and value not in {"Unavailable", "Not created yet"}:
                header.operator("xiv_ie.copy_text", text="", icon="COPYDOWN", emboss=False).text = value
            if value:
                column = layout.column(align=True)
                for line in _wrap_display_value(value, width):
                    column.label(text=line, icon="BLANK1")
        layout.separator()
        layout.operator("xiv_ie.open_context_folder", icon="FILE_FOLDER")


class XIVIE_PT_export_scope_popover(Panel):
    bl_idname = "XIVIE_PT_export_scope_popover"
    bl_label = "Quick Export Options"
    bl_space_type = "VIEW_3D"
    bl_region_type = "HEADER"
    bl_ui_units_x = 16

    def draw(self, context: Context) -> None:
        layout = self.layout
        layout.use_property_split = True
        layout.use_property_decorate = False
        props = get_instant_edit_props()
        layout.prop(props, "export_scope", text="Parts")
        if props.export_scope == "VISIBLE_NO_MANNEQUIN":
            layout.prop(props, "export_excluded_mesh", text="Except")
        draw_shortcut(layout, context, "xiv_ie.instant_export", "Shortcut")
        toggles = layout.column(align=True)
        toggles.use_property_split = False
        toggles.prop(props, "create_attribute_groups", text="Create Penumbra Attribute Group")


# ---- Instant Edit (session) panel ---------------------------------------------------


def _draw_status_row(layout, props) -> None:
    message = props.last_status or ""
    if not message or message == DEFAULT_STATUS:
        return
    icon = _status_icon(message)
    row = layout.row(align=True)
    row.alert = icon == "CANCEL"
    text = row.row(align=True)
    text.active = icon != "INFO"
    text.label(text=message, icon=icon)
    row.popover("XIVIE_PT_last_status_popover", text="", icon="DOWNARROW_HLT")


def _draw_empty_session(layout, context: Context, props) -> None:
    box = layout.box()
    box.label(text="No model loaded", icon="INFO")
    _draw_wrapped_label(
        box, context,
        "In XIV Instant Edit, press the pen icon next to a model to edit it here.",
        "BLANK1",
        indent=6,
    )
    running, port, _error = server_status()
    if not running:
        row = layout.row(align=True)
        row.alert = True
        row.label(text=f"Not listening on port {port}", icon="CANCEL")
        row.popover("XIVIE_PT_connection_popover", text="", icon="DOWNARROW_HLT")
    layout.operator("xiv_ie.simple_import", text="Import a Model File...", icon="IMPORT")
    _draw_status_row(layout, props)


def _draw_context_rows(layout, context: Context, props, entries, ref) -> None:
    column = layout.column(align=True)
    for entry in entries:
        row = column.row(align=True)
        layer = _layer_collection(context.view_layer.layer_collection, entry.collection)
        if layer is not None:
            row.prop(layer, "hide_viewport", text="", emboss=False)
        selected = props.export_destination == entry.context_id
        button = row.row(align=True)
        button.alert = bool(entry.error)
        button.operator(
            "xiv_ie.select_export_context",
            text=entry.label,
            icon="ERROR" if entry.error else ("RADIOBUT_ON" if selected else "RADIOBUT_OFF"),
            depress=selected,
        ).context_id = entry.context_id
        if selected and ref is not None:
            row.popover("XIVIE_PT_context_details_popover", text="", icon="THREE_DOTS")
        else:
            row.label(text="", icon="BLANK1")


def _draw_target(layout, props, ref) -> bool:
    """Draw the target dropdown and its name fields; return whether an option name is needed."""
    row = layout.row(align=True)
    text, icon = _target_summary(props)
    row.menu("XIVIE_MT_export_targets", text=text, icon=icon)
    row.operator("xiv_ie.refresh_variant_targets", text="", icon="FILE_REFRESH")
    if props.variant_targets_context_id != ref.context_id:
        hint = layout.row()
        hint.active = False
        hint.label(text="Refresh to load this mod's options.", icon="INFO")

    target = _selected_target(props)
    needs_group = props.variant_target == "NEW_GROUP"
    needs_option = needs_group or (target is not None and target.kind == "GROUP")
    if needs_option:
        fields = layout.column(align=True)
        fields.use_property_split = True
        fields.use_property_decorate = False
        if needs_group:
            fields.prop(props, "variant_group_name", text="Group")
        fields.prop(props, "variant_name", text="Option", placeholder="Option name")
    return needs_option


def _draw_destination(layout, props, ref, needs_option: bool) -> None:
    if props.variant_target in {MASHUP_TARGET, SAVE_NEW_MOD_TARGET}:
        return
    if needs_option:
        try:
            normalise_variant_name(props.variant_name)
        except ValueError:
            return
    row = layout.row()
    row.active = False
    row.label(text=f"→ {PurePosixPath(_export_destination_display(ref, props)).name}", icon="BLANK1")


def _draw_readiness(layout, context: Context, props, ref) -> None:
    issues, material_coverage_warning = cached_export_readiness()
    if not issues:
        layout.label(text="Ready to export", icon="CHECKMARK")
        return
    box = layout.box()
    column = box.column(align=True)
    width = max(20, _display_wrap_width(context) - 5)
    for severity, message in issues[:_MAX_SHOWN_ISSUES]:
        icon = "TIME" if message.startswith("Checking ") else _SEVERITY_ICONS.get(severity, "INFO")
        first = True
        for segment in _issue_display_lines(message):
            for line in _wrap_display_value(segment, width) or (segment,):
                row = column.row()
                row.alert = severity == "ERROR"
                row.label(text=line, icon=icon if first else "BLANK1")
                first = False
    if len(issues) > _MAX_SHOWN_ISSUES:
        column.label(text=f"+{len(issues) - _MAX_SHOWN_ISSUES} more", icon="BLANK1")

    actions = box.row(align=True)
    offers_mashup = False
    if material_coverage_warning and props.variant_target != MASHUP_TARGET:
        show_mashup, mashup_enabled, _message = mashup_target_state(context, ref)
        if show_mashup:
            offers_mashup = True
            fix = actions.row(align=True)
            fix.enabled = mashup_enabled
            fix.operator(
                "xiv_ie.select_variant_target", text="Use Create Mashup", icon="EXPERIMENTAL",
            ).selection_id = MASHUP_TARGET
    if not offers_mashup:
        actions.alignment = "RIGHT"
    actions.operator("xiv_ie.copy_target_status", text="", icon="COPYDOWN").status_message = "\n".join(
        message for _severity, message in issues)


def _draw_export_button(layout, props, ref) -> None:
    text, icon = "Quick Export", "EXPORT"
    if ref.destination_state == "new_mod_required":
        text, icon = "Create Penumbra Mod...", "NEWFOLDER"
    elif props.variant_target == MASHUP_TARGET:
        text, icon = "Create Mashup...", "EXPERIMENTAL"
    elif props.variant_target == SAVE_NEW_MOD_TARGET:
        text, icon = "Save as New Mod...", "NEWFOLDER"
    row = layout.row(align=True)
    row.scale_y = 1.4
    row.operator("xiv_ie.instant_export", text=text, icon=icon)
    row.popover("XIVIE_PT_export_scope_popover", text="", icon="PREFERENCES")


def _draw_scope_summary(layout, props) -> None:
    """Point out non-default Quick Export options that live in the popover."""
    notes = []
    if props.export_scope == "VISIBLE_NO_MANNEQUIN":
        excluded = props.export_excluded_mesh
        notes.append((f"Except {excluded.name}" if excluded is not None else "No mesh excluded yet", "FILTER"))
    elif props.export_scope == "CURRENT_COLLECTION":
        notes.append(("Context collection only", "FILTER"))
    if props.create_attribute_groups:
        notes.append(("Creates an attribute toggle group", "OUTLINER_COLLECTION"))
    hair_skeleton = cached_hair_skeleton()
    if hair_skeleton:
        notes.append((f"Sets hair EST entry {hair_skeleton}", "BONE_DATA"))
    column = layout.column(align=True)
    column.active = False
    for text, icon in notes:
        column.label(text=text, icon=icon)


# Luci_xiv's pages: (label, icon, URL).
LINKS = (
    ("XIV Mod Archive", "PACKAGE", "https://www.xivmodarchive.com/user/124593"),
    ("GitHub", "SCRIPT", "https://github.com/link-0402/XIV-Instant-Edit"),
    ("Bluesky", "COMMUNITY", "https://bsky.app/profile/xiv-luci.bsky.social"),
    ("Ko-fi", "FUND", "https://ko-fi.com/luci_xiv"),
)


class XIVIE_MT_links(Menu):
    """XIV Instant Edit on GitHub, and Luci_xiv's mods, Bluesky and Ko-fi"""

    bl_idname = "XIVIE_MT_links"
    bl_label = "Links"

    def draw(self, context: Context) -> None:
        for label, icon, url in LINKS:
            self.layout.operator("wm.url_open", text=label, icon=icon).url = url


class XIVIE_PT_session(Panel):
    bl_idname = "XIVIE_PT_session"
    bl_label = "Instant Edit"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_order = 0

    def draw_header_preset(self, context: Context) -> None:
        running, _port, _error = server_status()
        row = self.layout.row(align=True)
        status = row.row(align=True)
        status.alert = not running
        status.popover("XIVIE_PT_connection_popover", text="", icon="LINKED" if running else "UNLINKED")
        # The links sit at the right end of the header, unembossed, as in Luci_xiv's other add-ons.
        links = row.row(align=True)
        links.emboss = "NONE"
        links.menu("XIVIE_MT_links", text="", icon="URL")

    def draw(self, context: Context) -> None:
        layout = self.layout
        props = get_instant_edit_props()
        entries = _context_entries(context)
        if not entries:
            _draw_empty_session(layout, context, props)
            return
        try:
            ref = export_destination_context(context, persist=False)
        except ContextValidationError:
            ref = None

        if len(entries) > 1 or ref is None:
            _draw_context_rows(layout, context, props, entries, ref)
        if ref is None:
            if any(entry.ref is not None for entry in entries):
                layout.label(text="Choose the Context to export.", icon="INFO")
            else:
                _draw_wrapped_label(
                    layout, context, f"This Context cannot be exported: {entries[0].error}", "CANCEL")
            _draw_status_row(layout, props)
            return
        if len(entries) == 1:
            row = layout.row(align=True)
            row.label(text=entries[0].label, icon="FILE_3D")
            row.popover("XIVIE_PT_context_details_popover", text="", icon="THREE_DOTS")

        if ref.destination_state == "new_mod_required":
            _draw_wrapped_label(layout, context, "Quick Export creates a new Penumbra mod for this model.", "INFO")
        else:
            needs_option = _draw_target(layout, props, ref)
            _draw_destination(layout, props, ref, needs_option)
        _draw_readiness(layout, context, props, ref)
        _draw_export_button(layout, props, ref)
        _draw_scope_summary(layout, props)
        _draw_status_row(layout, props)


# ---- Mesh Groups panel ----------------------------------------------------------------


def _triangle_count(obj) -> int:
    obj.data.calc_loop_triangles()
    return len(obj.data.loop_triangles)


def _aligned_control(layout, label: str):
    row = layout.row(align=True).split(factor=0.25, align=True)
    label_row = row.row(align=True)
    label_row.alignment = "RIGHT"
    label_row.label(text=label)
    return row.row(align=True)


def _draw_empty_mesh_group(box, group: int) -> None:
    empty_row = box.box().row(align=True)
    empty_row.label(text=f"Mesh #{group}")
    empty_label = empty_row.row(align=True)
    empty_label.alignment = "RIGHT"
    empty_label.label(text="Empty", icon="MESH_DATA")


def _draw_mesh_group_header(mesh_box, group: int, vertices: int, dragged: bool) -> None:
    mesh_header = mesh_box.row(align=True).split(factor=0.4, align=True)
    mesh_id_row = mesh_header.row(align=True)
    mesh_id_row.label(text=f"Mesh #{group}", icon="MOUSE_LMB_DRAG" if dragged else "BLANK1")
    drag = mesh_id_row.operator(
        "xiv_ie.drag_mesh_order",
        text="",
        icon="MOUSE_LMB_DRAG" if dragged else "GRIP_V",
        emboss=dragged,
        depress=dragged,
    )
    drag.scope = "GROUP"
    drag.mesh_group = group
    vertex_row = mesh_header.row(align=True)
    vertex_row.alignment = "RIGHT"
    if vertices > _VERTEX_LIMIT:
        vertex_row.label(text="", icon="ERROR")
    elif vertices > _VERTEX_WARNING:
        vertex_row.label(text="", icon="INFO")
    vertex_row.label(text=f"Vertices: {vertices:,}")


def _draw_empty_part_slot(mesh_column, part_index: int) -> None:
    object_row = mesh_column.row(align=True).split(factor=0.4, align=True)
    name_row = object_row.row(align=True)
    name_row.label(text="", icon="BLANK1")
    name_row.label(text="Empty slot", icon="MESH_DATA")
    part_row = object_row.row(align=True)
    part_row.label(text="", icon="BLANK1")
    part_row.label(text=str(part_index))
    attribute_row = object_row.row(align=True)
    attribute_row.alignment = "EXPAND"
    attribute_row.label(text=" ", icon="BLANK1")


def _draw_mesh_part(mesh_column, part, duplicate: bool, mismatch: bool, selected: bool, dragged: bool) -> None:
    shown = lod_zero_objects(part.objects)
    object_row = mesh_column.row(align=True).split(factor=0.4, align=True)
    object_row.alert = mismatch and not dragged

    name_row = object_row.row(align=True)
    name_row.label(text="", icon="MOUSE_LMB_DRAG" if dragged else "BLANK1")
    select = name_row.operator(
        "xiv_ie.select_mesh_part",
        text=mesh_display_name(shown[0]),
        emboss=selected,
        depress=selected,
    )
    select.mesh_group = part.group
    select.mesh_part = part.part
    select.mesh_part_instance = part.instance_key

    part_row = object_row.row(align=True)
    part_row.label(text="", icon="ERROR" if duplicate or mismatch else "BLANK1")
    part_row.label(text=str(part.part))
    drag = part_row.operator(
        "xiv_ie.drag_mesh_order",
        text="",
        icon="MOUSE_LMB_DRAG" if dragged else "GRIP_V",
        emboss=dragged,
        depress=dragged,
    )
    drag.scope = "PART"
    drag.mesh_group = part.group
    drag.mesh_part = part.part
    drag.mesh_part_instance = part.instance_key

    attribute_row = object_row.row(align=True)
    attribute_row.alignment = "EXPAND"
    attributes = mesh_part_attributes(shown)
    if not attributes:
        attribute_row.label(text=" ", icon="BLANK1")
    for attribute in attributes:
        remove = attribute_row.operator("xiv_ie.mesh_attribute", text=attribute_display_name(attribute))
        remove.mesh_group = part.group
        remove.mesh_part = part.part
        remove.mesh_part_instance = part.instance_key
        remove.attribute = attribute
    add = attribute_row.operator("xiv_ie.mesh_attribute", text="", icon="ADD")
    add.mesh_group = part.group
    add.mesh_part = part.part
    add.mesh_part_instance = part.instance_key
    add.attribute = "NEW"


# Blender names an operator's properties after its bl_idname; this is
# xiv_ie.select_mesh_part, the operator behind every part name.
_PART_NAME_OPERATOR = "XIV_IE_OT_select_mesh_part"


def draw_mesh_part_context_menu(self, context: Context) -> None:
    """Add part actions to Blender's right-click menu of a Mesh Groups part name."""
    button = getattr(context, "button_operator", None)
    if button is None or button.rna_type.identifier != _PART_NAME_OPERATOR:
        return
    layout = self.layout
    layout.separator()
    duplicate = layout.operator("xiv_ie.duplicate_backfaces", icon="NORMALS_FACE")
    duplicate.mesh_group = button.mesh_group
    duplicate.mesh_part = button.mesh_part
    duplicate.mesh_part_instance = button.mesh_part_instance


def _draw_mesh_groups(layout, context: Context) -> None:
    """Draw the compact Mesh Studio overview adapted from Yet Another Addon."""
    box = layout.box()
    parts = scene_parts()
    if not parts:
        box.label(text="No visible FFXIV mesh groups.", icon="INFO")
        return
    drag_state = active_mesh_drag_state() or ("", -1, -1, None, "")
    objects = defaultdict(list)
    for part in parts:
        objects[part.group].extend(part.objects)
    selected = set(context.selected_objects)

    columns = box.row(align=True).split(factor=0.4, align=True)
    for title in ("OBJECT", "PART", "ATTR"):
        column = columns.row(align=True)
        column.alignment = "CENTER"
        column.label(text=title)

    total_triangles = 0
    shown_objects = set()
    # Draw exactly the rows mesh_list lays out, which is what lets a drag keep
    # the dragged row under the pointer.
    mesh_box = mesh_column = None
    for row in layout_rows(parts, maximum_group=drag_state[3]):
        if row.kind == "empty":
            _draw_empty_mesh_group(box, row.group)
        elif row.kind == "header":
            group_objects = objects[row.group]
            lod_zero = lod_zero_objects(group_objects)
            shown_objects.update(lod_zero)
            total_triangles += sum(_triangle_count(obj) for obj in lod_zero)
            mismatch = material_mismatch_parts(group_objects)
            uses = Counter(part.part for part in parts if part.group == row.group)
            mesh_box = box.box()
            _draw_mesh_group_header(
                mesh_box,
                row.group,
                sum(len(obj.data.vertices) for obj in lod_zero),
                drag_state[0] == "GROUP" and drag_state[1] == row.group,
            )
            mesh_box.separator(type="LINE", factor=0.2)
            mesh_column = mesh_box.column(align=True)
        elif row.kind == "gap":
            _draw_empty_part_slot(mesh_column, row.part_index)
        elif row.kind == "part":
            part = row.part
            _draw_mesh_part(
                mesh_column,
                part,
                uses[part.part] > 1,
                part.part in mismatch,
                any(obj in selected for obj in part.objects),
                drag_state[0] == "PART"
                and (drag_state[1], drag_state[2], drag_state[4]) == (part.group, part.part, part.instance_key),
            )
        else:
            mesh_box.separator(type="LINE", factor=0.5)
            paths = material_paths(objects[row.group])
            if not paths:
                text = "Add Mesh Properties"
            elif len(paths) > 1:
                text = "Multiple Materials"
            else:
                text = paths[0]
            material_row = _aligned_control(mesh_box.column(align=True), "Material:")
            material_row.operator("xiv_ie.mesh_material", text=text).mesh_group = row.group

    selected_triangles = sum(
        _triangle_count(obj)
        for obj in context.selected_objects
        if obj.type == "MESH" and obj in shown_objects
    )
    summary = box.row(align=True)
    summary.alignment = "RIGHT"
    count = f"{selected_triangles:,} / {total_triangles:,}" if selected_triangles else f"{total_triangles:,}"
    summary.label(text=f"Triangles: {count}")


class XIVIE_PT_mesh_groups(Panel):
    bl_idname = "XIVIE_PT_mesh_groups"
    bl_label = "Mesh Groups"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_order = 1

    def draw(self, context: Context) -> None:
        # During a reorder drag the rows show the planned IDs; the objects
        # themselves are only renamed when the drag is released.
        with planned_mesh_ids(active_mesh_drag_plan()):
            _draw_mesh_groups(self.layout, context)


# ---- Pose panel ---------------------------------------------------------------------


class XIVIE_MT_pose_actions(Menu):
    bl_idname = "XIVIE_MT_pose_actions"
    bl_label = "Pose Action"
    bl_options = {"SEARCH_ON_KEY_PRESS"}

    def draw(self, context: Context) -> None:
        layout = self.layout
        armature = pose_armature(context)
        if armature is None:
            layout.label(text="There is no armature to pose.", icon="INFO")
            return
        shown = shown_action(armature)
        layout.operator(
            "xiv_ie.show_pose_action", text="No Action",
            icon="RADIOBUT_ON" if shown is None else "RADIOBUT_OFF",
        ).action = ""
        actions = pose_actions(armature)
        if not actions:
            layout.label(text=f"No action keys the bones of {armature.name}.", icon="INFO")
            return
        layout.separator()
        for action in actions:
            layout.operator(
                "xiv_ie.show_pose_action", text=action.name,
                icon="RADIOBUT_ON" if action == shown else "RADIOBUT_OFF",
            ).action = action.name


class XIVIE_PT_pose(Panel):
    bl_idname = "XIVIE_PT_pose"
    bl_label = "Pose"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 2

    def draw_header_preset(self, context: Context) -> None:
        armature = pose_armature(context)
        if armature is not None:
            # Pressed while posed; a click shows the rest pose.
            self.layout.operator(
                "xiv_ie.toggle_rest_pose", text="", icon="POSE_HLT",
                depress=armature.data.pose_position == "POSE",
            )

    def draw(self, context: Context) -> None:
        layout = self.layout
        settings = get_settings()
        armature = pose_armature(context)
        column = layout.column()
        column.use_property_split = True
        column.use_property_decorate = False
        # Left empty, the field shows the armature it found.
        column.prop(settings, "pose_armature", text="Armature",
                    placeholder=armature.name if armature is not None else "None found")
        if armature is None:
            _draw_hint(layout, context, "Select an armature, or a mesh weighted to one.")
            return

        shown = shown_action(armature)
        _split_row(column, "Action").menu(
            "XIVIE_MT_pose_actions", text=shown.name if shown is not None else "No Action", icon="ACTION")
        if shown is not None:
            start, end = (int(round(value)) for value in shown.frame_range)
            if end > start:
                column.prop(context.scene, "frame_current", text=f"Frame ({start}-{end})")

        layout.row(align=True).prop(armature.data, "pose_position", expand=True)
        shortcut = layout.column()
        shortcut.use_property_split = True
        shortcut.use_property_decorate = False
        draw_shortcut(shortcut, context, "xiv_ie.toggle_rest_pose", "Toggle Shortcut")
        _draw_hint(layout, context, "Exports always use the rest pose.")


# ---- Simple Export / Import, Options, Backups, Tools ------------------------------


class XIVIE_PT_file_io(Panel):
    bl_idname = "XIVIE_PT_file_io"
    bl_label = "Simple Export / Import"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 3

    def draw(self, context: Context) -> None:
        layout = self.layout
        settings = get_settings()
        props = get_instant_edit_props()
        layout.row(align=True).prop(settings, "simple_io_tab", expand=True)
        column = layout.column()
        column.use_property_split = True
        column.use_property_decorate = False
        if settings.simple_io_tab == "IMPORT":
            column.row(align=True).prop(settings, "simple_import_armature", expand=True)
            if settings.simple_import_use_existing_skeleton:
                column.prop(settings, "simple_import_skeleton", text="Skeleton")
            layout.operator("xiv_ie.simple_import", text="Import MDL, FBX or glTF...", icon="IMPORT")
            return
        column.prop(settings, "export_directory", text="Folder")
        column.prop(settings, "export_name", text="File Name")
        column.row(align=True).prop(settings, "model_format", expand=True)
        column.prop(props, "export_scope", text="Parts")
        if props.export_scope == "VISIBLE_NO_MANNEQUIN":
            column.prop(props, "export_excluded_mesh", text="Except")
        layout.operator("xiv_ie.simple_export", text="Export Model File", icon="EXPORT")


class XIVIE_PT_options(Panel):
    bl_idname = "XIVIE_PT_options"
    bl_label = "Options"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 4

    def draw(self, context: Context) -> None:
        pass


class XIVIE_PT_options_import(Panel):
    bl_idname = "XIVIE_PT_options_import"
    bl_label = "Import"
    bl_parent_id = "XIVIE_PT_options"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"

    def draw(self, context: Context) -> None:
        settings = get_settings()
        column = self.layout.column(align=True)
        column.prop(settings, "resolve_mesh_group_conflicts")
        column.prop(settings, "simple_import_set_export_directory")


class XIVIE_PT_options_export(Panel):
    bl_idname = "XIVIE_PT_options_export"
    bl_label = "Export"
    bl_parent_id = "XIVIE_PT_options"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"

    def draw(self, context: Context) -> None:
        layout = self.layout
        settings = get_settings()
        column = layout.column(align=True)
        column.prop(settings, "keep_shapekeys")
        column.prop(settings, "reset_scaling_on_export")
        column.prop(settings, "calculate_heels_offset")
        _draw_hint(layout, context, "Every export requires triangulated meshes with bone weights.")


# ---- Vertex Data popover ------------------------------------------------------------

# (title, icon, what the data does, actions): the sections of TexTools' Modify
# Model Vertices dialog that the add-on offers.
_VERTEX_DATA_SECTIONS = (
    ("UV2", "GROUP_UVS",
     "Places decals such as crests and is usually empty. Hair checks opacity through it, "
     "so there it should match UV1.",
     (("CLEAR_UV2", "Clear UV2"), ("COPY_UV1_TO_UV2", "Copy UV1 to UV2"))),
    ("Vertex Color 1", "GROUP_VCOL",
     "Mask data whose use depends on the shader; white and opaque unless authored. "
     "Clearing it on skin or hair can cause seams or odd shadows.",
     (("CLEAR_COLOR1", "Clear Color"), ("CLEAR_ALPHA1", "Clear Alpha"))),
    ("Vertex Color 2", "GROUP_VCOL",
     "Fake wind; black unless the model should sway.",
     (("CLEAR_COLOR2", "Clear Color"),)),
    ("Hair Flow", "STRANDS",
     "The direction of the hair strands, which shapes highlights. Clearing it gives a "
     "generic, sharper highlight.",
     (("CLEAR_FLOW", "Clear Flow Data"),)),
)
_VERTEX_DATA_WRAP = 52


class XIVIE_PT_vertex_data_popover(Panel):
    bl_idname = "XIVIE_PT_vertex_data_popover"
    bl_label = "Vertex Data"
    bl_space_type = "VIEW_3D"
    bl_region_type = "HEADER"
    bl_ui_units_x = 18

    def draw(self, context: Context) -> None:
        layout = self.layout
        count = sum(obj.type == "MESH" for obj in context.selected_objects)
        if context.mode != "OBJECT":
            layout.label(text="Switch to Object Mode to change vertex data.", icon="INFO")
        elif not count:
            layout.label(text="Select the meshes to change.", icon="INFO")
        else:
            layout.label(text=f"Changes the {count} selected mesh{'es' if count != 1 else ''}.", icon="RESTRICT_SELECT_OFF")
        for title, icon, description, actions in _VERTEX_DATA_SECTIONS:
            layout.separator()
            layout.label(text=title, icon=icon)
            text = layout.column(align=True)
            text.active = False
            for line in textwrap.wrap(description, _VERTEX_DATA_WRAP):
                text.label(text=line)
            row = layout.row(align=True)
            for action, label in actions:
                row.operator("xiv_ie.vertex_data", text=label).action = action


def _grouped_backups(entries) -> list:
    """Group backups by original file name, newest group first."""
    groups: dict[str, list] = {}
    for entry in entries:
        groups.setdefault(entry.original_name, []).append(entry)
    return list(groups.items())


class XIVIE_PT_backups(Panel):
    bl_idname = "XIVIE_PT_backups"
    bl_label = "Backups"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 5

    def draw_header(self, context: Context) -> None:
        self.layout.prop(get_settings(), "backup_models_on_export", text="")

    def draw(self, context: Context) -> None:
        layout = self.layout
        settings = get_settings()
        try:
            ref = export_destination_context(context, persist=False)
        except ContextValidationError:
            ref = None
        if ref is not None and ref.destination_state == "new_mod_required":
            _draw_wrapped_label(layout, context, "Backups start once Quick Export has created the mod.", "INFO")
            return
        if not settings.backup_models_on_export:
            hint = layout.row()
            hint.active = False
            hint.label(text="New backups are off (checkbox above).", icon="INFO")
        folder, source = target_folder(settings, context, persist=False)
        if folder is None:
            _draw_wrapped_label(
                layout, context,
                "Choose a Simple Export folder to see its backups." if source == "Simple Export folder"
                else f"The {source} is unavailable.",
                "INFO",
            )
            return
        entries = list_backups(folder)
        if not entries:
            layout.label(text="No backups yet.", icon="INFO")
            return
        for original_name, group in _grouped_backups(entries):
            layout.label(text=original_name, icon="FILE_3D")
            column = layout.column(align=True)
            for entry in group:
                row = column.row(align=True)
                row.label(text=entry.created.astimezone().strftime("%Y-%m-%d %H:%M:%S"), icon="TIME")
                row.operator("xiv_ie.import_backup", text="", icon="IMPORT").backup_name = entry.path.name
                row.operator("xiv_ie.restore_backup", text="", icon="RECOVER_LAST").backup_name = entry.path.name
        layout.operator("xiv_ie.clear_backups", text="Delete All Backups...", icon="TRASH")


class XIVIE_PT_tools(Panel):
    bl_idname = "XIVIE_PT_tools"
    bl_label = "Tools"
    bl_category = CATEGORY
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 6

    def draw(self, context: Context) -> None:
        layout = self.layout
        layout.popover("XIVIE_PT_vertex_data_popover", text="Vertex Data", icon="GROUP_VCOL")
        layout.operator("xiv_ie.combine_armatures", text="Combine Armatures...", icon="ARMATURE_DATA")
        column = layout.column(align=True)
        column.operator("xiv_ie.convert_mesh_names", text="Move Mesh IDs to Front", icon="SORTALPHA")
        column.operator("xiv_ie.compact_context_parts", text="Fill Mesh Part Gaps", icon="SORTSIZE")
        layout.operator("xiv_ie.clear_contexts", text="Clear All Contexts...", icon="TRASH")
        row = layout.row(align=True)
        row.operator("xiv_ie.open_cache_folder", text="Cache", icon="FILE_FOLDER")
        row.operator("xiv_ie.open_diagnostics_folder", text="Diagnostics", icon="FILE_FOLDER")
