"""Meshes reshaped for another race: sent that way for preview, or reshaped in Blender to export.

With racial scaling on, and always for Send my character, the plugin sends a model made for one
race in the shape the character's race wears it (c0201 gear on a c0801 character, as the game's
racial deformer shows it), to check it against that race's face and hair. Each mesh of such an import carries the xiv_racial_scaling
custom property, "c0201 to c0801". Quick Export and mashups refuse these meshes (the plugin also
refuses every export from a scaled import); Simple Export asks first and warns.

The Tools panel's Apply Racial Scaling reshapes meshes in Blender instead, with the deformer
matrices the plugin reads from the player's collection. That shape is meant to be exported (as a
model for the target race), so no export checks it. The mesh data (not the object, so undo and
duplicates keep it with the vertices it describes) carries xiv_applied_racial_scaling,
"c0201 to c0801", so applying another race replaces that scaling instead of stacking on it.
"""

import json
import re

import numpy as np

PROPERTY = "xiv_racial_scaling"
APPLIED_PROPERTY = "xiv_applied_racial_scaling"
_RACE = re.compile(r"c\d{4}")
# A model's race in a game path or file name: c0201e6000_top.mdl, chara/human/c0801/...
_MODEL_RACE = re.compile(r"(?i)(?<![a-z0-9])c(\d{4})(?:[abefhtz]\d{4}|/)")

REQUEST_SCHEMA = "instant-edit.racial-deformer-request"
REQUEST_TIMEOUT_SECONDS = 8
MAX_RESPONSE_BYTES = 4 * 1024 * 1024

_RACE_NAMES = ("Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera")
# Player races in code order: 0101 Male Midlander, 0201 Female Midlander, ... 1801 Female Viera.
PLAYER_RACES = tuple(
    ((index * 2 + female + 1) * 100 + 1, f"{'Female' if female else 'Male'} {name}")
    for index, name in enumerate(_RACE_NAMES)
    for female in (0, 1)
)
RACE_LABELS = dict(PLAYER_RACES)


def race_label(code: int) -> str:
    """Like "Female Miqo'te (c0801)"."""
    label = RACE_LABELS.get(code)
    return f"{label} (c{code:04d})" if label else f"c{code:04d}"


def applied(obj) -> tuple[int, int] | None:
    """(model race, current race) of the scaling Apply Racial Scaling left on the mesh, or None."""
    match = re.fullmatch(r"c(\d{4}) to c(\d{4})", str(obj.data.get(APPLIED_PROPERTY) or ""))
    if not match:
        return None
    model_race, race = int(match.group(1)), int(match.group(2))
    return (model_race, race) if model_race in RACE_LABELS and race in RACE_LABELS and model_race != race else None


def set_applied(obj, model_race: int, race: int) -> None:
    """Record the mesh's scaling; none when it is back to the model's own race."""
    if model_race == race:
        obj.data.pop(APPLIED_PROPERTY, None)
    else:
        obj.data[APPLIED_PROPERTY] = f"c{model_race:04d} to c{race:04d}"
    # Before 2026-10-04 the mark sat on the object, where undo could leave it behind.
    obj.pop(APPLIED_PROPERTY, None)


def detect_model_race(obj) -> int | None:
    """The player race the mesh's model is made for, from the game path its import recorded or a
    model file name in its own, its armature's or its collections' names. None when unclear."""
    from .context import _value

    if marked := applied(obj):
        return marked[0]
    holders = [obj, *obj.users_collection]
    texts = [str(_value(holder, key, "") or "") for holder in holders for key in ("source_game_path", "resolved_game_path")]
    texts += [holder.name for holder in (obj, obj.parent, *obj.users_collection) if holder is not None]
    for text in texts:
        for match in _MODEL_RACE.finditer(text.replace("\\", "/")):
            code = int(match.group(1))
            if code in RACE_LABELS:
                return code
    return None


def request_deformer(model_race: int, race: int, port: int | None = None) -> tuple[int, dict[str, np.ndarray]]:
    """Ask the running plugin for the deformer that reshapes a model of ``model_race`` for ``race``
    (0: the player's race), as the player's collection loads it.

    Returns (the target race, {bone: 3×4 matrix in game space}); raises ValueError with why not."""
    if port is None:
        try:
            from ..preferences import get_prefs
            port = int(get_prefs().instant_edit_plugin_port)
        except Exception:
            raise ValueError("the plugin's port is unknown") from None
    from .plugin_http import PluginResponseTooLarge, post_json

    payload = {"schema": REQUEST_SCHEMA, "version": 1, "modelRace": model_race, "race": race}
    try:
        status, body = post_json(port, "/racial-deformer", payload, timeout=REQUEST_TIMEOUT_SECONDS,
                                 max_response_size=MAX_RESPONSE_BYTES)
    except PluginResponseTooLarge:
        raise ValueError("the plugin's answer was too large") from None
    except OSError:
        raise ValueError("the XIV Instant Edit plugin isn't running") from None
    try:
        response = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        response = None
    if not isinstance(response, dict):
        raise ValueError(f"the plugin gave an unreadable answer (HTTP {status})")
    if status == 404 and response.get("code") == "endpoint_not_found":
        raise ValueError("the plugin is too old for this tool; update XIV Instant Edit")
    if status == 422 and "not an ancestor" in str(response.get("error")):
        # Each race's deformer builds on its parent race's: c0201 reaches c0801, never the other way.
        target = RACE_LABELS.get(race, "that race") if race else "your character's race"
        raise ValueError(f"the game can't reshape c{model_race:04d} models for {target}")
    if not 200 <= status < 300:
        message = response.get("error") or response.get("cause") or response.get("message")
        raise ValueError(str(message or f"the plugin refused (HTTP {status})")[:300])
    return parse_deformer(response)


def parse_deformer(response: dict) -> tuple[int, dict[str, np.ndarray]]:
    """(target race, {bone: 3×4 matrix}) from the plugin's answer; ValueError when malformed."""
    race = response.get("race")
    bones = response.get("bones")
    if not isinstance(race, int) or isinstance(race, bool) or race not in RACE_LABELS or not isinstance(bones, list):
        raise ValueError("the plugin's racial deformer is malformed")
    matrices = {}
    for bone in bones:
        name = bone.get("name") if isinstance(bone, dict) else None
        values = bone.get("matrix") if isinstance(bone, dict) else None
        if not isinstance(name, str) or not name or not isinstance(values, list) or len(values) != 12:
            raise ValueError("the plugin's racial deformer is malformed")
        try:
            matrix = np.array(values, dtype=np.float64).reshape(3, 4)
        except (TypeError, ValueError):
            raise ValueError("the plugin's racial deformer is malformed") from None
        if not np.all(np.isfinite(matrix)):
            raise ValueError("the plugin's racial deformer is malformed")
        matrices[name] = matrix
    return race, matrices


def parse_request(value) -> str:
    """The property value for an import request's racialScaling field, or "" without one.

    Raises ValueError for a field the plugin wouldn't send.
    """
    if value is None:
        return ""
    if not isinstance(value, dict):
        raise ValueError("racialScaling is not an object")
    model_race, race = value.get("modelRace"), value.get("race")
    if not all(isinstance(item, str) and _RACE.fullmatch(item) for item in (model_race, race)) or model_race == race:
        raise ValueError("racialScaling names invalid races")
    return f"{model_race} to {race}"


def mark(objects, value: str) -> None:
    """Tag an import's meshes with the scaling they were sent with."""
    if value:
        for obj in objects:
            obj[PROPERTY] = value


def marks(objects) -> dict[str, list[str]]:
    """Each racial scaling the objects carry, with the names of the objects carrying it."""
    found: dict[str, list[str]] = {}
    for obj in objects:
        value = obj.get(PROPERTY)
        if value:
            found.setdefault(str(value), []).append(obj.name)
    return found


def _describe(found: dict[str, list[str]]) -> tuple[str, str, bool]:
    """(the first few mesh names, the scalings, whether several meshes)."""
    names = sorted(name for group in found.values() for name in group)
    listed = ", ".join(names[:3]) + (f" (+{len(names) - 3} more)" if len(names) > 3 else "")
    return listed, " and ".join(sorted(found)), len(names) != 1


def quick_export_refusal(objects) -> str:
    """Why Quick Export and mashups refuse these objects, or "" when none is racially scaled."""
    found = marks(objects)
    if not found:
        return ""
    listed, scalings, several = _describe(found)
    return (
        f"{listed} {'were' if several else 'was'} sent racially scaled ({scalings}) for preview only, "
        "so Quick Export can't write them. Re-import the model with racial scaling off to edit and export it."
    )


def simple_export_warning(objects) -> str:
    """What Simple Export warns about when it writes these objects, or "" when none is racially scaled."""
    found = marks(objects)
    if not found:
        return ""
    listed, scalings, several = _describe(found)
    return (
        f"{listed} {'are' if several else 'is'} racially scaled ({scalings}) for preview. "
        "The file keeps that shape, not the model's own race's."
    )
