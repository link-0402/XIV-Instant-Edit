using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The open clip's key markers for the timeline: the output motion's markers at the Motion keys layer's detail, searched
/// in the background (see <see cref="MarkerSearch"/>). Runs on the draw thread; only the search itself runs on a task.
/// </summary>
internal sealed class KeyMarkers(TimelineSession session, MotionDecode decode)
{
    private readonly MarkerSearch search = new();

    /// <summary>
    /// The markers of <paramref name="source"/>'s motion as <paramref name="current"/>'s cuts and length play it; null on an
    /// expression timeline, before the motion is decoded and before the first search finishes. During a drag or a field
    /// edit the last set stands.
    /// </summary>
    public MotionMarkerSet? For(TimelineEditor current, AnimationTimelineSource source)
    {
        var doc = current.Document;
        if (doc.Expression || source.Standalone || session.Animations == null || decode.DecodedPose(source) is not { } decoded)
            return null;
        var detail = MotionEdits.MotionKeysLayer(doc)?.Markers?.Detail ?? MarkerDetail.Normal;
        // A field drag holds history open without a pointer gesture; both edit the document every frame.
        var gesture = current.Gesture != TimelineGesture.None || current.History.InGesture;
        return search.Find(decode.DecodedKey ?? "", decode.OutputMotion(current, decoded), doc, detail, gesture);
    }
}
