from pathlib import Path
import uuid

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty, StringProperty
from bpy.types import Context, Operator

from .instant_edit.context import ContextValidationError
from .materials import (
    ATTRIBUTE_VARIANT_PRESETS,
    FACE_ATTRIBUTE_PRESETS,
    attribute_display_name,
    assign_material_path,
    backface_copies,
    commit_mesh_id_plan,
    find_material_group,
    mesh_part_instance_objects,
    mesh_part_instances,
    material_mismatch_parts,
    material_paths,
    material_suggestions,
    matching_material_path,
    other_group_materials,
    set_mesh_part_attribute,
    convert_suffix_mesh_names,
)
from .mesh_list import DragSession, ListMetrics, list_parts, moved_part, placement_plan, scene_parts
from .mesh.export import check_triangulation, check_weights, export_result, get_export_stats
from .mesh.objects import visible_meshobj
from .mesh.armatures import available_armatures, combine_armatures
from .mesh.vertex_data import (
    ACTIONS as VERTEX_DATA_ACTIONS,
    action_name as vertex_data_action_name,
    apply_vertex_data,
    missing_data_label,
)
from .pose import pose_armature, show_action, toggle_rest_pose
from .properties import get_settings
from .xivpy.model import XIVModel
from .backups import clear_backups, list_backups, restore_local, target_folder


_ACTIVE_MESH_DRAG: tuple[str, int, int, int, str] | None = None
_ACTIVE_MESH_DRAG_PLAN: dict[int, tuple[int, int]] | None = None
_ATTRIBUTE_PRESET_ITEMS = tuple(
    (attribute, attribute_display_name(attribute), f"Add {attribute} to this mesh part")
    for attribute in ATTRIBUTE_VARIANT_PRESETS + FACE_ATTRIBUTE_PRESETS
)


def active_mesh_drag_state() -> tuple[str, int, int, int, str] | None:
    """Return scope, current group/part, ceiling, and optional instance key."""
    return _ACTIVE_MESH_DRAG


def active_mesh_drag_plan() -> dict[int, tuple[int, int]] | None:
    """Return the planned mesh IDs of the drag in progress, if any."""
    return _ACTIVE_MESH_DRAG_PLAN


def _redraw(context: Context) -> None:
    if context.screen:
        for area in context.screen.areas:
            area.tag_redraw()


def _region_zoom(region) -> float:
    """Pixels per layout pixel of a region's View2D (1.0 unless the user zoomed it)."""
    try:
        _x, bottom = region.view2d.view_to_region(0.0, 0.0, clip=False)
        _x, top = region.view2d.view_to_region(0.0, 1000.0, clip=False)
    except (AttributeError, RuntimeError, TypeError):
        return 1.0
    zoom = abs(top - bottom) / 1000.0
    return zoom if zoom > 0.0 else 1.0


class _new_objects_since:
    """Diff bpy.data.objects around a block that runs a built-in import operator.

    Blender's built-in importers (e.g. import_scene.fbx) don't report which
    objects they created, unlike ModelImport.from_file's explicit
    created_objects parameter, so this is the only way to recover that list.
    """

    def __enter__(self):
        self._before = set(bpy.data.objects)
        self.created: list = []
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.created = [obj for obj in bpy.data.objects if obj not in self._before]


def _simple_import_bind_existing_skeleton(imported_objects: list, skeleton) -> list:
    """Rebind this import's meshes and remove only its imported armatures."""
    mesh_objects = [obj for obj in imported_objects if obj.type == "MESH"]
    for obj in mesh_objects:
        for modifier in tuple(obj.modifiers):
            if modifier.type == "ARMATURE":
                obj.modifiers.remove(modifier)
        obj.parent = None
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = skeleton

    remaining = []
    for obj in imported_objects:
        if obj.type != "ARMATURE":
            remaining.append(obj)
            continue
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if data is not None and data.users == 0 and data.name in bpy.data.armatures:
            bpy.data.armatures.remove(data)
    return remaining


def _simple_import_create_armature(
    context: Context,
    file_path: Path,
    mesh_objects: list,
    imported_objects: list | None = None,
):
    """Create the armature for an MDL imported from disk: the game's skeleton when the running
    plugin finds one for the file's name, else the model's bones without a rest pose.

    Returns (armature, note for the report or an empty string)."""
    from .instant_edit.skeleton import create_armature, request_skeleton

    model = XIVModel.from_file(str(file_path))
    game_skeleton, reason = request_skeleton(file_path)
    target_collection = context.collection or context.scene.collection
    previous_selection = tuple(context.selected_objects)
    previous_active = context.view_layer.objects.active
    armature_obj, report = create_armature(
        context, target_collection, "InstantEditArmature", model.bones, mesh_objects, game_skeleton,
        created_objects=imported_objects)

    for obj in mesh_objects:
        obj.parent = armature_obj
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = armature_obj

    for obj in previous_selection:
        if obj.name in bpy.data.objects:
            obj.select_set(True)
    context.view_layer.objects.active = (
        previous_active
        if previous_active is not None and previous_active.name in bpy.data.objects
        else armature_obj
    )
    if game_skeleton is None:
        return armature_obj, f"bones have no rest pose: {reason}" if report.placeholders else ""
    return armature_obj, report.summary()


def _simple_import_remove_objects(objects: list) -> None:
    """Remove only objects created by a failed simple import."""
    for obj in reversed(objects):
        if obj.name not in bpy.data.objects:
            continue
        data = getattr(obj, "data", None)
        object_type = getattr(obj, "type", "")
        bpy.data.objects.remove(obj, do_unlink=True)
        data_collection = {"MESH": bpy.data.meshes, "ARMATURE": bpy.data.armatures}.get(object_type)
        if data is not None and data_collection is not None and data.users == 0 and data.name in data_collection:
            data_collection.remove(data)


def _simple_import_select_objects(objects: list) -> None:
    for obj in tuple(bpy.context.selected_objects):
        obj.select_set(False)
    valid_objects = [obj for obj in objects if obj.name in bpy.data.objects]
    for obj in valid_objects:
        obj.select_set(True)
    if valid_objects:
        bpy.context.view_layer.objects.active = valid_objects[-1]


class XIVIE_OT_simple_export(Operator):
    bl_idname = "xiv_ie.simple_export"
    bl_label = "Export Model File"
    bl_description = "Export the meshes chosen by Export Parts to the export folder in the selected format"
    bl_options = {"REGISTER"}

    @classmethod
    def poll(cls, context: Context):
        if context.mode != "OBJECT":
            cls.poll_message_set("Switch to Object Mode to export")
            return False
        if not visible_meshobj():
            cls.poll_message_set("There are no visible meshes to export")
            return False
        return True

    def execute(self, context: Context):
        settings = get_settings()
        directory = Path(bpy.path.abspath(settings.export_directory)).resolve()
        name = (settings.export_name or "").strip()
        if not directory.is_dir():
            self.report({"ERROR"}, "Choose an existing export folder.")
            return {"CANCELLED"}
        if not name or name in {".", ".."} or Path(name).name != name:
            self.report({"ERROR"}, "Enter a valid file name without a path.")
            return {"CANCELLED"}

        suffix = {"MDL": ".mdl", "FBX": ".fbx", "GLTF": ".gltf"}[settings.model_format]
        if name.lower().endswith(suffix):
            name = name[:-len(suffix)]
        from .instant_edit.ops import export_destination_context, export_objects_for_scope

        scope = getattr(context.scene.xiv_ie_instant_edit_props, "export_scope", "VISIBLE")
        try:
            ref = export_destination_context(context) if scope == "CURRENT_COLLECTION" else None
            objects = export_objects_for_scope(ref, scope)
        except ContextValidationError as error:
            message = (
                "Select a Context before exporting the XIV Instant Edit Collection."
                if scope == "CURRENT_COLLECTION" else str(error)
            )
            self.report({"ERROR"}, message)
            return {"CANCELLED"}
        if not objects:
            self.report({"ERROR"}, "No visible mesh objects match Export Parts.")
            return {"CANCELLED"}
        # The game only takes weighted triangles, whichever format carries them there.
        not_triangulated = check_triangulation(objects)
        if not_triangulated:
            self.report({"ERROR"}, "Not Triangulated: " + ", ".join(not_triangulated))
            return {"CANCELLED"}
        unweighted = check_weights(objects)
        if unweighted:
            self.report({"ERROR"}, "No bone weights: " + ", ".join(unweighted))
            return {"CANCELLED"}

        try:
            export_result(directory / name, settings.model_format, export_objects=objects)
            get_export_stats(context)
        except Exception as error:
            self.report({"ERROR"}, f"Export failed: {error}")
            return {"CANCELLED"}

        from .instant_edit.ops import refresh_variant_targets_after_operation

        refresh_error = refresh_variant_targets_after_operation(context)
        message = f"Exported {name}{suffix}"
        if refresh_error is not None:
            message += f"; Penumbra targets could not refresh: {refresh_error}"
        self.report({"WARNING"} if refresh_error is not None else {"INFO"}, message)
        return {"FINISHED"}


_IMPORT_SUFFIXES = {"MDL": (".mdl",), "FBX": (".fbx",), "GLTF": (".gltf", ".glb")}


class XIVIE_OT_simple_import(Operator):
    bl_idname = "xiv_ie.simple_import"
    bl_label = "Import Model File"
    bl_description = "Import an MDL, FBX, or glTF file into the current scene"
    bl_options = {"REGISTER", "UNDO"}

    filepath: StringProperty(options={"HIDDEN"})  # type: ignore
    filter_glob: StringProperty(
        default="*.mdl;*.fbx;*.gltf;*.glb",
        subtype="FILE_PATH",
        options={"HIDDEN"},
    )  # type: ignore
    import_format: EnumProperty(
        items=[
            ("AUTO", "Automatic", "Choose the format from the file extension"),
            ("MDL", "MDL", "FFXIV model"),
            ("FBX", "FBX", "Autodesk FBX"),
            ("GLTF", "glTF", "glTF"),
        ],
        default="AUTO",
        options={"HIDDEN", "SKIP_SAVE"},
    )  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        if context.mode != "OBJECT":
            cls.poll_message_set("Switch to Object Mode to import")
            return False
        return True

    def invoke(self, context: Context, event):
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context: Context):
        file_path = Path(bpy.path.abspath(self.filepath)).resolve()
        suffix = file_path.suffix.casefold()
        import_format = self.import_format
        if import_format == "AUTO":
            import_format = next(
                (name for name, suffixes in _IMPORT_SUFFIXES.items() if suffix in suffixes), "")
        expected_suffixes = _IMPORT_SUFFIXES.get(import_format, ())
        settings = get_settings()
        skeleton = settings.simple_import_skeleton
        use_existing_skeleton = settings.simple_import_use_existing_skeleton

        if not file_path.is_file() or suffix not in expected_suffixes:
            valid = "/".join(
                item[1:].upper() for item in (expected_suffixes or (".mdl", ".fbx", ".gltf", ".glb")))
            self.report({"ERROR"}, f"Choose a valid {valid} file.")
            return {"CANCELLED"}
        if use_existing_skeleton and (skeleton is None or skeleton.type != "ARMATURE"):
            self.report({"ERROR"}, "Choose an existing Blender Armature for the imported meshes.")
            return {"CANCELLED"}

        imported_objects = []
        skeleton_note = ""
        try:
            if import_format == "MDL":
                from .io.model import ModelImport

                created_objects = []
                imported = ModelImport.from_file(
                    str(file_path),
                    file_path.stem,
                    select_objects=False,
                    created_objects=created_objects,
                )
                imported_objects = list(created_objects or imported)
                mesh_objects = [obj for obj in imported_objects if obj.type == "MESH"]
                if use_existing_skeleton:
                    imported_objects = _simple_import_bind_existing_skeleton(imported_objects, skeleton)
                elif mesh_objects:
                    _armature, skeleton_note = _simple_import_create_armature(
                        context,
                        file_path,
                        mesh_objects,
                        imported_objects,
                    )
            else:
                with _new_objects_since() as tracker:
                    if import_format == "FBX":
                        result = bpy.ops.import_scene.fbx(
                            filepath=str(file_path),
                            colors_type="LINEAR",
                        )
                    else:
                        result = bpy.ops.import_scene.gltf(filepath=str(file_path))
                    if "FINISHED" not in result:
                        return set(result)
                imported_objects = tracker.created
                if use_existing_skeleton:
                    imported_objects = _simple_import_bind_existing_skeleton(imported_objects, skeleton)

                import_instance_id = uuid.uuid4().hex
                for obj in imported_objects:
                    if obj.type == "MESH":
                        obj["instant_edit_import_instance_id"] = import_instance_id

            _simple_import_select_objects(imported_objects)
            imported_count = sum(obj.type == "MESH" for obj in imported_objects)
            if settings.simple_import_set_export_directory:
                settings.export_directory = str(file_path.parent)
        except Exception as error:
            _simple_import_remove_objects(imported_objects)
            self.report({"ERROR"}, f"Import failed: {error}")
            return {"CANCELLED"}

        from .instant_edit.ops import refresh_variant_targets_after_operation

        refresh_error = refresh_variant_targets_after_operation(context)
        count_text = f" ({imported_count} mesh object{'s' if imported_count != 1 else ''})" if imported_count else ""
        message = f"Imported {file_path.name}{count_text}"
        if skeleton_note:
            message += f"; {skeleton_note}"
        if refresh_error is not None:
            message += f"; Penumbra targets could not refresh: {refresh_error}"
        self.report({"WARNING"} if refresh_error is not None or skeleton_note else {"INFO"}, message)
        return {"FINISHED"}


class XIVIE_OT_restore_backup(Operator):
    bl_idname = "xiv_ie.restore_backup"
    bl_label = "Restore Backup"
    bl_description = "Restore this model backup and preserve the current model first"
    bl_options = {"REGISTER", "UNDO"}

    backup_name: StringProperty(options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def invoke(self, context: Context, event):
        folder, _source = target_folder(get_settings(), context)
        entry = next((item for item in list_backups(folder) if item.path.name == self.backup_name), None)
        if entry is None:
            self.report({"ERROR"}, "That backup no longer exists.")
            return {"CANCELLED"}
        stamp = entry.created.astimezone().strftime("%Y-%m-%d %H:%M:%S")
        return context.window_manager.invoke_confirm(
            self,
            event,
            title="Restore Backup?",
            message=(
                f"Replace {entry.original_name} with the backup from {stamp}? "
                "The current file is backed up first."
            ),
            confirm_text="Restore",
            icon="WARNING",
        )

    def execute(self, context: Context):
        settings = get_settings()
        folder, source = target_folder(settings, context)
        if folder is None:
            self.report({"ERROR"}, "The current target export folder is unavailable.")
            return {"CANCELLED"}
        entry = next((item for item in list_backups(folder) if item.path.name == self.backup_name), None)
        if entry is None:
            self.report({"ERROR"}, "That backup no longer exists.")
            return {"CANCELLED"}
        try:
            quick = False
            try:
                from .instant_edit.context import ContextValidationError
                from .instant_edit.ops import (export_destination_context,
                                               plugin_warning_summary,
                                               restore_quick_backup)

                ref = export_destination_context(context)
                quick = source == "Quick Export target" and entry.original_name.lower().endswith(".mdl")
                if quick:
                    result = restore_quick_backup(context, entry.path.name)
            except (ContextValidationError, ImportError):
                pass
            if not quick:
                restore_local(folder, entry)
        except Exception as error:
            self.report({"ERROR"}, f"Restore failed: {error}")
            return {"CANCELLED"}
        warnings = result.get("warnings", []) if quick else []
        message = (
            f"Restored {entry.original_name} with warnings: {plugin_warning_summary(warnings)}"
            if warnings else f"Restored {entry.original_name}"
        )
        self.report({"WARNING"} if warnings else {"INFO"}, message)
        return {"FINISHED"}


class XIVIE_OT_import_backup(Operator):
    bl_idname = "xiv_ie.import_backup"
    bl_label = "Import Backup"
    bl_description = "Import this model backup into a new collection"
    bl_options = {"REGISTER", "UNDO"}

    backup_name: StringProperty(options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        folder, _source = target_folder(get_settings(), context)
        if folder is None:
            self.report({"ERROR"}, "The current target export folder is unavailable.")
            return {"CANCELLED"}
        entry = next((item for item in list_backups(folder) if item.path.name == self.backup_name), None)
        if entry is None:
            self.report({"ERROR"}, "That backup no longer exists.")
            return {"CANCELLED"}
        collection = bpy.data.collections.new(f"Backup - {Path(entry.original_name).stem}")
        context.scene.collection.children.link(collection)
        is_mdl = entry.original_name.lower().endswith(".mdl")
        tracker = _new_objects_since()
        try:
            with tracker:
                if is_mdl:
                    from .io.model import ModelImport

                    imported = ModelImport.from_file(
                        str(entry.path), Path(entry.original_name).stem,
                        collection=collection, require_collection=True,
                    )
                    count = len(imported)
                else:
                    result = bpy.ops.import_scene.fbx(filepath=str(entry.path), colors_type="LINEAR")
                    if "FINISHED" not in result:
                        raise RuntimeError("Blender FBX importer did not finish")
            if not is_mdl:
                new_objects = tracker.created
                for obj in new_objects:
                    if collection not in obj.users_collection:
                        collection.objects.link(obj)
                    for old_collection in tuple(obj.users_collection):
                        if old_collection != collection:
                            old_collection.objects.unlink(obj)
                count = len(new_objects)
        except Exception as error:
            for obj in tracker.created:
                bpy.data.objects.remove(obj, do_unlink=True)
            if collection.name in bpy.data.collections:
                bpy.data.collections.remove(collection)
            self.report({"ERROR"}, f"Import failed: {error}")
            return {"CANCELLED"}
        from .instant_edit.ops import refresh_variant_targets_after_operation

        refresh_error = refresh_variant_targets_after_operation(context)
        message = (
            f"Imported {entry.original_name} into {collection.name} "
            f"({count} object{'s' if count != 1 else ''})"
        )
        if refresh_error is not None:
            message += f"; Penumbra targets could not refresh: {refresh_error}"
        self.report({"WARNING"} if refresh_error is not None else {"INFO"}, message)
        return {"FINISHED"}


class XIVIE_OT_clear_backups(Operator):
    bl_idname = "xiv_ie.clear_backups"
    bl_label = "Clear All Backups"
    bl_description = "Delete all recognized model backups in the current target folder"
    # Not REGISTER: Repeat Last would delete another target's backups without the confirmation.

    folder_label: StringProperty(options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    backup_count: IntProperty(default=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    def invoke(self, context: Context, event):
        folder, _source = target_folder(get_settings(), context)
        self.folder_label = str(folder) if folder is not None else "Unavailable folder"
        self.backup_count = len(list_backups(folder))
        # invoke_confirm never calls draw(); the dialog must show what is deleted.
        return context.window_manager.invoke_props_dialog(
            self, width=460, title="Delete All Backups?", confirm_text="Delete")

    def draw(self, context: Context):
        layout = self.layout
        count = self.backup_count
        layout.label(text=f"Delete {count} backup{'s' if count != 1 else ''} from:", icon="ERROR")
        layout.label(text=self.folder_label, icon="BLANK1")
        layout.label(text="This cannot be undone.", icon="BLANK1")

    def execute(self, context: Context):
        folder, source = target_folder(get_settings(), context)
        if source == "Quick Export target":
            try:
                from .instant_edit.ops import clear_quick_backups
                clear_quick_backups(context)
                removed = self.backup_count
            except Exception as error:
                self.report({"ERROR"}, f"Clear failed: {error}")
                return {"CANCELLED"}
        else:
            removed = clear_backups(folder)
        self.report({"INFO"}, f"Cleared {removed} backup{'s' if removed != 1 else ''}.")
        return {"FINISHED"}


class XIVIE_OT_drag_mesh_order(Operator):
    bl_idname = "xiv_ie.drag_mesh_order"
    bl_label = "Drag to Reorder"
    bl_description = "Click, move the pointer to the new position, then click again to drop"
    bl_options = {"REGISTER", "UNDO", "BLOCKING"}

    scope: EnumProperty(
        items=(
            ("GROUP", "Mesh Group", "Reorder the complete mesh group"),
            ("PART", "Mesh Part", "Move this part within or between mesh groups"),
        ),
        default="GROUP",
        options={"HIDDEN", "SKIP_SAVE"},
    )  # type: ignore
    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    @classmethod
    def description(cls, context, properties):
        item = "mesh group" if properties.scope == "GROUP" else "mesh part"
        # Blender runs button operators on release, so the drag starts on a click.
        return (
            f"Click to pick up this {item}, move the pointer to where it should go, "
            "and click again to drop it there. Esc or right-click cancels"
        )

    def invoke(self, context: Context, event):
        global _ACTIVE_MESH_DRAG, _ACTIVE_MESH_DRAG_PLAN
        try:
            self._session = DragSession(
                scene_parts(),
                ListMetrics.from_preferences(context.preferences),
                self.scope,
                self.mesh_group,
                self.mesh_part,
                self.mesh_part_instance,
            )
        except LookupError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        # The layout is in unzoomed pixels; Ctrl+Middle-mouse can zoom the sidebar.
        self._zoom = _region_zoom(getattr(context, "region", None))
        self._start_y = event.mouse_y
        self._visible_objects = tuple(visible_meshobj())
        _ACTIVE_MESH_DRAG = self._session.drag_state()
        _ACTIVE_MESH_DRAG_PLAN = self._session.plan()

        context.window.cursor_modal_set("MOVE_Y")
        context.workspace.status_text_set(
            "Move the pointer to where it should go and click to drop; Esc or right-click to cancel"
        )
        context.window_manager.modal_handler_add(self)
        _redraw(context)
        return {"RUNNING_MODAL"}

    def modal(self, context: Context, event):
        global _ACTIVE_MESH_DRAG, _ACTIVE_MESH_DRAG_PLAN
        if event.type == "MOUSEMOVE":
            if self._session.update((self._start_y - event.mouse_y) / self._zoom):
                _ACTIVE_MESH_DRAG = self._session.drag_state()
                _ACTIVE_MESH_DRAG_PLAN = self._session.plan()
                _redraw(context)
            return {"RUNNING_MODAL"}

        if event.type in {"ESC", "RIGHTMOUSE", "WINDOW_DEACTIVATE"}:
            if event.type == "WINDOW_DEACTIVATE" or event.value == "PRESS":
                return self._finish(context, cancelled=True)

        if event.type in {"RET", "NUMPAD_ENTER", "SPACE"} and event.value == "PRESS":
            return self._finish(context, cancelled=False)

        if event.type == "LEFTMOUSE":
            if event.value == "RELEASE":
                return self._finish(context, cancelled=False)

        return {"RUNNING_MODAL"}

    def _finish(self, context: Context, cancelled: bool):
        global _ACTIVE_MESH_DRAG, _ACTIVE_MESH_DRAG_PLAN
        _ACTIVE_MESH_DRAG = None
        _ACTIVE_MESH_DRAG_PLAN = None
        context.window.cursor_modal_restore()
        context.workspace.status_text_set(None)
        plan = self._session.plan()
        if not cancelled and plan:
            try:
                commit_mesh_id_plan(plan, self._visible_objects)
            except ValueError as error:
                self.report({"ERROR"}, str(error))
                cancelled = True
        _redraw(context)
        return {"CANCELLED"} if cancelled or not plan else {"FINISHED"}


class XIVIE_OT_select_mesh_part(Operator):
    bl_idname = "xiv_ie.select_mesh_part"
    bl_label = "Select Mesh Part"
    bl_description = "Select this part's objects; Shift-click adds or removes them"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    extend: BoolProperty(name="Extend", default=False, options={"SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        if context.mode != "OBJECT":
            cls.poll_message_set("Switch to Object Mode to select mesh parts")
            return False
        return True

    @classmethod
    def description(cls, context, properties):
        lines = [cls.bl_description]
        group = find_material_group(context, properties.mesh_group)
        if group is not None:
            if properties.mesh_part in material_mismatch_parts(group.objects):
                lines.append("Its material differs from the rest of the mesh group.")
            instances = mesh_part_instances(group.objects, properties.mesh_group, properties.mesh_part)
            if len(instances) > 1:
                lines.append("Another visible part uses the same number; drag one of them to a free slot.")
        return "\n".join(lines)

    def invoke(self, context: Context, event):
        self.extend = event.shift
        return self.execute(context)

    def execute(self, context: Context):
        objects = mesh_part_instance_objects(
            visible_meshobj(),
            self.mesh_group,
            self.mesh_part,
            self.mesh_part_instance or None,
        )
        objects = [obj for obj in objects if obj.name in context.view_layer.objects]
        if not objects:
            self.report({"ERROR"}, f"Mesh part {self.mesh_group}.{self.mesh_part} is no longer visible.")
            return {"CANCELLED"}
        # Shift-click toggles like the Outliner: a fully selected part is removed.
        deselect = self.extend and all(obj.select_get() for obj in objects)
        if not self.extend:
            for obj in context.selected_objects:
                obj.select_set(False)
        for obj in objects:
            obj.select_set(not deselect)
        if not deselect:
            context.view_layer.objects.active = objects[0]
        _redraw(context)
        return {"FINISHED"}


def _share_local_views(context: Context, pairs) -> None:
    """Show each copy in every local view its original is shown in, as Duplicate does."""
    if context.screen is None:
        return
    for area in context.screen.areas:
        space = area.spaces.active if area.type == "VIEW_3D" else None
        if space is None or space.local_view is None:
            continue
        for original, copy in pairs:
            if original.local_view_get(space):
                copy.local_view_set(space, True)


def _flip_normals_in_edit_mode(context: Context, objects) -> None:
    """Run Edit Mode's Normals > Flip on every face of `objects`, leaving them selected.

    Only the Edit Mode operator also reverses custom normals, which imported
    meshes carry; Mesh.flip_normals() and bmesh's reverse_faces leave them.
    """
    for obj in context.selected_objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    context.view_layer.objects.active = objects[0]
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        if any(obj.mode != "EDIT" for obj in objects):
            raise RuntimeError("the duplicate could not enter Edit Mode")
        # Hidden faces are neither selected nor flipped, so reveal them on the copy first.
        bpy.ops.mesh.reveal()
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.flip_normals()
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")


def _remove_objects(objects) -> None:
    for obj in objects:
        mesh = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh is not None and mesh.users == 0:
            bpy.data.meshes.remove(mesh)


class XIVIE_OT_duplicate_backfaces(Operator):
    bl_idname = "xiv_ie.duplicate_backfaces"
    bl_label = "Generate Duplicate with Backfaces"
    bl_description = (
        "Duplicate this part as the next part number, moving parts in the way up by one, "
        "and flip the duplicate's normals"
    )
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        if context.mode != "OBJECT":
            cls.poll_message_set("Switch to Object Mode to duplicate mesh parts")
            return False
        return True

    def execute(self, context: Context):
        visible = tuple(visible_meshobj())
        originals = mesh_part_instance_objects(
            visible,
            self.mesh_group,
            self.mesh_part,
            self.mesh_part_instance or None,
        )
        if not originals:
            self.report({"ERROR"}, f"Mesh part {self.mesh_group}.{self.mesh_part} is no longer visible.")
            return {"CANCELLED"}
        copies = backface_copies(originals)
        _share_local_views(context, list(zip(originals, copies)))
        try:
            _flip_normals_in_edit_mode(context, copies)
            objects = visible + tuple(copies)
            parts = list_parts(objects)
            added = next(part for part in parts if copies[0] in part.objects)
            # The copy shares the original's number, so dropping it one higher
            # inserts it: parts in the way move up until a free number.
            placement = moved_part(parts, added.ident, self.mesh_group, self.mesh_part + 1)
            commit_mesh_id_plan(placement_plan(parts, placement), objects)
        except (RuntimeError, ValueError) as error:
            _remove_objects(copies)
            self.report({"ERROR"}, f"Could not add the backface part: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Added part {self.mesh_group}.{self.mesh_part + 1} with flipped normals")
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_copy_text(Operator):
    bl_idname = "xiv_ie.copy_text"
    bl_label = "Copy"
    bl_description = "Copy this text to the clipboard"
    bl_options = {"INTERNAL"}

    text: StringProperty(default="", maxlen=8192, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def description(cls, context, properties):
        return f"Copy to the clipboard:\n{properties.text}" if properties.text else cls.bl_description

    def execute(self, context: Context):
        context.window_manager.clipboard = self.text
        self.report({"INFO"}, "Copied to the clipboard")
        return {"FINISHED"}


class XIVIE_OT_combine_armatures(Operator):
    bl_idname = "xiv_ie.combine_armatures"
    bl_label = "Combine Armatures"
    bl_description = "Create a combined rest rig, hide the originals, and reassign their visible meshes"
    bl_options = {"REGISTER", "UNDO"}

    def _armature_search(self, context, edit_text):
        return [obj.name for obj in available_armatures(context)]

    base_armature: StringProperty(
        name="Base Armature",
        description="Keep this rig's rest bones wherever both rigs share a bone name",
        search=_armature_search,
    )  # type: ignore
    additional_armature: StringProperty(
        name="Additional Armature",
        description="Add this rig's missing rest bones to the new combined armature",
        search=_armature_search,
    )  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        if context.mode != "OBJECT":
            cls.poll_message_set("Switch to Object Mode to combine armatures")
            return False
        if len(available_armatures(context)) < 2:
            cls.poll_message_set("Two armatures are required in the active view layer")
            return False
        return True

    def invoke(self, context: Context, event):
        active = context.view_layer.objects.active
        self.base_armature = active.name if active and active.type == "ARMATURE" else ""
        self.additional_armature = next(
            (obj.name for obj in context.selected_objects
             if obj.type == "ARMATURE" and obj != active), "")
        # With exactly two rigs available there is only one sensible pairing.
        armatures = [obj.name for obj in available_armatures(context)]
        if len(armatures) == 2:
            if self.base_armature not in armatures:
                self.base_armature = armatures[0]
            if self.additional_armature not in armatures or self.additional_armature == self.base_armature:
                self.additional_armature = next(name for name in armatures if name != self.base_armature)
        return context.window_manager.invoke_props_dialog(
            self, width=480, title="Combine Armatures", confirm_text="Combine")

    def draw(self, context: Context):
        self.layout.prop(self, "base_armature")
        self.layout.prop(self, "additional_armature")
        self.layout.label(text="Shared bone names use the base rig.")
        self.layout.label(text="Original rigs are kept and hidden in this view layer.")
        self.layout.label(text="Only visible meshes are reassigned; weights stay unchanged.")
        self.layout.label(text="The new rig starts at rest without animation controls.")

    def execute(self, context: Context):
        try:
            result, count = combine_armatures(
                context, context.view_layer.objects.get(self.base_armature),
                context.view_layer.objects.get(self.additional_armature),
            )
        except Exception as error:
            self.report({"ERROR"}, f"Armatures were not combined: {error}")
            return {"CANCELLED"}
        _redraw(context)
        self.report({"INFO"}, f'{result.name}: {len(result.data.bones)} bones, {count} meshes reassigned.')
        return {"FINISHED"}


class XIVIE_OT_convert_mesh_names(Operator):
    bl_idname = "xiv_ie.convert_mesh_names"
    bl_label = "Move Mesh IDs to Front"
    bl_description = "Convert suffix-form mesh IDs in every scene mesh to the prefix naming convention"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def execute(self, context: Context):
        try:
            converted = convert_suffix_mesh_names(bpy.context.scene.objects)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        if converted:
            self.report({"INFO"}, f"Moved mesh IDs to the front on {converted} object{'s' if converted != 1 else ''}.")
        else:
            self.report({"INFO"}, "No suffix-form mesh IDs found.")
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_mesh_attribute(Operator):
    bl_idname = "xiv_ie.mesh_attribute"
    bl_label = "Mesh Part Attribute"
    bl_description = "Add or remove an XIV attribute on this mesh part"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    mesh_part_instance: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    attribute: StringProperty(default="NEW", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore
    custom: BoolProperty(name="Custom", default=False)  # type: ignore
    custom_attribute: StringProperty(
        name="",
        description="Attribute name, such as atrx_cape, or a heels offset such as heels_offset=0.15",
        default="",
        maxlen=128,
    )  # type: ignore
    selection: EnumProperty(
        name="",
        items=(
            ("atr_nek", "Neck", ""),
            ("atr_ude", "Elbow", ""),
            ("atr_hij", "Wrist", ""),
            ("atr_arm", "Glove", ""),
            ("atr_kod", "Waist", ""),
            ("atr_hiz", "Knee", ""),
            ("atr_sne", "Shin", ""),
            ("atr_leg", "Boot", ""),
            ("atr_lpd", "Knee Pad", ""),
        ) + _ATTRIBUTE_PRESET_ITEMS,
        default="atr_nek",
    )  # type: ignore

    @classmethod
    def description(cls, context, properties):
        if properties.attribute == "NEW":
            return "Add an XIV attribute to this mesh part"
        return f"Remove {properties.attribute} from this mesh part"

    def invoke(self, context: Context, event):
        if self.attribute != "NEW":
            return self.execute(context)
        return context.window_manager.invoke_props_dialog(
            self, width=340, title=f"Add Attribute to Part {self.mesh_group}.{self.mesh_part}",
            confirm_text="Add")

    def draw(self, context: Context):
        layout = self.layout
        row = layout.row(align=True)
        row.prop(self, "custom", text="Preset", toggle=True, invert_checkbox=True)
        row.prop(self, "custom", text="Custom", toggle=True)
        if self.custom:
            layout.prop(self, "custom_attribute", placeholder="atrx_name or heels_offset=0.15")
        else:
            layout.prop(self, "selection")

    def execute(self, context: Context):
        value = self.custom_attribute if self.custom else self.selection
        enabled = self.attribute == "NEW"
        if not enabled:
            value = self.attribute
        try:
            attribute = set_mesh_part_attribute(
                visible_meshobj(),
                self.mesh_group,
                self.mesh_part,
                value,
                enabled,
                self.mesh_part_instance or None,
            )
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _redraw(context)
        verb = "Added" if enabled else "Removed"
        self.report({"INFO"}, f"{verb} {attribute} on mesh part {self.mesh_group}.{self.mesh_part}")
        return {"FINISHED"}


_QUICK_MATERIAL_NONE = "NONE"
# The material dialog's quick selectors as (path, mesh groups) pairs, plus the
# enum items built from them. Blender requires dynamically generated enum
# strings to remain alive for as long as the enum is in use.
_QUICK_MATERIALS: tuple[tuple[str, tuple[int, ...]], ...] = ()
_QUICK_MATERIAL_ITEMS = [(_QUICK_MATERIAL_NONE, "None", "")]


def _mesh_groups_label(groups) -> str:
    return "Mesh " + ", ".join(f"#{group}" for group in groups)


def _model_mesh_objects(context: Context) -> list:
    """Return the meshes Quick Export writes as the selected Context's model."""
    from .instant_edit.ops import export_destination_context, export_objects_for_scope

    try:
        ref = export_destination_context(context, persist=False)
    except ContextValidationError:
        ref = None
    scope = getattr(context.scene.xiv_ie_instant_edit_props, "export_scope", "VISIBLE")
    try:
        return export_objects_for_scope(ref, scope)
    except ContextValidationError:
        return visible_meshobj()


def _prepare_quick_materials(context: Context, mesh_group: int) -> tuple:
    """Offer the materials of the model's other mesh groups in the material dialog."""
    global _QUICK_MATERIALS, _QUICK_MATERIAL_ITEMS
    _QUICK_MATERIALS = tuple(other_group_materials(_model_mesh_objects(context), mesh_group))
    _QUICK_MATERIAL_ITEMS = [(_QUICK_MATERIAL_NONE, "None", "")] + [
        (path, path, f"Use the material of {_mesh_groups_label(groups)}")
        for path, groups in _QUICK_MATERIALS
    ]
    return _QUICK_MATERIALS


class XIVIE_OT_mesh_material(Operator):
    bl_idname = "xiv_ie.mesh_material"
    bl_label = "Mesh Material"
    bl_description = "View or change the FFXIV material path for this mesh group"
    bl_options = {"REGISTER", "UNDO"}

    mesh_group: IntProperty(default=0, min=0, options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def description(cls, context, properties):
        group = find_material_group(context, properties.mesh_group)
        paths = material_paths(group.objects) if group is not None else []
        if len(paths) == 1:
            return f"{paths[0]}\nChange the FFXIV material of mesh group {properties.mesh_group}"
        if paths:
            return "The parts of this mesh group use different materials:\n" + "\n".join(paths)
        return f"Set the FFXIV material of mesh group {properties.mesh_group}"

    def _material_search(self, context, edit_text):
        group = find_material_group(context, self.mesh_group)
        suggestions = material_suggestions(group) if group is not None else []
        listed = [path for path, _usage in suggestions]
        suggestions.extend(
            (path, _mesh_groups_label(groups))
            for path, groups in _QUICK_MATERIALS
            if matching_material_path(path, listed) is None
        )
        return suggestions

    def _material_edited(self, _context):
        # Keep a quick selector pressed only while it matches the material path.
        choice = matching_material_path(
            self.material, [path for path, _groups in _QUICK_MATERIALS]
        ) or _QUICK_MATERIAL_NONE
        if self.quick_material != choice:
            self.quick_material = choice

    def _quick_material_items(self, _context):
        return _QUICK_MATERIAL_ITEMS

    def _quick_material_selected(self, _context):
        choice = self.quick_material
        if choice not in {"", _QUICK_MATERIAL_NONE} and matching_material_path(
            self.material, (choice,)
        ) is None:
            self.material = choice

    material: StringProperty(
        name="Material Path",
        description="FFXIV .mtrl path used when exporting this mesh group",
        search=_material_search,
        search_options={"SUGGESTION"},
        update=_material_edited,
    )  # type: ignore
    quick_material: EnumProperty(
        name="Other Mesh Group Materials",
        description="Use a material already assigned to another mesh group of this model",
        items=_quick_material_items,
        update=_quick_material_selected,
        options={"SKIP_SAVE"},
    )  # type: ignore

    @classmethod
    def poll(cls, context: Context):
        return context.mode == "OBJECT"

    def invoke(self, context: Context, event):
        group = find_material_group(context, self.mesh_group)
        if group is None:
            self.report({"ERROR"}, f"Mesh group {self.mesh_group} is no longer available.")
            return {"CANCELLED"}
        _prepare_quick_materials(context, self.mesh_group)
        paths = material_paths(group.objects)
        self.material = paths[0] if len(paths) == 1 else ""
        return context.window_manager.invoke_props_dialog(
            self,
            width=520,
            title=f"Material for Mesh Group {self.mesh_group}",
            confirm_text="Assign Material",
        )

    def draw(self, context: Context):
        self.layout.prop(self, "material", text="")
        if not _QUICK_MATERIALS:
            return
        self.layout.separator()
        self.layout.label(text="Materials on other mesh groups:")
        column = self.layout.column(align=True)
        for path, groups in _QUICK_MATERIALS:
            row = column.row(align=True).split(factor=0.75, align=True)
            row.prop_enum(self, "quick_material", path, text=path)
            row.label(text=_mesh_groups_label(groups))

    def execute(self, context: Context):
        group = find_material_group(context, self.mesh_group)
        if group is None:
            self.report({"ERROR"}, f"Mesh group {self.mesh_group} is no longer available.")
            return {"CANCELLED"}
        try:
            path = assign_material_path(group.objects, self.material)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}

        if context.screen:
            for area in context.screen.areas:
                area.tag_redraw()
        self.report({"INFO"}, f"Mesh group {self.mesh_group}: {path}")
        return {"FINISHED"}


def _selected_meshes(context: Context) -> list:
    return [obj for obj in context.selected_objects if obj.type == "MESH"]


class XIVIE_OT_vertex_data(Operator):
    bl_idname = "xiv_ie.vertex_data"
    bl_label = "Modify Vertex Data"
    bl_description = "Change a vertex data channel of the selected meshes"
    bl_options = {"REGISTER", "UNDO"}

    action: EnumProperty(
        name="Action",
        items=[(identifier, name, description) for identifier, name, description in VERTEX_DATA_ACTIONS],
        options={"SKIP_SAVE"},
    )  # type: ignore

    @classmethod
    def description(cls, context, properties):
        return next(
            (f"{description}.\nApplies to the selected meshes"
             for identifier, _name, description in VERTEX_DATA_ACTIONS if identifier == properties.action),
            cls.bl_description,
        )

    @classmethod
    def poll(cls, context: Context):
        if context.mode != "OBJECT":
            cls.poll_message_set("Switch to Object Mode to change vertex data")
            return False
        if not _selected_meshes(context):
            cls.poll_message_set("Select the meshes to change")
            return False
        return True

    def execute(self, context: Context):
        try:
            changed, untouched = apply_vertex_data(self.action, _selected_meshes(context))
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        name = vertex_data_action_name(self.action)
        if not changed:
            self.report({"WARNING"}, f"{name}: the selected meshes have {missing_data_label(self.action)}.")
            return {"CANCELLED"}
        message = f"{name}: {len(changed)} mesh{'es' if len(changed) != 1 else ''}"
        if untouched:
            names = ", ".join(obj.name for obj in untouched[:3]) + (", ..." if len(untouched) > 3 else "")
            message += f"; {missing_data_label(self.action)} on {names}"
        self.report({"INFO"}, message)
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_show_pose_action(Operator):
    bl_idname = "xiv_ie.show_pose_action"
    bl_label = "Show Action"
    bl_description = "Pose the armature with this action; the timeline frame picks the pose"
    bl_options = {"REGISTER", "UNDO"}

    action: StringProperty(default="", options={"HIDDEN", "SKIP_SAVE"})  # type: ignore

    @classmethod
    def description(cls, context, properties):
        if not properties.action:
            return "Take the action off the armature, leaving its current pose"
        return cls.bl_description

    @classmethod
    def poll(cls, context: Context):
        if pose_armature(context) is None:
            cls.poll_message_set("There is no armature to pose")
            return False
        return True

    def execute(self, context: Context):
        armature = pose_armature(context)
        action = bpy.data.actions.get(self.action) if self.action else None
        if self.action and action is None:
            self.report({"ERROR"}, f'The action "{self.action}" no longer exists.')
            return {"CANCELLED"}
        try:
            show_action(context, armature, action)
        except (AttributeError, RuntimeError, TypeError) as error:
            self.report({"ERROR"}, f"Could not pose {armature.name}: {error}")
            return {"CANCELLED"}
        _redraw(context)
        return {"FINISHED"}


class XIVIE_OT_toggle_rest_pose(Operator):
    bl_idname = "xiv_ie.toggle_rest_pose"
    bl_label = "Toggle Rest Pose"
    bl_description = "Switch the armature between its rest pose and its pose or action"
    # Not UNDO: like a view toggle, it should not add a step in any mode.
    bl_options = {"REGISTER"}

    @classmethod
    def poll(cls, context: Context):
        if pose_armature(context) is None:
            cls.poll_message_set("There is no armature to pose")
            return False
        return True

    def execute(self, context: Context):
        armature = pose_armature(context)
        try:
            position = toggle_rest_pose(armature)
        except (AttributeError, RuntimeError, TypeError) as error:
            self.report({"ERROR"}, f"Could not switch {armature.name}: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"{armature.name}: {'rest pose' if position == 'REST' else 'posed'}")
        _redraw(context)
        return {"FINISHED"}
