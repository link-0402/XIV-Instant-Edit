using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Place section above an effect's look: move, turn and size. A VFX event (C012) is placed by its
/// own Position, Rotation and Scale fields; an Async VFX event (C173) by its effect file, through the
/// look (<see cref="TimelineEffectEdit.Move"/>, <see cref="TimelineEffectEdit.Turn"/>, <see cref="TimelineEffectEdit.Size"/>).
/// </summary>
internal sealed class EffectPlaceEditor(TimelineSession session, EffectLookEditor looks)
{
    private const string EventMagic = "C012", FileMagic = "C173";
    private const float Degrees = 180 / MathF.PI;
    private static readonly string[] Axes = ["X", "Y", "Z"];
    private static readonly TimelineEffectEdit NoLook = new();
    private bool sizePerAxis;

    /// <summary> The VFX event fields Place edits, which the properties' field rows leave out. </summary>
    public static bool Placed(TimelineEvent ev, TimelineField field) =>
        ev.Magic == EventMagic && (field.Name.StartsWith("Position.", StringComparison.Ordinal) ||
                                   field.Name.StartsWith("Rotation.", StringComparison.Ordinal) || field.Name.StartsWith("Scale.", StringComparison.Ordinal));

    /// <summary> What a change of place moves, shown under the header. </summary>
    public static string RouteHint(TimelineEvent ev, TimelineEffectLook? look) =>
        ev.Magic == EventMagic ? "Moves this event only. Scales with the character's race."
        : look is { Bound: true } ? "Attached to a bone; moved on its attach point"
        : "Moves the effect file; other events playing it move too";

    /// <summary>
    /// The turn in radians after a drag of its degrees from <paramref name="shown"/> to <paramref name="dragged"/>:
    /// an axis the drag didn't change keeps its stored <paramref name="radians"/>, so it never drifts.
    /// </summary>
    public static Vector3 Turned(Vector3 radians, Vector3 shown, Vector3 dragged) =>
        new(dragged.X == shown.X ? radians.X : dragged.X / Degrees, dragged.Y == shown.Y ? radians.Y : dragged.Y / Degrees,
            dragged.Z == shown.Z ? radians.Z : dragged.Z / Degrees);

    public void Draw(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        var onEvent = ev.Magic == EventMagic;
        // A VFX event's place is its own fields, so it needs no clip's file read.
        if (ev.Lane != TimelineLane.Effect || !onEvent && (session.Source == null || ev.Magic != FileMagic || !looks.ReadsFile(ev)))
            return;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Place##effect-place", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        var look = onEvent ? null : looks.LookOrStatus(ev);
        if (!onEvent && look == null)
            return;
        Widgets.HintWrapped(RouteHint(ev, look));
        using (ImRaii.Disabled(!editable))
        {
            using (var table = ImRaii.Table("##effect-place", 2, ImGuiTableFlags.SizingFixedFit))
            {
                if (table.Success)
                {
                    ImGui.TableSetupColumn("##place-name", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("##place-value", ImGuiTableColumnFlags.WidthStretch);
                    if (onEvent)
                        DrawEventRows(current, ev);
                    else
                        DrawFileRows(current, ev, look!);
                }
            }
            if (onEvent)
                DrawEventReset(current, ev);
            else
                DrawFileReset(current, ev);
        }
    }

    /// <summary> A VFX event's Position (metres), Rotation (radians, shown in degrees) and Scale; each drag is one undo step. </summary>
    private void DrawEventRows(TimelineEditor current, TimelineEvent ev)
    {
        TimelineUi.Fact("Move");
        var move = Shown(ev, "Position", 1);
        var dragged = move;
        ImGui.SetNextItemWidth(-1);
        var changed = ImGui.DragFloat3("##move", ref dragged, .01f, (float)Field("Position.X").Min, (float)Field("Position.X").Max, "%.2f m",
            ImGuiSliderFlags.AlwaysClamp);
        TimelineUi.TrackField(current, changed, changed ? Written(ev, "Position", move, dragged, 1) : null);
        TimelineUi.Tip("Moves the effect, in metres.");

        TimelineUi.Fact("Turn");
        var turn = Shown(ev, "Rotation", Degrees);
        dragged = turn;
        var most = (float)(Field("Rotation.X").Max * Degrees);
        ImGui.SetNextItemWidth(-1);
        changed = ImGui.DragFloat3("##turn", ref dragged, .5f, -most, most, "%.0f°", ImGuiSliderFlags.AlwaysClamp);
        TimelineUi.TrackField(current, changed, changed ? Written(ev, "Rotation", turn, dragged, Degrees) : null);
        TimelineUi.Tip("Turns the effect, in degrees.");

        TimelineUi.Fact("Size");
        var size = Shown(ev, "Scale", 1);
        dragged = size;
        changed = SizeDrag(ref dragged, (float)Field("Scale.X").Min, (float)Field("Scale.X").Max);
        TimelineUi.TrackField(current, changed, changed ? Written(ev, "Scale", size, dragged, 1) : null);
        TimelineUi.Tip("Scales the effect.");
        SizeAxesButton(size);
    }

    /// <summary> An Async VFX event's move, turn and size, written into a copy of its effect; an attached one can only be moved. </summary>
    private void DrawFileRows(TimelineEditor current, TimelineEvent ev, TimelineEffectLook look)
    {
        var edit = ev.Look ?? NoLook;
        if (look.Offset != null || look.Bound)
        {
            TimelineUi.Fact("Move");
            var refusal = TimelineEffectLooks.MoveRefusal(look, new TimelineEffectEdit { Move = Vector3.UnitY });
            var move = edit.Move ?? Vector3.Zero;
            using (ImRaii.Disabled(refusal != null))
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.DragFloat3("##move", ref move, .01f, -TimelineEffectEdit.MaxMove, TimelineEffectEdit.MaxMove, "%.2f m", ImGuiSliderFlags.AlwaysClamp))
                    looks.SetLook(current, ev, edit with { Move = EffectLookEditor.Changed(move, Vector3.Zero) });
            }
            TimelineUi.Tip(refusal ?? "Moves the whole effect, in metres.");
        }
        if (look.Turn != null)
        {
            TimelineUi.Fact("Turn");
            var refusal = TimelineEffectLooks.MoveRefusal(look, new TimelineEffectEdit { Turn = Vector3.UnitX });
            const float most = TimelineEffectEdit.MaxTurn * Degrees;
            var radians = edit.Turn ?? Vector3.Zero;
            var shown = radians * Degrees;
            var dragged = shown;
            using (ImRaii.Disabled(refusal != null))
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.DragFloat3("##turn", ref dragged, .5f, -most, most, "%.0f°", ImGuiSliderFlags.AlwaysClamp))
                {
                    var turned = Vector3.Clamp(Turned(radians, shown, dragged), new Vector3(-TimelineEffectEdit.MaxTurn), new Vector3(TimelineEffectEdit.MaxTurn));
                    looks.SetLook(current, ev, edit with { Turn = EffectLookEditor.Changed(turned, Vector3.Zero) });
                }
            }
            TimelineUi.Tip(refusal ?? "Turns the whole effect, in degrees.");
        }
        if (look.Size != null)
        {
            TimelineUi.Fact("Size");
            var refusal = TimelineEffectLooks.MoveRefusal(look, new TimelineEffectEdit { Size = new Vector3(2) });
            var size = edit.Size ?? Vector3.One;
            using (ImRaii.Disabled(refusal != null))
            {
                var dragged = size;
                if (SizeDrag(ref dragged, TimelineEffectEdit.MinSize, TimelineEffectEdit.MaxSize))
                    looks.SetLook(current, ev, edit with { Size = EffectLookEditor.Changed(dragged, Vector3.One) });
                TimelineUi.Tip(refusal ?? "Scales the whole effect.");
                SizeAxesButton(size);
            }
        }
    }

    /// <summary> The size as one value, or per axis once the XYZ button is on or the axes differ; true when dragged. </summary>
    private bool SizeDrag(ref Vector3 size, float min, float max)
    {
        var button = ImGui.CalcTextSize("XYZ").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(60), ImGui.GetContentRegionAvail().X - button - Theme.Gap));
        if (PerAxis(size))
            return ImGui.DragFloat3("##size", ref size, .01f, min, max, "%.2f", ImGuiSliderFlags.AlwaysClamp);
        var all = size.X;
        var changed = ImGui.DragFloat("##size", ref all, .01f, min, max, "%.2f", ImGuiSliderFlags.AlwaysClamp);
        size = new Vector3(all);
        return changed;
    }

    private bool PerAxis(Vector3 size) => sizePerAxis || size.X != size.Y || size.Y != size.Z;

    /// <summary> The XYZ toggle beside the size. </summary>
    private void SizeAxesButton(Vector3 size)
    {
        var perAxis = PerAxis(size);
        ImGui.SameLine(0, Theme.Gap);
        // Back to one value only when the axes agree; otherwise it would flatten them.
        var differ = perAxis && !sizePerAxis;
        using (ImRaii.Disabled(differ))
        {
            if (ImGui.SmallButton(perAxis ? "All##size-axes" : "XYZ##size-axes"))
                sizePerAxis = !perAxis;
        }
        TimelineUi.Tip(differ ? "The axes differ, so each is set on its own." : perAxis ? "Set all axes together." : "Set each axis on its own.");
    }

    private static void DrawEventReset(TimelineEditor current, TimelineEvent ev)
    {
        var reset = ev;
        foreach (var axis in Axes)
            reset = TimelineUi.With(TimelineUi.With(TimelineUi.With(reset, "Position." + axis, 0), "Rotation." + axis, 0), "Scale." + axis, 1);
        var placed = Axes.Any(axis => Value(ev, "Position." + axis) != 0 || Value(ev, "Rotation." + axis) != 0 || Value(ev, "Scale." + axis) != 1);
        using (ImRaii.Disabled(!placed))
        {
            if (ImGui.SmallButton("Reset place"))
            {
                current.BeginFieldEdit();
                current.Replace(reset);
                current.EndFieldEdit();
            }
        }
        TimelineUi.Tip("No move, no turn and a size of 1.");
    }

    private void DrawFileReset(TimelineEditor current, TimelineEvent ev)
    {
        var edit = ev.Look ?? NoLook;
        using (ImRaii.Disabled(edit is { Moves: null, Turns: null, Sizes: null }))
        {
            if (ImGui.SmallButton("Reset place"))
                looks.SetLook(current, ev, edit with { Move = null, Turn = null, Size = null });
        }
        TimelineUi.Tip("Put the effect back where the file has it.");
    }

    private static TimelineField Field(string name) => TmbEntrySchema.Find(EventMagic)!.Fields.First(f => f.Name == name);

    private static double Value(TimelineEvent ev, string name) => ev.Values?.GetValueOrDefault(name) ?? 0;

    /// <summary> The event's <paramref name="vector"/> fields, times <paramref name="scale"/>, as a drag shows them. </summary>
    private static Vector3 Shown(TimelineEvent ev, string vector, double scale) =>
        new((float)(Value(ev, vector + ".X") * scale), (float)(Value(ev, vector + ".Y") * scale), (float)(Value(ev, vector + ".Z") * scale));

    /// <summary>
    /// The event with each axis of <paramref name="vector"/> the drag changed set to its dragged value over
    /// <paramref name="scale"/>, within the field's range; an axis it didn't change keeps its stored value.
    /// </summary>
    private static TimelineEvent Written(TimelineEvent ev, string vector, Vector3 shown, Vector3 dragged, double scale)
    {
        for (var i = 0; i < Axes.Length; i++)
            if (dragged[i] != shown[i])
            {
                var field = Field(vector + "." + Axes[i]);
                ev = TimelineUi.With(ev, field, Math.Clamp(dragged[i] / scale, field.Min, field.Max));
            }
        return ev;
    }
}
