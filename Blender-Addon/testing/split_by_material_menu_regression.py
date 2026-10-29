"""Split by Material as the Material Properties specials menu (the v menu) runs it, then one undo.

smoke_export.assert_split_by_material checks the split itself (names, numbers, data) with the
operator called as a script calls it. This runs it the way a click on the menu entry does: the
entry the menu draws, invoked (not executed) with an undo step pushed after it, and the scene put
back by a single undo, which is what Ctrl+Z runs in Blender's window (ed.undo in the Screen keymap).
The click and the key press themselves need Blender's window: by hand.

Usage: python Blender-Addon/testing/run_blender_suites.py split_by_material_menu_regression
"""

import importlib
import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))

from blender_fixtures import addon_session  # noqa: E402


def _require(condition: bool, message: str, detail: str = "") -> None:
    if not condition:
        raise AssertionError(f"{message} ({detail})" if detail else message)
    print(f"[PASS] {message}")


class _MenuLayout:
    """Stands in for the menu's layout: keeps each operator entry with the context menus call it in."""

    def __init__(self):
        self.operator_context = "INVOKE_REGION_WIN"
        self.entries = []

    def separator(self):
        pass

    def operator(self, idname, **options):
        self.entries.append((idname, self.operator_context, options))


def _mesh(name, materials, face_slots):
    mesh = bpy.data.meshes.new(f"{name} Data")
    vertices, faces = [], []
    for index in range(len(face_slots)):
        start = len(vertices)
        vertices += [(index * 2, 0, 0), (index * 2 + 1, 0, 0), (index * 2 + 1, 1, 0), (index * 2, 1, 0)]
        faces.append((start, start + 1, start + 2, start + 3))
    mesh.from_pydata(vertices, [], faces)
    for material in materials:
        mesh.materials.append(material)
    mesh.polygons.foreach_set("material_index", face_slots)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def run_menu_split(addon) -> None:
    ui = importlib.import_module(f"{addon.__name__}.ui")
    operators = importlib.import_module(f"{addon.__name__}.operators")

    # The entry the v menu draws, and how a menu calls it.
    _require(ui.draw_material_context_menu in getattr(bpy.types.MATERIAL_MT_context_menu.draw, "_draw_funcs", ()),
             "the Material Properties specials menu draws the add-on's entries")
    layout = _MenuLayout()
    ui.draw_material_context_menu(type("Menu", (), {"layout": layout})(), bpy.context)
    entry = next((e for e in layout.entries if e[0] == "xiv_ie.split_by_material"), None)
    _require(entry is not None, "the menu has Split by Material", str(layout.entries))
    _require(entry[1].startswith("INVOKE"), "the menu invokes it, as a click does", entry[1])
    options = operators.XIVIE_OT_split_by_material.bl_options
    _require({"REGISTER", "UNDO"} <= set(options), "the operator pushes an undo step when a menu runs it", str(options))

    # A body mesh holds number 0; the outfit has three materials and a LOD.
    skin, cloth, lace, silk = (bpy.data.materials.new(name) for name in ("Menu Skin", "Menu Cloth", "Menu Lace", "Menu Silk"))
    _mesh("0.0 Body", (skin,), (0,))
    outfit = _mesh("Outfit", (cloth, lace, silk), (0, 1, 2, 1))
    bpy.context.view_layer.update()
    for obj in bpy.context.view_layer.objects:
        obj.select_set(obj.name == "Outfit")
    bpy.context.view_layer.objects.active = outfit
    # The user's last action before the click, on the undo stack as Blender's window keeps it.
    bpy.ops.ed.undo_push(message="set up the outfit")

    # The click: the menu's entry, invoked, with the undo push a menu click makes.
    idname = entry[0].split(".")
    result = getattr(getattr(bpy.ops, idname[0]), idname[1])("INVOKE_DEFAULT", True)
    _require(result == {"FINISHED"}, "Split by Material from the menu finishes", str(result))
    pieces = sorted(obj.name for obj in bpy.context.scene.objects if obj.type == "MESH" and obj.name != "0.0 Body")
    print("pieces:", pieces)
    _require(len(pieces) == 3, "each material becomes its own mesh", str(pieces))
    numbers = []
    for name in pieces:
        obj = bpy.data.objects[name]
        used = {obj.material_slots[p.material_index].material.name for p in obj.data.polygons}
        _require(len(used) == 1, f"{name} uses one material", str(used))
        numbers.append(name.split(" ", 1)[0].split(".")[0])
    _require(len(set(numbers)) == 3 and "0" not in numbers, "each gets a free mesh number (0 is the body's)", str(numbers))

    # One Ctrl+Z (ed.undo): everything is back.
    keymap = bpy.context.window_manager.keyconfigs.default.keymaps.get("Screen") if bpy.context.window_manager.keyconfigs.default else None
    if keymap is not None and len(keymap.keymap_items) > 0:
        _require(any(item.idname == "ed.undo" and item.type == "Z" and item.ctrl and not item.shift for item in keymap.keymap_items),
                 "Ctrl+Z runs ed.undo in Blender's window")
    else:
        print("[SKIP] the default keymap isn't loaded in background mode: Ctrl+Z is ed.undo there by Blender's own keymap")
    bpy.ops.ed.undo()
    # Every reference from before the undo points into freed memory: look things up by name again.
    meshes = sorted(obj.name for obj in bpy.context.scene.objects if obj.type == "MESH")
    _require(meshes == ["0.0 Body", "Outfit"], "one undo puts the meshes back", str(meshes))
    outfit = bpy.data.objects["Outfit"]
    _require(len(outfit.material_slots) == 3 and len(outfit.data.polygons) == 4,
             "the outfit has its three materials and four faces again",
             f"{len(outfit.material_slots)} slots, {len(outfit.data.polygons)} faces")


def run() -> None:
    with addon_session("_xiv_instant_edit_split_menu") as addon:
        run_menu_split(addon)


if __name__ == "__main__":
    run()
