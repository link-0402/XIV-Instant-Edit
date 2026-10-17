"""Context records used by the safe XIV Instant Edit bridge.

The collection and object custom properties in this module are deliberately
duplicated (plain and ``instant_edit_*`` names).  The plain names make the
metadata easy to inspect in Blender, while the prefixed names avoid confusing
it with metadata produced by the normal exporter.
"""
# Modified for XIV Instant Edit, 2026.

from contextlib import contextmanager
from dataclasses import dataclass, replace
from typing import Iterable
import re
import uuid

import bpy


SCHEMA = "instant-edit.context"
VERSION = 3
SUPPORTED_VERSIONS = {1, 2, 3}
COLLECTION_TAG = "instant_edit_context_id"
OBJECT_TAG = "instant_edit_context_id"
MASHUP_SOURCE_MATERIAL_PROPERTY = "instant_edit_mashup_source_material"


@dataclass(frozen=True)
class ContextField:
    """One field of an instant-edit.context record.

    ``name`` is the field in Blender: the import operator's property and the context collection's
    custom property (under both spellings, see _set). ``wire`` is its name in the plugin's JSON, in
    an import request and in a reattach answer. A ``required`` field must be present in both; the
    others fall back to ``default``.
    """

    name: str
    wire: str
    kind: type = str
    default: object = ""
    max_length: int = 4096
    required: bool = False


# Every field of the context, in one place. The plugin's side is InstantEditImportContext and
# BlenderClient's import payload; Tests/TestSupport/context-protocol.json holds the wire names both
# suites check theirs against.
CONTEXT_FIELDS = (
    ContextField("context_id", "contextId", max_length=256, required=True),
    ContextField("schema", "schema", max_length=64, required=True),
    ContextField("version", "version", int, 0, required=True),
    ContextField("plugin_instance_id", "pluginInstanceId", max_length=256, required=True),
    ContextField("capability", "capability", max_length=1024, required=True),
    ContextField("source_game_path", "sourceGamePath", required=True),
    ContextField("source_kind", "sourceKind", default="mod", max_length=16),
    # Defaults to the source game path (see context_default).
    ContextField("resolved_game_path", "resolvedGamePath"),
    ContextField("destination_state", "destinationState", default="ready", max_length=32),
    ContextField("managed_destination", "managedDestination"),
    ContextField("target_file_path", "targetFilePath"),
    ContextField("source_mod_directory", "sourceModDirectory", max_length=256),
    ContextField("source_mod_stable_id", "sourceModStableId", max_length=64),
    ContextField("source_mod_name", "sourceModName", max_length=512),
    ContextField("source_mod_root_path", "sourceModRootPath"),
    ContextField("target_relative_path", "targetRelativePath"),
    ContextField("target_collection_id", "targetCollectionId", max_length=64),
    ContextField("target_collection_name", "targetCollectionName", max_length=512),
    ContextField("resource_manifest_version", "resourceManifestVersion", int, 0),
    ContextField("resource_manifest_status", "resourceManifestStatus", default="capture_failed", max_length=32),
    ContextField("backup_target_id", "backupTargetId", max_length=128),
    ContextField("backup_directory", "backupDirectory"),
    ContextField("import_id", "importId", max_length=256, required=True),
    ContextField("callback_port", "callbackPort", int, 0, required=True),
)
CONTEXT_FIELD_NAMES = tuple(field.name for field in CONTEXT_FIELDS)

# Everything clear_context_metadata removes: the context, and what the import added beside it.
CONTEXT_METADATA_FIELDS = CONTEXT_FIELD_NAMES + (
    "import_file_name", "collection_kind", "mashup_source_material",
)


def context_default(field: ContextField, values: dict, *, wire: bool = False):
    """The value an absent or empty optional field takes, given the record's other ``values``
    (keyed by wire names when ``wire``)."""
    if field.name == "resolved_game_path":
        return values.get("sourceGamePath" if wire else "source_game_path") or ""
    return field.default

REQUIRED_OBJECT_FIELDS = (
    "xiv_material",
    "original_material",
    "material_index",
    "mesh_index",
    "submesh_index",
    "context_id",
    "schema",
    "version",
)


class ContextValidationError(ValueError):
    """Raised when an XIV Instant Edit context cannot be used safely."""


class ContextSceneError(ContextValidationError):
    """The context's routing is fine, but its objects aren't (a mesh named outside "1.2 Name", no
    meshes, damaged object metadata): the scene's to fix, not a reason to drop the routing."""


@dataclass(frozen=True)
class MeshNameInfo:
    """Parsed YAA mesh identifier and the label/orientation around it."""

    mesh_group: int
    mesh_part: int
    lod: int
    label: str
    prefix: bool


# The name a sizes export gives its hidden working copies (mesh/export.py) until it writes them.
SIZE_COPY_NAME = "xiv_ie_size_copy"

_MESH_ID_PREFIX = re.compile(r"^(\d+)\.(\d+)(?:\s+(.*))?$")
_MESH_ID_SUFFIX = re.compile(r"^(.+?)\s+(\d+)\.(\d+)$")
# Blender puts a duplicate's ".001" after the LOD suffix.
_LOD_SUFFIX = re.compile(r"\s+LOD(\d+)(?:\.\d+)?$", re.IGNORECASE)


_planned_mesh_ids: dict[int, tuple[int, int]] | None = None


@contextmanager
def planned_mesh_ids(plan: dict[int, tuple[int, int]] | None):
    """Read mesh IDs from a plan (object pointer -> (group, part)) instead of names.

    A mesh-order drag records its moves here, so the panel can preview them
    while nothing is renamed until the drop is committed.
    """
    global _planned_mesh_ids
    previous = _planned_mesh_ids
    _planned_mesh_ids = plan
    try:
        yield plan
    finally:
        _planned_mesh_ids = previous


def active_mesh_id_plan() -> dict[int, tuple[int, int]] | None:
    return _planned_mesh_ids


def mesh_name_info(obj) -> MeshNameInfo:
    """Parse a mesh name while retaining its ID orientation and display label."""
    info = _parse_mesh_name(obj)
    if _planned_mesh_ids:
        planned = _planned_mesh_ids.get(obj.as_pointer())
        if planned is not None:
            return replace(info, mesh_group=planned[0], mesh_part=planned[1])
    return info


def _parse_mesh_name(obj) -> MeshNameInfo:
    name = str(obj.name).strip()
    lod = 0
    lod_match = _LOD_SUFFIX.search(name)
    if lod_match:
        lod = int(lod_match.group(1))
        name = name[:lod_match.start()].rstrip()
    if lod > 2:
        raise ContextValidationError(f"{obj.name}: invalid mesh name")

    prefix = _MESH_ID_PREFIX.match(name)
    if prefix:
        return MeshNameInfo(
            int(prefix.group(1)),
            int(prefix.group(2)),
            lod,
            (prefix.group(3) or "").strip(),
            True,
        )

    suffix = _MESH_ID_SUFFIX.match(name)
    if suffix:
        return MeshNameInfo(
            int(suffix.group(2)),
            int(suffix.group(3)),
            lod,
            suffix.group(1).strip(),
            False,
        )

    raise ContextValidationError(
        f'{obj.name}: expected a mesh name such as "1.2 Name" or "Name 1.2"'
    )


def is_safe_game_model_path(value: str) -> bool:
    """Accept only normalized, non-rooted game paths for model contexts."""
    if not isinstance(value, str) or not value or len(value) > 4096:
        return False
    if value.startswith(("/", "\\")) or "\\" in value or ":" in value or "\0" in value:
        return False
    parts = value.split("/")
    return all(part not in {"", ".", ".."} for part in parts) and value.casefold().endswith(".mdl")


@dataclass(frozen=True)
class ContextRef:
    collection: object
    objects: tuple
    mesh_objects: tuple
    context_id: str
    import_id: str
    plugin_instance_id: str
    capability: str
    source_game_path: str
    source_kind: str
    resolved_game_path: str
    destination_state: str
    managed_destination: str
    target_file_path: str
    source_mod_directory: str
    source_mod_stable_id: str
    source_mod_name: str
    source_mod_root_path: str
    target_relative_path: str
    target_collection_id: str
    target_collection_name: str
    resource_manifest_version: int
    resource_manifest_status: str
    backup_target_id: str
    backup_directory: str
    callback_port: int


def _value(obj, name: str, default=None):
    """Read a context property, accepting the prefixed spelling as well."""
    if name in obj:
        return obj[name]
    return obj.get(f"instant_edit_{name}", default)


def _set(obj, name: str, value) -> None:
    obj[name] = value
    obj[f"instant_edit_{name}"] = value


def _check_aliases(obj, names: Iterable[str]) -> None:
    for name in names:
        plain = obj.get(name, None)
        prefixed = obj.get(f"instant_edit_{name}", None)
        if name in obj and f"instant_edit_{name}" in obj and plain != prefixed:
            raise ContextValidationError(f"{getattr(obj, 'name', 'context')}: inconsistent {name} metadata")


def tag_object(obj, metadata: dict) -> None:
    """Attach immutable import metadata to an object."""
    for name, value in metadata.items():
        _set(obj, name, value)


def create_collection(scene, metadata: dict):
    context_id = metadata["context_id"]
    if any(_value(collection, "context_id") == context_id for collection in bpy.data.collections):
        raise ContextValidationError(f"Context {context_id!r} already exists")

    collection = bpy.data.collections.new(f"XIV Instant Edit [{context_id}]")
    scene.collection.children.link(collection)
    for name, value in metadata.items():
        _set(collection, name, value)
    _set(collection, "collection_kind", "instant_edit")
    return collection


def _in_scene(scene, collection) -> bool:
    def walk(parent) -> bool:
        for child in parent.children:
            if child == collection or walk(child):
                return True
        return False

    return walk(scene.collection)


def _context_collections(context_id: str, scene=None) -> list:
    collections = [
        collection for collection in bpy.data.collections
        if _value(collection, "context_id") == context_id
    ]
    if scene is not None:
        collections = [collection for collection in collections if _in_scene(scene, collection)]
    return collections


def context_collections(scene=None) -> list:
    """Return the XIV Instant Edit context collections currently held in a scene."""
    scene = scene or bpy.context.scene
    return [
        collection for collection in bpy.data.collections
        if _value(collection, "collection_kind") == "instant_edit"
        and _value(collection, "context_id", "")
        and _in_scene(scene, collection)
    ]


def collection_visible_in_view_layer(collection, view_layer=None) -> bool:
    """Return whether a collection is enabled through the current layer tree."""
    if getattr(collection, "hide_viewport", False):
        return False
    view_layer = view_layer or getattr(bpy.context, "view_layer", None)
    if view_layer is None:
        return False

    def walk(layer_collection, ancestors_visible=True) -> bool:
        visible = (
            ancestors_visible
            and not getattr(layer_collection, "exclude", False)
            and not getattr(layer_collection, "hide_viewport", False)
        )
        if layer_collection.collection == collection:
            return visible
        return any(walk(child, visible) for child in layer_collection.children)

    return walk(view_layer.layer_collection)


def visible_collections_in_view_layer(collections, view_layer=None) -> set:
    """Return the subset of ``collections`` enabled through the current layer tree.

    Walks the view layer's layer-collection tree once regardless of how many
    collections are being checked, instead of once per collection like
    repeated collection_visible_in_view_layer() calls would. Callers that
    need to check many collections at once (e.g. handlers that run on every
    depsgraph update) should use this instead.
    """
    view_layer = view_layer or getattr(bpy.context, "view_layer", None)
    wanted = {
        collection for collection in collections
        if not getattr(collection, "hide_viewport", False)
    }
    if view_layer is None or not wanted:
        return set()
    visible = set()

    def walk(layer_collection, ancestors_visible=True) -> None:
        collection_visible = (
            ancestors_visible
            and not getattr(layer_collection, "exclude", False)
            and not getattr(layer_collection, "hide_viewport", False)
        )
        collection = layer_collection.collection
        if collection_visible and collection in wanted:
            visible.add(collection)
        for child in layer_collection.children:
            walk(child, collection_visible)

    walk(view_layer.layer_collection)
    return visible


def _remove_metadata(obj, fields=CONTEXT_METADATA_FIELDS) -> None:
    for field in fields:
        obj.pop(field, None)
        obj.pop(f"instant_edit_{field}", None)


def clear_context_metadata(scene=None) -> int:
    """Detach all XIV Instant Edit routing metadata without deleting scene objects."""
    collections = context_collections(scene)
    context_ids = {
        _value(collection, "context_id", "")
        for collection in collections
    }
    collection_objects = {
        obj
        for collection in collections
        for obj in collection.objects
    }
    for obj in bpy.data.objects:
        if obj in collection_objects or _value(obj, "context_id", "") in context_ids:
            _remove_metadata(obj)
    for collection in collections:
        _remove_metadata(collection)
    return len(collections)


def apply_authoritative_context(collection, payload: dict) -> None:
    """Replace persisted routing metadata with the plugin's reattach response."""
    context_id = _value(collection, "context_id", "")
    import_id = _value(collection, "import_id", "")
    if payload.get("contextId") != context_id:
        raise ContextValidationError("context reattach response changed the context id")
    if import_id and payload.get("importId") != import_id:
        raise ContextValidationError("context reattach response changed the import id")
    for name, value in context_metadata_from_wire(payload, "context reattach response").items():
        _set(collection, name, value)


def context_metadata_from_wire(payload, what: str) -> dict:
    """A context the plugin sent (a reattach answer, a new mod's context), named as in Blender.

    Raises ContextValidationError naming ``what`` when a required field is missing or mistyped.
    """
    if not isinstance(payload, dict):
        raise ContextValidationError(f"{what} is not an object")
    if payload.get("schema") != SCHEMA or payload.get("version") not in SUPPORTED_VERSIONS:
        raise ContextValidationError(f"{what} has an invalid schema or version")
    fields = {}
    for field in CONTEXT_FIELDS:
        value = payload.get(field.wire)
        if field.required:
            if field.kind is int:
                if isinstance(value, bool) or not isinstance(value, int):
                    raise ContextValidationError(f"{what} has an invalid {field.wire}")
            elif not isinstance(value, str) or not value:
                raise ContextValidationError(f"{what} is missing {field.wire}")
        elif not value:
            value = context_default(field, payload, wire=True)
        fields[field.name] = value
    return fields


def routing_snapshot(collection) -> dict:
    """The collection's context fields as stored, to put back with restore_routing."""
    snapshot = {}
    for name in CONTEXT_FIELD_NAMES:
        for key in (name, f"instant_edit_{name}"):
            if key in collection:
                snapshot[key] = collection[key]
    return snapshot


def restore_routing(collection, snapshot: dict) -> None:
    for name in CONTEXT_FIELD_NAMES:
        for key in (name, f"instant_edit_{name}"):
            if key in snapshot:
                collection[key] = snapshot[key]
            else:
                collection.pop(key, None)


def reattach_context(collection, payload: dict, scene) -> "ContextRef":
    """Take on the plugin's answer for a saved context, or leave the collection as it was.

    Raises ContextValidationError when the answer doesn't fit or the result would not validate,
    with the collection's previous metadata put back. Raises ContextSceneError, keeping the answer,
    when only the collection's objects don't validate: that is the scene's to fix, and export says so.
    """
    snapshot = routing_snapshot(collection)
    try:
        apply_authoritative_context(collection, payload)
        return validate_context(_value(collection, "context_id", ""), scene)
    except ContextSceneError:
        raise
    except ContextValidationError:
        restore_routing(collection, snapshot)
        raise


def _require_int(value, name: str, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise ContextValidationError(f"{name} must be a non-negative integer")
    return value


def context_id_for_object(obj) -> str:
    """Resolve an object's routing context.

    An object's custom-property tag records where it was imported from, but an
    explicit link to an XIV Instant Edit collection is the user's routing
    choice.  This lets a model imported under one context be moved into
    another context and exported there without having to edit Blender's ID
    properties manually.  Objects linked to more than one Instant Edit
    collection remain ambiguous and are rejected.
    """
    collection_ids = set()
    for collection in obj.users_collection:
        collection_id = _value(collection, "context_id", "")
        if (
            isinstance(collection_id, str)
            and collection_id
            and _value(collection, "collection_kind") == "instant_edit"
        ):
            collection_ids.add(collection_id)
    if len(collection_ids) > 1:
        raise ContextValidationError(f"{obj.name}: object belongs to multiple XIV Instant Edit contexts")
    if collection_ids:
        return next(iter(collection_ids))

    tagged = _value(obj, "context_id", "")
    return tagged if isinstance(tagged, str) else ""


def mesh_ids_from_name(obj) -> tuple[int, int, int]:
    """Parse YAA-compatible group, part, and LOD identifiers from an object name."""
    info = mesh_name_info(obj)
    return info.mesh_group, info.mesh_part, info.lod


def try_mesh_ids_from_name(obj) -> tuple[int, int, int] | None:
    """mesh_ids_from_name, or None when the name isn't a mesh part name."""
    try:
        return mesh_ids_from_name(obj)
    except ContextValidationError:
        return None


def validate_context(context_id: str, scene=None) -> ContextRef:
    """Validate one complete context and return its authoritative collection."""
    scene = scene or bpy.context.scene
    if not isinstance(context_id, str) or not context_id:
        raise ContextValidationError("context id is missing")

    collections = _context_collections(context_id, scene)
    if len(collections) != 1:
        raise ContextValidationError(
            f"context {context_id!r} must have exactly one scene collection"
        )
    collection = collections[0]
    _check_aliases(collection, CONTEXT_FIELD_NAMES)

    if _value(collection, "schema") != SCHEMA or _value(collection, "version") not in SUPPORTED_VERSIONS:
        raise ContextValidationError("context collection has an invalid schema or version")

    stored = {}
    for field in CONTEXT_FIELDS:
        stored[field.name] = _value(collection, field.name, context_default(field, stored))
    plugin_instance_id = stored["plugin_instance_id"]
    capability = stored["capability"]
    source_game_path = stored["source_game_path"]
    source_kind = stored["source_kind"]
    resolved_game_path = stored["resolved_game_path"]
    destination_state = stored["destination_state"]
    managed_destination = stored["managed_destination"]
    target_file_path = stored["target_file_path"]
    source_mod_directory = stored["source_mod_directory"]
    source_mod_stable_id = stored["source_mod_stable_id"]
    source_mod_name = stored["source_mod_name"]
    source_mod_root_path = stored["source_mod_root_path"]
    target_relative_path = stored["target_relative_path"]
    target_collection_id = stored["target_collection_id"]
    target_collection_name = stored["target_collection_name"]
    resource_manifest_version = stored["resource_manifest_version"]
    resource_manifest_status = stored["resource_manifest_status"]
    backup_target_id = stored["backup_target_id"]
    backup_directory = stored["backup_directory"]
    import_id = stored["import_id"]
    callback_port = stored["callback_port"]
    if not all(isinstance(value, str) and value for value in (
        plugin_instance_id, capability, source_game_path, source_kind,
        resolved_game_path, destination_state
    )):
        raise ContextValidationError("context collection is missing immutable reference data")
    if source_kind not in {"mod", "game"} or destination_state not in {"ready", "new_mod_required"}:
        raise ContextValidationError("context collection has an invalid source or destination state")
    if source_mod_stable_id:
        try:
            if uuid.UUID(source_mod_stable_id).int == 0:
                raise ValueError
        except (ValueError, AttributeError, TypeError):
            raise ContextValidationError("context collection has an invalid Penumbra mod identity")
    if source_kind == "game" and destination_state != "ready" and source_mod_stable_id:
        raise ContextValidationError("game context contains unexpected Penumbra mod identity")
    if source_kind == "game" and (
        not is_safe_game_model_path(source_game_path) or
        not is_safe_game_model_path(resolved_game_path)
    ):
        raise ContextValidationError("game context contains an unsafe model path")
    if destination_state == "ready" and not all(isinstance(value, str) and value for value in (
        managed_destination, target_file_path, source_mod_directory, source_mod_name
    )):
        raise ContextValidationError("ready context collection is missing destination data")
    if _value(collection, "version") >= 3 and destination_state == "ready" and (
        not isinstance(backup_target_id, str) or len(backup_target_id) != 64 or
        any(char not in "0123456789abcdef" for char in backup_target_id) or
        not isinstance(backup_directory, str) or not backup_directory
    ):
        raise ContextValidationError("ready context collection is missing managed backup data")
    if destination_state == "new_mod_required" and (
        source_kind != "game" or any((managed_destination, target_file_path,
                                      source_mod_directory, source_mod_name,
                                      source_mod_root_path, target_relative_path))
    ):
        raise ContextValidationError("pending game context contains unexpected destination data")
    callback_port = _require_int(callback_port, "callback port", 1)
    resource_manifest_version = _require_int(resource_manifest_version, "resource manifest version")
    if resource_manifest_status not in {"capture_failed", "ready"}:
        raise ContextValidationError("context collection has an invalid resource manifest status")
    if callback_port > 65535:
        raise ContextValidationError("callback port is out of range")

    objects = tuple(collection.objects)
    try:
        mesh_objects = _validated_meshes(objects)
    except ContextSceneError:
        raise
    except ContextValidationError as error:
        raise ContextSceneError(str(error)) from error

    return ContextRef(
        collection=collection,
        objects=objects,
        mesh_objects=tuple(mesh_objects),
        context_id=context_id,
        import_id=import_id if isinstance(import_id, str) else "",
        plugin_instance_id=plugin_instance_id,
        capability=capability,
        source_game_path=source_game_path,
        source_kind=source_kind,
        resolved_game_path=resolved_game_path,
        destination_state=destination_state,
        managed_destination=managed_destination,
        target_file_path=target_file_path,
        source_mod_directory=source_mod_directory,
        source_mod_stable_id=source_mod_stable_id,
        source_mod_name=source_mod_name,
        source_mod_root_path=source_mod_root_path if isinstance(source_mod_root_path, str) else "",
        target_relative_path=target_relative_path if isinstance(target_relative_path, str) else "",
        target_collection_id=target_collection_id if isinstance(target_collection_id, str) else "",
        target_collection_name=target_collection_name if isinstance(target_collection_name, str) else "",
        resource_manifest_version=resource_manifest_version,
        resource_manifest_status=resource_manifest_status,
        backup_target_id=backup_target_id if isinstance(backup_target_id, str) else "",
        backup_directory=backup_directory if isinstance(backup_directory, str) else "",
        callback_port=callback_port,
    )


def _validated_meshes(objects) -> list:
    """The context collection's mesh objects, each checked (see validate_context)."""
    mesh_objects = []
    for obj in objects:
        if obj.name.startswith(SIZE_COPY_NAME):
            # A sizes export's hidden working copy, seen while the panel redraws mid-export.
            continue
        tagged_context_id = _value(obj, "context_id", "")
        if tagged_context_id:
            _check_aliases(obj, REQUIRED_OBJECT_FIELDS + ("plugin_instance_id", "capability"))
            # The collection is the explicit routing choice.  A tagged object
            # may have originated in a different context and then have been
            # moved here by the user; context_id_for_object() resolves that
            # case to this collection's ID.
            if _value(obj, "schema") != SCHEMA or _value(obj, "version") not in SUPPORTED_VERSIONS:
                raise ContextValidationError(f"{obj.name}: invalid XIV Instant Edit metadata schema")
        if obj.type != "MESH":
            continue

        # Imported and duplicated objects retain the immutable source metadata.
        # Newly-created objects are authorized by being explicitly linked into
        # this context collection and intentionally have no source metadata.
        if tagged_context_id:
            for field in REQUIRED_OBJECT_FIELDS:
                if field not in obj and f"instant_edit_{field}" not in obj:
                    raise ContextValidationError(f"{obj.name}: missing {field} metadata")
            material = _value(obj, "xiv_material")
            original = _value(obj, "original_material")
            if not isinstance(material, str) or not material:
                raise ContextValidationError(f"{obj.name}: invalid xiv_material metadata")
            if not isinstance(original, str) or not original:
                raise ContextValidationError(f"{obj.name}: invalid original material metadata")
            _require_int(_value(obj, "material_index"), "material index")
            _require_int(_value(obj, "mesh_index"), "mesh index")
            _require_int(_value(obj, "submesh_index"), "submesh index")

        # Multiple objects sharing the same group/part/LOD are a supported
        # "duplicate part instance" (see materials.mesh_part_instances) and
        # are flagged as a warning in the UI rather than rejected here; the
        # scene may also contain hidden duplicates that never affect export.
        # mesh_ids_from_name still runs so an unparsable mesh name is caught.
        mesh_ids_from_name(obj)
        mesh_objects.append(obj)

    if not mesh_objects:
        raise ContextSceneError("context contains no mesh objects")
    return mesh_objects


def active_context(context=None) -> ContextRef:
    """Resolve the single context represented by the active/selected objects."""
    context = context or bpy.context
    selected = list(context.selected_objects)
    active = context.active_object
    relevant = selected + ([] if active is None or active in selected else [active])

    resolved = [context_id_for_object(obj) for obj in relevant]
    ids = {context_id for context_id in resolved if context_id}
    if len(ids) > 1:
        raise ContextValidationError("selection contains mixed XIV Instant Edit contexts")

    if not ids:
        # Added meshes may intentionally live outside the imported collection.
        # The cached id selects only the validated export destination; geometry
        # is collected independently from all visible YAA-named meshes.
        scene_props = getattr(context.scene, "xiv_ie_instant_edit_props", None)
        context_id = getattr(scene_props, "context_id", "") if scene_props else ""
        if not context_id:
            raise ContextValidationError("no active XIV Instant Edit context")
    else:
        context_id = next(iter(ids))

    return validate_context(context_id, context.scene)


def metadata_for_export(obj) -> tuple[int, int, int]:
    """Return YAA-compatible name-based ordering within an XIV Instant Edit context."""
    context_id = context_id_for_object(obj)
    if not context_id:
        raise ContextValidationError(f"{obj.name}: object is outside an XIV Instant Edit context")
    return mesh_ids_from_name(obj)
