"""FFXIV material-path helpers shared by the panel and export operators."""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass
import re
import uuid

import bpy

from .io.model.exp.validators import clean_material_path, USHORT_LIMIT
from .instant_edit.context import (
    MASHUP_SOURCE_MATERIAL_PROPERTY,
    active_mesh_id_plan,
    context_id_for_object,
    mesh_ids_from_name,
    mesh_name_info,
)
from .mesh.heels import is_heels_offset_attribute, normalize_heels_offset
from .mesh.objects import visible_meshobj
from .xivpy.model import is_model_attribute_name


MATERIAL_PRESETS = {
    "gen2/vanilla": "/mt_c0101b0001_a.mtrl",
    "gen3/tbse": "/mt_c0101b0001_b.mtrl",
    "bibo": "/mt_c0101b0001_bibo.mtrl",
    "bibopube": "/mt_c0201b0001_bibopube.mtrl",
    "betterpube": "/mt_c0201b0001_betterpube.mtrl",
    "yet another piercing": "/mt_c0201b0001_piercings.mtrl",
    "yet another fingernail": "/mt_c0201b0001_yafinger.mtrl",
    "yet another toenail": "/mt_c0201b0001_yatoe.mtrl",
}

_CUSTOM_ATTRIBUTE = re.compile(r"^[a-z0-9_]+$")

ATTRIBUTE_NAMES = {
    "nek": "Neck",
    "ude": "Elbow",
    "hij": "Wrist",
    "arm": "Hand",
    "kod": "Waist",
    "hiz": "Knee",
    "sne": "Shin",
    "leg": "Boot",
    "lpd": "Knee Pad",
}

ATTRIBUTE_VARIANTS = {
    "mv": "Head",
    "tv": "Body",
    "gv": "Glove",
    "dv": "Leg",
    "sv": "Shoe",
    "ev": "Earring",
    "nv": "Necklace",
    "wv": "Bracelet",
    "rv": "Ring",
    "fv": "Face",
    "hv": "Hair",
}

# These are the model-visible attribute families that can be represented by
# the generated Penumbra IMC group. Face attributes deliberately remain out of
# this preset; face custom attributes can still be authored with the atrx_
# convention and exported through the regular ATR group.
ATTRIBUTE_GROUP_FAMILIES = (
    "mv", "tv", "gv", "dv", "sv", "ev", "nv", "wv", "rv", "hv",
)
ATTRIBUTE_GROUP_SUFFIXES = tuple("abcdefgh")
ATTRIBUTE_VARIANT_PRESETS = tuple(
    f"atr_{family}_{suffix}"
    for family in ATTRIBUTE_GROUP_FAMILIES
    for suffix in ATTRIBUTE_GROUP_SUFFIXES
)
_ATTRIBUTE_VARIANT_PATTERN = re.compile(
    r"^atr_(?:" + "|".join(ATTRIBUTE_GROUP_FAMILIES) + r")_(?:[a-h])$"
)

# Face toggles (atr_fv_a - atr_fv_g) are addable presets like the other
# variant families above, but Glamourer already drives them directly in-game,
# so they're kept out of ATTRIBUTE_GROUP_FAMILIES: attribute_group_data() must
# never turn one into a generated Penumbra Option group.
FACE_ATTRIBUTE_SUFFIXES = tuple("abcdefg")
FACE_ATTRIBUTE_PRESETS = tuple(f"atr_fv_{suffix}" for suffix in FACE_ATTRIBUTE_SUFFIXES)
_FACE_ATTRIBUTE_PATTERN = re.compile(
    r"^atr_fv_(?:" + "|".join(FACE_ATTRIBUTE_SUFFIXES) + r")$"
)

_BUILTIN_ATTRIBUTE_NAMES = frozenset(f"atr_{name}" for name in ATTRIBUTE_NAMES)


@dataclass(frozen=True)
class MaterialGroup:
    mesh_index: int
    objects: tuple

    @property
    def parts(self) -> tuple[int, ...]:
        return tuple(sorted({mesh_ids_from_name(obj)[1] for obj in self.objects}))

    @property
    def part_instances(self) -> tuple["MeshPartInstance", ...]:
        return mesh_part_instances(self.objects, self.mesh_index)


@dataclass(frozen=True)
class MeshPartInstance:
    """One visible part, kept separate from other objects with the same IDs."""

    mesh_index: int
    part_index: int
    instance_key: str
    objects: tuple


def mesh_part_objects(objects, mesh_index: int, part_index: int) -> tuple:
    """Return all visible objects representing one part, including its LODs."""
    result = []
    for obj in objects:
        try:
            group, part, _lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if group == mesh_index and part == part_index:
            result.append(obj)
    return tuple(sorted(result, key=lambda obj: obj.name))


def mesh_part_instance_key(obj) -> str:
    """Return the stable identity used to separate duplicate visible parts.

    Numeric mesh IDs are export coordinates, not object identity.  Imported
    objects carry a per-import key; older context imports fall back to their
    context and display label so their LODs remain together.  Untagged scene
    objects use their collection and label as the least-invasive fallback.
    """
    imported = obj.get("instant_edit_import_instance_id", "")
    if isinstance(imported, str) and imported:
        return f"import:{imported}"

    try:
        context_id = context_id_for_object(obj)
    except Exception:
        context_id = ""
    try:
        label = " ".join(mesh_name_info(obj).label.split()).casefold()
    except Exception:
        label = str(getattr(obj, "name", "")).strip().casefold()

    if context_id:
        return f"context:{context_id}|label:{label}"

    collection_ids = sorted(
        str(collection.as_pointer())
        for collection in getattr(obj, "users_collection", ())
    )
    return f"collection:{','.join(collection_ids)}|label:{label}"


def mesh_part_instances(
    objects,
    mesh_index: int,
    part_index: int | None = None,
) -> tuple[MeshPartInstance, ...]:
    """Group visible objects into independently movable part instances."""
    grouped = defaultdict(list)
    for obj in objects:
        if getattr(obj, "type", None) != "MESH":
            continue
        try:
            group, part, _lod = mesh_ids_from_name(obj)
        except Exception:
            continue
        if group != mesh_index or (part_index is not None and part != part_index):
            continue
        grouped[(part, mesh_part_instance_key(obj))].append(obj)

    instances = []
    for (part, instance_key), part_objects in grouped.items():
        instances.append(
            MeshPartInstance(
                mesh_index,
                part,
                instance_key,
                tuple(sorted(part_objects, key=lambda obj: obj.name.casefold())),
            )
        )
    return tuple(
        sorted(
            instances,
            key=lambda item: (
                item.part_index,
                _planned_object_name(item.objects[0]).casefold() if item.objects else "",
                item.instance_key,
            ),
        )
    )


def mesh_part_instance_objects(
    objects,
    mesh_index: int,
    part_index: int,
    instance_key: str | None = None,
) -> tuple:
    """Return one duplicate-safe part instance, including all of its LODs."""
    if instance_key is None:
        return mesh_part_objects(objects, mesh_index, part_index)
    for instance in mesh_part_instances(objects, mesh_index, part_index):
        if instance.instance_key == instance_key:
            return instance.objects
    return ()


def mesh_display_name(obj) -> str:
    """Return the editable human label while hiding the exporter mesh ID."""
    display_object = obj
    instance_objects = getattr(obj, "objects", None)
    if instance_objects is not None:
        display_object = next(iter(instance_objects), None)
        if display_object is None:
            return "Unnamed Part"
    try:
        label = mesh_name_info(display_object).label
    except Exception:
        label = str(getattr(display_object, "name", "")).strip()
    return label or "Unnamed Part"


def _front_mesh_name(info) -> str:
    label = " ".join(info.label.split())
    identifier = f"{info.mesh_group}.{info.mesh_part}"
    lod_suffix = f" LOD{info.lod}" if info.lod else ""
    return f"{identifier}{f' {label}' if label else ''}{lod_suffix}"


def convert_suffix_mesh_names(objects) -> int:
    """Move suffix-form mesh IDs to the front without allowing name collisions."""
    candidates = []
    for obj in objects:
        if obj.type != "MESH":
            continue
        try:
            info = mesh_name_info(obj)
        except Exception:
            continue
        if info.prefix:
            continue
        candidates.append((obj, _front_mesh_name(info)))

    if not candidates:
        return 0

    candidate_ids = {obj.as_pointer() for obj, _target in candidates}
    existing_names = {
        obj.name
        for obj in objects
        if obj.as_pointer() not in candidate_ids
    }
    targets = defaultdict(list)
    for obj, target in candidates:
        targets[target].append(obj.name)
    conflicts = {
        target: names
        for target, names in targets.items()
        if target in existing_names or len(names) > 1
    }
    if conflicts:
        details = "; ".join(
            f"{target} ({', '.join(names)})"
            for target, names in sorted(conflicts.items())
        )
        raise ValueError(f"Mesh ID conversion would create name collisions: {details}")

    all_names = {obj.name for obj in objects}
    temporary_names = []
    for index, (obj, _target) in enumerate(candidates):
        temporary = f"__xiv_ie_mesh_name_convert_{obj.as_pointer()}_{index}"
        while temporary in all_names or temporary in temporary_names:
            temporary += "_"
        temporary_names.append(temporary)

    originals = [(obj, obj.name) for obj, _target in candidates]
    try:
        for (obj, _target), temporary in zip(candidates, temporary_names):
            obj.name = temporary
        for (obj, target), _temporary in zip(candidates, temporary_names):
            obj.name = target
    except Exception:
        for (obj, _old_name), temporary in zip(originals, temporary_names):
            obj.name = temporary
        for obj, old_name in originals:
            obj.name = old_name
        raise
    return len(candidates)


def mesh_part_attributes(objects) -> tuple[str, ...]:
    """Return the enabled XIV attributes represented by a mesh part."""
    attributes = {
        key
        for obj in objects
        for key, value in obj.items()
        if is_model_attribute_name(key) and value
    }
    return tuple(sorted(attributes))


def attribute_group_data(
    objects,
    *,
    use_lods: bool = True,
) -> tuple[tuple[str, ...], dict[str, int]]:
    """Return model attributes and canonical IMC suffix masks for Penumbra groups.

    Penumbra's IMC attribute columns are defined by the tag suffix rather than
    the attribute's position in the MDL table: _a is bit 0, _b is bit 1, and
    so on. Body-part attributes are intentionally excluded because they are
    vanilla visibility controls, not gear/accessory part tags. Face toggles
    (atr_fv_*) are excluded for a different reason: Glamourer already drives
    them directly, so they're still exported on the mesh but never get a
    generated Option group here.
    """
    model_attributes: list[str] = []
    exported_lods = range(3 if use_lods else 1)
    for lod_level in exported_lods:
        for obj in objects:
            if getattr(obj, "type", None) != "MESH":
                continue
            data = getattr(obj, "data", None)
            if data is not None and hasattr(data, "vertices") and len(data.vertices) == 0:
                continue
            try:
                object_lod = mesh_ids_from_name(obj)[2]
            except Exception:
                continue
            if object_lod != lod_level:
                continue
            for key in obj.keys():
                attribute = str(key).strip()
                if not is_model_attribute_name(attribute) or not obj[key]:
                    continue
                if attribute not in model_attributes:
                    model_attributes.append(attribute)

    tags: list[str] = []
    masks: dict[str, int] = {}
    invalid_custom: list[str] = []
    for attribute in model_attributes:
        if attribute in _BUILTIN_ATTRIBUTE_NAMES:
            continue
        if _ATTRIBUTE_VARIANT_PATTERN.fullmatch(attribute):
            tags.append(attribute)
            suffix = attribute.rsplit("_", 1)[-1]
            masks[attribute] = 1 << ATTRIBUTE_GROUP_SUFFIXES.index(suffix)
        elif _FACE_ATTRIBUTE_PATTERN.fullmatch(attribute):
            # Exported on the mesh like any other attribute, but never turned
            # into a Penumbra Option group: Glamourer already drives face
            # toggles directly, so a generated group would just conflict.
            continue
        elif attribute.startswith("atrx_"):
            tags.append(attribute)
        elif attribute.startswith("atr"):
            invalid_custom.append(attribute)

    if invalid_custom:
        names = ", ".join(sorted(invalid_custom))
        raise ValueError(
            "Custom Penumbra attribute groups require attributes beginning with "
            f"atrx_: {names}"
        )
    return tuple(tags), masks


# Magic Fit's Hair Weights tags the hair meshes it weights with the hair skeleton it
# used: the EST entry (160 loads skl_c0801h0160.sklb) and the race it belongs to.
EST_HAIR_PROPERTY = "xiv_est_hair"
EST_RACE_PROPERTY = "xiv_est_race"
_EST_RACE_PATTERN = re.compile(r"c(?:0[1-9]|1[0-8])01")


def hair_skeleton_tags(objects) -> tuple[list[dict] | None, str]:
    """Return the hair EST entry the meshes' Magic Fit tags ask for, and any problem.

    ``[]`` means no mesh carries a tag (Hair Weights removes them when it weights
    without a hair skeleton), so the plugin takes back an entry it set before. One
    entry means every tagged mesh agrees; untagged parts use body bones only and
    fit any hair skeleton. ``None`` comes with a message when tags disagree or are
    malformed: the plugin then leaves the mod's EST entries as they are.
    """
    tagged: dict[tuple[int, str], list[str]] = {}
    invalid: list[str] = []
    for obj in objects:
        if getattr(obj, "type", None) != "MESH":
            continue
        entry = obj.get(EST_HAIR_PROPERTY)
        race = obj.get(EST_RACE_PROPERTY)
        if entry is None and race is None:
            continue
        if isinstance(entry, float) and entry.is_integer():
            entry = int(entry)
        if (
            isinstance(entry, bool) or not isinstance(entry, int) or not 1 <= entry <= 9999
            or not isinstance(race, str) or not _EST_RACE_PATTERN.fullmatch(race)
        ):
            invalid.append(obj.name)
            continue
        tagged.setdefault((entry, race), []).append(obj.name)
    if invalid:
        return None, (
            f"Invalid hair skeleton tags ({EST_HAIR_PROPERTY}/{EST_RACE_PROPERTY}) on "
            f"{', '.join(sorted(invalid)[:3])}{'…' if len(invalid) > 3 else ''}; "
            "the EST entry is left unchanged."
        )
    if len(tagged) > 1:
        described = "; ".join(
            f"{entry} ({race}) on {', '.join(sorted(names)[:2])}{'…' if len(names) > 2 else ''}"
            for (entry, race), names in sorted(tagged.items())
        )
        return None, (
            f"The meshes are weighted to different hair skeletons: {described}. "
            "Weight them all to one; the EST entry is left unchanged."
        )
    if not tagged:
        return [], ""
    ((entry, race),) = tagged
    return [{"slot": "Hair", "entry": entry, "race": race}], ""


def attribute_display_name(attribute: str) -> str:
    """Turn common XIV attribute keys into Mesh Studio's compact labels."""
    if attribute.startswith("atr_"):
        parts = attribute.split("_")
        attribute_id = parts[1] if len(parts) > 1 else ""
        if attribute_id in ATTRIBUTE_VARIANTS and len(parts) > 2:
            return f"{ATTRIBUTE_VARIANTS[attribute_id]} {parts[2].upper()}"
        return ATTRIBUTE_NAMES.get(attribute_id, attribute)
    if attribute.startswith("heels_offset="):
        return f"Heels: {attribute.split('=', 1)[1]}"
    if attribute.startswith("skin_suffix="):
        return f"Skin: {attribute.split('=', 1)[1]}"
    return attribute


def normalize_mesh_attribute(value: str) -> str:
    text = str(value or "").strip()
    # SimpleHeels' heels_offset=<number> is the one attribute that carries a
    # value, so it gets its own validation instead of the name-only pattern.
    if is_heels_offset_attribute(text) and "=" in text:
        return normalize_heels_offset(text)
    attribute = text.lower().replace(" ", "_")
    if not _CUSTOM_ATTRIBUTE.fullmatch(attribute):
        raise ValueError(
            "Custom attributes must use only letters, numbers, or underscores "
            "(or heels_offset=<number>)."
        )
    if not is_model_attribute_name(attribute):
        raise ValueError(
            "Custom attributes must start with atr, heels_offset, or skin_suffix."
        )
    return attribute


def set_mesh_part_attribute(
    objects,
    mesh_index: int,
    part_index: int,
    value: str,
    enabled: bool,
    instance_key: str | None = None,
) -> str:
    attribute = normalize_mesh_attribute(value) if enabled else str(value or "").strip()
    if not is_model_attribute_name(attribute):
        raise ValueError("This is not a valid mesh attribute.")
    replaces_heels = enabled and is_heels_offset_attribute(attribute)
    for obj in mesh_part_instance_objects(objects, mesh_index, part_index, instance_key):
        if replaces_heels:
            for key in [key for key in obj.keys() if is_heels_offset_attribute(key)]:
                del obj[key]
        if enabled:
            obj[attribute] = True
        elif attribute in obj:
            del obj[attribute]
    return attribute


def _mesh_object_name(mesh_index: int, part_index: int, label: str, lod: int | None) -> str:
    lod_suffix = f" LOD{lod}" if lod else ""
    return f"{mesh_index}.{part_index} {label}{lod_suffix}"


def backface_copies(objects) -> list:
    """Copy one part's objects (every LOD) as a new part named "<name> Backfaces".

    The copies keep the part's IDs, collections and properties, and share a new
    import instance id so they form one part of their own for the caller to
    renumber.
    """
    instance_id = uuid.uuid4().hex
    copies = []
    for obj in objects:
        group, part, lod = mesh_ids_from_name(obj)
        copy = obj.copy()
        copy.data = obj.data.copy()
        copy["instant_edit_import_instance_id"] = instance_id
        for collection in obj.users_collection:
            collection.objects.link(copy)
        copy.name = _mesh_object_name(group, part, f"{mesh_display_name(obj)} Backfaces", lod)
        copies.append(copy)
    return copies


def _planned_object_name(obj) -> str:
    """Return the object's name, or the name a pending drag will give it."""
    plan = active_mesh_id_plan()
    if not plan or obj.as_pointer() not in plan:
        return obj.name
    info = mesh_name_info(obj)
    return _mesh_object_name(info.mesh_group, info.mesh_part, mesh_display_name(obj), info.lod)


def commit_mesh_id_plan(plan: dict[int, tuple[int, int]], objects) -> int:
    """Rename every object a drag moved, in one collision-checked pass.

    Must run outside planned_mesh_ids() so the objects' current names are read.
    """
    targets = []
    for obj in objects:
        planned = plan.get(obj.as_pointer())
        if planned is None:
            continue
        info = mesh_name_info(obj)
        if planned == (info.mesh_group, info.mesh_part):
            continue
        targets.append((obj, planned[0], planned[1], info.lod, mesh_display_name(obj)))
    return _rename_mesh_targets(targets, objects)


def _rename_mesh_targets(targets, objects=None) -> int:
    """Apply mesh-ID renames without allowing Blender to suffix collisions.

    Collisions are only checked against `objects` (the visible mesh objects
    shown in the materials list). Hidden objects aren't listed there and
    can't be moved or renamed alongside the visible ones, so they must not
    block a move.
    """
    targets = tuple(targets)
    if not targets:
        return 0

    desired = [
        (obj, _mesh_object_name(group, part, label, lod))
        for obj, group, part, lod, label in targets
    ]
    desired_names = [name for _obj, name in desired]
    if len(set(desired_names)) != len(desired_names):
        raise ValueError("Mesh movement would create duplicate object names.")

    target_ids = {obj.as_pointer() for obj, _name in desired}
    pool = bpy.data.objects if objects is None else objects
    existing_names = {
        _planned_object_name(obj) for obj in pool if obj.as_pointer() not in target_ids
    }
    conflicts = sorted(set(desired_names) & existing_names)
    if conflicts:
        raise ValueError(
            "Mesh movement would collide with existing objects: " + ", ".join(conflicts)
        )

    plan = active_mesh_id_plan()
    if plan is not None:
        # A drag in progress only records the move; commit_mesh_id_plan
        # renames once the drop is released.
        for obj, group, part, _lod, _label in targets:
            plan[obj.as_pointer()] = (group, part)
        return len(desired)

    originals = [(obj, obj.name) for obj, _name in desired]
    all_names = {obj.name for obj in bpy.data.objects}
    temporary_names = []
    for index, (obj, _name) in enumerate(desired):
        temporary = f"__xiv_ie_move_{obj.as_pointer()}_{index}"
        while temporary in all_names or temporary in temporary_names:
            temporary += "_"
        temporary_names.append(temporary)

    try:
        for (obj, _name), temporary in zip(desired, temporary_names):
            obj.name = temporary
        for obj, name in desired:
            obj.name = name
    except Exception:
        for (obj, _old_name), temporary in zip(originals, temporary_names):
            obj.name = temporary
        for obj, old_name in originals:
            obj.name = old_name
        raise
    return len(desired)


def compact_mesh_part_indices(collections) -> int:
    """Fill part-index gaps in each supplied context collection atomically.

    Direct mesh objects are compacted independently per collection and mesh
    group.  Existing part-instance ordering is retained, while every LOD in
    an instance receives the same new part index.
    """
    targets = []
    moved_instances = 0
    claimed_objects = {}

    for collection in collections:
        collection_objects = tuple(
            sorted(
                (obj for obj in collection.objects if obj.type == "MESH"),
                key=lambda obj: obj.name.casefold(),
            )
        )
        if not collection_objects:
            continue

        for obj in collection_objects:
            pointer = obj.as_pointer()
            previous_collection = claimed_objects.get(pointer)
            if previous_collection is not None and previous_collection != collection:
                raise ValueError(
                    f"{obj.name}: object is linked to multiple visible Instant Edit collections."
                )
            claimed_objects[pointer] = collection
            try:
                mesh_ids_from_name(obj)
            except Exception as error:
                raise ValueError(f"{obj.name}: invalid mesh name") from error

        grouped = defaultdict(list)
        for obj in collection_objects:
            group, _part, _lod = mesh_ids_from_name(obj)
            grouped[group].append(obj)

        for mesh_index, group_objects in sorted(grouped.items()):
            instances = mesh_part_instances(group_objects, mesh_index)
            for target_part, instance in enumerate(instances):
                if target_part == instance.part_index:
                    continue
                moved_instances += 1
                for obj in instance.objects:
                    _group, _part, lod = mesh_ids_from_name(obj)
                    targets.append(
                        (
                            obj,
                            mesh_index,
                            target_part,
                            lod,
                            mesh_display_name(obj),
                        )
                    )

    if not targets:
        return 0
    _rename_mesh_targets(targets)
    return moved_instances


def _property(obj, name: str, default=None):
    if name in obj:
        return obj[name]
    return obj.get(f"instant_edit_{name}", default)


def _mesh_index(obj) -> int:
    return mesh_ids_from_name(obj)[0]


def group_mesh_objects(objects) -> list[MaterialGroup]:
    """Group mesh objects by XIV Instant Edit context and FFXIV mesh index."""
    grouped = defaultdict(list)
    for obj in objects:
        if obj.type != "MESH" or len(obj.data.vertices) == 0:
            continue
        try:
            mesh_index = _mesh_index(obj)
        except Exception:
            continue
        grouped[mesh_index].append(obj)

    return [
        MaterialGroup(mesh_index, tuple(sorted(group, key=lambda obj: obj.name)))
        for mesh_index, group in sorted(grouped.items(), key=lambda item: item[0])
    ]


def visible_material_groups() -> list[MaterialGroup]:
    return group_mesh_objects(visible_meshobj())


def material_paths(objects) -> list[str]:
    """Return the distinct export material paths represented by a mesh group."""
    paths = {}
    for obj in objects:
        path = export_material_path(obj)
        if path:
            paths.setdefault(_material_identity_key(path), path)
    return sorted(paths.values())


def export_material_path(obj) -> str:
    """Return the normalized path the MDL exporter will read from one object."""
    value = _property(obj, "xiv_material", "")
    if not isinstance(value, str) or not value.strip():
        slots = getattr(obj, "material_slots", ())
        if not slots or slots[0].material is None:
            return ""
        value = slots[0].material.name
    try:
        return clean_material_path(value.strip())
    except (AttributeError, TypeError, ValueError):
        return ""


def _material_identity_key(material: str) -> tuple[str, str]:
    """Return the material identity used by group consistency checks.

    FFXIV's Bibo body material is intentionally shared by several model
    prefixes, so its full path is the one supported exception to exact-path
    matching.  Other materials retain their complete normalized path as the
    identity used for comparisons.
    """
    filename = material.rsplit("/", 1)[-1].casefold()
    if filename.endswith("_bibo.mtrl"):
        return ("bibo", "_bibo.mtrl")
    return ("path", material)


def material_mismatch_parts(objects) -> set[int]:
    """Identify part rows that diverge from the group's authoritative material.

    The exporter builds each LOD mesh from its lowest-numbered part. The lowest
    available LOD/part is therefore the group-wide reference; a part is warned
    when any of its LOD objects is missing that material or exports another one.
    """
    ordered = sorted(
        objects,
        key=lambda obj: (
            mesh_ids_from_name(obj)[2],
            mesh_ids_from_name(obj)[1],
            obj.name.casefold(),
        ),
    )
    if not ordered:
        return set()
    authoritative = export_material_path(ordered[0])
    authoritative_key = _material_identity_key(authoritative) if authoritative else None
    mismatches = set()
    for obj in ordered:
        _group, part, _lod = mesh_ids_from_name(obj)
        path = export_material_path(obj)
        if not authoritative_key or not path or _material_identity_key(path) != authoritative_key:
            mismatches.add(part)
    return mismatches


def normalize_material_path(value: str) -> str:
    value = value.strip()
    if not value:
        raise ValueError("Material path cannot be empty")
    return clean_material_path(MATERIAL_PRESETS.get(value.lower(), value))


def find_material_group(context, mesh_index: int) -> MaterialGroup | None:
    return next(
        (group for group in visible_material_groups() if group.mesh_index == mesh_index),
        None,
    )


def assign_material_path(objects, value: str) -> str:
    """Assign one normalized FFXIV material path to every submesh in a group."""
    path = normalize_material_path(value)
    for obj in objects:
        obj["xiv_material"] = path
        obj.pop(MASHUP_SOURCE_MATERIAL_PROPERTY, None)
        if "instant_edit_xiv_material" in obj or context_id_for_object(obj):
            obj["instant_edit_xiv_material"] = path
    return path


def material_suggestions(group: MaterialGroup) -> list[tuple[str, str]]:
    """Return material candidates and per-part usage information for one group."""
    counts = defaultdict(int)
    representatives = {}

    for part_instance in group.part_instances:
        part_materials = set()
        for obj in part_instance.objects:
            path = export_material_path(obj)
            if not path:
                continue
            identity = _material_identity_key(path)
            part_materials.add(identity)
            representatives.setdefault(identity, path)

        for identity in part_materials:
            counts[identity] += 1

    suggestions = []
    for identity, path in sorted(
        representatives.items(),
        key=lambda item: item[1].casefold(),
    ):
        count = counts[identity]
        suffix = "part" if count == 1 else "parts"
        suggestions.append((path, f"{count} {suffix}"))
    return suggestions


def other_group_materials(objects, mesh_index: int) -> list[tuple[str, tuple[int, ...]]]:
    """Return the materials of a model's other mesh groups and the groups using each.

    The material dialog offers these as quick selectors so one group can reuse
    another group's material.  Paths are deduplicated with the same identity
    rules as the group consistency checks and ordered by their first group.
    """
    usage = {}
    for group in group_mesh_objects(objects):
        if group.mesh_index == mesh_index:
            continue
        for path in material_paths(group.objects):
            _path, groups = usage.setdefault(_material_identity_key(path), (path, []))
            groups.append(group.mesh_index)
    return [(path, tuple(groups)) for path, groups in usage.values()]


def matching_material_path(value: str, paths) -> str | None:
    """Return the entry of ``paths`` that ``value`` would export as, if any."""
    try:
        identity = _material_identity_key(normalize_material_path(value))
    except (AttributeError, TypeError, ValueError):
        return None
    return next((path for path in paths if _material_identity_key(path) == identity), None)
