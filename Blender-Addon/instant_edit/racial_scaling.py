"""Meshes the plugin sent reshaped for another race, for preview only.

With racial scaling on, and always for Send my character, the plugin sends a model made for one
race in the shape the character's race wears it (c0201 gear on a c0801 character, as the game's
racial deformer shows it), to check it against that race's face and hair. Each mesh of such an import carries the xiv_racial_scaling
custom property, "c0201 to c0801". Quick Export and mashups refuse these meshes (the plugin also
refuses every export from a scaled import); Simple Export asks first and warns.
"""

import re

PROPERTY = "xiv_racial_scaling"
_RACE = re.compile(r"c\d{4}")


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
