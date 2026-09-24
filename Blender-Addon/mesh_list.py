"""Rows of the Mesh Groups list and the drag that reorders them.

The panel draws each mesh group as a box: a header row, a line, the part rows
in one aligned column, another line and the material row. Every row is one
widget unit tall and the space between them follows Blender's style metrics.
A drag lays out every arrangement it could produce with the same rules the
panel draws with, and shows the arrangement whose dragged row lies under the
pointer.
"""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass, replace
from math import floor
from struct import pack, unpack

from .instant_edit.context import mesh_ids_from_name
from .materials import group_mesh_objects, mesh_display_name, mesh_part_instances
from .mesh.objects import visible_meshobj


def _f32(value: float) -> float:
    """Round to a 32-bit float; Blender computes its UI sizes in single precision."""
    return unpack("f", pack("f", value))[0]


def lod_zero_objects(objects) -> tuple:
    """Choose LOD 0, or the lowest available LOD, without failing on empty slots."""
    objects = tuple(objects)
    if not objects:
        return ()
    lod_zero = tuple(obj for obj in objects if mesh_ids_from_name(obj)[2] == 0)
    if lod_zero:
        return lod_zero
    lowest_lod = min(mesh_ids_from_name(obj)[2] for obj in objects)
    return tuple(obj for obj in objects if mesh_ids_from_name(obj)[2] == lowest_lod)


@dataclass(frozen=True)
class ListMetrics:
    """Pixel sizes of the list, as Blender 4.5 to 5.2 lay out panels.

    Every row is `unit` tall. The gaps are the space between a group's header
    row and its first part row, between its last part row and its material
    row, and between the last row of one group box and the first row of the
    next.
    """

    unit: int
    header_gap: int
    material_gap: int
    group_gap: int

    @classmethod
    def from_preferences(cls, preferences) -> "ListMetrics":
        system = preferences.system
        scale = _f32(system.ui_scale)

        def scaled(size: float, factor: float = 1.0) -> float:
            return _f32(_f32(size * scale) * _f32(factor))

        # A widget unit is 18 scaled pixels, rounded, plus a line-width border
        # on both sides. Items in a box are a column space (8 scaled pixels)
        # apart, a line separator is 6 scaled pixels times its factor tall,
        # and a box pads its content by 5 scaled pixels.
        column = int(scaled(8.0))
        return cls(
            unit=floor(scaled(18.0) + 0.5) + 2 * round(system.pixel_size),
            header_gap=2 * column + int(scaled(6.0, 0.2)),
            material_gap=2 * column + int(scaled(6.0, 0.5)),
            group_gap=2 * int(scaled(5.0)) + column,
        )


@dataclass(frozen=True)
class ListPart:
    """One visible part instance (all of its LODs) and its row in the list."""

    ident: int
    group: int
    part: int
    label: str
    instance_key: str
    objects: tuple = ()

    def order(self) -> tuple:
        return (self.part, self.label.casefold(), self.instance_key)


@dataclass(frozen=True)
class ListRow:
    kind: str  # "header", "part", "gap", "material" or "empty"
    group: int
    top: int = 0
    part: ListPart | None = None
    part_index: int = -1


def group_slots(occupied, maximum_group: int | None = None) -> list[int]:
    """Return the group indices the list shows for the occupied groups.

    Empty groups between occupied ones stay visible, and one empty group after
    the last is a drop target. During a drag that trailing group stops at the
    drag's fixed ceiling.
    """
    occupied = set(occupied)
    if not occupied:
        return []
    highest = max(occupied) + 1
    if maximum_group is not None:
        highest = max(max(occupied), min(highest, maximum_group))
    return list(range(highest + 1))


def layout_rows(parts, metrics: ListMetrics | None = None, maximum_group: int | None = None) -> list[ListRow]:
    """Lay out the list rows; without metrics, tops count rows and ignore the gaps."""
    metrics = metrics or ListMetrics(1, 0, 0, 0)
    unit = metrics.unit
    by_group = defaultdict(list)
    for part in parts:
        by_group[part.group].append(part)

    rows = []
    top = 0
    for group in group_slots(by_group, maximum_group):
        members = sorted(by_group.get(group, ()), key=ListPart.order)
        if not members:
            rows.append(ListRow("empty", group, top))
            top += unit + metrics.group_gap
            continue
        rows.append(ListRow("header", group, top))
        top += unit + metrics.header_gap
        previous = None
        for part in members:
            if previous is not None and part.part > previous + 1:
                # One placeholder row stands for the whole gap.
                rows.append(ListRow("gap", group, top, part_index=previous + 1))
                top += unit
            rows.append(ListRow("part", group, top, part=part))
            top += unit
            previous = part.part
        top += metrics.material_gap
        rows.append(ListRow("material", group, top))
        top += unit + metrics.group_gap
    return rows


def scene_parts() -> list[ListPart]:
    """Return a row for every visible part instance, using planned IDs during a drag."""
    return list_parts(visible_meshobj())


def list_parts(objects) -> list[ListPart]:
    """Return a row for every part instance among `objects`."""
    parts = []
    for group in group_mesh_objects(objects):
        for instance in mesh_part_instances(group.objects, group.mesh_index):
            shown = lod_zero_objects(instance.objects)
            parts.append(ListPart(
                ident=len(parts),
                group=group.mesh_index,
                part=instance.part_index,
                label=mesh_display_name(shown[0]),
                instance_key=instance.instance_key,
                objects=instance.objects,
            ))
    return parts


def _placement(parts) -> dict[int, tuple[int, int]]:
    return {part.ident: (part.group, part.part) for part in parts}


def placement_plan(parts, placement) -> dict[int, tuple[int, int]]:
    """Object pointer -> planned (group, part) for every object a placement moves."""
    plan = {}
    for item in parts:
        target = placement[item.ident]
        if target != (item.group, item.part):
            for obj in item.objects:
                plan[obj.as_pointer()] = target
    return plan


def moved_part(parts, ident: int, group: int, index: int) -> dict[int, tuple[int, int]]:
    """Return every part's (group, index) after dropping one part at `index` of `group`.

    Inside its own group a unique part rotates: the parts between its old and
    new index shift one step toward the index it left. A duplicate, or a part
    entering another group, is inserted instead: the parts from `index` upward
    shift by one until a free index absorbs the shift, so no other part takes
    over a duplicate ID. The parts of the group a part leaves keep their IDs.
    """
    moving = next(part for part in parts if part.ident == ident)
    placement = _placement(parts)
    others = [part for part in parts if part.ident != ident and part.group == group]
    rotates = group == moving.group and all(part.part != moving.part for part in others)
    if rotates:
        for part in others:
            if moving.part < part.part <= index:
                placement[part.ident] = (group, part.part - 1)
            elif index <= part.part < moving.part:
                placement[part.ident] = (group, part.part + 1)
    else:
        occupied = defaultdict(list)
        for part in others:
            occupied[part.part].append(part.ident)
        shifted = index
        while shifted in occupied:
            for other in occupied[shifted]:
                placement[other] = (group, shifted + 1)
            shifted += 1
    placement[ident] = (group, index)
    return placement


def moved_group(parts, group: int, index: int) -> dict[int, tuple[int, int]]:
    """Return every part's (group, index) after moving a whole group to slot `index`."""
    placement = _placement(parts)
    for part in parts:
        if part.group == group:
            placement[part.ident] = (index, part.part)
        elif group < part.group <= index:
            placement[part.ident] = (part.group - 1, part.part)
        elif index <= part.group < group:
            placement[part.ident] = (part.group + 1, part.part)
    return placement


def part_targets(parts, ident: int, maximum_group: int) -> list[tuple[int, int]]:
    """Every (group, index) a part can be dropped at, in list order."""
    moving = next(part for part in parts if part.ident == ident)
    targets = []
    for group in range(maximum_group + 1):
        indices = [part.part for part in parts if part.group == group and part.ident != ident]
        if group == moving.group:
            # A duplicate may also take the free index after the group's end.
            bounds = indices + [moving.part]
            last = max(bounds) + (1 if moving.part in indices else 0)
            targets.extend((group, index) for index in range(min(bounds), last + 1))
        elif indices:
            targets.extend((group, index) for index in range(min(indices), max(indices) + 2))
        else:
            targets.append((group, 0))
    return targets


class DragSession:
    """Candidate arrangements of one drag and the one under the pointer."""

    def __init__(
        self,
        parts,
        metrics: ListMetrics,
        scope: str,
        group: int,
        part: int = -1,
        instance_key: str = "",
    ):
        self.parts = tuple(parts)
        self.metrics = metrics
        self.scope = scope
        if not self.parts:
            raise LookupError("There are no visible mesh parts.")
        self.maximum_group = max(item.group for item in self.parts) + 1
        original = _placement(self.parts)
        if scope == "GROUP":
            if all(item.group != group for item in self.parts):
                raise LookupError(f"Mesh group {group} is no longer visible.")
            self.moving = None
            self.group = group
            placements = [moved_group(self.parts, group, index) for index in range(self.maximum_group + 1)]
        else:
            self.moving = next(
                (
                    item for item in self.parts
                    if item.group == group and item.part == part
                    and (not instance_key or item.instance_key == instance_key)
                ),
                None,
            )
            if self.moving is None:
                raise LookupError(f"Mesh part {group}.{part} is no longer visible.")
            placements = [
                moved_part(self.parts, self.moving.ident, target_group, index)
                for target_group, index in part_targets(self.parts, self.moving.ident, self.maximum_group)
            ]
        # The unchanged arrangement always comes first, so a click without a
        # drag, or a return to the start, changes nothing.
        self._candidates = [(self._anchor(original), original)]
        for placement in placements:
            if placement != original:
                self._candidates.append((self._anchor(placement), placement))
        self._current = 0
        self.origin = self._candidates[0][0]
        self._hysteresis = max(1, metrics.unit // 5)

    def _arranged(self, placement) -> list[ListPart]:
        return [
            replace(item, group=placement[item.ident][0], part=placement[item.ident][1])
            for item in self.parts
        ]

    def _anchor(self, placement) -> float:
        """Center of the dragged row (a part's name row, or a group's header)."""
        rows = layout_rows(self._arranged(placement), self.metrics, self.maximum_group)
        if self.moving is not None:
            row = next(row for row in rows if row.part is not None and row.part.ident == self.moving.ident)
        else:
            target = placement[next(item.ident for item in self.parts if item.group == self.group)][0]
            row = next(row for row in rows if row.kind == "header" and row.group == target)
        return row.top + self.metrics.unit / 2

    def update(self, offset: float) -> bool:
        """Follow a pointer `offset` pixels below where the drag started; return whether it changed."""
        pointer = self.origin + offset
        distance = [abs(anchor - pointer) for anchor, _placement_ in self._candidates]
        best = min(range(len(distance)), key=lambda index: (distance[index], index != self._current))
        if best == self._current or distance[best] + self._hysteresis >= distance[self._current]:
            return False
        self._current = best
        return True

    @property
    def placement(self) -> dict[int, tuple[int, int]]:
        return self._candidates[self._current][1]

    def plan(self) -> dict[int, tuple[int, int]]:
        """Object pointer -> planned (group, part) for every object the drop moves."""
        return placement_plan(self.parts, self.placement)

    def drag_state(self) -> tuple[str, int, int, int, str]:
        """Scope, current group/part, ceiling, and instance key of the dragged row."""
        if self.moving is None:
            target = self.placement[next(item.ident for item in self.parts if item.group == self.group)][0]
            return ("GROUP", target, -1, self.maximum_group, "")
        group, part = self.placement[self.moving.ident]
        return ("PART", group, part, self.maximum_group, self.moving.instance_key)
