using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui.Pose;

/// <summary>A selected bone with its value in the layer at the playhead and whether its key there sets its position.</summary>
internal readonly record struct PoseTarget(string Bone, MotionTransform Value, bool Moved);

/// <summary>Bones copied from a layer of <paramref name="Kind"/> at the playhead, each as Key would key it, for Paste pose.</summary>
internal sealed record PoseCopy(MotionLayerKind Kind, IReadOnlyList<PoseTarget> Bones);

/// <summary>
/// The Bones window's edits of a layer's keys at the playhead, for every selected bone at once:
/// values, changes made to all of them, keying, resetting and mirroring to the other side. Pure
/// and immutable like <see cref="MotionEdits"/>, which they build on.
/// </summary>
internal static class PoseEdits
{
    /// <summary>
    /// A bone's value in <paramref name="layer"/> at <paramref name="frame"/>, as the panels show it: its
    /// keys' (between keys the layer's way, held outside them or round a <paramref name="loop"/> the layer
    /// covers), else no change for an offset, else <paramref name="motion"/>: the transform the layers before it leave
    /// for a pose, the spot the bone is at in its pin's space for a pin. Null while that is unknown.
    /// </summary>
    public static MotionTransform? Value(MotionLayer layer, string bone, int frame, MotionTransform? motion, float loop = 0) =>
        MotionEvaluator.KeyValue(layer, bone, frame, loop) ?? (layer.Kind is MotionLayerKind.Pose or MotionLayerKind.Pin ? motion : MotionTransform.Identity);

    /// <summary>Whether the key at <paramref name="frame"/> sets <paramref name="bone"/>'s position: a pose key otherwise only turns it.</summary>
    public static bool Moved(MotionLayer layer, string bone, int frame) =>
        MotionEdits.KeyIndex(layer, frame) is var at and >= 0 && layer.Keys[at].Moved.Contains(bone);

    /// <summary>Whether a key at <paramref name="frame"/> has <paramref name="bone"/>.</summary>
    public static bool Keyed(MotionLayer layer, string bone, int frame) =>
        MotionEdits.KeyIndex(layer, frame) is var at and >= 0 && layer.Keys[at].Bones.ContainsKey(bone);

    /// <summary>
    /// Whether any key of the layer has <paramref name="bone"/>, or for an amplitude or a fix whether it is limited to
    /// it. A symmetry's or a pin's bones come from the skeleton, an idle's from what it moves (<paramref name="changed"/>, see BonePosing.BonesOf).
    /// </summary>
    public static bool Changes(MotionLayer layer, string bone, IReadOnlySet<string>? changed = null) => layer.Kind switch
    {
        MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Swing or MotionLayerKind.Smooth => layer.Bones.Contains(bone),
        MotionLayerKind.Symmetry or MotionLayerKind.Pin => changed?.Contains(bone) == true,
        MotionLayerKind.Idle => changed?.Contains(bone) ?? MotionIdles.Moved(layer).Contains(bone),
        _ => layer.Keys.Any(key => key.Bones.ContainsKey(bone)),
    };

    /// <summary>
    /// The selected bones an edit turns, moves and scales: the primary bone and every other selected bone
    /// with no selected bone above it. The bones above the primary one stay out, with the rest of what
    /// hangs from them (an arm's attachment bones when the hand is primary), and a bone below a changed
    /// one follows it rather than turning twice.
    /// </summary>
    public static List<PoseTarget> Turned(IReadOnlyList<PoseTarget> targets, string primary, SkeletonDescription skeleton)
    {
        var index = MotionEvaluator.Index(skeleton);
        var selected = new HashSet<int>();
        foreach (var target in targets)
            if (index.TryGetValue(target.Bone, out var at))
                selected.Add(at);
        var first = index.TryGetValue(primary, out var found) ? found : -1;
        var abovePrimary = new HashSet<int>();
        if (first >= 0)
            for (var above = skeleton.Bones[first].Parent; above >= 0; above = skeleton.Bones[above].Parent)
                abovePrimary.Add(above);
        var turned = new List<PoseTarget>();
        foreach (var target in targets)
        {
            if (!index.TryGetValue(target.Bone, out var at) || abovePrimary.Contains(at))
                continue;
            var follows = false;
            if (at != first)
                for (var above = skeleton.Bones[at].Parent; above >= 0 && !follows; above = skeleton.Bones[above].Parent)
                    follows = selected.Contains(above);
            if (!follows)
                turned.Add(target);
        }
        return turned;
    }

    /// <summary>A turn about the own axes of something turned <paramref name="frame"/> in character space, as a turn in character space.</summary>
    public static Quaternion Around(Quaternion frame, Quaternion turn)
    {
        frame = Quaternion.Normalize(frame);
        return Quaternion.Normalize(frame * Quaternion.Normalize(turn) * Quaternion.Inverse(frame));
    }

    /// <summary>
    /// Keys <paramref name="primary"/> at <paramref name="frame"/> with <paramref name="value"/>, and makes the same
    /// change to every other target as the character sees it: turned about the same axes and moved the same
    /// way, each about its own joint, and scaled as much. <paramref name="space"/> gives a bone's frame: the
    /// rotation from character space to the space its value turns in, for an offset the bone's own rotation
    /// before the offset, for a pose its parent's. <paramref name="moved"/> sets whether pose keys set the
    /// bones' positions; null leaves each bone's as it is. A pose bone whose key starts setting its position
    /// here moves from the clip's own (<paramref name="own"/>), as in <see cref="Move"/>.
    /// </summary>
    public static TimelineDocument Set(TimelineDocument doc, string layer, int frame, PoseTarget primary, MotionTransform value,
        IReadOnlyList<PoseTarget> others, Func<string, Quaternion> space, bool offset, bool? moved = null, Func<string, Vector3?>? own = null)
    {
        doc = MotionEdits.SetKey(doc, layer, frame, primary.Bone, value.ToBone(), moved);
        var frameOf = Quaternion.Normalize(space(primary.Bone));
        var before = Quaternion.Normalize(frameOf * Quaternion.Normalize(primary.Value.Rotation));
        var after = Quaternion.Normalize(frameOf * Quaternion.Normalize(value.Rotation));
        var turn = Quaternion.Normalize(after * Quaternion.Inverse(before));
        // An offset moves in character space already; a pose moves in its parent's.
        var move = value.Position - primary.Value.Position;
        if (!offset)
            move = Vector3.Transform(move, frameOf);
        var scale = Ratio(value.Scale, primary.Value.Scale);
        foreach (var other in others)
        {
            if (string.Equals(other.Bone, primary.Bone, StringComparison.Ordinal))
                continue;
            var frameOfOther = Quaternion.Normalize(space(other.Bone));
            var from = !offset && moved == true && !other.Moved ? own?.Invoke(other.Bone) ?? other.Value.Position : other.Value.Position;
            var changed = new MotionTransform(
                from + (offset ? move : Vector3.Transform(move, Quaternion.Inverse(frameOfOther))),
                Quaternion.Normalize(Quaternion.Inverse(frameOfOther) * turn * frameOfOther * Quaternion.Normalize(other.Value.Rotation)),
                ClampScale(other.Value.Scale * scale));
            doc = MotionEdits.SetKey(doc, layer, frame, other.Bone, changed.ToBone(), moved);
        }
        return doc;
    }

    /// <summary>
    /// Turns every target by <paramref name="turn"/>, a turn in character space, each about its own joint.
    /// <paramref name="space"/> gives the bones' frames, as for <see cref="Set"/>.
    /// </summary>
    public static TimelineDocument Turn(TimelineDocument doc, string layer, int frame, IReadOnlyList<PoseTarget> targets, Quaternion turn,
        Func<string, Quaternion> space)
    {
        turn = Quaternion.Normalize(turn);
        foreach (var target in targets)
        {
            var own = Quaternion.Normalize(space(target.Bone));
            var rotation = Quaternion.Normalize(Quaternion.Inverse(own) * turn * own * Quaternion.Normalize(target.Value.Rotation));
            doc = MotionEdits.SetKey(doc, layer, frame, target.Bone, (target.Value with { Rotation = rotation }).ToBone());
        }
        return doc;
    }

    /// <summary>
    /// Moves every target by <paramref name="move"/>, a move in character space, each from where it is. An
    /// offset moves in character space already. A pose moves in its parent's space (<paramref name="space"/>),
    /// and its key then sets the position, starting from the clip's own (<paramref name="own"/>) where it
    /// left the position to the clip before.
    /// </summary>
    public static TimelineDocument Move(TimelineDocument doc, string layer, int frame, IReadOnlyList<PoseTarget> targets, Vector3 move,
        Func<string, Quaternion> space, bool offset, Func<string, Vector3?>? own = null)
    {
        foreach (var target in targets)
        {
            var value = target.Value;
            if (offset)
                value = value with { Position = value.Position + move };
            else
            {
                var from = target.Moved ? value.Position : own?.Invoke(target.Bone) ?? value.Position;
                value = value with { Position = from + Vector3.Transform(move, Quaternion.Inverse(Quaternion.Normalize(space(target.Bone)))) };
            }
            doc = MotionEdits.SetKey(doc, layer, frame, target.Bone, value.ToBone(), offset ? null : true);
        }
        return doc;
    }

    /// <summary>
    /// Keys the chain above <paramref name="bone"/> in <paramref name="layer"/> at <paramref name="frame"/> so the
    /// layer, at <paramref name="weight"/>, bends it to put the bone at <paramref name="aim"/> (character space).
    /// <paramref name="before"/> is the pose as the layers before leave it, <paramref name="through"/> as the layers
    /// through this one leave it (both local, the clip skeleton's order). The bone's own rotation is not keyed.
    /// Returns <paramref name="doc"/> unchanged when nothing can bend, the weight is under MinFollowWeight, or no rotation changes.
    /// <paramref name="layer"/> must be the layer as <paramref name="doc"/> holds it: values are read from it, keys written to the doc.
    /// A key never turns a bone a half turn or more, so a layer at a low weight bends the chain only as far as it can carry it.
    /// A drag passes <paramref name="start"/>, <paramref name="through"/> as it began: every frame solves from there, so where the
    /// chain ends up depends only on the aim, and turns can't build up in its bones over a long drag. It may also pass
    /// <paramref name="limb"/>, <see cref="Limb"/> for <paramref name="bone"/>, so its frames don't find the chain again.
    /// </summary>
    public static TimelineDocument Reach(TimelineDocument doc, MotionLayer layer, int frame, SkeletonDescription skeleton,
        ReadOnlySpan<MotionTransform> before, ReadOnlySpan<MotionTransform> through, string bone, Vector3 aim, float weight, float loop = 0,
        MotionTransform[]? start = null, (PinChain Chain, Vector3 RestNormal)? limb = null)
    {
        var (chain, normal) = limb ?? Limb(skeleton, bone);
        if (chain.Shape == PinChainShape.None || !(weight >= MinFollowWeight))
            return doc;
        var local = start?.ToArray() ?? through.ToArray();
        var model = MotionEvaluator.ModelSpace(skeleton, local);
        PinSolver.Reach(skeleton, local, model, chain, aim, normal);
        return KeyTurns(doc, layer, frame, skeleton, before, through, local, chain.Bones, weight, loop);
    }

    /// <summary>
    /// Keys the turns a hip drag makes in <paramref name="layer"/> at <paramref name="frame"/>: from <paramref name="start"/> (the pose the
    /// layers through this one gave when the drag began) the hips are moved so their model position is <paramref name="aim"/> and the
    /// <paramref name="anchors"/> held (<see cref="HipSolver.Solve"/>); every bone the solve turned is keyed as <see cref="Reach"/> keys a
    /// chain, against <paramref name="before"/> and <paramref name="through"/>. The hips' own position is keyed by <see cref="Move"/>, not here.
    /// Returns <paramref name="doc"/> unchanged when the weight is under MinFollowWeight or no rotation changes.
    /// </summary>
    public static TimelineDocument DragHips(TimelineDocument doc, MotionLayer layer, int frame, SkeletonDescription skeleton,
        ReadOnlySpan<MotionTransform> before, ReadOnlySpan<MotionTransform> through, MotionTransform[] start, Vector3 aim,
        ReadOnlySpan<HipAnchor> anchors, float weight, float loop = 0)
    {
        if (!MotionEvaluator.Index(skeleton).TryGetValue(PinChains.Hips, out var hips))
            throw MotionEvaluator.Missing(PinChains.Hips);
        var turned = HipSolver.Turned(anchors);
        if (!(weight >= MinFollowWeight) || turned.IsEmpty)
            return doc;
        var local = start.ToArray();
        var startModel = MotionEvaluator.ModelSpace(skeleton, start);
        var model = startModel.ToArray();
        HipSolver.Solve(skeleton, local, model, hips, aim, anchors, startModel);
        return KeyTurns(doc, layer, frame, skeleton, before, through, local, turned, weight, loop);
    }

    /// <summary>
    /// Keys the turns a head drag makes in <paramref name="layer"/> at <paramref name="frame"/>, as <see cref="DragHips"/> keys a hip drag's:
    /// from <paramref name="start"/> the head is put at <paramref name="aim"/>, <paramref name="back"/> leaning and the hips moving, with the
    /// <paramref name="anchors"/> held (<see cref="HeadSolver.Solve"/>). Returns the keyed document and the hips' move from the start (model
    /// space), which <see cref="Move"/> keys for the hips and the back they carry, not here. The document comes back unchanged, with no move,
    /// when the weight is under MinFollowWeight, and unchanged when no rotation changes.
    /// </summary>
    public static (TimelineDocument Doc, Vector3 HipsMove) DragHead(TimelineDocument doc, MotionLayer layer, int frame, SkeletonDescription skeleton,
        ReadOnlySpan<MotionTransform> before, ReadOnlySpan<MotionTransform> through, MotionTransform[] start, Vector3 aim, PinChain back,
        ReadOnlySpan<HipAnchor> anchors, float weight, float loop = 0)
    {
        if (!MotionEvaluator.Index(skeleton).TryGetValue(PinChains.Hips, out var hips))
            throw MotionEvaluator.Missing(PinChains.Hips);
        if (!(weight >= MinFollowWeight))
            return (doc, Vector3.Zero);
        var local = start.ToArray();
        var startModel = MotionEvaluator.ModelSpace(skeleton, start);
        var model = startModel.ToArray();
        var move = HeadSolver.Solve(skeleton, local, model, hips, back, aim, anchors, startModel);
        return (KeyTurns(doc, layer, frame, skeleton, before, through, local, HeadSolver.Turned(back, anchors), weight, loop), move);
    }

    /// <summary>
    /// Keys each of <paramref name="bones"/> in <paramref name="layer"/> at <paramref name="frame"/> so the layer, at
    /// <paramref name="weight"/>, turns it as in <paramref name="solved"/> (local): the turn from <paramref name="before"/> scaled
    /// by the weight's inverse, never a half turn or more. A bone already turned so in <paramref name="through"/> is left as it is.
    /// </summary>
    private static TimelineDocument KeyTurns(TimelineDocument doc, MotionLayer layer, int frame, SkeletonDescription skeleton,
        ReadOnlySpan<MotionTransform> before, ReadOnlySpan<MotionTransform> through, MotionTransform[] solved, IEnumerable<int> bones,
        float weight, float loop)
    {
        // A key past a half turn reads back the short way round, the opposite turn.
        var most = weight * 179 * MathF.PI / 180;
        foreach (var b in bones)
        {
            var rotation = Quaternion.Normalize(solved[b].Rotation);
            // Float noise only: |dot| > 1 - 1e-8 (about 0.016 degrees), read off the turn's sine, as a float can't hold 1 - 1e-8.
            var moved = Quaternion.Inverse(Quaternion.Normalize(through[b].Rotation)) * rotation;
            if (new Vector3(moved.X, moved.Y, moved.Z).LengthSquared() < 2e-8f)
                continue;
            // The turn the layer makes from the pose before it, scaled so that at its weight it gives the solved one.
            var from = Quaternion.Normalize(before[b].Rotation);
            var change = Quaternion.Normalize(Quaternion.Inverse(from) * rotation);
            var angle = 2 * MathF.Acos(Math.Clamp(Math.Abs(change.W), 0, 1));
            if (angle > most)
                change = MotionEvaluator.Pow(change, most / angle);
            var turn = MotionEvaluator.Pow(change, 1 / weight);
            var name = skeleton.Bones[b].Name;
            var value = Value(layer, name, frame, through[b], loop)!.Value;
            value = value with { Rotation = layer.Kind == MotionLayerKind.Pose ? Quaternion.Normalize(from * turn) : turn };
            doc = MotionEdits.SetKey(doc, layer.Key, frame, name, value.ToBone());
        }
        return doc;
    }

    /// <summary>The chain <see cref="Reach"/> bends to move <paramref name="bone"/>, and its bend's side at rest.</summary>
    public static (PinChain Chain, Vector3 RestNormal) Limb(SkeletonDescription skeleton, string bone)
    {
        var chain = PinChains.Chain(skeleton, new MotionPin(bone));
        return (chain, PinSolver.RestNormal(skeleton, chain));
    }

    /// <summary>The least weight a layer needs at the playhead for the gizmo to change its keys: below it the bone barely moves.</summary>
    public const float MinFollowWeight = .05f;

    /// <summary>
    /// A gizmo drag's turn and move in character space, made to a layer that applies its keys at
    /// <paramref name="weight"/> there (<see cref="MotionEvaluator.Weight"/>): scaled by its inverse, so the bone
    /// turns and moves as far as the gizmo did. Null where the layer barely applies.
    /// </summary>
    public static (Quaternion Turn, Vector3 Move)? Follow(Quaternion turn, Vector3 move, float weight) =>
        weight >= MinFollowWeight ? (MotionEvaluator.Pow(turn, 1 / weight), move / weight) : null;

    /// <summary>
    /// Keys <paramref name="pose"/> at <paramref name="frame"/> in a pose layer for each of <paramref name="bones"/> it
    /// has: the bone's rotation, and with <paramref name="positions"/> its position where the pose moves it. The
    /// scale and the other positions stay as the layers before leave them (<paramref name="own"/>), so a pose never reshapes the body.
    /// </summary>
    public static TimelineDocument KeyPose(TimelineDocument doc, string layer, int frame, PoseImport pose, IEnumerable<string> bones, bool positions,
        Func<string, MotionTransform?> own)
    {
        foreach (var bone in bones)
        {
            if (!pose.Local.TryGetValue(bone, out var value))
                continue;
            var moved = positions && pose.Moved.Contains(bone);
            doc = MotionEdits.SetKey(doc, layer, frame, bone, PoseKey(value, own(bone), moved).ToBone(), moved);
        }
        return doc;
    }

    /// <summary>A pose's bone as <see cref="KeyPose"/> keys it: its rotation, its position when it moves, the scale the layers before leave.</summary>
    private static MotionTransform PoseKey(MotionTransform value, MotionTransform? clip, bool moved) =>
        new(moved || clip is not { } kept ? value.Position : kept.Position, value.Rotation, clip?.Scale ?? Vector3.One);

    /// <summary>
    /// The body's key that stands the pose <see cref="KeyPose"/> keys on the ground: its feet as low as in the
    /// pose the layers before leave there (<paramref name="own"/>, one local transform per bone of <paramref name="skeleton"/>).
    /// Null on a skeleton without a body bone or feet.
    /// </summary>
    public static MotionTransform? Grounded(SkeletonDescription skeleton, MotionTransform[] own, PoseImport pose, IEnumerable<string> bones, bool positions)
    {
        var posed = (MotionTransform[])own.Clone();
        var index = MotionEvaluator.Index(skeleton);
        foreach (var bone in bones)
            if (pose.Local.TryGetValue(bone, out var value) && index.TryGetValue(bone, out var at))
                posed[at] = PoseKey(value, own[at], positions && pose.Moved.Contains(bone));
        if (!index.TryGetValue(MotionHold.Body, out var body) || MotionHold.OnGround(skeleton, posed, own) is not { } place)
            return null;
        return posed[body] with { Position = place };
    }

    /// <summary>
    /// Bones a whole-body pose never keys: the skeleton's root nodes, and the face, which a body clip doesn't move. A
    /// face's own pose (<paramref name="face"/>, on a face skeleton) keys its face bones.
    /// </summary>
    public static bool Unposed(string bone, bool face = false) =>
        bone is "n_root" or "n_throw" or "n_hara_noanim_trans" || !face && bone.StartsWith("j_f_", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="skeleton"/> is a face's: face bones and no body.</summary>
    public static bool FaceSkeleton(SkeletonDescription skeleton) =>
        skeleton.Bones.All(bone => bone.Name != MotionHold.Body) && skeleton.Bones.Any(bone => bone.Name.StartsWith("j_f_", StringComparison.Ordinal));

    /// <summary>
    /// The bones a pose keys, of the ones it has (<paramref name="available"/>): the <paramref name="selected"/> ones, or
    /// with none given the whole body but its root nodes, the face and the groups in <paramref name="leftOut"/>; on a
    /// <paramref name="face"/>, the whole face.
    /// </summary>
    public static List<string> PoseBones(IEnumerable<string> available, IReadOnlyCollection<string>? selected, IEnumerable<PoseGroup> leftOut,
        bool face = false)
    {
        if (selected != null)
        {
            var chosen = selected.ToHashSet(StringComparer.Ordinal);
            return [.. available.Where(chosen.Contains)];
        }
        var groups = leftOut.ToArray();
        return [.. available.Where(bone => !Unposed(bone, face) && !groups.Any(group => group.Contains(bone)))];
    }

    /// <summary>Keys every target at <paramref name="frame"/> with the value it has there now.</summary>
    public static TimelineDocument Key(TimelineDocument doc, string layer, int frame, IReadOnlyList<PoseTarget> targets, bool pose)
    {
        foreach (var target in targets)
            doc = MotionEdits.SetKey(doc, layer, frame, target.Bone, target.Value.ToBone(), pose ? target.Moved : null);
        return doc;
    }

    /// <summary>
    /// Keys every bone at <paramref name="frame"/> back to the motion under the layer: no change for an offset, the
    /// transform the layers before leave (<paramref name="motion"/>) for a pose, which then leaves the position to them.
    /// Bones whose motion is unknown are left as they are.
    /// </summary>
    public static TimelineDocument Reset(TimelineDocument doc, string layer, int frame, IEnumerable<string> bones, bool pose,
        Func<string, MotionTransform?> motion)
    {
        foreach (var bone in bones)
        {
            if (!pose)
                doc = MotionEdits.SetKey(doc, layer, frame, bone, MotionTransform.Identity.ToBone());
            else if (motion(bone) is { } own)
                doc = MotionEdits.SetKey(doc, layer, frame, bone, own.ToBone(), false);
        }
        return doc;
    }

    /// <summary>Takes every bone out of the key at <paramref name="frame"/>.</summary>
    public static TimelineDocument Unkey(TimelineDocument doc, string layer, int frame, IEnumerable<string> bones)
    {
        foreach (var bone in bones)
            doc = MotionEdits.SetKey(doc, layer, frame, bone, null);
        return doc;
    }

    /// <summary>Takes every bone out of all of the layer's keys.</summary>
    public static TimelineDocument Clear(TimelineDocument doc, string layer, IEnumerable<string> bones)
    {
        foreach (var bone in bones)
            doc = MotionEdits.ClearBone(doc, layer, bone);
        return doc;
    }

    /// <summary>Sets whether the pose keys at <paramref name="frame"/> set the targets' positions, keying them where they are not keyed yet.</summary>
    public static TimelineDocument SetMoved(TimelineDocument doc, string layer, int frame, IReadOnlyList<PoseTarget> targets, bool moved)
    {
        foreach (var target in targets)
            doc = MotionEdits.SetKey(doc, layer, frame, target.Bone, target.Value.ToBone(), moved);
        return doc;
    }

    /// <summary>
    /// The bones Mirror copies from and to: each selected bone with a twin on the other side that the
    /// skeleton has. When both of a pair are selected, the one on the primary bone's side is copied, or
    /// the left one when the primary is in the middle.
    /// </summary>
    public static List<(string From, string To)> MirrorPairs(IReadOnlyList<string> bones, string? primary, Func<string, bool> exists)
    {
        var selected = bones.ToHashSet(StringComparer.Ordinal);
        var side = primary == null || PoseMap.Side(primary) == '\0' ? 'l' : PoseMap.Side(primary);
        var pairs = new List<(string, string)>();
        foreach (var bone in bones)
        {
            if (PoseMap.Mirror(bone) is not { } twin || !exists(twin) || selected.Contains(twin) && PoseMap.Side(bone) != side)
                continue;
            pairs.Add((bone, twin));
        }
        return pairs;
    }

    /// <summary>
    /// Gives each pair's other side the same edits, mirrored across the character's middle: every key
    /// of the bone in the layer, each read against the motion at its frame, replaces the twin's keys in
    /// the layer. The middle turns with the body: it is the skeleton's at rest, carried along by the
    /// nearest bone above both sides. <paramref name="localAt"/> gives the pose the layers before leave at a frame;
    /// null while it is unknown, which makes the result null too. Only offset and pose keys mirror: an
    /// amplitude has none, and a pin's hold spots, not turns.
    /// </summary>
    public static TimelineDocument? Mirror(TimelineDocument doc, string layerKey, IReadOnlyList<(string From, string To)> pairs,
        SkeletonDescription skeleton, Func<int, MotionTransform[]?> localAt)
    {
        if (MotionEdits.Find(doc, layerKey) is not { Kind: MotionLayerKind.Offset or MotionLayerKind.Pose } layer)
            return doc;
        var index = MotionEvaluator.Index(skeleton);
        var rest = RestModel(skeleton);
        var poses = new Dictionary<int, (MotionTransform[] Local, MotionTransform[] Model)>();
        var mirrored = new List<(string Twin, List<(int Frame, MotionTransform Value, bool Moved)> Keys)>();
        foreach (var (from, to) in pairs)
        {
            if (!index.TryGetValue(from, out var bone) || !index.TryGetValue(to, out var twin))
                continue;
            var keys = new List<(int, MotionTransform, bool)>();
            foreach (var key in layer.Keys)
            {
                if (!key.Bones.TryGetValue(from, out var stored))
                    continue;
                if (!poses.TryGetValue(key.Frame, out var pose))
                {
                    if (localAt(key.Frame) is not { } local || local.Length != skeleton.Bones.Length)
                        return null;
                    poses[key.Frame] = pose = (local, MotionEvaluator.ModelSpace(skeleton, local));
                }
                var body = Body(skeleton, pose.Model, rest, bone, twin);
                var value = MotionTransform.From(stored);
                var result = layer.Kind == MotionLayerKind.Offset
                    ? MirrorOffset(value, pose.Model[bone].Rotation, pose.Model[twin].Rotation, body)
                    : MirrorPose(value, pose.Local[bone], ParentRotation(skeleton, pose.Model, bone),
                        pose.Local[twin], ParentRotation(skeleton, pose.Model, twin), body);
                keys.Add((key.Frame, result, key.Moved.Contains(from)));
            }
            if (keys.Count > 0)
                mirrored.Add((to, keys));
        }
        var sets = layer.Kind == MotionLayerKind.Pose;
        foreach (var (twin, keys) in mirrored)
        {
            doc = MotionEdits.ClearBone(doc, layerKey, twin);
            foreach (var (frame, value, moved) in keys)
                doc = MotionEdits.SetKey(doc, layerKey, frame, twin, value.ToBone(), sets ? moved : null);
        }
        return doc;
    }

    /// <summary>
    /// Keys <paramref name="copy"/>'s bones at <paramref name="frame"/> in a layer of the kind they were copied from,
    /// as one edit. With <paramref name="mirrored"/> each goes to its twin (a middle bone onto itself), mirrored. A pose
    /// key becomes the copied pose's mirror image: its change from the skeleton at rest, mirrored onto the twin at rest,
    /// whatever the motion does here. An offset is read against the pose the layers before leave here
    /// (<paramref name="local"/>), as <see cref="Mirror"/> does; null when that pose is unknown. Bones the skeleton lacks,
    /// or for a mirrored paste whose twin it lacks, are left out and counted in <paramref name="skipped"/>.
    /// </summary>
    public static TimelineDocument? Paste(TimelineDocument doc, string layerKey, int frame, PoseCopy copy, bool mirrored,
        SkeletonDescription skeleton, MotionTransform[]? local, out int skipped)
    {
        skipped = 0;
        if (MotionEdits.Find(doc, layerKey) is not { Kind: MotionLayerKind.Offset or MotionLayerKind.Pose } layer || layer.Kind != copy.Kind)
            return doc;
        var pose = layer.Kind == MotionLayerKind.Pose;
        if (mirrored && !pose && (local == null || local.Length != skeleton.Bones.Length))
            return null;
        var index = MotionEvaluator.Index(skeleton);
        MotionTransform[] restLocal = mirrored ? RestLocal(skeleton) : [], rest = mirrored ? MotionEvaluator.ModelSpace(skeleton, restLocal) : [];
        MotionTransform[] model = mirrored && !pose ? MotionEvaluator.ModelSpace(skeleton, local!) : [];
        foreach (var (bone, value, moved) in copy.Bones)
        {
            var to = mirrored ? MotionMirror.Twin(bone) ?? bone : bone;
            var from = -1;
            if (!index.TryGetValue(to, out var twin) || mirrored && !index.TryGetValue(bone, out from))
            {
                skipped++;
                continue;
            }
            var result = value;
            if (mirrored)
                result = pose
                    ? MirrorPose(value, restLocal[from], ParentRotation(skeleton, rest, from), restLocal[twin], ParentRotation(skeleton, rest, twin),
                        Quaternion.Identity)
                    : MirrorOffset(value, model[from].Rotation, model[twin].Rotation, Body(skeleton, model, rest, from, twin));
            doc = MotionEdits.SetKey(doc, layerKey, frame, to, result.ToBone(), pose ? moved : null);
        }
        return doc;
    }

    /// <summary>The lowest pose layer that is on: it applies last, so a pose pasted into it shows as copied.</summary>
    public static MotionLayer? LowestPose(TimelineDocument doc) =>
        doc.Motion.LastOrDefault(layer => layer.Kind == MotionLayerKind.Pose && layer.Enabled);

    /// <summary>
    /// Every bone a whole-body pose keys as <paramref name="through"/> leaves it (one local transform per bone of
    /// <paramref name="skeleton"/>, the frames through the lowest pose <paramref name="layer"/>), for Paste pose.
    /// Only the bones in <paramref name="moving"/> (the clip's tracks and what the layers move, see
    /// <see cref="MotionSymmetry.Animated"/>), so a big skeleton's still and physics bones stay unkeyed; null takes every bone.
    /// Each sets its position where that layer's key at <paramref name="frame"/> does.
    /// </summary>
    public static PoseCopy Shown(MotionLayer layer, int frame, SkeletonDescription skeleton, MotionTransform[] through, IReadOnlySet<int>? moving) =>
        new(MotionLayerKind.Pose, [.. skeleton.Bones.Select((bone, at) => (bone.Name, at))
            .Where(bone => !Unposed(bone.Name, FaceSkeleton(skeleton)) && (moving == null || moving.Contains(bone.at)))
            .Select(bone => new PoseTarget(bone.Name, through[bone.at], Moved(layer, bone.Name, frame)))]);

    /// <summary><paramref name="copy"/> as one key for Paste keys (Ctrl+V): each bone's value, its position set where the copy moves it.</summary>
    public static KeyCopy AsKeys(PoseCopy copy) =>
        new(copy.Kind, [new MotionKey(0, copy.Bones.ToImmutableDictionary(bone => bone.Bone, bone => bone.Value.ToBone(), StringComparer.Ordinal),
            [.. copy.Bones.Where(bone => bone.Moved).Select(bone => bone.Bone)])]);

    private static MotionTransform[] RestLocal(SkeletonDescription skeleton) =>
        [.. skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference))];

    private static MotionTransform[] RestModel(SkeletonDescription skeleton) => MotionEvaluator.ModelSpace(skeleton, RestLocal(skeleton));

    /// <summary>How the bone above both sides is turned from rest in <paramref name="model"/>: the body's middle turns with it.</summary>
    private static Quaternion Body(SkeletonDescription skeleton, MotionTransform[] model, MotionTransform[] rest, int bone, int twin) =>
        CommonAncestor(skeleton, bone, twin) is var middle and >= 0
            ? Quaternion.Normalize(model[middle].Rotation * Quaternion.Inverse(Quaternion.Normalize(rest[middle].Rotation)))
            : Quaternion.Identity;

    /// <summary>The nearest bone above both <paramref name="a"/> and <paramref name="b"/>; -1 when they share none.</summary>
    public static int CommonAncestor(SkeletonDescription skeleton, int a, int b) => MotionMirror.CommonAncestor(skeleton, a, b);

    /// <summary>A rotation mirrored across the character's middle at rest (its YZ plane): turns about X stay, about Y and Z reverse.</summary>
    public static Quaternion MirrorRotation(Quaternion rotation) => MotionMirror.Rotation(rotation);

    /// <summary>A position or move mirrored across the character's middle at rest.</summary>
    public static Vector3 MirrorPosition(Vector3 position) => MotionMirror.Position(position);

    /// <summary>A turn in character space mirrored across the middle of a body turned <paramref name="body"/> from rest.</summary>
    public static Quaternion MirrorRotation(Quaternion rotation, Quaternion body) => MotionMirror.Rotation(rotation, body);

    /// <summary>A move in character space mirrored across the middle of a body turned <paramref name="body"/> from rest.</summary>
    public static Vector3 MirrorPosition(Vector3 position, Quaternion body) => MotionMirror.Position(position, body);

    /// <summary>
    /// An offset for the twin of a bone: the bone's turn as the character makes it (the bone's rotation in
    /// the motion is <paramref name="bone"/>), mirrored across the middle of the body (turned
    /// <paramref name="body"/> from rest), then in the twin's own axes (<paramref name="twin"/>). The move is
    /// in character space and mirrors the same way; the scale factor stays.
    /// </summary>
    public static MotionTransform MirrorOffset(MotionTransform value, Quaternion bone, Quaternion twin, Quaternion body)
    {
        bone = Quaternion.Normalize(bone);
        twin = Quaternion.Normalize(twin);
        var turn = bone * Quaternion.Normalize(value.Rotation) * Quaternion.Inverse(bone);
        var rotation = Quaternion.Inverse(twin) * MirrorRotation(turn, body) * twin;
        return new(MirrorPosition(value.Position, body), Quaternion.Normalize(rotation), value.Scale);
    }

    /// <summary>
    /// A pose key for the twin of a bone: the key's change from the transform under the layer
    /// (<paramref name="motion"/>, under a parent turned <paramref name="parent"/>), as the character makes
    /// it, mirrored across the middle of the body and made to the twin's own transform.
    /// </summary>
    public static MotionTransform MirrorPose(MotionTransform value, MotionTransform motion, Quaternion parent,
        MotionTransform twinMotion, Quaternion twinParent, Quaternion body)
    {
        parent = Quaternion.Normalize(parent);
        twinParent = Quaternion.Normalize(twinParent);
        var turn = parent * Quaternion.Normalize(value.Rotation) * Quaternion.Inverse(Quaternion.Normalize(motion.Rotation)) * Quaternion.Inverse(parent);
        var rotation = Quaternion.Inverse(twinParent) * MirrorRotation(turn, body) * twinParent * Quaternion.Normalize(twinMotion.Rotation);
        var move = Vector3.Transform(value.Position - motion.Position, parent);
        var position = twinMotion.Position + Vector3.Transform(MirrorPosition(move, body), Quaternion.Inverse(twinParent));
        return new(position, Quaternion.Normalize(rotation), ClampScale(twinMotion.Scale * Ratio(value.Scale, motion.Scale)));
    }

    /// <summary>The model rotation of a bone's parent in <paramref name="model"/>, or none for a root.</summary>
    public static Quaternion ParentRotation(SkeletonDescription skeleton, MotionTransform[] model, int bone) =>
        skeleton.Bones[bone].Parent is var parent and >= 0 ? model[parent].Rotation : Quaternion.Identity;

    private static Vector3 Ratio(Vector3 value, Vector3 by) => new(
        Math.Abs(by.X) < 1e-6f ? 1 : value.X / by.X,
        Math.Abs(by.Y) < 1e-6f ? 1 : value.Y / by.Y,
        Math.Abs(by.Z) < 1e-6f ? 1 : value.Z / by.Z);

    private static Vector3 ClampScale(Vector3 scale) =>
        Vector3.Clamp(scale, new Vector3(MotionEvaluator.MinScale), new Vector3(MotionEvaluator.MaxScale));
}
