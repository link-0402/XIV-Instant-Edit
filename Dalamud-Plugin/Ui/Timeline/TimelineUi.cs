using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary> Small drawing helpers the Timeline window's parts share: fact rows, tooltips, field edits, icons and colours. </summary>
internal static class TimelineUi
{
    public static readonly FontAwesomeIcon[] LaneIcons =
    [
        FontAwesomeIcon.Running, FontAwesomeIcon.Smile, FontAwesomeIcon.CommentDots, FontAwesomeIcon.VolumeUp,
        FontAwesomeIcon.Magic, FontAwesomeIcon.Toolbox, FontAwesomeIcon.EllipsisH,
    ];
    // Offset, amplitude and pose layers.
    public static readonly FontAwesomeIcon[] LayerIcons = [FontAwesomeIcon.Bone, FontAwesomeIcon.WaveSquare, FontAwesomeIcon.StreetView];

    public static FontAwesomeIcon LaneIcon(TimelineLane lane) => LaneIcons[Math.Min((int)lane, LaneIcons.Length - 1)];

    public static FontAwesomeIcon LayerIcon(MotionLayerKind kind) => LayerIcons[Math.Min((int)kind, LayerIcons.Length - 1)];

    public static uint U32(Vector4 colour) => ImGui.GetColorU32(colour);

    /// <summary> A pixel-snapped x for crisp one-pixel lines. </summary>
    public static float Crisp(float x) => MathF.Floor(x) + .5f;

    /// <summary> Starts a fact row: its muted label, then the value's cell. </summary>
    public static void Fact(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, label);
        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
    }

    public static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    /// <summary> An icon button that stays filled while its setting is on. </summary>
    public static bool Toggle(string id, FontAwesomeIcon icon, bool on, string tooltip)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, on)
            .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), on);
        return Widgets.IconButton(id, icon, tooltip);
    }

    /// <summary> Opens <paramref name="window"/> in front, or closes it. </summary>
    public static void Toggle(Window window)
    {
        window.IsOpen = !window.IsOpen;
        if (window.IsOpen)
            window.BringToFront();
    }

    /// <summary> Makes the last control's changes one undo step: begun when it activates, ended when it is left. </summary>
    public static void TrackField(TimelineEditor current, bool changed, TimelineEvent? next)
    {
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed && next != null)
            current.Replace(next);
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
    }

    public static TimelineEvent With(TimelineEvent ev, TimelineField field, double value) => With(ev, field.Name, value);

    public static TimelineEvent With(TimelineEvent ev, string field, double value) =>
        ev with { Values = (ev.Values ?? ImmutableDictionary<string, double>.Empty).SetItem(field, value) };
}
