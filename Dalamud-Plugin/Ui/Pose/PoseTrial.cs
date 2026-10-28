using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Pose;

/// <summary>
/// The pose browser's last pick, so the next one replaces it: a pick keys onto the document from before
/// the last one while the timeline still shows what that pick made, in the same layer at the same frame.
/// Anything in between (an edit, an undo, another layer or frame) keeps the last pick and keys anew.
/// No ImGui or Dalamud dependencies.
/// </summary>
internal sealed class PoseTrial
{
    private (TimelineDocument Before, TimelineDocument After, string Layer, int Frame, string Path)? last;

    /// <summary>The file the last pick keyed, while the next pick in <paramref name="layer"/> at <paramref name="frame"/> replaces it.</summary>
    public string? Replacing(TimelineDocument current, string layer, int frame) =>
        last is { } pick && ReferenceEquals(pick.After, current) && pick.Layer == layer && pick.Frame == frame ? pick.Path : null;

    /// <summary>The document a pick in <paramref name="layer"/> at <paramref name="frame"/> keys onto, and whether it replaces the last one.</summary>
    public TimelineDocument Base(TimelineDocument current, string layer, int frame, out bool replaces)
    {
        replaces = Replacing(current, layer, frame) != null;
        return replaces ? last!.Value.Before : current;
    }

    /// <summary>Remembers a pick: the document it keyed onto, the one the timeline shows after it, and where.</summary>
    public void Keyed(TimelineDocument before, TimelineDocument after, string layer, int frame, string path) =>
        last = (before, after, layer, frame, path);

    public void Forget() => last = null;
}
