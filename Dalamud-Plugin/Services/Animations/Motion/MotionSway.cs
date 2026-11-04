using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// How a sway layer sways its bones: the top bone of each chain swings <paramref name="Count"/> times back and forth over the layer,
/// <paramref name="Angle"/> degrees each way toward <paramref name="Direction"/> degrees in the floor plane (0 side to side, 90 front to
/// back), and the bones under it follow with momentum. <paramref name="Stiffness"/> (0.05 to 1) is how firmly each bone keeps the shape
/// the motion gives it, <paramref name="Bounce"/> (0 to 0.95) how freely it swings on before settling and <paramref name="Hang"/>
/// (0 to 1) how much it hangs.
/// </summary>
internal sealed record MotionSway(int Count = 2, float Direction = 0, float Angle = 15, float Stiffness = 0.4f, float Bounce = 0.3f, float Hang = 0.1f);

/// <summary>
/// Sway layers: their settings checked, and their bones swayed over the clip as a small chain of weighted tips, each pulled toward
/// where the motion under it has it, the top of each chain swung by the layer. Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionSways
{
    /// <summary>The most swings, the largest Angle (degrees), the softest Stiffness and the most Bounce.</summary>
    public const int MaxCount = 64;
    public const float MaxAngle = 60, MinStiffness = .05f, MaxBounce = .95f;

    // The longest substep, in seconds, and the most of the stiffest bone's own turn rate one may take, so it stays stable.
    private const float MaxStep = 1 / 120f, MaxStiffStep = .5f;

    // A wrapping layer runs the loop again until every tip moves less than this between runs (metres), at most so many times.
    private const float SeamSettled = .05e-3f;
    private const int MaxPasses = 8;

    // Hang 1 pulls a level segment down by this share of its length, held against the bone's own stiffness.
    private const float HangPull = .707f;

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    public static void Validate(MotionSway sway, string name)
    {
        if (sway.Count is < 1 or > MaxCount || !In(sway.Direction, -180, 180) || !In(sway.Angle, 0, MaxAngle) || !In(sway.Stiffness, MinStiffness, 1) ||
            !In(sway.Bounce, 0, MaxBounce) || !In(sway.Hang, 0, 1))
            throw new InvalidDataException($"{name} has a sway out of range.");
    }

    private static bool In(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;

    /// <summary>The top bones' swing rate in swings a second: 0.5 × 12^Stiffness, from about 0.6 at the softest to 6 at the stiffest.</summary>
    public static float Frequency(MotionSway sway) => .5f * MathF.Pow(12, sway.Stiffness);

    /// <summary>
    /// The top bones' turn rate ω (radians a second, 2π times <see cref="Frequency"/>) and the damping ratio ζ = 1 - Bounce (1 never
    /// overshoots). A bone further down its chain is softer: the deepest has half the top's rate.
    /// </summary>
    public static (float Omega, float Zeta) Constants(MotionSway sway) => (2 * MathF.PI * Frequency(sway), 1 - sway.Bounce);

    /// <summary>
    /// How far the swayed result applies at <paramref name="frame"/> before the layer's weight: 1, but on a layer that ends before
    /// the motion does, or reaches a loop's end (<paramref name="loop"/>) without wrapping round it, it eases back onto the motion on a
    /// smoothstep over one swing at the top bones' rate, so a layer without a fade-out doesn't pop where it ends or at the seam.
    /// </summary>
    public static float EndEase(MotionLayer layer, float frame, float end, float loop)
    {
        if (layer.Sway is not { } sway)
            return 1;
        var reaches = MotionEvaluator.ReachesEnd(layer, end);
        if (reaches && (loop <= 0 || MotionEvaluator.Wraps(layer, loop)))
            return 1;
        var frames = MathF.Ceiling(TimelineDocument.Fps / Frequency(sway));
        var t = ((reaches ? Math.Min(layer.End, end) : layer.End) - frame) / frames;
        return t >= 1 ? 1 : AnimationPoseRules.SmoothStep(Math.Clamp(t, 0, 1));
    }

    /// <summary>
    /// Sways the layer's bones over the samples inside it. A top bone, one whose parent isn't swayed, starts a chain of the swayed
    /// bones under it. Each bone's segment runs from it to its tip: its swayed child, else its first child, else as far on along
    /// its own direction as it is from its parent. Each tip is a weight pulled toward where the bone's goal turn puts it, slowed by
    /// its damping and pulled down by Hang, with the segment's length kept, in substeps of at most 1/120 s, parents first. A top
    /// bone's goal is its turn in the motion swung by the layer, its head where the motion has it; a bone under it takes its turn
    /// in the motion under its parent as swayed, its head on its parent's tip. Its local turn is then its parent's undone from the
    /// swayed one, blended from the motion by the layer's weight; only turns are written, and bones under it that aren't swayed
    /// ride along. On a loop the layer covers, the loop runs again with the tips as the last run left them at the seam, until
    /// every tip moves less than 0.05 mm (at most 8 runs). Every value is read from the motion before the layer; a bone left
    /// where it was keeps its sample, so no track is made from rounding.
    /// </summary>
    internal static void Apply(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float loop, float end, CancellationToken token)
    {
        if (layer.Sway is not { } sway || layer.Bones.IsEmpty || clip.Samples < 2)
            return;
        var swayed = layer.Bones.Select(name => index.TryGetValue(name, out var bone) ? bone : throw MotionEvaluator.Missing(name)).Distinct().Order().ToArray();
        int first = -1, last = -1;
        for (var i = 0; i < clip.Samples; i++)
            if (MotionEvaluator.Inside(layer, clip.Frame(i), end))
            {
                if (first < 0) first = i;
                last = i;
            }
        if (first < 0)
            return;
        var weights = new float[clip.Samples];
        var any = false;
        for (var i = first; i <= last; i++)
        {
            var frame = clip.Frame(i);
            weights[i] = MotionEvaluator.Weight(layer, frame, end) * EndEase(layer, frame, end, loop);
            any |= weights[i] > 0;
        }
        if (!any)
            return;

        var bones = clip.Skeleton.Bones;
        var count = swayed.Length;
        // Which slot each bone has, -1 for one that isn't swayed; each slot's swayed parent, its tip bone and its depth in its chain.
        var slot = Enumerable.Repeat(-1, bones.Length).ToArray();
        for (var s = 0; s < count; s++)
            slot[swayed[s]] = s;
        var parentSlot = new int[count];
        var depth = new int[count];
        var top = new int[count];
        var tip = Enumerable.Repeat(-1, count).ToArray();
        for (var s = 0; s < count; s++)
        {
            var parent = bones[swayed[s]].Parent;
            parentSlot[s] = parent >= 0 ? slot[parent] : -1;
            depth[s] = parentSlot[s] < 0 ? 0 : depth[parentSlot[s]] + 1;
            top[s] = parentSlot[s] < 0 ? s : top[parentSlot[s]];
        }
        // A swayed child is the tip; else the first child.
        var swayedTip = new bool[count];
        for (var c = 0; c < bones.Length; c++)
            if (bones[c].Parent is var p and >= 0 && slot[p] is var ps and >= 0 && !swayedTip[ps] && (tip[ps] < 0 || slot[c] >= 0))
            {
                tip[ps] = c;
                swayedTip[ps] = slot[c] >= 0;
            }
        var deepest = new int[count];
        for (var s = 0; s < count; s++)
            deepest[top[s]] = Math.Max(deepest[top[s]], depth[s]);
        var (omega, zeta) = Constants(sway);
        // The tip is half as stiff as the top of its chain.
        var rate = Enumerable.Range(0, count).Select(s => omega * (1 - .5f * depth[s] / Math.Max(1, deepest[top[s]]))).ToArray();
        int[][] chains = [.. Enumerable.Range(0, count).Where(s => top[s] == s).Select(t => Enumerable.Range(t, count - t).Where(s => top[s] == t).ToArray())];
        var radians = sway.Direction * MathF.PI / 180;
        var toward = new Vector3(MathF.Cos(radians), 0, MathF.Sin(radians));
        var angle = sway.Angle * MathF.PI / 180;

        // The motion at the sample before and at this one; a top bone's head and its turn in the motion, unswung.
        var (localRot, localPos, segment, head, turn) = (new Quaternion[count], new Vector3[count], new Vector3[count], new Vector3[count], new Quaternion[count]);
        var (lastRot, lastPos, lastSegment, lastHead, lastTurn) = (new Quaternion[count], new Vector3[count], new Vector3[count], new Vector3[count], new Quaternion[count]);
        // Each tip from its head and its speed, each bone's swayed turn and head, and its turn as the layer leaves it.
        var (offset, velocity, swayRot, swayHead, outModel) = (new Vector3[count], new Vector3[count], new Quaternion[count], new Vector3[count], new Quaternion[count]);
        var result = new Quaternion[(last - first + 1) * count];
        var wraps = MotionEvaluator.Wraps(layer, loop);
        Vector3[]? seam = null;
        for (var pass = 0; pass < (wraps ? MaxPasses : 1); pass++)
        {
            for (var i = first; i <= last; i++)
            {
                if ((i & 63) == 0)
                    token.ThrowIfCancellationRequested();
                // The first sample starts settled; round a loop it carries on from the run before, whose last sample is its moment.
                var settle = i == first && pass == 0;
                var carry = i == first && pass > 0;
                var frame = clip.Frame(i);
                var lastFrame = i > first ? clip.Frame(i - 1) : frame;
                var seconds = i > first ? clip.Time(i) - clip.Time(i - 1) : 0;
                var steps = seconds > 0 ? Math.Max((int)MathF.Ceiling(seconds / MaxStep), (int)MathF.Ceiling(omega * seconds / MaxStiffStep)) : 0;
                var row = (i - first) * count;
                foreach (var chain in chains)
                {
                    foreach (var s in chain)
                    {
                        var value = clip.Get(i, swayed[s]);
                        localRot[s] = Quaternion.Normalize(value.Rotation);
                        localPos[s] = value.Position;
                        segment[s] = tip[s] >= 0 ? clip.Get(i, tip[s]).Position : Vector3.Transform(value.Position, Quaternion.Inverse(localRot[s]));
                    }
                    // The top bone's parent as the layers so far leave it: the motion, or a swayed bone above it as this sample has it.
                    var t0 = chain[0];
                    var (parentRot, parentPos) = Model(clip, i, bones[swayed[t0]].Parent, slot, result, row);
                    head[t0] = parentPos + Vector3.Transform(localPos[t0], parentRot);
                    turn[t0] = Quaternion.Normalize(parentRot * localRot[t0]);
                    if (settle || carry)
                        foreach (var s in chain)
                            (lastRot[s], lastPos[s], lastSegment[s], lastHead[s], lastTurn[s]) = (localRot[s], localPos[s], segment[s], head[s], turn[s]);
                    if (settle)
                        Settle(chain, parentSlot, localRot, localPos, segment, head[t0], Drive(turn[t0], segment[t0], toward, angle, sway, layer, frame, loop, end) * turn[t0],
                            offset, velocity, swayRot, swayHead);
                    else if (!carry)
                        for (var k = 1; k <= steps; k++)
                        {
                            var t = (float)k / steps;
                            var drive = Drive(Between(lastTurn[t0], turn[t0], t), Between(lastSegment[t0], segment[t0], t), toward, angle, sway, layer,
                                lastFrame + (frame - lastFrame) * t, loop, end);
                            foreach (var s in chain)
                            {
                                var bone = Between(lastSegment[s], segment[s], t);
                                Vector3 at;
                                Quaternion goal;
                                if (parentSlot[s] < 0)
                                {
                                    at = Between(lastHead[s], head[s], t);
                                    goal = drive * Between(lastTurn[s], turn[s], t);
                                }
                                else
                                {
                                    var p = parentSlot[s];
                                    at = swayHead[p] + Vector3.Transform(Between(lastPos[s], localPos[s], t), swayRot[p]);
                                    goal = swayRot[p] * Between(lastRot[s], localRot[s], t);
                                }
                                Step(ref offset[s], ref velocity[s], ref swayRot[s], ref swayHead[s], at, Quaternion.Normalize(goal), bone, rate[s], zeta, sway.Hang,
                                    (float)seconds / steps);
                            }
                        }
                    foreach (var s in chain)
                    {
                        (lastRot[s], lastPos[s], lastSegment[s], lastHead[s], lastTurn[s]) = (localRot[s], localPos[s], segment[s], head[s], turn[s]);
                        // The bone's own turn under its parent as the layer leaves it, blended from the motion by the layer's weight there.
                        var parentOut = parentSlot[s] < 0 ? parentRot : outModel[parentSlot[s]];
                        var swayLocal = Quaternion.Normalize(Quaternion.Inverse(parentOut) * swayRot[s]);
                        var output = weights[i] > 0 ? MotionEvaluator.Slerp(localRot[s], swayLocal, Math.Min(1, weights[i])) : localRot[s];
                        result[row + s] = output;
                        outModel[s] = Quaternion.Normalize(parentOut * output);
                    }
                }
            }
            if (!wraps)
                break;
            // Round the loop the tips at the seam carry into the next run; once they settle the last run is kept.
            if (seam != null && seam.Zip(offset, swayHead).All(pair => Vector3.Distance(pair.First, pair.Second + pair.Third) < SeamSettled))
                break;
            seam = [.. offset.Zip(swayHead, (o, h) => o + h)];
        }

        for (var i = first; i <= last; i++)
            for (var s = 0; s < count; s++)
            {
                var value = clip.Get(i, swayed[s]);
                var output = result[(i - first) * count + s];
                if (Angle(value.Rotation, output) > 1e-6f)
                    clip.Set(i, swayed[s], value with { Rotation = MotionEvaluator.Align(output, value.Rotation) });
            }
    }

    /// <summary>
    /// The drive turn on a top bone at <paramref name="frame"/>: Angle times the swing's wave (<see cref="MotionSwings.Value(int, float, float, MotionLayer, float, float, float)"/>),
    /// about the axis that takes its segment, as <paramref name="turn"/> points it in the character's space, toward
    /// <paramref name="toward"/>; up when the segment points along it.
    /// </summary>
    private static Quaternion Drive(Quaternion turn, Vector3 segment, Vector3 toward, float angle, MotionSway sway, MotionLayer layer, float frame, float loop, float end)
    {
        var swing = angle * MotionSwings.Value(sway.Count, 0, 0, layer, frame, loop, end);
        if (swing == 0 || segment.LengthSquared() < 1e-12f)
            return Quaternion.Identity;
        var across = Vector3.Cross(Vector3.Normalize(Vector3.Transform(segment, turn)), toward);
        var axis = across.Length() < 1e-3f ? Vector3.UnitY : Vector3.Normalize(across);
        return Quaternion.CreateFromAxisAngle(axis, swing);
    }

    /// <summary>At the layer's first sample each tip sits on its goal, still: the top bone's <paramref name="goal"/>, the others the motion under it.</summary>
    private static void Settle(int[] chain, int[] parentSlot, Quaternion[] localRot, Vector3[] localPos, Vector3[] segment, Vector3 head, Quaternion goal,
        Vector3[] offset, Vector3[] velocity, Quaternion[] swayRot, Vector3[] swayHead)
    {
        foreach (var s in chain)
        {
            var p = parentSlot[s];
            var rot = p < 0 ? Quaternion.Normalize(goal) : Quaternion.Normalize(swayRot[p] * localRot[s]);
            swayHead[s] = p < 0 ? head : swayHead[p] + Vector3.Transform(localPos[s], swayRot[p]);
            swayRot[s] = rot;
            offset[s] = Vector3.Transform(segment[s], rot);
            velocity[s] = Vector3.Zero;
        }
    }

    /// <summary>
    /// One substep of <paramref name="dt"/> seconds of one bone, semi-implicit Euler: its head moves to <paramref name="head"/>, its tip
    /// is pulled toward where <paramref name="goal"/> puts the segment, ω² times the gap less 2ζω times its speed, and down by Hang;
    /// then the segment keeps its length, the tip's speed along it is dropped, and the bone turns the short way from its goal to the
    /// tip, so its twist stays the motion's.
    /// </summary>
    private static void Step(ref Vector3 offset, ref Vector3 velocity, ref Quaternion rot, ref Vector3 swayHead, Vector3 head, Quaternion goal, Vector3 segment,
        float omega, float zeta, float hang, float dt)
    {
        var length = segment.Length();
        if (length < 1e-6f)
        {
            // A bone without a segment has no tip to swing: it keeps its goal.
            (offset, velocity, rot, swayHead) = (Vector3.Zero, Vector3.Zero, goal, head);
            return;
        }
        var aim = Vector3.Transform(segment, goal);
        // Where the tip is, from the new head: the head's move leaves it behind.
        var from = offset - (head - swayHead);
        var gravity = new Vector3(0, -hang * omega * omega * length * HangPull, 0);
        velocity += dt * (omega * omega * (aim - from) - 2 * zeta * omega * velocity + gravity);
        from += dt * velocity;
        var reach = from.Length();
        var along = reach > 1e-9f ? from / reach : aim / length;
        // Only a segment that changed length is set back, so a still bone isn't moved by rounding.
        if (MathF.Abs(reach - length) > 1e-6f * length)
            from = along * length;
        velocity -= Vector3.Dot(velocity, along) * along;
        (offset, swayHead) = (from, head);
        rot = Quaternion.Normalize(PinSolver.Between(aim, from) * goal);
    }

    /// <summary>
    /// <paramref name="bone"/>'s turn and position in the character's space at <paramref name="sample"/>, its swayed ancestors as
    /// <paramref name="result"/> has them in <paramref name="row"/>; identity and the origin for none.
    /// </summary>
    private static (Quaternion Rotation, Vector3 Position) Model(MotionClip clip, int sample, int bone, int[] slot, Quaternion[] result, int row)
    {
        if (bone < 0)
            return (Quaternion.Identity, Vector3.Zero);
        var (rotation, position) = Model(clip, sample, clip.Skeleton.Bones[bone].Parent, slot, result, row);
        var local = clip.Get(sample, bone);
        var turn = slot[bone] >= 0 ? result[row + slot[bone]] : Quaternion.Normalize(local.Rotation);
        return (Quaternion.Normalize(rotation * turn), position + Vector3.Transform(local.Position, rotation));
    }

    /// <summary>From <paramref name="from"/> to <paramref name="to"/> at <paramref name="t"/>, exactly <paramref name="to"/> when they are the same.</summary>
    private static Vector3 Between(Vector3 from, Vector3 to, float t) => from == to ? to : from + (to - from) * t;

    private static Quaternion Between(Quaternion from, Quaternion to, float t) => from == to ? to : MotionEvaluator.Slerp(from, to, t);

    /// <summary>The angle between two turns, in radians, the short way; read from the turn's vector part, which floats keep precise near 0.</summary>
    private static float Angle(Quaternion a, Quaternion b)
    {
        var turn = Quaternion.Inverse(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        return 2 * MathF.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), Math.Abs(turn.W));
    }
}
