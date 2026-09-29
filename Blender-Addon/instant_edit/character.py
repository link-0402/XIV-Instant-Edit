"""Whole characters sent by the plugin's Send my character to Blender, bound to one armature.

The plugin sends each model a character shows as its own import with its own Instant Edit
context, so that each model can still be exported on its own. The imports of one send carry the
same ``character`` entry: the send's id, the character it belongs to (a key and a display name),
the name its armature gets, and the model's role.

Body models (the body, face, hair, tail or ears and gear: everything the character's own
skeleton moves) bind to one armature. The send's first import builds it from the character's
whole skeleton, merged from all of its partial skeletons (body, face, hair, headgear and top);
later imports add only the bones their model weights that the armature lacks, placed at their
vertices.

A weapon keeps its own skeleton, whose bones repeat body bone names such as n_root, so it gets
its own armature, as a single import does. Given the character bone the game holds it by and its
place relative to that bone, that armature hangs from the bone and follows its pose. Exports put
such an armature back at the origin, where its own skeleton puts the model.

A send's armatures and model collections live in one collection per character. A new send of the
same character first removes the previous one: its collections, the objects imported into them,
and its Instant Edit contexts, which the plugin is told to forget. Objects that someone put into
those collections themselves are moved to the scene's own collection instead.

Validation needs neither bpy nor numpy, so the server can check requests anywhere.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import math
import re


# Custom properties on the send's collection, armatures and meshes.
KIND_PROPERTY = "xiv_ie_character"
SEND_PROPERTY = "xiv_ie_character_send"
KEY_PROPERTY = "xiv_ie_character_key"
NAME_PROPERTY = "xiv_ie_character_name"
ROLE_PROPERTY = "xiv_ie_character_role"
COLLECTION_KIND = "character"
BODY = "body"
WEAPON = "weapon"
ROLES = (BODY, WEAPON)
MAX_KEY_LENGTH = 256
MAX_NAME_LENGTH = 128
MAX_BONE_NAME_LENGTH = 128
# Blender cuts object names at 63 bytes.
MAX_ARMATURE_NAME_LENGTH = 63
# Translation xyz, rotation xyzw, scale xyz, as in skeletons and animation takes.
STRIDE = 10
_SEND_ID = re.compile(r"[0-9a-f]{32}")


@dataclass(frozen=True)
class CharacterImport:
    """One import's part of a character send."""
    send_id: str
    key: str
    name: str
    role: str
    armature_name: str
    # A weapon: the character bone that holds it, and its place relative to that bone in the
    # game's Y-up space (translation, rotation x/y/z/w, scale). Empty when the game named none.
    attach_bone: str = ""
    attach_offset: tuple[float, ...] = ()


def _text(value, name: str, max_length: int, *, required: bool = True) -> str:
    if value is None and not required:
        return ""
    if not isinstance(value, str) or len(value) > max_length or (required and not value.strip()):
        raise ValueError(f"character {name} is missing or invalid")
    return value.strip()


def _offset(value) -> list[float]:
    if (not isinstance(value, list) or len(value) != STRIDE
            or not all(isinstance(item, (int, float)) and not isinstance(item, bool) for item in value)):
        raise ValueError("the weapon's place on its bone is invalid")
    try:
        values = [float(item) for item in value]
    except OverflowError as error:
        raise ValueError("the weapon's place on its bone is not finite") from error
    if not all(math.isfinite(item) for item in values):
        raise ValueError("the weapon's place on its bone is not finite")
    if abs(math.sqrt(sum(item * item for item in values[3:7])) - 1.0) > 0.01:
        raise ValueError("the weapon's rotation on its bone is not a unit quaternion")
    if any(abs(item) < 1e-6 for item in values[7:10]):
        raise ValueError("the weapon's scale on its bone is zero")
    return values


def parse_request(value) -> dict | None:
    """The import request's character entry, checked, or None without one.

    Raises ValueError for an entry the plugin wouldn't send."""
    if value is None:
        return None
    if not isinstance(value, dict):
        raise ValueError("character is not an object")
    send_id = value.get("sendId")
    if not isinstance(send_id, str) or not _SEND_ID.fullmatch(send_id):
        raise ValueError("the character send id is invalid")
    role = value.get("role")
    if role not in ROLES:
        raise ValueError("the character model role is invalid")
    attach = value.get("attach")
    if attach is not None:
        if role != WEAPON or not isinstance(attach, dict):
            raise ValueError("only weapons hang from a character bone")
        attach = {
            "bone": _text(attach.get("bone"), "attach bone", MAX_BONE_NAME_LENGTH),
            "offset": _offset(attach.get("offset")),
        }
    return {
        "sendId": send_id,
        "key": _text(value.get("key"), "key", MAX_KEY_LENGTH),
        "name": _text(value.get("name"), "name", MAX_NAME_LENGTH),
        "role": role,
        "armatureName": _text(value.get("armatureName"), "armature name", MAX_ARMATURE_NAME_LENGTH),
        "attach": attach,
    }


def to_property(value: dict | None) -> str:
    """A checked entry as the import operator's character property; "" for none."""
    return json.dumps(value, separators=(",", ":")) if value else ""


def from_property(text: str) -> CharacterImport | None:
    """The CharacterImport an import operator's character property holds, or None for ""."""
    if not text:
        return None
    entry = parse_request(json.loads(text))
    attach = entry["attach"] or {}
    return CharacterImport(
        send_id=entry["sendId"],
        key=entry["key"],
        name=entry["name"],
        role=entry["role"],
        armature_name=entry["armatureName"],
        attach_bone=attach.get("bone", ""),
        attach_offset=tuple(attach.get("offset", ())),
    )


# ----------------------------------------------------------------------
# The scene.


def _tag(item, character: CharacterImport, role: str = "") -> None:
    item[SEND_PROPERTY] = character.send_id
    item[KEY_PROPERTY] = character.key
    if role:
        item[ROLE_PROPERTY] = role


def _is_character_collection(collection) -> bool:
    return collection.get(KIND_PROPERTY) == COLLECTION_KIND


def character_collection(scene, character: CharacterImport, *, create: bool = True):
    """The collection holding this send's armatures and model collections, made on first use."""
    import bpy
    from .context import _in_scene

    for collection in bpy.data.collections:
        if (_is_character_collection(collection) and collection.get(SEND_PROPERTY) == character.send_id
                and _in_scene(scene, collection)):
            return collection
    if not create:
        return None
    collection = bpy.data.collections.new(f"XIV Instant Edit Character [{character.name}]")
    scene.collection.children.link(collection)
    collection[KIND_PROPERTY] = COLLECTION_KIND
    collection[NAME_PROPERTY] = character.name
    _tag(collection, character)
    return collection


def body_armature(scene, send_id: str):
    """The armature a send's body models are bound to, or None."""
    for obj in scene.objects:
        if (obj.type == "ARMATURE" and obj.get(SEND_PROPERTY) == send_id
                and obj.get(ROLE_PROPERTY) == BODY):
            return obj
    return None


def adopt_collection(scene, character: CharacterImport, collection) -> None:
    """Move an import's context collection into the send's collection."""
    holder = character_collection(scene, character)
    if collection.name not in holder.children:
        holder.children.link(collection)
    if collection.name in scene.collection.children:
        scene.collection.children.unlink(collection)
    _tag(collection, character)


def _remove_objects(objects) -> None:
    import bpy

    for obj in objects:
        if obj.name not in bpy.data.objects:
            continue
        data, kind = obj.data, obj.type
        bpy.data.objects.remove(obj, do_unlink=True)
        data_blocks = {"MESH": bpy.data.meshes, "ARMATURE": bpy.data.armatures}.get(kind)
        if data is not None and data_blocks is not None and data.users == 0 and data.name in data_blocks:
            data_blocks.remove(data)


def _revoke(context_collections) -> None:
    """Tell the plugin to forget removed contexts; a context that can't be queued is left to it."""
    if not context_collections:
        return
    from .revocation import queue_context_revocations, schedule_revocations

    queued = 0
    for collection in context_collections:
        try:
            queued += queue_context_revocations([collection])
        except Exception as error:
            print(f"XIV Instant Edit: could not queue the revocation of a replaced context: {error}")
    if queued:
        schedule_revocations()


def replace_previous(scene, character: CharacterImport) -> int:
    """Remove the earlier sends of this character from the scene. Returns how many were removed."""
    import bpy
    from .context import _in_scene, _value

    stale = [
        collection for collection in bpy.data.collections
        if _is_character_collection(collection) and collection.get(KEY_PROPERTY) == character.key
        and collection.get(SEND_PROPERTY) != character.send_id and _in_scene(scene, collection)
    ]
    for holder in stale:
        send_id = holder.get(SEND_PROPERTY)
        tree = [holder, *holder.children_recursive]
        contexts = [collection for collection in tree if _value(collection, "collection_kind") == "instant_edit"]
        context_ids = {_value(collection, "context_id", "") for collection in contexts} - {""}
        imported, kept = [], []
        for obj in holder.all_objects:
            ours = obj.get(SEND_PROPERTY) == send_id or _value(obj, "context_id", "") in context_ids
            (imported if ours else kept).append(obj)
        _revoke(contexts)
        for obj in kept:
            # Something the user put there, and nowhere else: keep it in the scene.
            if all(collection in tree for collection in obj.users_collection):
                scene.collection.objects.link(obj)
        _remove_objects(imported)
        for collection in reversed(tree):
            if collection.name in bpy.data.collections:
                bpy.data.collections.remove(collection)
    return len(stale)


def discard_if_empty(scene, character: CharacterImport) -> None:
    """Remove the send's collection when a failed import leaves it empty."""
    import bpy

    holder = character_collection(scene, character, create=False)
    if holder is not None and not holder.objects and not holder.children:
        bpy.data.collections.remove(holder)


def bind_body(context, character: CharacterImport, collection, mesh_objects, bone_names, skeleton,
              created_objects=None) -> str:
    """Bind a body model's meshes to the send's armature, which the send's first import builds
    from the character's skeleton. Returns a note for the status line, or ""."""
    from .skeleton import add_bones, create_armature

    scene = context.scene
    holder = character_collection(scene, character)
    armature = body_armature(scene, character.send_id)
    if armature is None:
        armature, report = create_armature(context, holder, character.armature_name, bone_names, mesh_objects,
                                           skeleton, created_objects=created_objects)
        _tag(armature, character, BODY)
        # As for single imports, the bones stay out of the viewport; the armature still deforms.
        armature.hide_set(True)
    else:
        report = add_bones(context, armature, bone_names, mesh_objects, skeleton)
    adopt_collection(scene, character, collection)
    for obj in mesh_objects:
        obj.parent = armature
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = armature
        _tag(obj, character)
    return report.summary()


def _offset_matrix(offset):
    """The weapon armature's place relative to its bone, both in Blender's space: the game's
    offset, taken from the weapon's own Z-up space back to the game's Y-up space first."""
    import numpy as np
    from mathutils import Matrix

    from .animation import GAME_TO_BLENDER, _matrices

    matrix = _matrices(np.asarray(offset, dtype=np.float64)) @ GAME_TO_BLENDER.T
    return Matrix(matrix.tolist())


def hang_weapon(context, character: CharacterImport, armature, mesh_objects=()) -> str:
    """Hang a weapon's armature from the character bone that holds it in the game, at the place
    the game shows it there. Returns a note for the status line, or ""."""
    scene = context.scene
    _tag(armature, character, WEAPON)
    for obj in mesh_objects:
        _tag(obj, character)
    if not character.attach_bone:
        return "the game named no bone holding the weapon, so it stays at the origin"
    body = body_armature(scene, character.send_id)
    if body is None:
        return "the character's armature is missing, so the weapon stays at the origin"
    pose_bone = body.pose.bones.get(character.attach_bone)
    if pose_bone is None:
        return f"the character's armature has no {character.attach_bone} bone, so the weapon stays at the origin"
    context.view_layer.update()
    # The game's offset is relative to the bone as it is posed now; Blender's pose bone matrix is
    # its rest pose while the armature carries no pose.
    world = body.matrix_world @ pose_bone.matrix @ _offset_matrix(character.attach_offset)
    armature.parent = body
    armature.parent_type = "BONE"
    armature.parent_bone = character.attach_bone
    context.view_layer.update()
    armature.matrix_world = world
    return ""


def is_hung_weapon(armature) -> bool:
    """Whether an armature is a weapon that hangs from a character bone (see hang_weapon)."""
    return armature.get(ROLE_PROPERTY) == WEAPON and armature.parent is not None
