using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary> How the properties panel shows one of a prop event's raw field rows. </summary>
internal enum PropRawRow { Editable, ReadOnly, Hidden }

/// <summary>
/// The Place section of a grouped prop: the attach state, bone, move, turn and size of its summon, kept as a
/// <see cref="TimelinePropPlace"/> on the summon (<see cref="TimelineEvent.Place"/>) and written on save. A grouped
/// prop control (C014, C015, C174, C175) shows the state rows of the summon on its slot.
/// </summary>
internal sealed class PropPlaceEditor(TimelineSession session)
{
    public const string RaceHint = "Changes where this prop attaches for your race. Other races get the same change when the file is rebuilt or borrowed for them.";
    public const string SharedHint = "Other emotes with the same kind of prop move too";
    public const string OwnHint = "Uses a spare attach point no other animation shows a prop on";
    public const string UnlearnedHint = "Preview once to place it";
    private const float Degrees = 180 / MathF.PI;
    private static readonly string[] States = ["Drawn", "Stowed", "In use", "Switch hand", "Throw", "Switch reverse"];

    /// <summary> An attach state's name: 0 Drawn, 1 Stowed, 2 In use, 3 Switch hand, 4 Throw, 5 Switch reverse. </summary>
    public static string StateName(int state) => state >= 0 && state < States.Length ? States[state] : state.ToString(CultureInfo.InvariantCulture);

    /// <summary> The vanilla bone of attach point <paramref name="code"/> in <paramref name="state"/>, with its size when not 1; empty when the table lacks it. </summary>
    public static string BoneLine(AtchTable table, string code, int state) => table.Entry(code, state) is not { } entry ? ""
        : entry.Scale == 1 ? entry.Bone : $"{entry.Bone}, size {entry.Scale.ToString("0.##", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// How a raw field row of <paramref name="ev"/> shows. On a grouped prop whose Place section is drawn
    /// (<paramref name="placed"/>), its Attach combo owns the state, so a C198's AtchState and a control's state row are
    /// hidden; without the section a C198's AtchState only reads. The rows that would split the group (a C198's Slot,
    /// a C031's Plays on) only read.
    /// </summary>
    public static PropRawRow RawRow(TimelineEvent ev, TimelineField field, bool placed) => ev.Group == null ? PropRawRow.Editable : (ev.Magic, field.Name) switch
    {
        ("C198", "AtchState") => placed ? PropRawRow.Hidden : PropRawRow.ReadOnly,
        ("C014" or "C174", "ObjectPosition") or ("C015" or "C175", "ObjectScale") when placed => PropRawRow.Hidden,
        ("C198", "SummonId") or ("C031", "TargetType") => PropRawRow.ReadOnly,
        _ => PropRawRow.Editable,
    };

    /// <summary> Whether the Place section is drawn for <paramref name="ev"/>: a grouped prop summon or control with a summon, and a race known. </summary>
    public static bool Shows(TimelineDocument doc, TimelineEvent ev, ushort? race) =>
        ev.Lane == TimelineLane.Prop && race != null && SummonOf(doc, ev) != null;

    /// <summary> <see cref="Shows"/> for the open clip. </summary>
    public bool Shows(TimelineDocument doc, TimelineEvent ev) => Shows(doc, ev, Race());

    /// <summary>
    /// The summon <paramref name="ev"/> places: itself for a grouped summon, the summon of its group on its slot for a
    /// grouped prop control; null otherwise.
    /// </summary>
    public static TimelineEvent? SummonOf(TimelineDocument doc, TimelineEvent ev)
    {
        if (ev.Group is not { } group)
            return null;
        if (IsSummon(ev))
            return ev;
        if (!TmbEntrySchema.PropControls.Contains(ev.Magic))
            return null;
        var slot = (int)Value(ev, "ObjectControl") - 2;
        return doc.Events.FirstOrDefault(e => e.Group == group && IsSummon(e) && Slot(e) == slot);
    }

    /// <summary> <paramref name="place"/>, or null when it changes nothing: no own state, bone, move or turn, a size of 1 and the state the TMB shows (<paramref name="current"/>). </summary>
    public static TimelinePropPlace? Settled(TimelinePropPlace place, int current) =>
        place is { OwnState: false, Bone: null, Size: 1 } && place.Move == Vector3.Zero && place.Turn == Vector3.Zero && place.State == current ? null : place;

    // The textures a prop from a mod lacks (or why its copy can't be built), read on a task when the prop is selected.
    private (string Key, ushort Set, ushort Body, TimelinePropSource Source, Task<(ImmutableArray<string> Missing, string? Error)> Result)? missing;

    public void Draw(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        DrawMissing(current.Document, ev);
        var race = Race();
        if (!Shows(current.Document, ev, race) || SummonOf(current.Document, ev) is not { } summon)
            return;
        var stateOnly = summon.Key != ev.Key;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Place##prop-place", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        Widgets.HintWrapped(RaceHint);
        var code = session.Animations?.PropTypes.CodeOf((ushort)Value(summon, "Model"));
        if (code == null)
            Widgets.HintWrapped(UnlearnedHint);
        var table = session.AttachTable(race!.Value);
        var shownState = TimelinePropPlaces.CurrentState(current.Document, summon);
        var place = summon.Place ?? new TimelinePropPlace(shownState, false, null, Vector3.Zero, Vector3.Zero, 1);
        using (ImRaii.Disabled(!editable || code == null))
        {
            using (var rows = ImRaii.Table("##prop-place", 2, ImGuiTableFlags.SizingFixedFit))
            {
                if (rows.Success)
                {
                    ImGui.TableSetupColumn("##place-name", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("##place-value", ImGuiTableColumnFlags.WidthStretch);
                    DrawState(current, summon, place, table, code);
                    if (!stateOnly)
                        DrawPlaceRows(current, summon, place);
                }
            }
            if (place is { State: 0 or 1, OwnState: false })
                Widgets.HintWrapped(SharedHint);
            var own = place.OwnState;
            var changed = ImGui.Checkbox("Only this animation", ref own);
            TimelineUi.TrackField(current, changed, changed ? Placed(current, summon, place with { OwnState = own }) : null);
            Widgets.HintWrapped(OwnHint);
            if (!stateOnly)
                DrawReset(current, summon);
        }
    }

    /// <summary>
    /// For a prop from a mod, "Missing: path" for each texture of its copy that neither the mod nor the game has, or why
    /// the copy can't be built. Built once each time the prop is selected, for its private body.
    /// </summary>
    private void DrawMissing(TimelineDocument doc, TimelineEvent ev)
    {
        if (SummonOf(doc, ev)?.Source is not { } source)
            return;
        if (missing is not { } cached || cached.Key != ev.Key || cached.Set != source.Set || cached.Body != source.Body || !ReferenceEquals(cached.Source, source))
        {
            Func<string, byte[]?> game = path => session.Data.GetFile(path)?.Data;
            cached = (ev.Key, source.Set, source.Body, source, Task.Run(() =>
            {
                try { return (PropCopies.Build(source, game).Missing, (string?)null); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return (ImmutableArray<string>.Empty, e.Message);
                }
            }));
            missing = cached;
        }
        if (!cached.Result.IsCompletedSuccessfully)
            return;
        var (paths, error) = cached.Result.Result;
        using var wrap = ImRaii.TextWrapPos(0);
        if (error != null)
            ImGui.TextColored(Theme.Warning, error);
        foreach (var path in paths)
            ImGui.TextColored(Theme.Warning, $"Missing: {path}");
    }

    /// <summary> Attach: the state the place starts from, each with its vanilla bone. </summary>
    private static void DrawState(TimelineEditor current, TimelineEvent summon, TimelinePropPlace place, AtchTable? table, string? code)
    {
        TimelineUi.Fact("Attach");
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##prop-state", StateName(place.State));
        if (!combo.Success)
            return;
        for (var state = 0; state < States.Length; state++)
        {
            if (ImGui.Selectable($"{StateName(state)}##state{state}", state == place.State) && state != place.State)
                current.Replace(Placed(current, summon, place with { State = state }));
            if (table != null && code != null && BoneLine(table, code, state) is { Length: > 0 } line)
            {
                ImGui.SameLine(Theme.Scaled(120));
                ImGui.TextColored(Theme.Muted, line);
            }
        }
    }

    /// <summary> Bone, Move (metres), Turn (degrees, kept in radians) and Size (a factor); each drag is one undo step. </summary>
    private void DrawPlaceRows(TimelineEditor current, TimelineEvent summon, TimelinePropPlace place)
    {
        TimelineUi.Fact("Bone");
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##prop-bone", place.Bone ?? "As vanilla"))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("As vanilla", place.Bone == null) && place.Bone != null)
                    current.Replace(Placed(current, summon, place with { Bone = null }));
                foreach (var bone in Bones())
                    if (ImGui.Selectable(bone, bone == place.Bone) && bone != place.Bone)
                        current.Replace(Placed(current, summon, place with { Bone = bone }));
            }
        }

        TimelineUi.Fact("Move");
        var move = place.Move;
        ImGui.SetNextItemWidth(-1);
        var changed = ImGui.DragFloat3("##prop-move", ref move, .01f, -TimelineEffectEdit.MaxMove, TimelineEffectEdit.MaxMove, "%.2f m", ImGuiSliderFlags.AlwaysClamp);
        TimelineUi.TrackField(current, changed, changed ? Placed(current, summon, place with { Move = move }) : null);
        TimelineUi.Tip("Moves the prop, in metres.");

        TimelineUi.Fact("Turn");
        const float most = TimelineEffectEdit.MaxTurn * Degrees;
        var shown = place.Turn * Degrees;
        var dragged = shown;
        ImGui.SetNextItemWidth(-1);
        changed = ImGui.DragFloat3("##prop-turn", ref dragged, .5f, -most, most, "%.0f°", ImGuiSliderFlags.AlwaysClamp);
        var turned = Vector3.Clamp(EffectPlaceEditor.Turned(place.Turn, shown, dragged), new Vector3(-TimelineEffectEdit.MaxTurn), new Vector3(TimelineEffectEdit.MaxTurn));
        TimelineUi.TrackField(current, changed, changed ? Placed(current, summon, place with { Turn = turned }) : null);
        TimelineUi.Tip("Turns the prop, in degrees.");

        TimelineUi.Fact("Size");
        var size = place.Size;
        ImGui.SetNextItemWidth(-1);
        changed = ImGui.DragFloat("##prop-size", ref size, .01f, TimelineEffectEdit.MinSize, TimelineEffectEdit.MaxSize, "%.2f", ImGuiSliderFlags.AlwaysClamp);
        TimelineUi.TrackField(current, changed, changed ? Placed(current, summon, place with { Size = size }) : null);
        TimelineUi.Tip("Scales the prop.");
    }

    private static void DrawReset(TimelineEditor current, TimelineEvent summon)
    {
        using (ImRaii.Disabled(summon.Place == null))
        {
            if (ImGui.SmallButton("Reset place"))
            {
                current.BeginFieldEdit();
                current.Replace(summon with { Place = null });
                current.EndFieldEdit();
            }
        }
        TimelineUi.Tip("Put the prop back where the game has it.");
    }

    /// <summary> The summon with <paramref name="place"/>, dropped when it changes nothing. </summary>
    private static TimelineEvent Placed(TimelineEditor current, TimelineEvent summon, TimelinePropPlace place) =>
        summon with { Place = Settled(place, TimelinePropPlaces.CurrentState(current.Document, summon)) };

    /// <summary> The bone names of the skeleton the clip is edited on, in name order. </summary>
    private IEnumerable<string> Bones() =>
        session.EditingSkeleton() is { } skeleton ? skeleton.Bones.Select(b => b.Name).Where(n => n.Length > 0).Distinct().Order(StringComparer.Ordinal) : [];

    /// <summary> The character's race code (101 for c0101), else the clip's; null when neither is known. </summary>
    private ushort? Race() =>
        session.Source?.Clip.Resolution?.ActorModel is ['c', .. var digits] && ushort.TryParse(digits, out var race) ? race : session.CatalogKey?.Race;

    private static bool IsSummon(TimelineEvent e) => e.Magic is "C198" or "C043" && Value(e, "Model") > 0;

    /// <summary> A summon's slot: a C198's SummonId; a C043 is always slot 0. </summary>
    private static int Slot(TimelineEvent e) => e.Magic == "C198" ? (int)Value(e, "SummonId") : 0;

    private static double Value(TimelineEvent ev, string name) => ev.Values?.GetValueOrDefault(name) ?? 0;
}
