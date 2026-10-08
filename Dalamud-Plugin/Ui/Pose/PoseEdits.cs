using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui.Pose;

/// <summary>A selected bone with its value in the layer at the playhead and whether its key there sets its position.</summary>
internal readonly record struct PoseTarget(string Bone, MotionTransform Value, bool Moved);

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
    /// covers), else no change for an offset, else <paramref name="motion"/>, the clip's own transform, for
    /// a pose. Null while that is unknown.
    /// </summary>
    public static MotionTransform? Value(MotionLayer layer, string bone, int frame, MotionTransform? motion, float loop = 0) =>
        MotionEvaluator.KeyValue(layer, bone, frame, loop) ?? (layer.Kind == MotionLayerKind.Pose ? motion : MotionTransform.Identity);

    /// <summary>Whether the key at <paramref name="frame"/> sets <paramref name="bone"/>'s position: a pose key otherwise only turns it.</summary>
    public static bool Moved(MotionLayer layer, string bone, int frame) =>
        MotionEdits.KeyIndex(layer, frame) is var at and >= 0 && layer.Keys[at].Moved.Contains(bone);

    /// <summary>Whether a key at <paramref name="frame"/> has <paramref name="bone"/>.</summary>
    public static bool Keyed(MotionLayer layer, string bone, int frame) =>
        MotionEdits.KeyIndex(layer, frame) is var at and >= 0 && layer.Keys[at].Bones.ContainsKey(bone);

    /// <summary>Whether any key of the layer has <paramref name="bone"/>, or for an amplitude whether it is limited to it.</summary>
    public static bool Changes(MotionLayer layer, string bone) => layer.Kind == MotionLayerKind.Amplitude
        ? layer.Bones.Contains(bone)
        : layer.Keys.Any(key => key.Bones.ContainsKey(bone));

    /// <summary>
    /// The selected bones an edit turns, moves and scales: the primary bone and every other selected bone
    /// with no selected bone above it. The bones above the primary one stay out, and a bone below a
    /// changed one follows it rather than turning twice.
    /// </summary>
    public static List<PoseTarget> Turned(IReadOnlyList<PoseTarget> targets, string primary, SkeletonDescription skeleton)
    {
        var index = MotionEvaluator.Index(skeleton);
        var chosen = new HashSet<int>();
        foreach (var target in targets)
            if (index.TryGetValue(target.Bone, out var at))
                chosen.Add(at);
        if (index.TryGetValue(primary, out var first))
            for (var above = skeleton.Bones[first].Parent; above >= 0; above = skeleton.Bones[above].Parent)
                chosen.Remove(above);
        var turned = new List<PoseTarget>();
        foreach (var target in targets)
        {
            if (!index.TryGetValue(target.Bone, out var at) || !chosen.Contains(at))
                continue;
            var follows = false;
            for (var above = skeleton.Bones[at].Parent; above >= 0 && !follows; above = skeleton.Bones[above].Parent)
                follows = chosen.Contains(above);
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
    /// bones' positions; null leaves each bone's as it is.
    /// </summary>
    public static TimelineDocument Set(TimelineDocument doc, string layer, int frame, PoseTarget primary, MotionTransform value,
        IReadOnlyList<PoseTarget> others, Func<string, Quaternion> space, bool offset, bool? moved = null)
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
            var own = Quaternion.Normalize(space(other.Bone));
            var changed = new MotionTransform(
                other.Value.Position + (offset ? move : Vector3.Transform(move, Quaternion.Inverse(own))),
                Quaternion.Normalize(Quaternion.Inverse(own) * turn * own * Quaternion.Normalize(other.Value.Rotation)),
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
    /// Keys <paramref name="pose"/> at <paramref name="frame"/> in a pose layer for each of <paramref name="bones"/> it
    /// has: the bone's rotation, and with <paramref name="positions"/> its position where the pose moves it. The
    /// scale and the other positions stay the clip's own (<paramref name="own"/>), so a pose never reshapes the body.
    /// </summary>
    public static TimelineDocument KeyPose(TimelineDocument doc, string layer, int frame, PoseImport pose, IEnumerable<string> bones, bool positions,
        Func<string, MotionTransform?> own)
    {
        foreach (var bone in bones)
        {
            if (!pose.Local.TryGetValue(bone, out var value))
                continue;
            var clip = own(bone);
            var moved = positions && pose.Moved.Contains(bone);
            var key = new MotionTransform(moved || clip is not { } kept ? value.Position : kept.Position, value.Rotation, clip?.Scale ?? Vector3.One);
            doc = MotionEdits.SetKey(doc, layer, frame, bone, key.ToBone(), moved);
        }
        return doc;
    }

    /// <summary>Bones a whole-body pose never keys: the skeleton's root nodes, and the face, which a body clip doesn't move.</summary>
    public static bool Unposed(string bone) =>
        bone is "n_root" or "n_throw" or "n_hara_noanim_trans" || bone.StartsWith("j_f_", StringComparison.Ordinal);

    /// <summary>
    /// The bones a pose keys, of the ones it has (<paramref name="available"/>): the <paramref name="selected"/> ones, or
    /// with none given the whole body but its root nodes, the face and the groups in <paramref name="leftOut"/>.
    /// </summary>
    public static List<string> PoseBones(IEnumerable<string> available, IReadOnlyCollection<string>? selected, IEnumerable<PoseGroup> leftOut)
    {
        if (selected != null)
        {
            var chosen = selected.ToHashSet(StringComparer.Ordinal);
            return [.. available.Where(chosen.Contains)];
        }
        var groups = leftOut.ToArray();
        return [.. available.Where(bone => !Unposed(bone) && !groups.Any(group => group.Contains(bone)))];
    }

    /// <summary>Keys every target at <paramref name="frame"/> with the value it has there now.</summary>
    public static TimelineDocument Key(TimelineDocument doc, string layer, int frame, IReadOnlyList<PoseTarget> targets, bool pose)
    {
        foreach (var target in targets)
            doc = MotionEdits.SetKey(doc, layer, frame, target.Bone, target.Value.ToBone(), pose ? target.Moved : null);
        return doc;
    }

    /// <summary>
    /// Keys every bone at <paramref name="frame"/> back to the motion's own: no change for an offset, the
    /// clip's own transform (<paramref name="motion"/>) for a pose, which then leaves the position to the clip.
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
    /// nearest bone above both sides. <paramref name="localAt"/> gives the clip's own pose at a frame;
    /// null while it is unknown, which makes the result null too. Amplitude layers have no keys to mirror.
    /// </summary>
    public static TimelineDocument? Mirror(TimelineDocument doc, string layerKey, IReadOnlyList<(string From, string To)> pairs,
        SkeletonDescription skeleton, Func<int, MotionTransform[]?> localAt)
    {
        if (MotionEdits.Find(doc, layerKey) is not { Kind: not MotionLayerKind.Amplitude } layer)
            return doc;
        var index = MotionEvaluator.Index(skeleton);
        var rest = MotionEvaluator.ModelSpace(skeleton, skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray());
        var poses = new Dictionary<int, (MotionTransform[] Local, MotionTransform[] Model)>();
        var mirrored = new List<(string Twin, List<(int Frame, MotionTransform Value, bool Moved)> Keys)>();
        foreach (var (from, to) in pairs)
        {
            if (!index.TryGetValue(from, out var bone) || !index.TryGetValue(to, out var twin))
                continue;
            var middle = CommonAncestor(skeleton, bone, twin);
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
                // How the bone above both sides is turned from rest: the body's middle turns with it.
                var body = middle < 0 ? Quaternion.Identity
                    : Quaternion.Normalize(pose.Model[middle].Rotation * Quaternion.Inverse(Quaternion.Normalize(rest[middle].Rotation)));
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

    /// <summary>The nearest bone above both <paramref name="a"/> and <paramref name="b"/>; -1 when they share none.</summary>
    public static int CommonAncestor(SkeletonDescription skeleton, int a, int b)
    {
        var above = new HashSet<int>();
        for (var bone = skeleton.Bones[a].Parent; bone >= 0; bone = skeleton.Bones[bone].Parent)
            above.Add(bone);
        for (var bone = skeleton.Bones[b].Parent; bone >= 0; bone = skeleton.Bones[bone].Parent)
            if (above.Contains(bone))
                return bone;
        return -1;
    }

    /// <summary>A rotation mirrored across the character's middle at rest (its YZ plane): turns about X stay, about Y and Z reverse.</summary>
    public static Quaternion MirrorRotation(Quaternion rotation) => new(rotation.X, -rotation.Y, -rotation.Z, rotation.W);

    /// <summary>A position or move mirrored across the character's middle at rest.</summary>
    public static Vector3 MirrorPosition(Vector3 position) => new(-position.X, position.Y, position.Z);

    /// <summary>A turn in character space mirrored across the middle of a body turned <paramref name="body"/> from rest.</summary>
    public static Quaternion MirrorRotation(Quaternion rotation, Quaternion body) =>
        Quaternion.Normalize(body * MirrorRotation(Quaternion.Inverse(body) * rotation * body) * Quaternion.Inverse(body));

    /// <summary>A move in character space mirrored across the middle of a body turned <paramref name="body"/> from rest.</summary>
    public static Vector3 MirrorPosition(Vector3 position, Quaternion body) =>
        Vector3.Transform(MirrorPosition(Vector3.Transform(position, Quaternion.Inverse(body))), body);

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
    /// A pose key for the twin of a bone: the key's change from the clip's own transform
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
