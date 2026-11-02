using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// How a spring layer makes its bones follow the motion: about <paramref name="Lag"/> frames behind it, and with
/// <paramref name="Bounce"/> from 0 (settling without overshoot) toward 1 overshooting and swinging back before it settles.
/// </summary>
internal sealed record MotionSpring(float Lag = 4, float Bounce = 0.3f);

/// <summary>
/// Spring layers: their settings checked, and their bones' turns sprung over the clip, each following the motion under it
/// from its parent as the spring left it. Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionSprings
{
    /// <summary>The shortest and longest Lag (frames) and the most Bounce.</summary>
    public const float MinLag = 0.5f, MaxLag = 30, MaxBounce = 0.95f;

    // The longest substep, in seconds, and the most of the spring's own turn rate one may take, so the stiffest spring stays stable.
    private const float MaxStep = 1 / 120f, MaxStiffStep = .5f;

    // A wrapping layer runs the loop again until its seam moves less than this between runs (radians), at most so many times.
    private const float SeamSettled = .05f * MathF.PI / 180;
    private const int MaxPasses = 4;

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    public static void Validate(MotionSpring spring, string name)
    {
        if (!float.IsFinite(spring.Lag) || spring.Lag < MinLag || spring.Lag > MaxLag ||
            !float.IsFinite(spring.Bounce) || spring.Bounce < 0 || spring.Bounce > MaxBounce)
            throw new InvalidDataException($"{name} has a spring out of range.");
    }

    /// <summary>
    /// The spring's stiffness and damping: its turn rate ω (radians a second), so a spring that doesn't bounce follows a steady turn
    /// about Lag frames behind, and its damping ratio ζ = 1 - Bounce (1 never overshoots).
    /// </summary>
    public static (float Omega, float Zeta) Constants(MotionSpring spring) => (2 / (spring.Lag / TimelineDocument.Fps), 1 - spring.Bounce);

    /// <summary>
    /// How far the sprung result applies at <paramref name="frame"/> before the layer's weight: 1, but on a layer that ends
    /// before the motion does it eases back onto the motion on a smoothstep over its last <c>ceil(Lag)</c> frames, so a layer
    /// without a fade-out doesn't pop where it ends.
    /// </summary>
    public static float EndEase(MotionLayer layer, float frame, float end)
    {
        if (layer.Spring is not { } spring || MotionEvaluator.ReachesEnd(layer, end))
            return 1;
        var frames = MathF.Ceiling(spring.Lag);
        var t = (layer.End - frame) / frames;
        return t >= 1 ? 1 : AnimationPoseRules.SmoothStep(Math.Clamp(t, 0, 1));
    }

    /// <summary>
    /// Springs the layer's bones over the samples inside it. Each bone, parents first, has a turn in model space and a turn rate:
    /// at the layer's first sample it sits on its goal, still, and from there it is pulled toward its goal, the turn its parent
    /// has (as the spring left a sprung parent) times its own turn in the motion, and slowed by its damping, in substeps of at
    /// most 1/120 s. Its local turn is then its parent's undone from that, blended from the motion by the layer's weight. Bones
    /// under a sprung bone that aren't sprung keep their own turn and ride along. On a loop the layer covers, the loop runs again
    /// with the spring as the last run left it at the seam, until the seam settles (at most 4 runs). Every value is read from
    /// the motion before the layer; a bone left where it was keeps its sample, so no track is made from rounding.
    /// </summary>
    internal static void Apply(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float loop, float end, CancellationToken token)
    {
        if (layer.Spring is not { } spring || layer.Bones.IsEmpty || clip.Samples < 2)
            return;
        var sprung = layer.Bones.Select(name => index.TryGetValue(name, out var bone) ? bone : throw MotionEvaluator.Missing(name)).Distinct().Order().ToArray();
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
            weights[i] = MotionEvaluator.Weight(layer, frame, end) * EndEase(layer, frame, end);
            any |= weights[i] > 0;
        }
        if (!any)
            return;

        var (omega, zeta) = Constants(spring);
        var bones = clip.Skeleton.Bones;
        var top = sprung[^1];
        // Which slot of the state each bone has, -1 for one that isn't sprung.
        var slot = Enumerable.Repeat(-1, top + 1).ToArray();
        for (var s = 0; s < sprung.Length; s++)
            slot[sprung[s]] = s;
        var state = new Quaternion[sprung.Length];
        var velocity = new Vector3[sprung.Length];
        var goal = new Quaternion[sprung.Length];
        var result = new Quaternion[(last - first + 1) * sprung.Length];
        var model = new Quaternion[top + 1];
        var wraps = MotionEvaluator.Wraps(layer, loop);
        Quaternion[]? seam = null;
        for (var pass = 0; pass < (wraps ? MaxPasses : 1); pass++)
        {
            for (var i = first; i <= last; i++)
            {
                if ((i & 63) == 0)
                    token.ThrowIfCancellationRequested();
                // The first sample starts settled; round a loop it carries on from the run before, whose last sample is its moment.
                var settle = i == first && pass == 0;
                var carry = i == first && pass > 0;
                var seconds = i > first ? clip.Time(i) - clip.Time(i - 1) : 0;
                var steps = seconds > 0 ? Math.Max((int)MathF.Ceiling(seconds / MaxStep), (int)MathF.Ceiling(omega * seconds / MaxStiffStep)) : 0;
                for (var b = 0; b <= top; b++)
                {
                    var parent = bones[b].Parent;
                    var parentModel = parent >= 0 ? model[parent] : Quaternion.Identity;
                    var local = Quaternion.Normalize(clip.Get(i, b).Rotation);
                    var s = slot[b];
                    if (s < 0)
                    {
                        model[b] = Quaternion.Normalize(parentModel * local);
                        continue;
                    }
                    var target = Quaternion.Normalize(parentModel * local);
                    if (settle)
                    {
                        state[s] = target;
                        velocity[s] = Vector3.Zero;
                    }
                    else if (!carry)
                        Step(ref state[s], ref velocity[s], goal[s], target, omega, zeta, (float)seconds / steps, steps);
                    goal[s] = target;
                    // The bone's own turn under its parent as the spring has it, blended from the motion by the layer's weight there.
                    var springLocal = Quaternion.Normalize(Quaternion.Inverse(parentModel) * state[s]);
                    var output = weights[i] > 0 ? MotionEvaluator.Slerp(local, springLocal, Math.Min(1, weights[i])) : local;
                    result[(i - first) * sprung.Length + s] = output;
                    model[b] = Quaternion.Normalize(parentModel * output);
                }
            }
            if (!wraps)
                break;
            // Round the loop the spring's state at the seam carries into the next run; once it settles the last run is kept.
            if (seam != null && seam.Zip(state).All(pair => Angle(pair.First, pair.Second) < SeamSettled))
                break;
            seam = (Quaternion[])state.Clone();
        }

        for (var i = first; i <= last; i++)
            foreach (var b in sprung)
            {
                var value = clip.Get(i, b);
                var output = result[(i - first) * sprung.Length + slot[b]];
                if (Angle(value.Rotation, output) > 1e-6f)
                    clip.Set(i, b, value with { Rotation = MotionEvaluator.Align(output, value.Rotation) });
            }
    }

    /// <summary>
    /// <paramref name="steps"/> substeps of <paramref name="dt"/> seconds of one bone's spring, semi-implicit Euler, its goal going
    /// from <paramref name="from"/> to <paramref name="to"/> along the way: the pull is ω² times the turn from the state to the goal
    /// (the short way, as a rotation vector), less 2ζω times the turn rate.
    /// </summary>
    private static void Step(ref Quaternion state, ref Vector3 velocity, Quaternion from, Quaternion to, float omega, float zeta, float dt, int steps)
    {
        for (var k = 1; k <= steps; k++)
        {
            var goal = MotionEvaluator.Slerp(from, to, (float)k / steps);
            var error = Log(goal * Quaternion.Inverse(state));
            velocity += dt * (omega * omega * error - 2 * zeta * omega * velocity);
            state = Quaternion.Normalize(Exp(velocity * dt) * state);
        }
    }

    /// <summary>A turn as a rotation vector, the short way: its axis times its angle in radians.</summary>
    private static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0)
            q = -q;
        var v = new Vector3(q.X, q.Y, q.Z);
        var sin = v.Length();
        return sin < 1e-9f ? 2 * v : v * (2 * MathF.Atan2(sin, q.W) / sin);
    }

    /// <summary>The turn of a rotation vector.</summary>
    private static Quaternion Exp(Vector3 turn)
    {
        var angle = turn.Length();
        if (angle < 1e-9f)
            return Quaternion.Normalize(new Quaternion(turn / 2, 1));
        return Quaternion.CreateFromAxisAngle(turn / angle, angle);
    }

    /// <summary>The angle between two turns, in radians, the short way; read from the turn's vector part, which floats keep precise near 0.</summary>
    private static float Angle(Quaternion a, Quaternion b)
    {
        var turn = Quaternion.Inverse(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        return 2 * MathF.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), Math.Abs(turn.W));
    }
}
