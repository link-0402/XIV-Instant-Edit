using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>How a bone shows on the map, in the list and on the character, for the selected layer at the playhead.</summary>
internal enum BoneState : byte { Still, Animated, Changed, Keyed }

/// <summary>
/// The bones the timeline poses: the selection the canvas, the properties panel, the Bones window and
/// the bones on the character share, the skeleton's bone tree, and the clip's own pose and the bones'
/// frames at a frame, which the bone editor, the rotation ball and the gizmo work in.
/// </summary>
internal sealed class BonePosing(TimelineSession session, MotionDecode decode)
{
    private readonly PoseSelection selection = new();
    // Why choosing the source skeleton here failed.
    private string? skeletonChoiceError;
    // The skeleton the selection was last checked against.
    private string? selectionSkeleton;
    // Bone indices by name and children by parent, for one skeleton.
    private (SkeletonDescription Skeleton, Dictionary<string, int> Index, int[][] Children)? boneTree;
    // The bones the clip moves, for one skeleton match.
    private (SkeletonResolution? Resolution, HashSet<string> Bones)? animatedBones;
    // The clip's own pose at one frame, local and in model space, while the decode and the frame stay.
    private (MotionClip Clip, int Frame, MotionTransform[] Local, MotionTransform[] Model)? motionPose;
    // The bones' frames for one document, layer, frame and decode.
    private (TimelineDocument Doc, string Layer, int Frame, MotionClip Clip, BoneFrames Frames)? boneFrames;

    /// <summary>The selected bones, the primary one first.</summary>
    public PoseSelection Selection => selection;

    /// <summary>Why choosing the source skeleton failed, the last time one was chosen.</summary>
    public string? ChoiceError => skeletonChoiceError;

    /// <summary>Makes <paramref name="candidate"/> the skeleton the clip was made for; the Source page and the Bones window share it.</summary>
    public void ChooseSkeleton(AnimationTimelineSource fresh, SkeletonCandidate candidate)
    {
        try
        {
            session.Source = fresh with { Clip = session.Animations!.Observer.ChooseSkeleton(fresh.Clip, candidate) };
            skeletonChoiceError = null;
        }
        catch (InvalidOperationException e)
        {
            skeletonChoiceError = e.Message;
        }
    }

    /// <summary>Drops selected bones another clip's skeleton does not have.</summary>
    public void KeepSelection(SkeletonDescription skeleton)
    {
        if (selectionSkeleton == skeleton.Fingerprint)
            return;
        var index = BoneTree(skeleton).Index;
        selection.Keep(index.ContainsKey);
        selectionSkeleton = skeleton.Fingerprint;
    }

    public (Dictionary<string, int> Index, int[][] Children) BoneTree(SkeletonDescription skeleton)
    {
        // The decode's skeleton and the match's are the same one, not always the same instance.
        if (boneTree is { } cached && (ReferenceEquals(cached.Skeleton, skeleton) ||
                                       cached.Skeleton.Fingerprint == skeleton.Fingerprint && cached.Skeleton.Bones.Length == skeleton.Bones.Length))
            return (cached.Index, cached.Children);
        var index = MotionEvaluator.Index(skeleton);
        var children = new List<int>[skeleton.Bones.Length];
        for (var b = 0; b < children.Length; b++)
            children[b] = [];
        for (var b = 0; b < skeleton.Bones.Length; b++)
            if (skeleton.Bones[b].Parent is var parent and >= 0 && parent < children.Length)
                children[parent].Add(b);
        var tree = children.Select(list => list.ToArray()).ToArray();
        boneTree = (skeleton, index, tree);
        return (index, tree);
    }

    /// <summary>The bones the clip moves; every bone while that is unknown.</summary>
    public HashSet<string>? AnimatedBones(AnimationClip clip)
    {
        if (animatedBones is { } cached && ReferenceEquals(cached.Resolution, clip.Resolution))
            return cached.Bones.Count == 0 ? null : cached.Bones;
        var bones = AnimationBones.Animated(clip).ToHashSet(StringComparer.Ordinal);
        animatedBones = (clip.Resolution, bones);
        return bones.Count == 0 ? null : bones;
    }

    public static BoneState StateOf(string bone, MotionLayer? layer, int frame, HashSet<string>? animated)
    {
        if (layer != null)
        {
            if (layer.Kind != MotionLayerKind.Amplitude && PoseEdits.Keyed(layer, bone, frame))
                return BoneState.Keyed;
            if (PoseEdits.Changes(layer, bone))
                return BoneState.Changed;
        }
        return animated == null || animated.Contains(bone) ? BoneState.Animated : BoneState.Still;
    }

    public static string StateText(BoneState state, MotionLayer? layer) => state switch
    {
        BoneState.Keyed => "Keyed here in this layer.",
        BoneState.Changed => layer?.Kind == MotionLayerKind.Amplitude ? "This layer scales its movement." : "This layer changes it.",
        BoneState.Animated => "The clip moves it.",
        _ => "The clip doesn't move it.",
    };

    public static Vector4 StateColour(BoneState state, MotionLayer? layer) => state switch
    {
        BoneState.Keyed or BoneState.Changed when layer != null => TimelineWindowViews.LayerColour(layer.Kind),
        BoneState.Still => Theme.Inactive,
        _ => Theme.Label,
    };

    /// <summary>
    /// A click on <paramref name="bone"/> on the map or in the list: alone it selects only the bone,
    /// Ctrl adds or removes it, Shift selects it with every bone below it (Ctrl+Shift adds them).
    /// </summary>
    public void ClickBone(string bone, SkeletonDescription skeleton)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift)
        {
            var (index, children) = BoneTree(skeleton);
            var branch = new List<string>();
            var stack = new Stack<int>();
            if (index.TryGetValue(bone, out var start))
                stack.Push(start);
            while (stack.Count > 0)
            {
                var b = stack.Pop();
                branch.Add(skeleton.Bones[b].Name);
                for (var c = children[b].Length - 1; c >= 0; c--)
                    stack.Push(children[b][c]);
            }
            selection.Set(branch, io.KeyCtrl, bone);
            return;
        }
        selection.Click(bone, io.KeyCtrl);
    }

    /// <summary>Whether bone <paramref name="bone"/> sits below <paramref name="ancestor"/> in the skeleton.</summary>
    public static bool IsBelow(SkeletonDescription skeleton, int bone, int ancestor)
    {
        if (ancestor < 0)
            return false;
        for (var parent = skeleton.Bones[bone].Parent; parent >= 0; parent = skeleton.Bones[parent].Parent)
            if (parent == ancestor)
                return true;
        return false;
    }

    /// <summary>
    /// The bones' frames at the playhead for the selected layer, from the clip with its offset and pose
    /// layers applied there: what edits made to several bones at once and the rotation ball turn in.
    /// </summary>
    public sealed class BoneFrames(SkeletonDescription skeleton, Dictionary<string, int> index, MotionTransform[] before,
        MotionTransform[] model, bool offset)
    {
        /// <summary>
        /// The rotation from character space to the space a bone's value turns in: for an offset the bone's
        /// own rotation as the layers before this one leave it, under its parent as all of them do; for a
        /// pose its parent's.
        /// </summary>
        public Quaternion Space(string bone)
        {
            if (!index.TryGetValue(bone, out var at))
                return Quaternion.Identity;
            var parent = skeleton.Bones[at].Parent;
            var above = parent >= 0 ? model[parent].Rotation : Quaternion.Identity;
            return Quaternion.Normalize(offset ? above * before[at].Rotation : above);
        }
    }

    /// <summary>
    /// The bones' frames at <paramref name="frame"/> for <paramref name="layer"/>; null while the clip's motion is
    /// read, which this starts. Kept while the document, the layer, the frame and the decode stay.
    /// </summary>
    public BoneFrames? FramesAt(AnimationTimelineSource loaded, TimelineDocument doc, MotionLayer layer, int frame)
    {
        if (MotionPose(loaded, frame) is not { } own || decode.Decoded is not { } clip)
            return null;
        if (boneFrames is { } cached && ReferenceEquals(cached.Doc, doc) && cached.Layer == layer.Key && cached.Frame == frame &&
            ReferenceEquals(cached.Clip, clip))
            return cached.Frames;
        var skeleton = clip.Skeleton;
        var at = Math.Max(0, MotionEdits.IndexOf(doc, layer.Key));
        MotionTransform[] before, after;
        try
        {
            var loop = LoopFrames(loaded, doc);
            var end = MotionEnd(loaded, doc);
            before = MotionEvaluator.ApplyAt(skeleton, own.Local, doc.Motion.Take(at).ToArray(), frame, loop, end);
            after = MotionEvaluator.ApplyAt(skeleton, own.Local, doc.Motion, frame, loop, end);
        }
        catch (InvalidDataException)
        {
            // A layer names a bone this skeleton lacks: the clip's own pose still gives frames.
            before = after = own.Local;
        }
        var frames = new BoneFrames(skeleton, BoneTree(skeleton).Index, before, MotionEvaluator.ModelSpace(skeleton, after),
            layer.Kind == MotionLayerKind.Offset);
        boneFrames = (doc, layer.Key, frame, clip, frames);
        return frames;
    }

    /// <summary>
    /// The clip's own pose at <paramref name="frame"/>, local and in model space, from the decoded motion
    /// as the document cuts or stretches it; null while it is read, which this starts.
    /// </summary>
    public (MotionTransform[] Local, MotionTransform[] Model)? MotionPose(AnimationTimelineSource loaded, int frame)
    {
        var fresh = session.Fresh(loaded);
        if (decode.Decoded is not { } decoded || decode.DecodedKey != MotionDecode.KeyOf(fresh))
        {
            decode.Ensure(fresh);
            return null;
        }
        var clip = session.Editor is { } current ? decode.OutputMotion(current, decoded) : decoded;
        if (motionPose is { } cached && ReferenceEquals(cached.Clip, clip) && cached.Frame == frame)
            return (cached.Local, cached.Model);
        var local = clip.Pose(frame);
        var model = MotionEvaluator.ModelSpace(clip.Skeleton, local);
        motionPose = (clip, frame, local, model);
        return (local, model);
    }

    /// <summary>
    /// The selected bones the skeleton has, primary first, each with its value in the layer at
    /// <paramref name="frame"/>; null while a pose layer waits for the clip's own motion.
    /// </summary>
    public List<PoseTarget>? Targets(MotionLayer layer, int frame, SkeletonDescription skeleton, AnimationTimelineSource loaded, TimelineDocument doc)
    {
        var loop = LoopFrames(loaded, doc);
        var (index, _) = BoneTree(skeleton);
        (MotionTransform[] Local, MotionTransform[] Model)? motion = null;
        if (layer.Kind == MotionLayerKind.Pose && (motion = MotionPose(loaded, frame)) == null)
            return null;
        var targets = new List<PoseTarget>();
        foreach (var bone in selection.Primary is { } primary ? selection.Bones.Where(name => name != primary).Prepend(primary) : selection.Bones)
        {
            if (!index.TryGetValue(bone, out var at) || PoseEdits.Value(layer, bone, frame, motion?.Local[at], loop) is not { } value)
                continue;
            targets.Add(new PoseTarget(bone, value, PoseEdits.Moved(layer, bone, frame)));
        }
        return targets;
    }

    /// <summary>The motion's length in frames when the game loops it, which layers covering it wrap their keys round; else 0.</summary>
    public static float LoopFrames(AnimationTimelineSource loaded, TimelineDocument doc) => loaded.Clip.IsLoop && !doc.Expression ? MotionEnd(loaded, doc) : 0;

    /// <summary>
    /// The motion's length in frames, as the bake and Live sample it: the clip's own, which the timeline's may
    /// be a frame off; when cut or stretched, the new one.
    /// </summary>
    public static float MotionEnd(AnimationTimelineSource loaded, TimelineDocument doc) =>
        !doc.Stretched && !doc.Cut && loaded.Clip.Duration > 0 ? loaded.Clip.Duration * TimelineDocument.Fps : doc.Length;
}
