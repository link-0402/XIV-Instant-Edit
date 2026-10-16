"""Exports at every size picked in Magic Fit's Resize panel, through Magic Fit's API (its api.py).

Magic Fit resizes the clothing to each size as a shape key and shows one size at a time; an export
reads the meshes at rest pose, so it writes the size shown. A model's copies of the devkit's body
parts (the skin under a top, a navel piercing) are exported as the devkit's own parts, which take
each size with their own weights (Magic Fit's api.VERSION 3 on). Magic Fit is optional: it is looked
up when needed, never imported when this module loads, and without it exports work as before.
"""

import importlib
import re
import time
from collections import Counter
from contextlib import contextmanager, nullcontext

import bpy

from . import steps
from .props import get_instant_edit_props

# The api.VERSION of Magic Fit this module was written for.
API_VERSION = 1
# The api.VERSION from which Magic Fit resizes a size at a time (api.Resizer) and finds the devkit
# parts a model's meshes stand for (substitute=True).
SUBSTITUTE_VERSION = 3
NO_SIZES = "Pick sizes in Magic Fit's Resize panel."
# The most sizes Instant Edit takes in one export (its ExportServer.Sizes MaxSizes).
MAX_SIZES = 64
TOO_MANY = f"Pick at most {MAX_SIZES} sizes."
_INVALID_FILE_CHARACTERS = frozenset('<>:"/\\|?*')


def magic_fit_api():
    """Magic Fit's api module when an enabled Magic Fit offers it, whichever repository installed it."""
    for name in bpy.context.preferences.addons.keys():
        if name.rsplit(".", 1)[-1] == "magic_fit":
            try:
                api = importlib.import_module(name + ".api")
            except ImportError:
                return None
            return api if getattr(api, "VERSION", 0) >= API_VERSION else None
    return None


def wanted(props=None) -> bool:
    """Whether exports go out at Magic Fit's sizes: the export option is on and Magic Fit can do it."""
    props = props if props is not None else get_instant_edit_props()
    return bool(getattr(props, "export_sizes", False)) and magic_fit_api() is not None


def _picked(api, scene=None) -> list:
    """Magic Fit's picked sizes. Magic Fit refuses picks that make more sizes than it lists (64, as many as an
    export takes): that raises ValueError(TOO_MANY)."""
    try:
        return list(api.picked_sizes(scene))
    except getattr(api, "ResizeError", ()) as error:
        raise ValueError(TOO_MANY) from error


def picked_names(scene=None) -> list[str]:
    """The names of the sizes picked in Magic Fit's Resize panel; none without Magic Fit or sizes, or with too many."""
    api = magic_fit_api()
    if api is None:
        return []
    try:
        return option_names(_picked(api, scene))
    except (AttributeError, ReferenceError, RuntimeError, TypeError, ValueError):
        return []


def picked_issue(scene=None) -> str:
    """Why an export can't go out at the sizes picked in Magic Fit, or "" when it can."""
    api = magic_fit_api()
    if api is None:
        return ""
    try:
        names = option_names(_picked(api, scene))
    except ValueError as error:
        if str(error) == TOO_MANY:
            return TOO_MANY
        names = []
    except (AttributeError, ReferenceError, RuntimeError, TypeError):
        names = []
    return option_name_issue(names) if names else NO_SIZES


# The YAB devkit's words for its sizes, as Magic Fit names them (its sizing._Yab).
_CHEST = ("Large", "Medium", "Small", "Masc")
_LAVA_CHEST = ("Omoi", "Teardrop", "Cupcake", "Masc")
_LEGS = ("Melon", "Skull", "Yanilla", "Masc Legs", "Lava Legs", "Mini")
_YAB_SETTINGS = ("chest_size", "leg_size", "lavabod", "rue")


def _word(words, value) -> str:
    return words[min(max(int(value), 0), len(words) - 1)]


def _yab_name(values, chest: bool, legs: bool) -> str:
    """A YAB size's option name: the body, then the chest and legs sizes asked for, like 'Lava Omoi - Skull'."""
    lava, rue = bool(values.get("lavabod")), bool(values.get("rue"))
    body = "LaRue" if lava and rue else "Lava" if lava else "Rue" if rue else "YAB"
    parts = []
    if chest and values.get("chest_size") is not None:
        parts.append(_word(_LAVA_CHEST if lava else _CHEST, values["chest_size"]))
    if legs and values.get("leg_size") is not None:
        parts.append(_word(_LEGS, values["leg_size"]))
    return f"{body} {' - '.join(parts)}" if parts else body


def option_names(picked) -> list[str]:
    """The option (and file) names of Magic Fit's sizes ``picked``: for the YAB devkit, the body and then
    the size, like 'Rue Large', or 'YAB Large - Melon' when chest and legs both vary; Magic Fit's own
    names for other bodies, and for sizes that would still share a name."""
    picked = list(picked)
    values = [getattr(size, "values", None) or {} for size in picked]
    if not picked or not all(any(name in size for name in _YAB_SETTINGS) for size in values):
        return [size.name for size in picked]
    legs = len({size.get("leg_size") for size in values}) > 1
    chest = not legs or len({size.get("chest_size") for size in values}) > 1
    names = [_yab_name(size, chest, legs) for size in values]
    # Settings left out (Buff, Alt Hips...) can make sizes alike: add the other words of Magic Fit's name,
    # but not its 'Base' (a size changing nothing).
    counts = Counter(name.casefold() for name in names)
    for index, name in enumerate(names):
        if counts[name.casefold()] > 1:
            known = {word.casefold() for word in name.split()} | {"base"}
            names[index] = " ".join([name] + [word for word in picked[index].name.split()
                                              if word.casefold() not in known])
    counts = Counter(name.casefold() for name in names)
    return [size.name if counts[name.casefold()] > 1 else name for size, name in zip(picked, names)]


def export_names(props=None) -> list[str] | None:
    """The sizes exports go out at, for display: None when they go out as they are."""
    return picked_names() if wanted(props) else None


def batch_count(props=None) -> int:
    """How many sizes an export goes out at when it's a batch worth a warning (two or more), else 0."""
    names = export_names(props)
    return len(names) if names is not None and len(names) > 1 else 0


def batch_warning(count: int) -> list[str]:
    """The lines warning before an export at ``count`` sizes."""
    return [f"This exports {count} sizes one after another.", "It can take a while. Esc cancels."]


def file_part(name: str) -> str:
    """A size's part of a file name: its name, characters file names can't have replaced."""
    cleaned = "".join("-" if char in _INVALID_FILE_CHARACTERS or ord(char) < 32 else char for char in name)
    return cleaned.strip().rstrip(".").strip()


def option_name_issue(names) -> str:
    """Why sizes of these names can't be Penumbra options, or "" when they can."""
    names = list(names)
    if len(names) > MAX_SIZES:
        return TOO_MANY
    seen = set()
    for name in names:
        if not name or name != name.strip() or len(name) > 120 or any(ord(char) < 32 for char in name) \
                or not file_part(name):
            return f"The Magic Fit size {name!r} can't be an option name."
        if name.casefold() in seen:
            return f"Two Magic Fit sizes are named {name}."
        seen.add(name.casefold())
    return ""


@contextmanager
def _object_mode():
    """Leave Edit Mode for the block, which shows no shape key mix, so it would export no size; then
    make the active object active again (an MDL export leaves its own copy active, then deletes it)
    and come back."""
    view_layer = bpy.context.view_layer
    active = view_layer.objects.active
    edit = bpy.context.mode == "EDIT_MESH"
    if edit:
        bpy.ops.object.mode_set(mode="OBJECT")
    try:
        yield
    finally:
        try:
            if active is not None and active.name in view_layer.objects:
                view_layer.objects.active = active
                if edit:
                    bpy.ops.object.mode_set(mode="EDIT")
        except (ReferenceError, RuntimeError) as error:
            print(f"XIV Instant Edit: could not return to the object being edited: {error}")


class Step(steps.Step):
    """Where an export at sizes is: what it's ``doing`` to the size named ``name``, the ``sized``-th of
    ``count``."""

    def __init__(self, sized: int, count: int, name: str, doing: str):
        self.sized, self.count, self.name, self.doing = sized, count, name, doing
        text = f"{doing}." if sized >= count else f"{doing} {name} ({sized + 1} of {count})."
        super().__init__(text, sized * 2 + (doing != "Resizing to"), count * 2 + 1)

    def cancelled(self) -> str:
        return f"Size export cancelled after {self.sized} of {self.count} sizes."


# Magic Fit's note on how long a resize took: one per size here, replaced by one for all of them.
_RESIZE_TIME_NOTE = re.compile(r"Resized \d+ mesh\(es\) to \d+ size\(s\) in [\d.]+ s")


def _resizing(api, objects, picked):
    """How Magic Fit resizes ``objects`` to the sizes ``picked``: (resize(size, name) returning its
    SizedMeshes, the meshes to export, {devkit part: the mesh it's exported in place of}). From
    SUBSTITUTE_VERSION on, through an api.Resizer that reads the meshes once and swaps the model's
    copies of the devkit's parts for the parts; before it, with a resize_collection per size."""
    objects = list(objects)
    if getattr(api, "VERSION", 0) < SUBSTITUTE_VERSION:
        def resize(size, name):
            try:
                resized = api.resize_collection(objects, [size])
            except api.ResizeError as error:
                raise ValueError(str(error)) from error
            sized = next((item for item in resized if item.name == size.name), None)
            if sized is None:
                raise ValueError(f"Magic Fit didn't resize to {name}.")
            return sized
        return resize, objects, {}
    try:
        resizer = api.Resizer(objects, picked, substitute=True)
        substitutes = dict(resizer.substitutes)
        if len(set(substitutes.values())) < len(substitutes):
            # Two meshes stand for one part (a LOD0 and a LOD1 copy): the part can be exported as
            # only one of them, so the copies are resized themselves, as before substitutes.
            resizer = api.Resizer(objects, picked, substitute=False)
            substitutes = {}
    except api.ResizeError as error:
        raise ValueError(str(error)) from error

    def resize(size, _name):
        try:
            return resizer.resize(size)
        except api.ResizeError as error:
            raise ValueError(str(error)) from error
    return (resize, [substitutes.get(obj, obj) for obj in objects],
            {part: obj for obj, part in substitutes.items()})


def _material(obj):
    """The material path the MDL writer gives mesh ``obj``: its xiv_material, else its first material's name."""
    if "xiv_material" in obj:
        return obj["xiv_material"]
    return obj.material_slots[0].name if len(obj.material_slots) else None


def _layer_path(layer, collection) -> list:
    """The layer collections from ``layer`` down to the one of ``collection``, or [] when it's not under it."""
    if layer.collection == collection:
        return [layer]
    for child in layer.children:
        path = _layer_path(child, collection)
        if path:
            return [layer] + path
    return []


# What a model's mesh is called while a devkit part takes its name.
_STEPPED_ASIDE = "XIV IE stepped aside"


def _export_setting(key) -> bool:
    """Whether custom property ``key`` is one the MDL export reads from a mesh: its attributes,
    transparency and heels offsets (xiv_material is handled on its own)."""
    from ..mesh.heels import is_heels_offset_attribute
    from ..xivpy.model import is_model_attribute_name
    return is_model_attribute_name(key) or key == "xiv_transparency" or is_heels_offset_attribute(key)


def _put_settings(part, kept) -> None:
    """Give ``part`` back its own export settings ``kept``, dropping the ones it was lent."""
    for key in [key for key in part.keys() if _export_setting(key)]:
        del part[key]
    for key, value in kept.items():
        part[key] = value


@contextmanager
def _standing_in(stand_ins):
    """For the block, export each devkit part of ``stand_ins`` ({part: the model's mesh it's exported in
    place of}) as that mesh: shown in the viewport, so the export can copy it with its modifiers (a
    devkit hides its parts in excluded collections), under that mesh's name and with its material,
    attributes, transparency and heels offsets. Everything is put back afterwards."""
    undo = []
    view_layer = bpy.context.view_layer

    def turn(owner, attribute, value):
        old = getattr(owner, attribute)
        if old != value:
            setattr(owner, attribute, value)
            undo.append(lambda: setattr(owner, attribute, old))
    try:
        for part, obj in stand_ins.items():
            for collection in part.users_collection:
                for layer in _layer_path(view_layer.layer_collection, collection):
                    turn(layer, "exclude", False)
                    turn(layer, "hide_viewport", False)
                    if layer.collection != view_layer.layer_collection.collection:
                        turn(layer.collection, "hide_viewport", False)
            turn(part, "hide_viewport", False)
            if part.name in view_layer.objects and part.hide_get(view_layer=view_layer):
                part.hide_set(False, view_layer=view_layer)
                undo.append(lambda part=part: part.hide_set(True, view_layer=view_layer)
                            if part.name in view_layer.objects else None)
            material = _material(obj)
            if material is not None and part.get("xiv_material") != material:
                had, old = "xiv_material" in part, part.get("xiv_material")
                part["xiv_material"] = material
                undo.append(lambda part=part, had=had, old=old: part.__setitem__("xiv_material", old)
                            if had else part.pop("xiv_material", None))
            # The mesh's other export settings: attributes, transparency, heels offsets.
            kept = {key: part[key] for key in part.keys() if _export_setting(key)}
            for key in kept:
                del part[key]
            for key in [key for key in obj.keys() if _export_setting(key)]:
                part[key] = obj[key]
            undo.append(lambda part=part, kept=kept: _put_settings(part, kept))
            # The writer reads the mesh's group and part (and LOD) from its name, so the part takes
            # the mesh's name; the mesh steps aside meanwhile.
            mesh_name, part_name = obj.name, part.name
            obj.name = _STEPPED_ASIDE
            undo.append(lambda obj=obj, mesh_name=mesh_name: setattr(obj, "name", mesh_name))
            part.name = mesh_name
            undo.append(lambda part=part, part_name=part_name: setattr(part, "name", part_name))
        yield
    finally:
        for restore in reversed(undo):
            try:
                restore()
            except (ReferenceError, RuntimeError) as error:
                print(f"XIV Instant Edit: could not put a devkit part back as it was: {error}")


def export_steps(objects, export, session=None):
    """Resize ``objects``, the meshes an export writes, to each size picked in Magic Fit, and call
    ``export(sized, index, name)`` (``name`` from option_names) while each size shows, a size at a time: a generator yielding a Step
    before each resize and export, so Blender can draw between them (see steps.start). ``session``, when
    given, makes a context manager the exports run in from the meshes to export (``objects``, the
    devkit's parts in place of the model's copies of them), entered once out of Edit Mode before the
    first size (mesh.export.ShapeExports). Returns Magic Fit's SizedMeshes, in order.

    Raises ValueError with a message for the user.
    """
    api = magic_fit_api()
    if api is None:
        raise ValueError("Magic Fit isn't enabled.")
    picked = _picked(api)
    if not picked:
        raise ValueError(NO_SIZES)
    names = option_names(picked)
    issue = option_name_issue(names)
    if issue:
        raise ValueError(issue)
    total = len(picked)
    results = []
    resizing = 0.0
    with _object_mode():
        yield Step(0, total, names[0], "Resizing to")
        started = time.perf_counter()
        resize, exported, stand_ins = _resizing(api, objects, picked)
        resizing += time.perf_counter() - started
        with _standing_in(stand_ins), (session(exported) if session is not None else nullcontext()):
            for index, (size, name) in enumerate(zip(picked, names)):
                if index:
                    yield Step(index, total, name, "Resizing to")
                started = time.perf_counter()
                sized = resize(size, name)
                resizing += time.perf_counter() - started
                yield Step(index, total, name, "Exporting")
                with sized.shown():
                    export(sized, index, name)
                results.append(sized)
    for sized in results:
        sized.notes = [note for note in sized.notes if not _RESIZE_TIME_NOTE.fullmatch(note)]
    results[0].notes.append(
        f"Resized {len(results[0].keys)} mesh(es) to {total} size(s) in {resizing:.1f} s")
    return results


def export_each(objects, export, session=None) -> list:
    """export_steps run to its end at once."""
    return steps.run(export_steps(objects, export, session))


def default_name(results, names) -> str:
    """The size an option group turns on by default, of ``results`` named ``names``: the one the body
    shows, else the first."""
    return next((name for sized, name in zip(results, names) if sized.size is not None and sized.size.now),
                names[0])


def notes(results) -> list[str]:
    """Magic Fit's messages about the resize, each once."""
    return list(dict.fromkeys(note for sized in results for note in sized.notes))
