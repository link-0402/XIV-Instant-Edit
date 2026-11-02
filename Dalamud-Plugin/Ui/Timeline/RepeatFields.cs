using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's rows for a repeat layer, drawn in its facts table: the source's first frame and the frame it wraps at,
/// each with a button that sets it to the playhead, the cross-fade into each next repeat, and how many times it plays. A field's
/// changes until it is left are one undo step; a button click is one.
/// </summary>
internal static class RepeatFields
{
    public static void Draw(TimelineEditor current, MotionLayer layer)
    {
        if (layer.Repeat is not { } repeat)
            return;
        // MotionEdits.Replace keeps the source two frames long or more and the blend within half of it.
        Frame(current, layer, "From", "##repeat-from", "The first frame it plays.", repeat.From, from => repeat with { From = from });
        Frame(current, layer, "To", "##repeat-to", "Where it starts over. This frame isn't played.", repeat.To, to => repeat with { To = to });

        TimelineUi.Fact("Blend");
        var blend = repeat.Blend;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var most = MotionRepeats.Length(repeat) / 2;
        var changed = ImGui.SliderInt("##repeat-blend", ref blend, 0, most, "%d frames");
        LayerPanel.TrackLayer(current, changed, changed ? layer with { Repeat = repeat with { Blend = Math.Clamp(blend, 0, most) } } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many frames each repeat fades into the next. Ctrl+click to type.");

        TimelineUi.Fact("Plays");
        var count = MotionRepeats.Count(layer);
        var whole = MathF.Abs(count - MathF.Round(count)) < 1e-3f;
        // A wrapping layer that doesn't hold a whole number of repeats meets its own start partway through one at the loop's seam.
        var wraps = current.Loops && MotionEvaluator.Wraps(layer, current.Document.Length);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(wraps && !whole ? Theme.Warning : Theme.Text, Repeats(count));
    }

    /// <summary> "1 repeat", "3.4 repeats". </summary>
    public static string Repeats(float count) =>
        MathF.Abs(count - 1) < 1e-3f ? "1 repeat" : count.ToString("0.#", CultureInfo.InvariantCulture) + " repeats";

    /// <summary> A frame field with the frame's time beside it and a button that sets it to the playhead. </summary>
    private static void Frame(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, int value, Func<int, MotionRepeat> with)
    {
        TimelineUi.Fact(label);
        var number = value;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        var changed = ImGui.InputInt(id, ref number, 0, 0);
        LayerPanel.TrackLayer(current, changed, changed ? layer with { Repeat = with(Math.Clamp(number, 0, TimelineDocument.MaxFrames)) } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.GhostIconButton(id + "-playhead", FontAwesomeIcon.MapMarkerAlt, "Set to the playhead.") && current.Playhead != value)
            current.Commit(MotionEdits.Replace(current.Document, layer with { Repeat = with(current.Playhead) }));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, TimelineText.Seconds(value));
    }
}
