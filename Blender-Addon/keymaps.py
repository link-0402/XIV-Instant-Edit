"""Keyboard shortcuts the user can assign: they start without a key.

The items live in the add-on keyconfig; Blender keeps the key a user assigns
(in the preferences or in the sidebar) with its own keymap preferences. They
sit in "3D View Generic", so they work over the viewport and its sidebar in
every mode.
"""

import bpy


KEYMAP = "3D View Generic"
SHORTCUTS = (
    ("xiv_ie.instant_export", "Quick Export"),
    ("xiv_ie.toggle_rest_pose", "Toggle Rest Pose"),
)

_addon_items: list = []


def register_keymaps() -> None:
    keyconfig = bpy.context.window_manager.keyconfigs.addon
    if keyconfig is None:
        return
    keymap = keyconfig.keymaps.new(name=KEYMAP, space_type="VIEW_3D", region_type="WINDOW")
    for idname, _label in SHORTCUTS:
        _addon_items.append((keymap, keymap.keymap_items.new(idname, type="NONE", value="PRESS")))


def unregister_keymaps() -> None:
    for keymap, item in _addon_items:
        try:
            keymap.keymap_items.remove(item)
        except (ReferenceError, RuntimeError):
            pass
    _addon_items.clear()


def user_shortcut(context, idname: str):
    """The user's version of a shortcut, which is the one to show and edit, or None."""
    keyconfig = context.window_manager.keyconfigs.user
    keymap = keyconfig.keymaps.get(KEYMAP) if keyconfig is not None else None
    if keymap is None:
        return None, None
    item = next((item for item in keymap.keymap_items if item.idname == idname), None)
    return keymap, item


def draw_shortcut(layout, context, idname: str, text: str) -> None:
    """A key field for a shortcut; click it and press the keys to assign."""
    _keymap, item = user_shortcut(context, idname)
    if item is not None:
        layout.prop(item, "type", text=text, full_event=True)
