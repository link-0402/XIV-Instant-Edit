using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>The bones a symmetry or pin layer marks: the ones it pins (none for a symmetry) and the ones it changes.</summary>
internal sealed record LayerBoneSets(IReadOnlySet<string> Pinned, IReadOnlySet<string> Changed);

/// <summary>How a bone shows on the map, in the list and on the character, for the selected layer at the playhead.</summary>
internal enum BoneState : byte { Still, Animated, Changed, Keyed, Pinned }

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
    // The selected symmetry layer's targets on the skeleton they were found on.
    private (MotionLayer Layer, SkeletonDescription Skeleton, TimelineDocument? Doc, MotionClip? Decoded, SymmetryPlan Plan, string? Problem,
        HashSet<string> Bones)? symmetryTargets;
    // The selected pin layer's pinned and bent bones on the skeleton they were found on.
    private (MotionLayer Layer, SkeletonDescription Skeleton, LayerBoneSets Bones)? pinBones;
    // The selected idle layer's moved bones.
    private (MotionLayer Layer, LayerBoneSets Bones)? idleBones;
    // A pin layer's planted facts by bone, for what they depend on: the layers before it, its pins and range, and the decoded motion.
    private (string Layer, MotionLayer[] Before, ImmutableArray<MotionPin> Pins, int Start, int End, MotionClip Clip, IReadOnlyDictionary<string, string> Facts)? plantedFacts;
    // The motion with some layers baked over the whole clip, or why it can't be, for those layers and the clip.
    private (MotionLayer[] Before, MotionClip Clip, MotionClip? Baked, string? Problem)? bakedBefore;
    // The clip's own pose at one frame, local and in model space, while the decode and the frame stay.
    private (MotionClip Clip, int Frame, MotionTransform[] Local, MotionTransform[] Model)? motionPose;
    // The bones' frames for one document, layer, frame and decode.
    private (TimelineDocument Doc, string Layer, int Frame, MotionClip Clip, BoneFrames Frames)? boneFrames;
    // The onion skins' poses in model space, apart from motionPose so the gizmo's pose at the playhead stays cached.
    private readonly List<(TimelineDocument Doc, MotionClip Clip, int Frame, MotionTransform[] Model)> ghostPoses = [];
    // What the character's models have weight on, read again as they change.
    private readonly WornBones worn = new(session.Data, session.Log);
    // The bones the clip and its layers move, for the moved bones, the document and the skeleton they were found from.
    private (IReadOnlySet<string> Moved, TimelineDocument? Doc, SkeletonDescription Skeleton, IReadOnlySet<string> Bones)? movingBones;
    // The map's bones, for the worn and moving bones they were joined from.
    private (IReadOnlySet<string> Worn, IReadOnlySet<string> Moving, IReadOnlySet<string> Bones)? mapBones;

    /// <summary>The Bones window's onion skins switch: the overlay also shows the pose at the selected layer's keys either side of the playhead.</summary>
    public bool Ghosts { get; set; }

    /// <summary>The Bones window's filter switch: while it is off, the map and the bones on the character show only <see cref="MapBones"/>.</summary>
    public bool EveryBone { get; set; }

    /// <summary>
    /// The bones the map and the bones on the character show, besides the selected ones and the ones the selected layer changes:
    /// the ones a model the character draws has weight on, the ones the clip takes off their rest pose (its tracks while it is
    /// read) and the ones a layer that is on moves. Null shows every bone: with <see cref="EveryBone"/> on, and while what the
    /// character wears or the clip moves is unknown.
    /// </summary>
    public IReadOnlySet<string>? MapBones(AnimationTimelineSource loaded, SkeletonDescription skeleton)
    {
        if (EveryBone || worn.Current(session.Playback) is not { } wears || Moving(loaded, skeleton) is not { } moving)
            return null;
        if (mapBones is { } cached && ReferenceEquals(cached.Worn, wears) && ReferenceEquals(cached.Moving, moving))
            return cached.Bones;
        var bones = new HashSet<string>(wears, StringComparer.Ordinal);
        bones.UnionWith(moving);
        mapBones = (wears, moving, bones);
        return bones;
    }

    /// <summary>
    /// The bones the clip takes off their rest pose, its tracks while it is read, with every bone a layer that is on moves;
    /// null while the clip's tracks are unknown. Kept while the decode, the document and the skeleton stay.
    /// </summary>
    private IReadOnlySet<string>? Moving(AnimationTimelineSource loaded, SkeletonDescription skeleton)
    {
        var fresh = session.Fresh(loaded);
        var moved = (decode.DecodedPose(fresh) != null ? decode.Moved : null) ?? AnimatedBones(fresh.Clip);
        if (moved == null)
            return null;
        var doc = session.Editor?.Document;
        // Through a drag or a field edit the last bones stand, so a gizmo drag doesn't work them out again every frame.
        if (movingBones is { } cached && ReferenceEquals(cached.Moved, moved) && ReferenceEquals(cached.Skeleton, skeleton) &&
            (ReferenceEquals(cached.Doc, doc) || session.Editor is { History.InGesture: true }))
            return cached.Bones;
        var bones = new HashSet<string>(moved, StringComparer.Ordinal);
        if (doc?.Motion is { IsDefaultOrEmpty: false } layers)
        {
            var index = BoneTree(skeleton).Index;
            // Animated wants tracks: the root, which the map never shows, stands in for a clip that moves nothing.
            short[] tracks = [.. moved.Where(index.ContainsKey).Select(bone => (short)index[bone]).DefaultIfEmpty((short)0)];
            if (MotionSymmetry.Animated(skeleton, tracks, layers, layers.Length) is { } animated)
                bones.UnionWith(animated.Select(bone => skeleton.Bones[bone].Name));
        }
        movingBones = (moved, doc, skeleton, bones);
        return bones;
    }

    /// <summary>The selected bones, the primary one first.</summary>
    public PoseSelection Selection => selection;

    /// <summary>Why choosing the source skeleton failed, the last time one was chosen.</summary>
    public string? ChoiceError => skeletonChoiceError;

    /// <summary>Makes <paramref name="candidate"/> the skeleton the clip was made for; the Source page and the Bones window share it.</summary>
    public void ChooseSkeleton(AnimationTimelineSource fresh, SkeletonCandidate candidate)
    {
        try
        {
            // Kept without the document's motion from Blender, which undo must be able to take away.
            session.Source = session.Matched(session.Source ?? fresh) with { Clip = session.Animations!.Observer.ChooseSkeleton(fresh.Clip, candidate) };
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

    /// <summary>
    /// The bones a symmetry, pin or idle layer marks on the map, the list and the character, by name: a pin's
    /// pinned bones and the bones it bends; the bones a symmetry copies onto; the bones an idle moves. Null for other layers, whose
    /// keys say what they change. Kept while the layer stays.
    /// </summary>
    public LayerBoneSets? BonesOf(MotionLayer? layer, SkeletonDescription skeleton)
    {
        switch (layer?.Kind)
        {
            case MotionLayerKind.Symmetry:
                return new LayerBoneSets(EmptySet, Symmetry(layer, skeleton).Bones);
            case MotionLayerKind.Pin:
                if (pinBones is { } cached && ReferenceEquals(cached.Layer, layer) && ReferenceEquals(cached.Skeleton, skeleton))
                    return cached.Bones;
                var bones = LayerBones(layer, skeleton);
                pinBones = (layer, skeleton, bones);
                return bones;
            case MotionLayerKind.Idle:
                if (idleBones is { } idle && ReferenceEquals(idle.Layer, layer))
                    return idle.Bones;
                idleBones = (layer, new LayerBoneSets(EmptySet, MotionIdles.Moved(layer).ToHashSet(StringComparer.Ordinal)));
                return idleBones.Value.Bones;
            default:
                return null;
        }
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    /// <summary>
    /// A pin layer's pinned bones and the bones its pins bend, by name, on <paramref name="skeleton"/>; empty
    /// sets for other layers, and for pins the skeleton can't resolve (applying the layer reports them).
    /// </summary>
    public static LayerBoneSets LayerBones(MotionLayer? layer, SkeletonDescription skeleton)
    {
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (layer is { Kind: MotionLayerKind.Pin })
            foreach (var pin in layer.Pins)
                try
                {
                    var chain = PinChains.Chain(skeleton, pin);
                    pinned.Add(pin.Bone);
                    changed.UnionWith(chain.Bones.Select(bone => skeleton.Bones[bone].Name));
                }
                catch (InvalidDataException)
                {
                    // A pin on a bone this skeleton lacks marks nothing.
                }
        return new LayerBoneSets(pinned, changed);
    }

    /// <summary>
    /// What a symmetry layer copies on <paramref name="skeleton"/>, as Live and the save do: pairs the clip
    /// doesn't track and no earlier layer moves are left to physics once the motion is read. Worked out
    /// once per layer, document, skeleton and decode rather than every frame: its plan, or why it has none
    /// (a picked bone the skeleton lacks).
    /// </summary>
    public (SymmetryPlan Plan, string? Problem, IReadOnlySet<string> Bones) Symmetry(MotionLayer layer, SkeletonDescription skeleton)
    {
        var decoded = decode.Decoded is { } clip && clip.Skeleton.Fingerprint == skeleton.Fingerprint ? clip : null;
        var doc = session.Editor?.Document;
        if (symmetryTargets is { } cached && ReferenceEquals(cached.Layer, layer) && ReferenceEquals(cached.Skeleton, skeleton) &&
            ReferenceEquals(cached.Doc, doc) && ReferenceEquals(cached.Decoded, decoded))
            return (cached.Plan, cached.Problem, cached.Bones);
        SymmetryPlan plan;
        string? problem = null;
        try
        {
            IReadOnlyList<MotionLayer> layers = doc?.Motion is { IsDefault: false } motion ? motion : [layer];
            var at = Math.Max(0, layers.ToList().FindIndex(other => other.Key == layer.Key));
            plan = MotionSymmetry.Plan(skeleton, layer, MotionSymmetry.Animated(skeleton, decoded?.SourceTracks, layers, at));
        }
        catch (InvalidDataException e)
        {
            plan = SymmetryPlan.Empty;
            problem = e.Message;
        }
        var bones = plan.Pairs.Select(pair => skeleton.Bones[pair.Target].Name).ToHashSet(StringComparer.Ordinal);
        symmetryTargets = (layer, skeleton, doc, decoded, plan, problem, bones);
        return (plan, problem, bones);
    }

    /// <summary>
    /// How <paramref name="bone"/> shows for <paramref name="layer"/> at <paramref name="frame"/>: keyed there,
    /// pinned by it, changed by it, else whether the clip moves it. <paramref name="layerBones"/> are the
    /// layer's bones from the skeleton, for a symmetry or pin layer (<see cref="BonesOf"/>).
    /// </summary>
    public static BoneState StateOf(string bone, MotionLayer? layer, int frame, HashSet<string>? animated, LayerBoneSets? layerBones = null)
    {
        if (layer != null)
        {
            if (layer.Kind is not (MotionLayerKind.Amplitude or MotionLayerKind.Symmetry or MotionLayerKind.Fix or MotionLayerKind.Idle or MotionLayerKind.Swing or
                    MotionLayerKind.Smooth) && PoseEdits.Keyed(layer, bone, frame))
                return BoneState.Keyed;
            if (layerBones?.Pinned.Contains(bone) == true)
                return BoneState.Pinned;
            if (PoseEdits.Changes(layer, bone, layerBones?.Changed))
                return BoneState.Changed;
        }
        return animated == null || animated.Contains(bone) ? BoneState.Animated : BoneState.Still;
    }

    public static string StateText(BoneState state, MotionLayer? layer) => state switch
    {
        BoneState.Keyed => "Keyed here in this layer.",
        BoneState.Pinned => "Pinned by this layer.",
        BoneState.Changed => layer?.Kind switch
        {
            MotionLayerKind.Amplitude => "This layer scales its movement.",
            MotionLayerKind.Symmetry => "Copied from the other side.",
            MotionLayerKind.Pin => "Bent to hold a pin.",
            MotionLayerKind.Fix => "This layer fixes its motion.",
            MotionLayerKind.Idle => "This layer adds idle motion.",
            MotionLayerKind.Swing => "This layer swings its bones.",
            MotionLayerKind.Smooth => "This layer smooths its bones.",
            _ => "This layer changes it.",
        },
        BoneState.Animated => "The clip moves it.",
        _ => "The clip doesn't move it.",
    };

    public static Vector4 StateColour(BoneState state, MotionLayer? layer) => state switch
    {
        BoneState.Keyed or BoneState.Changed or BoneState.Pinned when layer != null => TimelineWindowViews.LayerColour(layer.Kind),
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
        MotionTransform[] beforeModel, MotionTransform[] through, MotionTransform[] throughModel, MotionTransform[] model, bool offset,
        MotionLayer? pins = null)
    {
        /// <summary>Every bone's local transform as the layers before this one leave it.</summary>
        public MotionTransform[] Before => before;

        /// <summary>Every bone's model transform as the layers before this one leave it.</summary>
        public MotionTransform[] BeforeModel => beforeModel;

        /// <summary>Every bone's local transform as the layers through this one leave it.</summary>
        public MotionTransform[] Through => through;

        /// <summary>Every bone's model transform as the layers through this one leave it.</summary>
        public MotionTransform[] ThroughModel => throughModel;

        /// <summary>Where <paramref name="bone"/> is in character space as the layers through this one leave it; null for a bone these frames lack.</summary>
        public Vector3? ThroughPosition(string bone) => index.TryGetValue(bone, out var at) ? throughModel[at].Position : null;

        /// <summary>
        /// The rotation from character space to the space a bone's value turns in: for an offset the bone's
        /// own rotation as the layers before this one leave it, under its parent as all of them do; for a
        /// pose its parent's; for a pin its relative bone's as the layers before leave it, or none for the character.
        /// </summary>
        public Quaternion Space(string bone)
        {
            if (pins != null)
                return MotionEdits.PinIndex(pins, bone) is var pin and >= 0 && pins.Pins[pin].RelativeTo is { } relative &&
                       index.TryGetValue(relative, out var riding)
                    ? Quaternion.Normalize(beforeModel[riding].Rotation)
                    : Quaternion.Identity;
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
        MotionTransform[] before, through, after;
        try
        {
            var loop = LoopFrames(loaded, doc);
            var end = MotionEnd(loaded, doc);
            var earlier = Earlier(doc.Motion, at);
            // One pose leaves out amplitudes, fixes and planted pins, which take the whole clip: the frames then start from
            // the bake, so the panels and the gizmo key the bone where Live has it.
            before = TakesWholeClip(earlier) && OutputClip(loaded) is { } output
                ? BakedBefore(output, earlier).Pose(frame)
                : MotionEvaluator.ApplyAt(skeleton, own.Local, earlier, frame, loop, end, clip.SourceTracks);
            // Alone is enough: the layer posed here is never a symmetry, the one kind that reads the layers before it.
            through = MotionEvaluator.ApplyAt(skeleton, before, [layer], frame, loop, end, clip.SourceTracks);
            // shortcut: after leaves out whole-clip layers, so Space's parent turn may be off by them; bake it if a second bake per frame is cheap.
            after = MotionEvaluator.ApplyAt(skeleton, own.Local, doc.Motion, frame, loop, end, clip.SourceTracks);
        }
        catch (InvalidDataException)
        {
            // A layer names a bone this skeleton lacks: the clip's own pose still gives frames.
            before = through = after = own.Local;
        }
        var frames = new BoneFrames(skeleton, BoneTree(skeleton).Index, before, MotionEvaluator.ModelSpace(skeleton, before), through,
            MotionEvaluator.ModelSpace(skeleton, through), MotionEvaluator.ModelSpace(skeleton, after), layer.Kind == MotionLayerKind.Offset,
            layer.Kind == MotionLayerKind.Pin ? layer : null);
        boneFrames = (doc, layer.Key, frame, clip, frames);
        return frames;
    }

    /// <summary>
    /// The layers before the one at <paramref name="at"/>, as they apply to it: all but Match startup to loop and Close loop
    /// fixes, which land after every layer.
    /// </summary>
    public static MotionLayer[] Earlier(IReadOnlyList<MotionLayer> motion, int at) =>
        [.. motion.Take(at).Where(layer => !MotionEdits.IsAnchored(layer))];

    /// <summary>Whether any of <paramref name="layers"/> takes the whole clip, which one pose leaves out: an amplitude, a fix, a smooth motion or a planted pin.</summary>
    public static bool TakesWholeClip(IEnumerable<MotionLayer> layers) => layers.Any(layer => MotionEvaluator.Active(layer) &&
        (layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Smooth ||
         layer.Kind == MotionLayerKind.Pin && layer.Pins.Any(pin => pin.Planted)));

    /// <summary>The bones the decoded clip has tracks for, when it was decoded on <paramref name="skeleton"/>; empty while unknown.</summary>
    public IReadOnlyCollection<short> DecodedTracks(SkeletonDescription skeleton) =>
        decode.Decoded is { } clip && clip.Skeleton.Fingerprint == skeleton.Fingerprint ? clip.SourceTracks : [];

    /// <summary>
    /// Where each planted pin of <paramref name="layer"/> holds, as a fact line by pinned bone ("Held 0-40,
    /// 72-119"), on the motion as the layers before it leave it; a key "" holds why there are none. Null
    /// while the motion is read, which this starts. Worked out again only when the layers before it, its
    /// pins, its range or the decode change, not while its pull, fades or name are edited, nor during a drag.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PlantedFacts(AnimationTimelineSource loaded, TimelineDocument doc, MotionLayer layer)
    {
        if (OutputClip(loaded) is not { } clip)
            return null;
        var before = Earlier(doc.Motion, Math.Max(0, MotionEdits.IndexOf(doc, layer.Key)));
        if (plantedFacts is { } cached && cached.Layer == layer.Key && ReferenceEquals(cached.Clip, clip) &&
            // Through a drag the last facts stand, so dragging the layer's edges doesn't work them out again every frame.
            (session.Editor is { Gesture: not TimelineGesture.None } ||
             cached.Start == layer.Start && cached.End == layer.End && cached.Pins.SequenceEqual(layer.Pins) &&
             cached.Before.SequenceEqual(before, ReferenceEqualityComparer.Instance)))
            return cached.Facts;
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var baked = BakedBefore(clip, before);
            foreach (var (bone, path) in MotionEvaluator.Planted(baked, layer))
                facts[bone] = PinPlanting.Facts(path, baked);
        }
        catch (InvalidDataException e)
        {
            facts[string.Empty] = e.Message;
        }
        plantedFacts = (layer.Key, before, layer.Pins, layer.Start, layer.End, clip, facts);
        return facts;
    }

    /// <summary>
    /// <paramref name="clip"/> with <paramref name="before"/> baked over it, as Live and the save bake them; kept
    /// while those layers and the clip stay. Read only. Throws for a layer naming a bone the skeleton lacks.
    /// </summary>
    private MotionClip BakedBefore(MotionClip clip, MotionLayer[] before)
    {
        // Through a drag the last bake of as many layers stands, so dragging an earlier layer doesn't bake the clip every frame.
        if (bakedBefore is not { } cached || !ReferenceEquals(cached.Clip, clip) || !cached.Before.SequenceEqual(before, ReferenceEqualityComparer.Instance) &&
            !(session.Editor is { Gesture: not TimelineGesture.None } && cached.Before.Length == before.Length))
        {
            MotionClip? baked = null;
            string? problem = null;
            try
            {
                baked = new MotionClip(clip.Skeleton, clip.Duration, clip.Samples) { Loops = clip.Loops, SourceTracks = clip.SourceTracks };
                baked.CopyFrom(clip);
                MotionEvaluator.Apply(baked, before);
            }
            catch (InvalidDataException e)
            {
                // Kept too, so a broken layer isn't baked again every frame.
                baked = null;
                problem = e.Message;
            }
            cached = (before, clip, baked, problem);
            bakedBefore = cached;
        }
        return cached.Baked ?? throw new InvalidDataException(cached.Problem);
    }

    /// <summary>The decoded motion as the document cuts or stretches it; null while it is read, which this starts.</summary>
    private MotionClip? OutputClip(AnimationTimelineSource loaded)
    {
        var fresh = session.Fresh(loaded);
        if (decode.Decoded is not { } decoded || decode.DecodedKey != MotionDecode.KeyOf(fresh))
        {
            decode.Ensure(fresh);
            return null;
        }
        return session.Editor is { } current ? decode.OutputMotion(current, decoded) : decoded;
    }

    /// <summary>
    /// The clip's own pose at <paramref name="frame"/>, local and in model space, from the decoded motion
    /// as the document cuts or stretches it; null while it is read, which this starts.
    /// </summary>
    public (MotionTransform[] Local, MotionTransform[] Model)? MotionPose(AnimationTimelineSource loaded, int frame)
    {
        if (OutputClip(loaded) is not { } clip)
            return null;
        if (motionPose is { } cached && ReferenceEquals(cached.Clip, clip) && cached.Frame == frame)
            return (cached.Local, cached.Model);
        var local = clip.Pose(frame);
        var model = MotionEvaluator.ModelSpace(clip.Skeleton, local);
        motionPose = (clip, frame, local, model);
        return (local, model);
    }

    /// <summary>
    /// The pose at <paramref name="frame"/> with every layer of <paramref name="doc"/> applied, in model space, for an
    /// onion skin; null while the motion is read, which this starts. Like the gizmo's frames it leaves out amplitudes,
    /// fixes and planted pins, which take the whole clip. Kept for the last three frames (the playhead and the keys either side)
    /// while the document and the decode stay.
    /// </summary>
    public MotionTransform[]? GhostPose(AnimationTimelineSource loaded, TimelineDocument doc, int frame)
    {
        if (OutputClip(loaded) is not { } clip)
            return null;
        foreach (var cached in ghostPoses)
            if (ReferenceEquals(cached.Doc, doc) && ReferenceEquals(cached.Clip, clip) && cached.Frame == frame)
                return cached.Model;
        var model = MotionEvaluator.ModelSpace(clip.Skeleton, Layered(clip, loaded, doc, frame));
        if (ghostPoses.Count >= 3)
            ghostPoses.RemoveAt(0);
        ghostPoses.Add((doc, clip, frame, model));
        return model;
    }

    /// <summary>
    /// The local pose at <paramref name="frame"/> with every layer of <paramref name="doc"/> baked in, as Live and the save
    /// show it, amplitudes, fixes and planted pins included; null while the motion is read, which this starts.
    /// </summary>
    public MotionTransform[]? LayeredLocal(AnimationTimelineSource loaded, TimelineDocument doc, int frame)
    {
        if (OutputClip(loaded) is not { } clip)
            return null;
        try
        {
            return BakedBefore(clip, doc.Motion.ToArray()).Pose(frame);
        }
        catch (InvalidDataException)
        {
            // A layer names a bone this skeleton lacks: the clip's own pose still shows.
            return clip.Pose(frame);
        }
    }

    private MotionTransform[] Layered(MotionClip clip, AnimationTimelineSource loaded, TimelineDocument doc, int frame)
    {
        var own = clip.Pose(frame);
        try
        {
            return MotionEvaluator.ApplyAt(clip.Skeleton, own, doc.Motion, frame, LoopFrames(loaded, doc), MotionEnd(loaded, doc), clip.SourceTracks);
        }
        catch (InvalidDataException)
        {
            // A layer names a bone this skeleton lacks: the clip's own pose still shows.
            return own;
        }
    }

    /// <summary>
    /// The selected bones the skeleton has, primary first, each with its value in the layer at
    /// <paramref name="frame"/>; null while a pose layer waits for the clip's own motion.
    /// </summary>
    public List<PoseTarget>? Targets(MotionLayer layer, int frame, SkeletonDescription skeleton, AnimationTimelineSource loaded, TimelineDocument doc,
        bool spots = false)
    {
        var loop = LoopFrames(loaded, doc);
        var (index, _) = BoneTree(skeleton);
        if (layer.Kind == MotionLayerKind.Pin)
        {
            // A pin's values are spots in its space, where the layers before it leave the bones.
            if (FramesAt(loaded, doc, layer, frame) is not { } frames)
                return null;
            var selected = selection.Primary is { } first ? selection.Bones.Where(name => name != first).Prepend(first) : selection.Bones;
            return PinTargets(layer, frame, selected, index, frames.BeforeModel, loop, spots);
        }
        // An unkeyed pose bone starts where the layers before this one leave it, so a second pose layer builds on the first.
        MotionTransform[]? before = null;
        if (layer.Kind == MotionLayerKind.Pose && (before = FramesAt(loaded, doc, layer, frame)?.Before) == null)
            return null;
        var targets = new List<PoseTarget>();
        foreach (var bone in selection.Primary is { } primary ? selection.Bones.Where(name => name != primary).Prepend(primary) : selection.Bones)
        {
            if (!index.TryGetValue(bone, out var at) || PoseEdits.Value(layer, bone, frame, before?[at], loop) is not { } value)
                continue;
            targets.Add(new PoseTarget(bone, value, PoseEdits.Moved(layer, bone, frame)));
        }
        return targets;
    }

    /// <summary>
    /// The pin layer's targets among <paramref name="bones"/> (primary first): the keyed pins the skeleton has,
    /// each at its key's value at <paramref name="frame"/>, else (and with <paramref name="spots"/> always) at the
    /// spot it is at in its pin's space in <paramref name="beforeModel"/>. Unpinned and planted bones have no
    /// keys to change and are left out.
    /// </summary>
    public static List<PoseTarget> PinTargets(MotionLayer layer, int frame, IEnumerable<string> bones, Dictionary<string, int> index,
        MotionTransform[] beforeModel, float loop, bool spots)
    {
        var targets = new List<PoseTarget>();
        foreach (var bone in bones)
        {
            if (MotionEdits.PinIndex(layer, bone) is not (var at and >= 0) || layer.Pins[at].Planted || !index.TryGetValue(bone, out var b))
                continue;
            var relative = layer.Pins[at].RelativeTo is { } name && index.TryGetValue(name, out var riding) ? riding : -1;
            var spot = PinSolver.PinSpace(beforeModel, b, relative) with { Scale = Vector3.One };
            var value = spots ? spot : PoseEdits.Value(layer, bone, frame, spot, loop) ?? spot;
            targets.Add(new PoseTarget(bone, value, false));
        }
        return targets;
    }

    /// <summary>The motion's length in frames when the game loops it, which layers covering it wrap their keys round; else 0.</summary>
    public static float LoopFrames(AnimationTimelineSource loaded, TimelineDocument doc) => loaded.Clip.IsLoop && doc.HasClip ? MotionEnd(loaded, doc) : 0;

    /// <summary>
    /// The motion's length in frames, as the bake and Live sample it: the clip's own, which the timeline's may
    /// be a frame off; when cut or stretched, the new one. A face's ends where the face does on the timeline.
    /// </summary>
    public static float MotionEnd(AnimationTimelineSource loaded, TimelineDocument doc) =>
        doc.Face is { } face ? face.End
        : doc.Stretched || doc.Cut ? doc.Length
        // A motion from Blender has its own length, in whole frames.
        : doc.Replacement is { } replacement ? replacement.Frames
        : loaded.Clip.Duration > 0 ? loaded.Clip.Duration * TimelineDocument.Fps : doc.Length;
}
