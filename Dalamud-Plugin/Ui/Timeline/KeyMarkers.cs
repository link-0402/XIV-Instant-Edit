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
    /// The markers of <paramref name="source"/>'s motion as <paramref name="current"/>'s cuts and length play it: Current
    /// only when found for exactly those cuts, length and detail (what edits reach with), Shown the last set found for this
    /// decode (what the timeline draws while newer ones are searched). Both null on an expression timeline and before the
    /// motion is decoded.
    /// </summary>
    public (MotionMarkerSet? Current, MotionMarkerSet? Shown) For(TimelineEditor current, AnimationTimelineSource source)
    {
        var doc = current.Document;
        if (!doc.HasClip || source.Standalone || session.Animations == null || decode.DecodedPose(source) is not { } decoded)
            return (null, null);
        var detail = MotionEdits.MotionKeysLayer(doc)?.Markers?.Detail ?? MarkerDetail.Normal;
        // A field drag holds history open without a pointer gesture; both edit the document every frame.
        var gesture = current.Gesture != TimelineGesture.None || current.History.InGesture;
        var decodeKey = decode.DecodedKey ?? "";
        var found = search.Find(decodeKey, decode.OutputMotion(current, decoded), doc, detail, gesture);
        return (found, found ?? search.Last(decodeKey));
    }
}
