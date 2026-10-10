"""Exports at Magic Fit's sizes (instant_edit/sizes.py).

Quick Export's request with an option per size, Simple Export's file per size and the refusals,
against a stand-in for Magic Fit's API; then against Magic Fit itself, when its repository is found
(MAGIC_FIT_ROOT, else a "Magic Fit" folder beside this repository): exported models checked against
the clothing's shapes, the size the body shows included while the clothing shows another size.
"""

import importlib
import json
import os
import shutil
import sys
import tempfile
import types
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace

import bmesh
import bpy
import numpy as np
from mathutils.kdtree import KDTree

sys.path.insert(0, str(Path(__file__).resolve().parent))

from blender_fixtures import addon_session, temporary_scene_data  # noqa: E402

GAME_PATH = "chara/equipment/e0001/model/c0101e0001_top.mdl"
MATERIAL = "/mt_c0101e0001_top_a.mtrl"
FAKE_PACKAGE = "xiv_ie_fake_addons"
GROUP_ID = "group:11111111-1111-1111-1111-111111111111"


def _require(condition: bool, message: str, detail: str = "") -> None:
    if not condition:
        raise AssertionError(f"{message} ({detail})" if detail else message)
    print(f"[PASS] {message}")


@contextmanager
def _addon_entry(module_name: str):
    """List a module among the enabled add-ons, as installing Magic Fit does, for the block."""
    entry = bpy.context.preferences.addons.new()
    entry.module = module_name
    try:
        yield entry
    finally:
        bpy.context.preferences.addons.remove(entry)


def _positions(obj, key=None) -> np.ndarray:
    data = obj.data.shape_keys.key_blocks[key].data if key else obj.data.vertices
    co = np.empty(len(data) * 3)
    data.foreach_get("co", co)
    return co.reshape(-1, 3)


def _rig(name: str = "SizesSkeleton"):
    armature = bpy.data.objects.new(name, bpy.data.armatures.new(name + "Data"))
    bpy.context.scene.collection.objects.link(armature)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="EDIT")
    bone = armature.data.edit_bones.new("root")
    bone.head, bone.tail = (0, 0, 0), (0, 0, 1)
    bpy.ops.object.mode_set(mode="OBJECT")
    return armature


def _make_exportable(obj, armature, name: str) -> None:
    """Make ``obj`` a part Quick Export takes: named like one, triangles, weighted, a UV map, a material."""
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    bmesh.ops.triangulate(mesh, faces=mesh.faces[:])
    mesh.to_mesh(obj.data)
    mesh.free()
    obj.name = name
    obj.parent = armature
    obj.modifiers.new(name="Armature", type="ARMATURE").object = armature
    obj.vertex_groups.new(name="root").add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")
    if not obj.data.uv_layers:
        obj.data.uv_layers.new(name="uv0")
    obj.data.materials.append(bpy.data.materials.new("SizesMaterial"))
    obj["xiv_material"] = MATERIAL


def _context(addon, objects, context_id: str = "sizes-context"):
    context_module = importlib.import_module(f"{addon.__name__}.instant_edit.context")
    collection = context_module.create_collection(bpy.context.scene, {
        "context_id": context_id,
        "schema": context_module.SCHEMA,
        "version": context_module.VERSION,
        "plugin_instance_id": "sizes-plugin",
        "capability": "sizes-capability",
        "source_game_path": GAME_PATH,
        "managed_destination": "C:/Penumbra/Sizes",
        "target_file_path": "C:/Penumbra/Sizes/model.mdl",
        "source_mod_directory": "SizesMod",
        "source_mod_name": "Sizes Mod",
        "backup_target_id": "a" * 64,
        "backup_directory": "C:/Cache/Backups/" + "a" * 64,
        "resource_manifest_version": 2,
        "resource_manifest_status": "ready",
        "callback_port": 42428,
    })
    for index, obj in enumerate(objects):
        for parent in tuple(obj.users_collection):
            parent.objects.unlink(obj)
        collection.objects.link(obj)
        context_module.tag_object(obj, {
            "context_id": context_id,
            "schema": context_module.SCHEMA,
            "version": context_module.VERSION,
            "xiv_material": MATERIAL,
            "original_material": "SizesMaterial",
            "material_index": 0,
            "mesh_index": 0,
            "submesh_index": index,
        })
    props = bpy.context.scene.xiv_ie_instant_edit_props
    props.context_id = context_id
    props.export_destination = context_id
    return collection


class FakePlugin:
    """The plugin's /export/sizes and /variant-targets, through the add-on's HTTP client: keeps each
    request and a copy of its files, and answers like the plugin."""

    def __init__(self, addon, capture: Path):
        self.http = importlib.import_module(f"{addon.__name__}.instant_edit.plugin_http")
        self.capture = capture
        self.payloads = []
        self.files = []
        self.groups = []
        self._original = None

    def __enter__(self):
        self._original = self.http.urllib.request.urlopen
        self.http.urllib.request.urlopen = self._urlopen
        return self

    def __exit__(self, *_exc):
        self.http.urllib.request.urlopen = self._original
        return False

    def _urlopen(self, request, timeout=0):
        payload = json.loads(request.data.decode("utf-8"))
        if request.full_url.endswith("/variant-targets"):
            return _Response({"ok": True, "groups": self.groups})
        if not request.full_url.endswith("/export/sizes"):
            raise OSError(f"unexpected request {request.full_url}")
        self.payloads.append(payload)
        copies = {}
        for index, size in enumerate(payload["sizes"]):
            copy = self.capture / f"{len(self.payloads)}_{index}.mdl"
            shutil.copyfile(size["filePath"], copy)
            copies[size["name"]] = copy
        self.files.append(copies)
        group = payload.get("variantGroupName") or next(
            item["name"] for item in self.groups if item["id"] == payload["variantTargetId"])
        options = [{"id": f"option:11111111-1111-1111-1111-111111111111:{index:08d}-0000-0000-0000-000000000000",
                    "name": size["name"], "modelPath": f"Files/top {size['name']}.mdl"}
                   for index, size in enumerate(payload["sizes"])]
        self.groups = [{"id": GROUP_ID, "name": group, "options": options}]
        return _Response({
            "ok": True, "code": "sizes_exported", "message": "done", "warnings": [],
            "targetFilePath": f"C:/Penumbra/Sizes/Files/top {payload['defaultSize']}.mdl",
            "destinationName": group,
        })


class _Response:
    status = 200

    def __init__(self, body: dict):
        self.body = json.dumps(body).encode("utf-8")

    def __enter__(self):
        return self

    def __exit__(self, *_exc):
        return False

    def getcode(self):
        return self.status

    def read(self, _size=-1):
        return self.body


def _model_positions(addon, path: Path) -> np.ndarray:
    """The vertex positions of an exported model, imported back."""
    importer = importlib.import_module(f"{addon.__name__}.io.model.importer")
    created = []
    importer.ModelImport.from_file(str(path), "sizes-roundtrip", select_objects=False, created_objects=created)
    meshes = [obj for obj in created if obj.type == "MESH"]
    positions = np.concatenate([
        np.array([tuple(obj.matrix_world @ vertex.co) for vertex in obj.data.vertices]) for obj in meshes])
    for obj in created:
        bpy.data.objects.remove(obj, do_unlink=True)
    return positions


def _distance(found: np.ndarray, expected: np.ndarray) -> float:
    """How far the farthest of ``expected`` is from the closest of ``found``."""
    tree = KDTree(len(found))
    for index, co in enumerate(found):
        tree.insert(co, index)
    tree.balance()
    return max(tree.find(co)[2] for co in expected)


# ---- A stand-in for Magic Fit's API -----------------------------------------------------------


class FakeSize:
    def __init__(self, name, now=False):
        self.name, self.values, self.now = name, {}, now


class FakeSized:
    """Magic Fit's SizedMeshes: shows its key on each mesh for the block."""

    def __init__(self, name, size, keys, notes):
        self.name, self.size, self.keys, self.parts, self.notes, self.own = name, size, keys, {}, notes, {}

    @contextmanager
    def shown(self):
        undo = []
        try:
            for obj, key in self.keys.items():
                if key is not None:
                    block = obj.data.shape_keys.key_blocks[key]
                    undo.append((block, block.value))
                    block.value = 1.0
            yield self
        finally:
            for block, value in reversed(undo):
                block.value = value


def fake_api(version: int = 1):
    api = types.ModuleType(f"{FAKE_PACKAGE}.magic_fit.api")
    api.VERSION = version
    api.picks = [("Large", True), ("Medium", False)]
    api.calls = []
    api.failure = None
    api.too_many = False

    class ResizeError(Exception):
        pass

    api.ResizeError = ResizeError

    def picked_sizes(scene=None):
        # As Magic Fit: picks making more than its 64 sizes are refused.
        if api.too_many:
            raise ResizeError("The sizes picked make more than 64 sizes: pick fewer")
        return [FakeSize(name, now) for name, now in api.picks]

    def resize_collection(target, sizes=None, *, scene=None, body=None, progress=None):
        api.calls.append(list(target))
        if api.failure:
            raise ResizeError(api.failure)
        if progress is not None:
            progress(0.5)
            progress(1.0)
        picks = api.picks if sizes is None else [(size.name, size.now) for size in sizes]
        # Every size but the body's own shows the mesh's Medium key.
        return [FakeSized(name, FakeSize(name, now), {obj: None if now else "Medium" for obj in target},
                          ["Resized in 0.1 s"] if index == 0 else [])
                for index, (name, now) in enumerate(picks)]

    api.picked_sizes = picked_sizes
    api.resize_collection = resize_collection
    return api


@contextmanager
def installed(api):
    """``api`` as the api module of an enabled add-on named like Magic Fit, under another package."""
    package = types.ModuleType(FAKE_PACKAGE)
    package.__path__ = []
    magic_fit = types.ModuleType(f"{FAKE_PACKAGE}.magic_fit")
    magic_fit.__path__ = []
    modules = {FAKE_PACKAGE: package, f"{FAKE_PACKAGE}.magic_fit": magic_fit, api.__name__: api}
    sys.modules.update(modules)
    try:
        with _addon_entry(f"{FAKE_PACKAGE}.magic_fit"):
            yield api
    finally:
        for name in modules:
            sys.modules.pop(name, None)


def run_stand_in(addon) -> None:
    # The plugin is faked from the start: choosing the Context loads its targets.
    with tempfile.TemporaryDirectory(prefix="xiv-ie-sizes-") as capture, FakePlugin(addon, Path(capture)) as plugin:
        _run_stand_in(addon, plugin)


def _run_stand_in(addon, plugin) -> None:
    sizes = importlib.import_module(f"{addon.__name__}.instant_edit.sizes")
    ops = importlib.import_module(f"{addon.__name__}.instant_edit.ops")
    props_module = importlib.import_module(f"{addon.__name__}.instant_edit.props")
    props = bpy.context.scene.xiv_ie_instant_edit_props

    _require("CON_SIZELIKE" in bpy.types.UILayout.bl_rna.functions["label"].parameters["icon"].enum_items.keys(),
             "the sizes icon exists")
    _require(sizes.file_part('Rue/Buff: "Big"') == "Rue-Buff- -Big-" and sizes.file_part("Large.") == "Large",
             "size names become file names without characters files can't have")
    _require(sizes.option_name_issue(["Large", "Medium"]) == "" and sizes.option_name_issue(["Large", "large"]) and
             sizes.option_name_issue([" Large"]) and sizes.option_name_issue(["..."]),
             "size names that can't be options are found")
    many = [f"Size {index}" for index in range(sizes.MAX_SIZES + 1)]
    _require(sizes.option_name_issue(many[:-1]) == "" and sizes.option_name_issue(many) == "Pick at most 64 sizes.",
             "more sizes than Instant Edit takes are found before the export")

    def yab(name, **values):
        size = FakeSize(name)
        size.values = {"chest_size": 1.0, "leg_size": 0.0, "lavabod": 0.0, "rue": 0.0, **values}
        return size

    _require(sizes.option_names([yab("Large", chest_size=0.0), yab("Medium"), yab("Large Rue", chest_size=0.0, rue=1.0),
                                 yab("Omoi Lavabod", chest_size=0.0, lavabod=1.0),
                                 yab("Omoi LaRue", chest_size=0.0, lavabod=1.0, rue=1.0)])
             == ["YAB Large", "YAB Medium", "Rue Large", "Lava Omoi", "LaRue Omoi"],
             "YAB sizes are named by body, then chest size")
    _require(sizes.option_names([yab("Large", chest_size=0.0), yab("Large Skull", chest_size=0.0, leg_size=1.0),
                                 yab("Teardrop Lavabod Skull", leg_size=1.0, lavabod=1.0)])
             == ["YAB Large - Melon", "YAB Large - Skull", "Lava Teardrop - Skull"],
             "chest and legs are both named when both vary")
    _require(sizes.option_names([yab("Base"), yab("Skull", leg_size=1.0), yab("Masc Legs Rue", leg_size=3.0, rue=1.0)])
             == ["YAB Melon", "YAB Skull", "Rue Masc Legs"], "only the legs are named when only they vary")
    _require(sizes.option_names([yab("Rue", rue=1.0), yab("Base")]) == ["Rue Medium", "YAB Medium"],
             "a body change alone still names the chest size")
    _require(sizes.option_names([yab("Base"), yab("Buff", buff=1.0)]) == ["YAB Medium", "YAB Medium Buff"],
             "sizes alike but for unnamed settings take Magic Fit's other words")
    _require(sizes.option_names([FakeSize("Large"), FakeSize("Medium")]) == ["Large", "Medium"],
             "sizes of other bodies keep Magic Fit's names")

    armature = _rig()
    mesh = bpy.data.meshes.new("SizesTop")
    mesh.from_pydata([(x * 0.1, 0.0, z * 0.1) for x in range(4) for z in range(4)], [],
                     [(x * 4 + z, (x + 1) * 4 + z, (x + 1) * 4 + z + 1, x * 4 + z + 1)
                      for x in range(3) for z in range(3)])
    top = bpy.data.objects.new("SizesTop", mesh)
    bpy.context.scene.collection.objects.link(top)
    _make_exportable(top, armature, "0.0 Top")
    top.shape_key_add(name="Basis", from_mix=False)
    medium = top.shape_key_add(name="Medium", from_mix=False)
    medium.value = 0.0  # New keys start on.
    own = _positions(top)
    medium.data.foreach_set("co", (own + (0.0, -0.02, 0.01)).astype(np.float32).ravel())
    _context(addon, [top])
    props.export_scope = "CURRENT_COLLECTION"
    props.variant_target = "NEW_GROUP"
    props.variant_group_name = "Size"
    props.variant_name = ""
    props.export_sizes = True

    # Without Magic Fit, or with an older one, the option does nothing.
    _require(sizes.magic_fit_api() is None and not sizes.wanted(props) and sizes.export_names(props) is None,
             "without Magic Fit, exports go out as they are")
    with installed(fake_api(version=0)):
        _require(sizes.magic_fit_api() is None, "a Magic Fit without the API version is left alone")

    with installed(fake_api()) as api:
        _require(sizes.magic_fit_api() is api and sizes.wanted(props) and
                 sizes.export_names(props) == ["Large", "Medium"],
                 "Magic Fit is found by its add-on name, whichever package installed it")
        ref = ops.export_destination_context(bpy.context)

        # Readiness: sizes go into a group, need picks, and need no option name.
        issues = [message for severity, message in ops.export_target_issues(
            bpy.context, ref, material_coverage_warning=False) if severity == "ERROR"]
        _require(not issues, "sizes to New Group need no option name", str(issues))
        props.variant_target = props_module.IN_PLACE_TARGET
        issues = [message for _severity, message in ops.export_target_issues(
            bpy.context, ref, material_coverage_warning=False)]
        _require(any("option group" in message for message in issues), "In Place can't take sizes")
        try:
            ops.perform_instant_export(bpy.context)
        except ValueError as error:
            _require("option group" in str(error) and not plugin.payloads and not api.calls,
                     "an export In Place with sizes is refused before resizing")
        else:
            raise AssertionError("Sizes were exported In Place")
        for selection in (ops.MASHUP_TARGET, ops.SAVE_NEW_MOD_TARGET):
            props.variant_target = selection
            _require("mashup" in ops.sizes_export_issue(props, ref), f"{selection} can't take sizes")
        _require("create the mod" in ops.sizes_export_issue(
            SimpleNamespace(variant_target="NEW_GROUP"), SimpleNamespace(destination_state="new_mod_required")),
            "a game model's first export can't take sizes")
        try:
            ops.perform_mashup_export(bpy.context, "NEW_MOD", "Sized Mod", allow_single_context=True)
        except ValueError as error:
            _require("mashup" in str(error) and not plugin.payloads, "mashups refuse sizes")
        else:
            raise AssertionError("A mashup took sizes")
        props.variant_target = "NEW_GROUP"
        api.picks = []
        issues = [message for _severity, message in ops.export_target_issues(
            bpy.context, ref, material_coverage_warning=False)]
        _require(sizes.NO_SIZES in issues, "with no sizes picked, readiness says to pick them")
        api.picks = [("Large", True), ("Medium", False)]
        # Magic Fit refuses picks making more than 64 sizes: readiness and Quick Export say so (bridge test 10).
        api.too_many = True
        issues = [message for _severity, message in ops.export_target_issues(
            bpy.context, ref, material_coverage_warning=False)]
        try:
            list(sizes.export_steps([], lambda *_: None))
            refusal = ""
        except ValueError as error:
            refusal = str(error)
        _require(sizes.picked_names() == [] and sizes.TOO_MANY in issues and sizes.NO_SIZES not in issues and
                 refusal == sizes.TOO_MANY, "more than 64 picked sizes read as too many, not as none, and the export refuses")
        api.too_many = False

        # Quick Export to a new group.
        values = [block.value for block in top.data.shape_keys.key_blocks]
        ops.perform_instant_export(bpy.context)
        payload = plugin.payloads[-1]
        _require(payload["schema"] == "instant-edit.export-sizes" and payload["version"] == 1 and
                 payload["variantTarget"] == "new_group" and payload["variantGroupName"] == "Size" and
                 "variantTargetId" not in payload and payload["defaultSize"] == "Large" and
                 [size["name"] for size in payload["sizes"]] == ["Large", "Medium"] and
                 payload["contextId"] == "sizes-context" and payload["capability"] == "sizes-capability" and
                 payload["backupExisting"] is False and payload["estEntries"] == [],
                 "Quick Export sends every size for an option of the new group, the size the body shows on",
                 str({name: value for name, value in payload.items() if name != "sizes"}))
        _require(all(len(size["sha256"]) == 64 and size["size"] > 0 for size in payload["sizes"]) and
                 api.calls[-1] == [top],
                 "each size's model goes with its size and hash; Magic Fit resizes the meshes the export writes")
        large_co = _model_positions(addon, plugin.files[-1]["Large"])
        medium_co = _model_positions(addon, plugin.files[-1]["Medium"])
        off = (_distance(large_co, own), _distance(medium_co, own + (0.0, -0.02, 0.01)),
               _distance(large_co, own + (0.0, -0.02, 0.01)))
        _require(off[0] < 1e-4 and off[1] < 1e-4 and off[2] > 0.01, "each model has its size's shape",
                 f"off by {off[0]:.2g} and {off[1]:.2g} m, Large and Medium {off[2]:.2g} m apart")
        _require([block.value for block in top.data.shape_keys.key_blocks] == values and
                 not any(Path(size["filePath"]).exists() for size in payload["sizes"]),
                 "the keys are as they were and the exported files are cleaned up")
        _require(props.last_status.startswith("Exported 2 sizes to Size: Large, Medium") and
                 "Resized in 0.1 s" in props.last_status and
                 props.variant_target.startswith("option:11111111-1111-1111-1111-111111111111:00000000"),
                 "the status names the sizes and the size the body shows is selected after the export")

        # An option of a group stands for the group; a group target is taken as it is.
        props.variant_target = plugin.groups[0]["options"][1]["id"]
        _require(ops._sizes_target(props, ref) == ("Size", GROUP_ID), "an option's group takes the sizes")
        ops.perform_instant_export(bpy.context)
        _require(plugin.payloads[-1]["variantTarget"] == "group" and
                 plugin.payloads[-1]["variantTargetId"] == GROUP_ID and
                 "variantGroupName" not in plugin.payloads[-1],
                 "sizes go into the selected option's group")
        props.variant_target = GROUP_ID
        _require(ops._sizes_target(props, ref) == ("Size", GROUP_ID), "a selected group takes the sizes")

        # From Edit Mode, each model still has its size.
        props.variant_target = "NEW_GROUP"
        bpy.context.view_layer.objects.active = top
        top.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        try:
            ops.perform_instant_export(bpy.context)
            mode = bpy.context.mode
        finally:
            bpy.ops.object.mode_set(mode="OBJECT")
        medium_co = _model_positions(addon, plugin.files[-1]["Medium"])
        _require(mode == "EDIT_MESH" and _distance(medium_co, own + (0.0, -0.02, 0.01)) < 1e-4,
                 "exports from Edit Mode have their sizes and stay in Edit Mode")

        # Magic Fit's refusal reaches the user; nothing is sent.
        sent = len(plugin.payloads)
        api.failure = "Pick the From Body in Magic Fit's Resize panel"
        try:
            ops.perform_instant_export(bpy.context)
        except ValueError as error:
            _require(str(error) == api.failure and len(plugin.payloads) == sent,
                     "Magic Fit's error is the export's error, and nothing is sent")
        else:
            raise AssertionError("A failed resize was exported")
        api.failure = None

        # Simple Export: a file per size.
        settings = bpy.context.scene.xiv_ie_settings
        with tempfile.TemporaryDirectory(prefix="xiv-ie-sizes-simple-") as folder:
            settings.export_directory = folder
            settings.export_name = "Top"
            api.picks = [("Large", True), ("Rue/Buff", False)]
            result = bpy.ops.xiv_ie.simple_export()
            written = sorted(path.name for path in Path(folder).iterdir())
            _require(result == {"FINISHED"} and written == ["Top Large.mdl", "Top Rue-Buff.mdl"],
                     "Simple Export writes a file per size, named after it", str(written))
            props.export_sizes = False
            for path in Path(folder).iterdir():
                path.unlink()
            bpy.ops.xiv_ie.simple_export()
            _require(sorted(path.name for path in Path(folder).iterdir()) == ["Top.mdl"],
                     "with the option off, Simple Export writes one file")
            props.export_sizes = True
            api.picks = [("Large", True), ("Medium", False)]

    props.export_sizes = False


# ---- Magic Fit itself ---------------------------------------------------------------------------


def _magic_fit_root() -> Path | None:
    configured = os.environ.get("MAGIC_FIT_ROOT")
    candidates = [Path(configured)] if configured else [Path(__file__).resolve().parents[2].parent / "Magic Fit"]
    return next((path for path in candidates if (path / "magic_fit" / "api.py").is_file()), None)


def run_magic_fit(addon, root: Path) -> None:
    sys.path[:0] = [str(root), str(root / "tests")]
    import magic_fit  # noqa: PLC0415
    import resize  # noqa: PLC0415  (Magic Fit's test fixtures)

    magic_fit.register()
    try:
        with _addon_entry("magic_fit"), \
                tempfile.TemporaryDirectory(prefix="xiv-ie-sizes-mf-") as capture, \
                FakePlugin(addon, Path(capture)) as plugin:
            ops = importlib.import_module(f"{addon.__name__}.instant_edit.ops")
            sizes = importlib.import_module(f"{addon.__name__}.instant_edit.sizes")
            # Magic Fit's devkit: a body whose keys its settings drive, and clothing that fits it as Large.
            body, garments = resize.devkit_body()
            brush = bpy.context.scene.body_fit_brush
            brush.resize_from_object = body
            brush.resize_use_sizes = True
            bpy.ops.magic_fit.resize_pick(setting="chest_size", option=0)
            bpy.ops.magic_fit.resize_pick(setting="chest_size", option=1, extend=True)
            suit = garments["Suit"]
            armature = _rig("MagicFitSkeleton")
            _make_exportable(suit, armature, "0.0 Suit")
            own = _positions(suit)
            _context(addon, [suit], "magic-fit-context")
            props = bpy.context.scene.xiv_ie_instant_edit_props
            props.export_scope = "CURRENT_COLLECTION"
            props.variant_target = "NEW_GROUP"
            props.variant_group_name = "Size"
            props.export_sizes = True
            _require(sizes.export_names(props) == ["YAB Large", "YAB Medium"], "Magic Fit's picked sizes are listed")

            ops.perform_instant_export(bpy.context)
            payload = plugin.payloads[-1]
            _require([size["name"] for size in payload["sizes"]] == ["YAB Large", "YAB Medium"] and
                     payload["defaultSize"] == "YAB Large", "Magic Fit's sizes are exported, the body's own on")
            key = suit.data.shape_keys.key_blocks["Medium"]
            resized = _positions(suit, "Medium")
            large_co = _model_positions(addon, plugin.files[-1]["YAB Large"])
            medium_co = _model_positions(addon, plugin.files[-1]["YAB Medium"])
            _require(_distance(large_co, own) < 1e-4 and _distance(medium_co, resized) < 1e-4 and
                     np.abs(resized - own).max() > 0.005,
                     "Magic Fit's sizes: the clothing as it is and resized to Medium")

            # Looking at Medium, the size the body shows still exports the clothing as it fits the body.
            key.value = 1.0
            ops.perform_instant_export(bpy.context)
            large_co = _model_positions(addon, plugin.files[-1]["YAB Large"])
            _require(_distance(large_co, own) < 1e-4 and key.value == 1.0,
                     "the size the body shows exports as the clothing fits it, also while it shows Medium")
    finally:
        magic_fit.unregister()
        for name in [name for name in sys.modules if name == "magic_fit" or name.startswith("magic_fit.")]:
            sys.modules.pop(name)
        sys.modules.pop("resize", None)
        sys.path[:] = [path for path in sys.path if path not in (str(root), str(root / "tests"))]


# ---- Exports from the same copies ---------------------------------------------------------------


def _vertex_streams(addon, path: Path) -> list[dict]:
    """Each LOD 0 mesh's vertex streams of an exported model: {stream: structured array}."""
    model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
    schema = importlib.import_module(f"{addon.__name__}.io.model.com.schema")
    model = model_module.XIVModel.from_file(str(path))
    meshes = []
    for index, mesh in enumerate(model.meshes[:model.lods[0].mesh_count]):
        types = schema.get_array_type(model.vertex_declarations[index])
        meshes.append({stream: np.frombuffer(model.buffers, array_type, mesh.vertex_count,
                                             mesh.vertex_buffer_offset[stream])
                       for stream, array_type in types.items()})
    return meshes


def _sphere(armature, name: str):
    """A UV sphere to export: UV seams, a sharp ring, a size key and a kept shp key, moved and turned."""
    mesh = bpy.data.meshes.new(name)
    work = bmesh.new()
    work.loops.layers.uv.new("uv0")
    bmesh.ops.create_uvsphere(work, u_segments=16, v_segments=8, radius=0.3, calc_uvs=True)
    work.to_mesh(mesh)
    work.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    _make_exportable(obj, armature, name)
    obj.data.shade_smooth()
    for edge in obj.data.edges:
        co = (obj.data.vertices[edge.vertices[0]].co + obj.data.vertices[edge.vertices[1]].co) / 2
        edge.use_edge_sharp = abs(co.z) < 1e-4
    obj.location = (0.1, 0.2, 0.05)
    obj.rotation_euler = (0.0, 0.0, 0.5)
    obj.shape_key_add(name="Basis", from_mix=False)
    co = _positions(obj)
    for key_name, offset in (("Size", co * (0.15, 0.05, 0.1)), ("shp_test", co * (0.0, 0.0, 0.2))):
        key = obj.shape_key_add(name=key_name, from_mix=False)
        key.value = 0.0
        key.data.foreach_set("co", (co + offset).astype(np.float32).ravel())
    return obj


def run_shape_exports(addon) -> None:
    export = importlib.import_module(f"{addon.__name__}.mesh.export")
    settings = bpy.context.scene.xiv_ie_settings
    settings.model_format = "MDL"
    settings.keep_shapekeys = True
    armature = _rig("ShapesSkeleton")
    obj = _sphere(armature, "0.0 Sphere")
    size = obj.data.shape_keys.key_blocks["Size"]
    kept = obj.data.shape_keys.key_blocks["shp_test"]
    kept.value = 0.4

    def both(folder: Path, label: str) -> list[tuple[Path, Path]]:
        """Each shape written through ShapeExports and by a fresh export."""
        pairs = []
        with export.ShapeExports([obj], "MDL") as exports:
            _require(exports.copies is not None and obj.name == "0.0 Sphere" and not obj.hide_get() and
                     abs(kept.value - 0.4) < 1e-6,
                     f"{label}: the copies are made once and the mesh keeps its name, visibility and keys",
                     f"{exports.copies is not None}, {obj.name}, {obj.hide_get()}, {kept.value}")
            for value in (0.0, 1.0, 0.5):
                size.value = value
                exports.write(folder / f"fast {label} {value}")
        for value in (0.0, 1.0, 0.5):
            size.value = value
            export.export_result(folder / f"full {label} {value}", "MDL", export_objects=[obj])
            pairs.append((folder / f"full {label} {value}.mdl", folder / f"fast {label} {value}.mdl"))
        size.value = 0.0
        return pairs

    with tempfile.TemporaryDirectory(prefix="xiv-ie-shape-exports-") as folder:
        folder = Path(folder)
        pairs = both(folder, "plain")
        _require(all(full.read_bytes() == fast.read_bytes() for full, fast in pairs),
                 "exports from the same copies are byte for byte fresh exports of each shape")
        _require(obj.name == "0.0 Sphere" and abs(kept.value - 0.4) < 1e-6 and size.value == 0.0 and
                 not [item.name for item in bpy.data.objects if item.name.startswith(("xiv_ie_size_copy",
                                                                                      "temp_export"))],
                 "afterwards the copies are gone and the mesh is as it was")

        # A heels offset calculated for one shape doesn't stay on the copies for a shape with nothing
        # below the floor.
        heels = settings.calculate_heels_offset
        settings.calculate_heels_offset = True
        lift = obj.shape_key_add(name="Lift", from_mix=False)
        lift.data.foreach_set("co", (_positions(obj) + (0.0, 0.0, 0.5)).astype(np.float32).ravel())
        lift.value = 0.0
        with export.ShapeExports([obj], "MDL") as exports:
            exports.write(folder / "fast heels low")
            calculated = any(note.startswith("Calculated heels_offset")
                             for notes in export._export_stats.values() for note in notes)
            lift.value = 1.0
            exports.write(folder / "fast heels high")
        export.export_result(folder / "full heels high", "MDL", export_objects=[obj])
        lift.value = 0.0
        export.export_result(folder / "full heels low", "MDL", export_objects=[obj])
        obj.shape_key_remove(lift)
        settings.calculate_heels_offset = heels
        _require(calculated and all((folder / f"full heels {shape}.mdl").read_bytes() ==
                                    (folder / f"fast heels {shape}.mdl").read_bytes() for shape in ("low", "high")),
                 "a heels offset is calculated for each shape on its own, none for a shape above the floor")

        # Custom normals are set again on the split copies: the normals come out within 0.001.
        normals = np.empty(len(obj.data.loops) * 3, dtype=np.float32)
        obj.data.corner_normals.foreach_get("vector", normals)
        normals = normals.reshape(-1, 3) + (0.05, 0.0, 0.0)
        obj.data.normals_split_custom_set(normals / np.linalg.norm(normals, axis=1)[:, None])
        worst = {}
        for full, fast in both(folder, "custom"):
            for a, b in zip(_vertex_streams(addon, full), _vertex_streams(addon, fast)):
                for stream in a:
                    for field in a[stream].dtype.names:
                        difference = np.abs(a[stream][field].astype(np.float64) -
                                            b[stream][field].astype(np.float64)).max()
                        worst[field] = max(worst.get(field, 0.0), float(difference))
        _require(worst["position"] == 0.0 and worst["normal"] < 1e-3 and worst["tangent"] <= 1 and
                 all(value == 0.0 for field, value in worst.items()
                     if field not in ("position", "normal", "tangent")),
                 "with custom normals, only normals and tangents differ, and barely", str(worst))
    settings.keep_shapekeys = False


def run() -> None:
    with addon_session("_xiv_instant_edit_sizes") as addon:
        with temporary_scene_data():
            run_shape_exports(addon)
        with temporary_scene_data():
            run_stand_in(addon)
        root = _magic_fit_root()
        if root is None:
            print("[SKIP] Magic Fit's repository wasn't found: set MAGIC_FIT_ROOT to test with it")
            return
        with temporary_scene_data():
            run_magic_fit(addon, root)


if __name__ == "__main__":
    run()
