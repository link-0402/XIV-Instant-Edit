using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The length popup the header's frame and time open: the clip's length in seconds, which stretches the motion, with Scale
/// events with length; a face session's Moves and the face's seconds. An expression's own timeline keeps its length.
/// </summary>
internal sealed class LengthPopup(TimelineSession session, BlenderMotionPanel blenderMotion)
{
    /// <summary>How long a still face becomes when it starts moving, in frames: 3 seconds.</summary>
    private const int DefaultMovingFrames = 3 * TimelineDocument.Fps;

    private bool lengthActive;
    private float lengthEdit;

    /// <summary>The popup's content for <paramref name="current"/>.</summary>
    public void Draw(TimelineEditor current)
    {
        if (current.Document.Face is { } face)
            DrawFaceLength(current, face, session.ReadOnly);
        else
            DrawLength(current, session.ReadOnly);
    }

    /// <summary>The clip's length in seconds and Scale events with length; an expression's timeline keeps its own.</summary>
    private void DrawLength(TimelineEditor current, bool readOnly)
    {
        if (current.FixedLength)
        {
            ImGui.TextColored(Theme.Muted, "An expression's timeline keeps its length.");
            return;
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Length");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(72));
        // While typing, the field shows what was typed; Enter or leaving it applies the length once.
        var seconds = lengthActive ? lengthEdit : current.Document.Length / (float)TimelineDocument.Fps;
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            if (ImGui.InputFloat("##timeline-length", ref seconds, 0, 0, "%.2f"))
                lengthEdit = seconds;
            if (ImGui.IsItemActivated())
                lengthEdit = seconds;
            lengthActive = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit() && float.IsFinite(lengthEdit))
                current.SetLength(TimelineWindowViews.Frames(lengthEdit));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Stretches the motion to this length. Press Enter to apply it. Dragging the ruler's end works too.");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "s");
        var scale = current.ScaleEvents;
        if (ImGui.Checkbox("Scale events with length##timeline-scale-events", ref scale))
            current.ScaleEvents = scale;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(TimelineUi.ScaleEventsTip(current.ScaleEvents));
    }

    /// <summary>
    /// A face's own length: Still (one shape, held) or Moving, then its length in seconds. Turning Moving on or off holds the
    /// face's first frame; a moving face's seconds change its length.
    /// </summary>
    private void DrawFaceLength(TimelineEditor current, TimelineFace face, bool readOnly)
    {
        var unavailable = blenderMotion.FaceUnavailable(current);
        var moves = !face.Still;
        using (ImRaii.Disabled(readOnly || unavailable != null))
        {
            if (ImGui.Checkbox("Moves##timeline-face-moves", ref moves))
                blenderMotion.HoldFace(current, moves ? DefaultMovingFrames : 1);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(unavailable ?? (face.Still ? "Still holds one shape. Tick to make the face move over a length." : "Untick to make the face one shape again."));
        if (face.Still)
            return;
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(72));
        var seconds = lengthActive ? lengthEdit : face.Frames / (float)TimelineDocument.Fps;
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.InputFloat("##timeline-face-length", ref seconds, 0, 0, "%.2f"))
                lengthEdit = seconds;
            if (ImGui.IsItemActivated())
                lengthEdit = seconds;
            lengthActive = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit() && float.IsFinite(lengthEdit))
                current.SetFaceLength(TimelineWindowViews.Frames(lengthEdit));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The face's length. Press Enter to apply it.");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "s");
    }
}
