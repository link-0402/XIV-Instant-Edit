using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Previews;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>Hand shape fixes: the hand's data, its validation, journals, how a layer is added and the finger rig.</summary>
internal static class HandShapeFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Data(check, reject);
        Rig(check);
        Shapes(check);
        Evaluate(check, reject);
        Library(check, reject);
        Tips(check);
        Views(check);
    }

    private static void Views(Action<bool, string> check)
    {
        var layer = HandLayer(new MotionHand(MotionHandSide.Right, "fist"));
        check(TimelineWindowViews.LayerHint(layer) == "Set a hand's fingers to a shape." && TimelineWindowViews.HandShapeName == "Hand shape" &&
              TimelineWindowViews.LayerHint(layer with { Fix = new MotionFix(MotionFixKind.SmoothJitter) }) == TimelineWindowViews.SmoothJitterHint,
            "hand views: a hand layer's hint says what it does");
        check(TimelineWindowViews.LayerContent(layer) == "Right hand" && TimelineWindowViews.LayerContent(layer with { Fix = layer.Fix! with { Hand = layer.Fix.Hand! with { Side = MotionHandSide.Both } } }) == "Both hands",
            "hand views: a hand layer's summary names its hand");

        var skeleton = TestHand(false, false);
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, new AnimationResource("chara/a.pap", "a.pap", "hash")), skeleton, 0, "");
        var clip = new AnimationClip("chara/a.pap", "loop", 0, 0, 1, "s.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [.. Enumerable.Range(0, skeleton.Bones.Length).Select(b => (short)b)]));
        var doc = new TimelineDocument(40, 40, null, [], 1) with { Motion = [layer] };
        check(TimelineWindowViews.BonesProblem(doc with { ExcludedBones = ["j_naka_b_r"] }, clip, clip.Resolution?.Selected?.Skeleton, null) is { } problem && problem.StartsWith("Bone layers move", StringComparison.Ordinal) &&
              TimelineWindowViews.BonesProblem(doc with { ExcludedBones = ["j_te_r"] }, clip, clip.Resolution?.Selected?.Skeleton, null) == null,
            "hand views: a finger bone left out of the save stops it while a hand layer shapes that hand, the wrist doesn't");
    }

    private static void Library(Action<bool, string> check, Action<Action, string> reject)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-hand-library-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var warnings = new List<string>();
            var file = Path.Combine(temp, "HandShapes.json");
            var turns = Turns(3).SetItem("j_a1", Quaternion.Normalize(new Quaternion(.1f, .2f, .3f, .9f)));
            var library = new HandShapeLibrary(temp, warnings.Add);
            check(library.Names.Count == 0 && library.Get("Mine") == null, "library: a new library is empty");

            var saved = library.Save("  Mine ", turns, out var error);
            var again = new HandShapeLibrary(temp, warnings.Add);
            var back = again.Get("MINE");
            check(saved && error == null && again.Names.SequenceEqual(["Mine"]) && back != null && back.Count == turns.Count &&
                  turns.All(pair => back.TryGetValue(pair.Key, out var turn) && turn == pair.Value) && warnings.Count == 0,
                "library save: a saved shape is trimmed, kept in the file and read back equal by a new library, ignoring case");

            var refused = new[] { "", "   ", new string('a', 41), "Fist", "fist", "RELAXED", " Spread " }
                .Where(name => library.Save(name, turns, out var why) || string.IsNullOrEmpty(why)).ToArray();
            check(refused.Length == 0 && library.Names.SequenceEqual(["Mine"]), "library save: an empty, 41-character or built-in name is refused with an error" + Failed(string.Join('|', refused)));
            check(library.Save(new string('a', 40), turns, out _) && library.Delete(new string('a', 40)), "library save: a 40-character name is kept");
            check(!library.Save("Bad", turns.SetItem("j_a0", new Quaternion(float.NaN, 0, 0, 1)), out var nan) && nan != null &&
                  !library.Save("Bad", turns.SetItem("j_a0", Quaternion.Zero), out var zero) && zero != null &&
                  !library.Save("Bad", turns.SetItem("", Quaternion.Identity), out var unnamed) && unnamed != null &&
                  !library.Save("Bad", Turns(33), out var many) && many != null && !library.Save("Bad", ImmutableDictionary<string, Quaternion>.Empty, out var none) && none != null &&
                  library.Get("Bad") == null,
                "library save: turns that aren't finite, have no length or bone name, are empty or number past 32 are refused");

            check(library.Save("mine", Turns(2), out _) && library.Names.SequenceEqual(["mine"]) && library.Get("MINE")!.Count == 2 &&
                  new HandShapeLibrary(temp, warnings.Add).Names.SequenceEqual(["mine"]),
                "library save: saving 'mine' over 'Mine' keeps one shape, in memory and in the file");

            library.Save("zed", turns, out _);
            library.Save("Alpha", turns, out _);
            check(library.Names.SequenceEqual(["Alpha", "mine", "zed"]), "library names: sorted ignoring case");

            check(library.Delete("MINE") && !library.Delete("mine") && library.Get("mine") == null &&
                  new HandShapeLibrary(temp, warnings.Add).Names.SequenceEqual(["Alpha", "zed"]) &&
                  !JsonNode.Parse(File.ReadAllText(file))!.AsObject().ContainsKey("mine"),
                "library delete: a deleted shape leaves memory and the file, and deleting it again does nothing");
            check(new HandShapeLibrary(null).Save("Memory", turns, out _) && warnings.Count == 0, "library memory: without a folder a shape is kept in memory");

            File.WriteAllText(file, "{ not json");
            warnings.Clear();
            var broken = new HandShapeLibrary(temp, warnings.Add);
            check(broken.Names.Count == 0 && warnings.Count == 1 && warnings[0].StartsWith("The saved hand shapes could not be read", StringComparison.Ordinal),
                "library load: a file of bad JSON loads empty with one warning");
            check(File.Exists(file + ".bak") && File.ReadAllText(file + ".bak") == "{ not json", "library load: a file that didn't read is kept as .bak before a save replaces it");
            check(broken.Save("Fresh", turns, out _) && new HandShapeLibrary(temp, warnings.Add).Names.SequenceEqual(["Fresh"]),
                "library load: saving after a bad file writes a new one");

            const string good = "\"Good\":{\"j_a\":{\"X\":0,\"Y\":0,\"Z\":0,\"W\":1}}";
            foreach (var bad in new[]
                     {
                         "\"Bad\":{\"j_a\":{\"X\":\"NaN\",\"Y\":0,\"Z\":0,\"W\":1}}", "\"Bad\":{\"j_a\":{\"X\":0,\"Y\":0,\"Z\":0,\"W\":0}}",
                         "\"Bad\":{\"j_a\":5}", "\"Bad\":null", "\"Fist\":{\"j_a\":{\"X\":0,\"Y\":0,\"Z\":0,\"W\":1}}",
                         "\"good\":{\"j_a\":{\"X\":0,\"Y\":0,\"Z\":0,\"W\":1}}",
                     })
            {
                File.WriteAllText(file, "{" + good + "," + bad + "}");
                warnings.Clear();
                var partial = new HandShapeLibrary(temp, warnings.Add);
                check(partial.Names.SequenceEqual(["Good"]) && warnings.Count == 1, $"library load: a bad shape is skipped with one warning and the good one loads ({bad})");
            }
            File.WriteAllText(file, "[1,2]");
            warnings.Clear();
            check(new HandShapeLibrary(temp, warnings.Add).Names.Count == 0 && warnings.Count == 1, "library load: a file that isn't an object loads empty with one warning");
            File.Delete(file);
            Directory.CreateDirectory(file);
            warnings.Clear();
            var blocked = new HandShapeLibrary(temp, warnings.Add);
            check(blocked.Save("Blocked", turns, out _) && blocked.Get("Blocked") != null && warnings.Count >= 1,
                "library save: a file that can't be written warns and keeps the shape in memory");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void Evaluate(Action<bool, string> check, Action<Action, string> reject)
    {
        var skeleton = TestHand(false, false);
        var rig = HandRig.For(skeleton, MotionSide.Right)!;
        var fist = new MotionHand(MotionHandSide.Right, "fist");
        var targets = HandShapes.Targets(skeleton, rig, fist, false);
        var still = rig.Chains.Single(chain => chain.Finger == HandFinger.Little).Bones;
        var source = Wiggling(skeleton, rig);
        var layer = HandLayer(fist) with { End = 60 };
        var keep = layer with { Fix = layer.Fix! with { Hand = fist with { KeepMotion = true } } };

        var replaced = Applied(source, layer);
        string? off = null;
        for (var i = 0; i < source.Samples; i++)
            for (var b = 0; b < source.Bones; b++)
            {
                var (before, after) = (source.Get(i, b), replaced.Get(i, b));
                var exact = targets.TryGetValue(b, out var target)
                    ? Near(after.Rotation, target, 1e-5f) && after.Position == before.Position && after.Scale == before.Scale
                    : after == before;
                if (!exact)
                    off ??= $"sample {i} {skeleton.Bones[b].Name}";
            }
        check(targets.Count == 10 && off == null,
            "eval replace: a Fist at full amount sets every finger bone to its target on every sample; the wrist, other bones, positions and scales stay exact" + Failed(off));

        var half = Applied(source, layer with { Amount = .5f });
        string? unhalved = null;
        for (var i = 0; i < source.Samples; i++)
            foreach (var (b, target) in targets)
                if (!(MathF.Abs(Between(half.Get(i, b).Rotation, target) - Between(source.Get(i, b).Rotation, target) / 2) <= 1e-4f))
                    unhalved ??= $"sample {i} {skeleton.Bones[b].Name}";
        check(unhalved == null, "eval amount: at Amount .5 every finger bone turns halfway to its target" + Failed(unhalved));
        check(!MotionEvaluator.Active(layer with { Amount = 0 }) && Same(Applied(source, layer with { Amount = 0 }), source) &&
              !MotionEvaluator.Active(layer with { Components = PoseComponents.Position }) &&
              !MotionEvaluator.Active(layer with { Fix = new MotionFix(MotionFixKind.HandShape) }) && MotionEvaluator.Active(layer),
            "eval amount: at Amount 0, without rotation or without a hand the layer is inactive and changes nothing");

        var faded = Applied(source, layer with { FadeIn = 10 });
        check(Enumerable.Range(0, source.Bones).All(b => faded.Get(0, b) == source.Get(0, b)) &&
              targets.All(pair => Near(faded.Get(10, pair.Key).Rotation, pair.Value, 1e-5f)),
            "eval fade: with a 10-frame fade in, frame 0 is the input and frame 10 the target");

        var kept = Applied(source, keep);
        string? lost = null;
        foreach (var (b, target) in targets)
        {
            for (var i = 1; i < source.Samples; i++)
                if (!Near(Quaternion.Inverse(kept.Get(i - 1, b).Rotation) * kept.Get(i, b).Rotation,
                        Quaternion.Inverse(source.Get(i - 1, b).Rotation) * source.Get(i, b).Rotation, 1e-4f))
                    lost ??= $"sample {i} {skeleton.Bones[b].Name}";
            if (!Near(Average(kept, b), target, 1e-4f))
                lost ??= $"{skeleton.Bones[b].Name} average";
        }
        check(lost == null, "eval keep: Keep finger motion keeps each sample's change from the one before and puts the average on the target" + Failed(lost));
        check(still.All(b => Enumerable.Range(0, source.Samples).All(i => Near(kept.Get(i, b).Rotation, replaced.Get(i, b).Rotation, 1e-5f))) &&
              Enumerable.Range(0, source.Samples).All(i => Enumerable.Range(0, source.Bones).All(b => float.IsFinite(kept.Get(i, b).Rotation.LengthSquared()))),
            "eval keep: on a finger that holds still Keep finger motion is Replace, with no NaN");

        var fading = layer with { FadeIn = 10 };
        var fadingClip = Applied(source, fading);
        string? apart = null;
        foreach (var i in new[] { 5, 25 })
        {
            var at = MotionEvaluator.ApplyAt(skeleton, Sample(source, i), [fading], source.Frame(i), 0, 60);
            for (var b = 0; b < source.Bones; b++)
                if (!Near(at[b].Rotation, fadingClip.Get(i, b).Rotation, 1e-5f) || at[b].Position != fadingClip.Get(i, b).Position)
                    apart ??= $"frame {i} {skeleton.Bones[b].Name}";
        }
        check(apart == null && MotionEvaluator.ApplyAt(skeleton, Sample(source, 25), [keep], 25, 0, 60).SequenceEqual(Sample(source, 25)),
            "eval at: ApplyAt applies a Replace hand layer as Apply does at that frame and skips a Keep one" + Failed(apart));

        var both = layer with { Fix = layer.Fix! with { Hand = fist with { Side = MotionHandSide.Both } } };
        MotionClip? bothClip = null;
        try
        {
            MotionEvaluator.CheckBones([both], skeleton);
            bothClip = Applied(source, both);
        }
        catch (InvalidDataException) { }
        check(bothClip != null && Same(bothClip, replaced), "eval both: Both on a skeleton with only the right hand passes CheckBones, sets the right hand and throws nothing");

        var looping = new MotionClip(skeleton, source.Duration, source.Samples) { Loops = true };
        looping.CopyFrom(source);
        for (var b = 0; b < source.Bones; b++)
            looping.Set(source.Samples - 1, b, source.Get(0, b));
        var seams = new[] { layer, keep }.Select(item => Applied(looping, item)).ToArray();
        check(seams.All(clip => Enumerable.Range(0, clip.Bones).All(b => clip.Get(0, b).Position == clip.Get(clip.Samples - 1, b).Position &&
              Near(clip.Get(0, b).Rotation, clip.Get(clip.Samples - 1, b).Rotation, 1e-6f))),
            "eval loop: a looping clip's equal first and last samples stay equal, Replace and Keep");

        check(Same(Applied(source, layer with { Fix = layer.Fix! with { Hand = fist with { Tips = true } } }), replaced),
            "eval tips: Tips on a skeleton without tip bones gives the same result as without");
    }

    /// <summary>The hand clip with a finger bone wiggling 5 degrees each way at .5 Hz and moving a millimetre, the wrist turning and moving, the little finger still: 2 seconds, 61 samples.</summary>
    private static MotionClip Wiggling(SkeletonDescription skeleton, HandRig rig)
    {
        var still = rig.Chains.Single(chain => chain.Finger == HandFinger.Little).Bones;
        var poses = new List<BoneTransform[]>();
        for (var i = 0; i < 61; i++)
        {
            var t = i / 30f;
            var pose = skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray();
            pose[rig.Wrist] = new MotionTransform(pose[rig.Wrist].Position + new Vector3(0, .02f * MathF.Sin(t), 0),
                pose[rig.Wrist].Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, .35f * MathF.Sin(t)), pose[rig.Wrist].Scale);
            foreach (var chain in rig.Chains)
                for (var j = 0; j < chain.Bones.Length; j++)
                {
                    var b = chain.Bones[j];
                    if (still.Contains(b))
                        continue;
                    var angle = 5 * MathF.PI / 180 * MathF.Sin(MathF.PI * t + b);
                    pose[b] = new MotionTransform(pose[b].Position + new Vector3(0, .001f * MathF.Cos(t + b), 0),
                        Quaternion.Normalize(pose[b].Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(chain.BendAxes[j]), angle)), pose[b].Scale);
                }
            poses.Add(pose.Select(value => value.ToBone()).ToArray());
        }
        return MotionClip.FromPoses(skeleton, 2, poses);
    }

    private static MotionClip Applied(MotionClip source, MotionLayer layer)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, [layer]);
        return clip;
    }

    private static MotionTransform[] Sample(MotionClip clip, int i) => Enumerable.Range(0, clip.Bones).Select(b => clip.Get(i, b)).ToArray();

    private static bool Same(MotionClip a, MotionClip b) =>
        Enumerable.Range(0, a.Samples).All(i => Enumerable.Range(0, a.Bones).All(bone => a.Get(i, bone) == b.Get(i, bone)));

    /// <summary>The angle in radians between two rotations.</summary>
    private static float Between(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(MathF.Min(1, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)))));

    /// <summary>A bone's average rotation over the clip: on the first sample's hemisphere, summed and normalized.</summary>
    private static Quaternion Average(MotionClip clip, int bone)
    {
        var first = clip.Get(0, bone).Rotation;
        var sum = Vector4.Zero;
        for (var i = 0; i < clip.Samples; i++)
        {
            var q = MotionEvaluator.Align(clip.Get(i, bone).Rotation, first);
            sum += new Vector4(q.X, q.Y, q.Z, q.W);
        }
        return Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
    }

    private static void Shapes(Action<bool, string> check)
    {
        check(HandShapes.Order.SequenceEqual(["relaxed", "flat", "fist", "point", "grip", "cupped", "spread"]) && HandShapes.BuiltIn.Count == 7 &&
              HandShapes.Order.All(HandShapes.BuiltIn.ContainsKey) &&
              HandShapes.Order.Select(id => HandShapes.BuiltIn[id].Name).SequenceEqual(["Relaxed", "Flat", "Fist", "Point", "Grip", "Cupped", "Spread"]),
            "shape order: seven built-in shapes in the picker's order, each named");

        var hands = new[] { (TestHand(false, false), MotionSide.Right), (TestHand(false, true), MotionSide.Left), (TestHand(true, false), MotionSide.Right), (TestHand(true, true), MotionSide.Left) }
            .Select(item => (Skeleton: item.Item1, Rig: HandRig.For(item.Item1, item.Item2)!)).ToArray();
        var pairs = new[] { (Right: hands[0], Left: hands[1]), (Right: hands[2], Left: hands[3]) };
        var bools = new[] { false, true };
        static MotionHand Hand(string shape, float curl = 0, float spread = 0, ImmutableDictionary<string, Quaternion>? turns = null) =>
            new(MotionHandSide.Right, shape, curl, spread, Turns: turns);

        // A thumb along the middle finger gives no across axis; turning about it must not scale the bone.
        var unit = hands.Append((hands[0].Skeleton, Rig: hands[0].Rig with { AcrossAxis = Vector3.Zero }));
        var off = (from hand in unit from tips in bools from id in HandShapes.Order from curl in new[] { -1f, 0, 1 } from spread in new[] { -1f, 0, 1 }
                   from target in HandShapes.Targets(hand.Skeleton, hand.Rig, Hand(id, curl, spread), tips && hand.Rig.HasTips)
                   where !(MathF.Abs(target.Value.Length() - 1) <= 1e-5f)
                   select $"{hand.Rig.Side} {id} curl {curl} spread {spread}").FirstOrDefault();
        check(off == null, "shape unit: every target of every built-in at Curl and Spread -1, 0 and 1 has length 1, without an across axis too" + Failed(off));

        string? far = null;
        foreach (var (skeleton, rig) in hands)
            foreach (var tips in bools)
            {
                var point = MotionSymmetry.RestModel(skeleton)[rig.Wrist].Position + Palm(skeleton, rig).Palm * .04f;
                var fist = Posed(skeleton, HandShapes.Targets(skeleton, rig, Hand("fist"), tips && rig.HasTips));
                foreach (var chain in rig.Chains)
                    if (!(Vector3.Distance(End(skeleton, chain, fist), point) < Vector3.Distance(End(skeleton, chain, Turned(skeleton)), point)))
                        far ??= $"{rig.Side} tips {tips && rig.HasTips} {chain.Finger}";
            }
        check(far == null, "shape fist: every finger's end comes closer to the palm than at rest, with and without tips, on both hands" + Failed(far));

        var unflat = (from hand in hands from tips in bools
                      let targets = HandShapes.Targets(hand.Skeleton, hand.Rig, Hand("flat"), tips && hand.Rig.HasTips)
                      where targets.Count != hand.Rig.Chains.Sum(chain => chain.Bones.Length) ||
                            targets.Any(target => !Near(target.Value, hand.Skeleton.Bones[target.Key].Reference.Rotation, 1e-5f))
                      select $"{hand.Rig.Side} tips {tips && hand.Rig.HasTips}").FirstOrDefault();
        check(unflat == null, "shape flat: Flat sets every finger bone to its rest rotation" + Failed(unflat));

        string? unmirrored = null;
        foreach (var (r, l) in pairs)
            foreach (var tips in bools)
                foreach (var hand in HandShapes.Order.SelectMany(id => new[] { Hand(id), Hand(id, .5f, -.7f) }))
                {
                    var rightModel = MotionEvaluator.ModelSpace(r.Skeleton, Posed(r.Skeleton, HandShapes.Targets(r.Skeleton, r.Rig, hand, tips && r.Rig.HasTips)));
                    var leftModel = MotionEvaluator.ModelSpace(l.Skeleton, Posed(l.Skeleton, HandShapes.Targets(l.Skeleton, l.Rig, hand, tips && l.Rig.HasTips)));
                    foreach (var (rc, lc) in r.Rig.Chains.Zip(l.Rig.Chains))
                        for (var i = 0; i < rc.Bones.Length; i++)
                            if (!Near(leftModel[lc.Bones[i]].Rotation, MotionMirror.Rotation(rightModel[rc.Bones[i]].Rotation), 1e-4f))
                                unmirrored ??= $"{hand.Shape} curl {hand.Curl} tips {tips && r.Rig.HasTips} {rc.Finger} joint {i}";
                }
        check(unmirrored == null, "shape mirror: every built-in's left hand is the right's mirror" + Failed(unmirrored));

        string? unfolded = null;
        foreach (var (skeleton, rig) in hands[2..])
            foreach (var shape in new[] { "grip", "relaxed" })
            {
                var folded = HandShapes.Targets(skeleton, rig, Hand(shape), false);
                var tipped = HandShapes.Targets(skeleton, rig, Hand(shape), true);
                foreach (var chain in rig.Chains.Where(chain => chain.Finger != HandFinger.Thumb))
                {
                    float Sum(Dictionary<int, Quaternion> targets) => chain.Bones.Sum(bone => Angle(skeleton, bone, targets[bone]));
                    if (!(MathF.Abs(Sum(folded) - Sum(tipped)) <= 1) || !Near(folded[chain.Bones[2]], skeleton.Bones[chain.Bones[2]].Reference.Rotation, 1e-5f))
                        unfolded ??= $"{rig.Side} {shape} {chain.Finger}: {Sum(folded)} against {Sum(tipped)}";
                }
            }
        check(unfolded == null, "shape fold: without tips a Grip's or Relaxed's tip bend goes into the b bone, the same total, the tip at rest" + Failed(unfolded));

        string? past = null;
        foreach (var (skeleton, rig) in hands)
            foreach (var tips in bools)
                foreach (var (shape, curl, limit) in new[] { ("fist", 1f, 110f), ("flat", -1f, -5f) })
                {
                    var t = tips && rig.HasTips;
                    var targets = HandShapes.Targets(skeleton, rig, Hand(shape, curl), t);
                    foreach (var chain in rig.Chains.Where(chain => chain.Finger != HandFinger.Thumb))
                        for (var i = 0; i < (t ? 3 : 2); i++)
                        {
                            var bend = Bend(skeleton, chain.Bones[i], targets[chain.Bones[i]], chain.BendAxes[i]);
                            if (!(MathF.Abs(bend - limit) <= 1e-2f))
                                past ??= $"{rig.Side} tips {t} {shape} {chain.Finger} joint {i}: {bend}";
                        }
                }
        check(past == null, "shape clamp: Fist with Curl +1 stops every finger joint at 110 degrees, a folded b too, Flat with Curl -1 at -5" + Failed(past));

        // Past 110 degrees one vanilla joint folds the finger back through the palm, so the fold gives up the total there.
        var tipFist = HandShapes.Capture(hands[2].Skeleton, Posed(hands[2].Skeleton, HandShapes.Targets(hands[2].Skeleton, hands[2].Rig, Hand("fist"), true)), hands[2].Rig);
        string? overFolded = null;
        foreach (var (skeleton, rig) in hands)
            foreach (var hand in new[] { Hand("fist"), Hand("mine", turns: tipFist) })
            {
                var targets = HandShapes.Targets(skeleton, rig, hand, false);
                foreach (var chain in rig.Chains.Where(chain => chain.Finger != HandFinger.Thumb))
                    if (Bend(skeleton, chain.Bones[1], targets[chain.Bones[1]], chain.BendAxes[1]) is var bend && !(MathF.Abs(bend - 110) <= 1e-2f))
                        overFolded ??= $"{rig.Side} {hand.Shape} {chain.Finger}: {bend}";
            }
        check(overFolded == null, "shape fold: a Fist without tips, built in or captured with tips, bends each b bone to 110 degrees" + Failed(overFolded));

        string? rolled = null;
        foreach (var (skeleton, rig) in hands)
        {
            var (_, across) = Palm(skeleton, rig);
            var fist = Posed(skeleton, HandShapes.Targets(skeleton, rig, Hand("fist"), rig.HasTips));
            var spread = Posed(skeleton, HandShapes.Targets(skeleton, rig, Hand("fist", spread: 1), rig.HasTips));
            var (fistModel, spreadModel) = (MotionEvaluator.ModelSpace(skeleton, fist), MotionEvaluator.ModelSpace(skeleton, spread));
            foreach (var chain in rig.Chains.Where(chain => chain.Finger is HandFinger.Index or HandFinger.Little))
            {
                Vector3 Along(MotionTransform[] model) => Vector3.Normalize(model[chain.Bones[1]].Position - model[chain.Bones[0]].Position);
                var side = chain.Finger == HandFinger.Index ? 1 : -1;
                var end = Vector3.Dot(End(skeleton, chain, spread) - End(skeleton, chain, fist), across) * side;
                // A roll about the curled a bone's own length would barely move its direction; a sideways swing of 15 degrees moves it about .26.
                var swing = Vector3.Dot(Along(spreadModel) - Along(fistModel), across) * side;
                if (!(end > 1e-3f) || !(swing > .2f))
                    rolled ??= $"{rig.Side} {chain.Finger}: end {end * 1000:0.00} mm, a bone {swing:0.000}";
            }
        }
        check(rolled == null, "shape spread: Spread on a Fist swings each curled finger sideways, the index toward its side and the little finger away, without rolling it" + Failed(rolled));

        var capped = (from hand in hands from id in HandShapes.Order
                      let low = HandShapes.Targets(hand.Skeleton, hand.Rig, Hand(id, spread: -1), hand.Rig.HasTips)
                      let cap = HandShapes.Targets(hand.Skeleton, hand.Rig, Hand(id, spread: HandShapes.MinSpread), hand.Rig.HasTips)
                      let above = HandShapes.Targets(hand.Skeleton, hand.Rig, Hand(id, spread: HandShapes.MinSpread + .1f), hand.Rig.HasTips)
                      where !low.All(target => cap[target.Key] == target.Value) || above.All(target => cap[target.Key] == target.Value)
                      select $"{hand.Rig.Side} {id}").FirstOrDefault();
        check(capped == null, "shape spread: a Spread below -30% applies as -30%, and one above it still changes the hand" + Failed(capped));

        string? uncaptured = null;
        foreach (var (r, l) in pairs)
            foreach (var tips in bools)
            {
                var t = tips && r.Rig.HasTips;
                var grip = HandShapes.Targets(r.Skeleton, r.Rig, Hand("grip"), t);
                var fromRight = HandShapes.Capture(r.Skeleton, Posed(r.Skeleton, grip), r.Rig);
                var fromLeft = HandShapes.Capture(l.Skeleton, Posed(l.Skeleton, HandShapes.Targets(l.Skeleton, l.Rig, Hand("grip"), t)), l.Rig);
                foreach (var (from, turns) in new[] { ("right", fromRight), ("left", fromLeft) })
                {
                    var mine = HandShapes.Targets(r.Skeleton, r.Rig, Hand("mine", turns: turns), t);
                    if (turns.Count != grip.Count || !turns.Keys.All(name => name.EndsWith("_r", StringComparison.Ordinal)) ||
                        grip.Any(target => !Near(mine.GetValueOrDefault(target.Key), target.Value, 1e-4f)))
                        uncaptured ??= $"from the {from} hand, tips {t}";
                }
            }
        check(uncaptured == null, "shape capture: a captured Grip, from either hand, gives the right hand's Grip back" + Failed(uncaptured));

        var tipHand = hands[2];
        var captured = HandShapes.Capture(tipHand.Skeleton, Posed(tipHand.Skeleton, HandShapes.Targets(tipHand.Skeleton, tipHand.Rig, Hand("grip"), true)), tipHand.Rig);
        var curled = HandShapes.Targets(tipHand.Skeleton, tipHand.Rig, Hand("mine", 1, .5f, captured), true);
        check(HandShapes.Targets(tipHand.Skeleton, tipHand.Rig, Hand("grip", 1, .5f), true).All(target => Near(curled.GetValueOrDefault(target.Key), target.Value, 1e-4f)),
            "shape capture: Curl and Spread on a captured Grip match them on the built-in, clamped the same");

        string? turned = null;
        foreach (var (tipped, plain) in new[] { (hands[2], hands[0]), (hands[3], hands[1]) })
        {
            var source = Posed(tipped.Skeleton, HandShapes.Targets(tipped.Skeleton, tipped.Rig, Hand("grip"), true));
            var turns = HandShapes.Capture(tipped.Skeleton, source, tipped.Rig);
            var applied = Posed(plain.Skeleton, HandShapes.Targets(plain.Skeleton, plain.Rig, Hand("mine", turns: turns), false));
            foreach (var (tc, pc) in tipped.Rig.Chains.Zip(plain.Rig.Chains))
            {
                var degrees = MathF.Acos(Math.Clamp(Vector3.Dot(Pointing(tipped.Skeleton, tc, source), Pointing(plain.Skeleton, pc, applied)), -1, 1)) * 180 / MathF.PI;
                if (!(degrees <= 2))
                    turned ??= $"{tipped.Rig.Side} {tc.Finger}: {degrees} degrees";
            }
        }
        check(turned == null, "shape capture fold: a shape captured with tips points each finger's end the same way on a hand without them" + Failed(turned));
    }

    /// <summary>The rest pose with <paramref name="targets"/> as the local rotations of their bones.</summary>
    private static MotionTransform[] Posed(SkeletonDescription skeleton, Dictionary<int, Quaternion> targets)
    {
        var local = Turned(skeleton);
        foreach (var (bone, rotation) in targets)
            local[bone] = local[bone] with { Rotation = rotation };
        return local;
    }

    private static bool Near(Quaternion a, Quaternion b, float tolerance) => (a - b).Length() <= tolerance || (a + b).Length() <= tolerance;

    /// <summary>How far, in degrees, <paramref name="target"/> turns the bone from its rest rotation.</summary>
    private static float Angle(SkeletonDescription skeleton, int bone, Quaternion target) =>
        2 * MathF.Acos(MathF.Min(1, MathF.Abs((Quaternion.Inverse(skeleton.Bones[bone].Reference.Rotation) * target).W))) * 180 / MathF.PI;

    /// <summary>The bone's signed turn from rest about <paramref name="axis"/> in degrees, -180 to 180.</summary>
    private static float Bend(SkeletonDescription skeleton, int bone, Quaternion target, Vector3 axis)
    {
        var turn = Quaternion.Inverse(skeleton.Bones[bone].Reference.Rotation) * target;
        if (turn.W < 0)
            turn = -turn;
        return 2 * MathF.Atan2(Vector3.Dot(new Vector3(turn.X, turn.Y, turn.Z), axis), turn.W) * 180 / MathF.PI;
    }

    private static void Rig(Action<bool, string> check)
    {
        var right = TestHand(false, false);
        var tipped = TestHand(true, false);
        var left = TestHand(false, true);
        var rig = HandRig.For(right, MotionSide.Right);
        var tipRig = HandRig.For(tipped, MotionSide.Right);
        var leftRig = HandRig.For(left, MotionSide.Left);
        check(rig is { Chains.Length: 5, HasTips: false } && rig.Wrist == MotionEvaluator.Index(right)["j_te_r"] &&
              rig.Chains.All(chain => chain.Bones.Length == 2 && chain.BendAxes.Length == 2) &&
              rig.Chains.Select(chain => chain.Finger).SequenceEqual([HandFinger.Thumb, HandFinger.Index, HandFinger.Middle, HandFinger.Ring, HandFinger.Little]) &&
              tipRig is { Chains.Length: 5, HasTips: true } && tipRig.Chains.All(chain => chain.Bones.Length == (chain.Finger == HandFinger.Thumb ? 2 : 3)) &&
              leftRig is { Side: MotionSide.Left, Chains.Length: 5 },
            "rig chains: five chains a hand, two bones a finger or three with tips, which the rig reports");
        var bent = rig == null || tipRig == null || leftRig == null ? "no rig" : Bends(right, rig) ?? Bends(tipped, tipRig) ?? Bends(left, leftRig);
        check(bent == null, "rig bend: every joint's bend and the thumb's across turn move the finger's end toward the palm" + Failed(bent));
        check(rig != null && Spreads(right, rig) && tipRig != null && Spreads(tipped, tipRig) && leftRig != null && Spreads(left, leftRig) &&
              rig.Chains.Single(chain => chain.Finger == HandFinger.Middle).SpreadSign == 0 &&
              rig.Chains.Single(chain => chain.Finger == HandFinger.Ring).SpreadSign is var ring && MathF.Abs(MathF.Abs(ring) - .6f) < 1e-6f &&
              MathF.Sign(ring) == -MathF.Sign(rig.Chains.Single(chain => chain.Finger == HandFinger.Index).SpreadSign),
            "rig spread: a positive spread moves the index toward the index side and the little finger away, the middle not at all");
        var unmirrored = rig == null || leftRig == null ? "no rig" : Mirrors(right, rig, left, leftRig);
        check(unmirrored == null, "rig mirror: a left hand built as the right's mirror turns to the mirror of the right's ends" + Failed(unmirrored));

        var fingerBones = right.Bones.Select(bone => bone.Name).Where(name => name.StartsWith("j_", StringComparison.Ordinal) && name != "j_te_r").ToArray();
        check(HandRig.For(TestHand(false, false, "j_ko_b_r"), MotionSide.Right) is { Chains.Length: 4 } short4 &&
              short4.Chains.All(chain => chain.Finger != HandFinger.Little),
            "rig missing: a finger without its b bone is left out");
        check(HandRig.For(TestHand(false, false, "j_te_r"), MotionSide.Right) == null &&
              HandRig.For(TestHand(false, false, fingerBones), MotionSide.Right) == null &&
              HandRig.For(right, MotionSide.Left) == null,
            "rig missing: a hand without its wrist or any finger has no rig");
        var flat = Moved(right, "j_naka_b_r", Vector3.Zero);
        var pinched = Moved(right, "j_ko_a_r", right.Bones.Single(bone => bone.Name == "j_hito_a_r").Reference.Position);
        check(Finite(() => HandRig.For(flat, MotionSide.Right)) && Finite(() => HandRig.For(pinched, MotionSide.Right)),
            "rig missing: a zero-length finger bone or index and little bases at one spot neither throw nor give axes that aren't numbers");

        var library = @"G:\XIV Instant Edit\skeleton-library\session-library.json";
        if (!File.Exists(library))
            return;
        var skeletons = JsonNode.Parse(File.ReadAllText(library))!["Skeletons"]!.AsArray().Select(entry => entry!["Skeleton"]!).ToArray();
        foreach (var (prefix, tips) in new[] { ("03CE6B92", false), ("04B93EF0", true), ("05F60030", true) })
        {
            var node = skeletons.FirstOrDefault(skeleton => skeleton["Fingerprint"]!.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal));
            check(node != null, $"rig real: the skeleton library has {prefix}");
            if (node == null)
                continue;
            var skeleton = node.Deserialize<SkeletonDescription>(new JsonSerializerOptions { IncludeFields = true })!;
            foreach (var side in new[] { MotionSide.Left, MotionSide.Right })
            {
                var real = HandRig.For(skeleton, side);
                var realBent = real == null ? "no rig" : Bends(skeleton, real);
                check(real is { Chains.Length: 5 } && real.HasTips == tips && realBent == null,
                    $"rig real: {prefix} {side} builds five chains, tips {tips}, and bends toward the palm" + Failed(realBent));
                // The thumb's base sits in front of the index base: a flipped palm sign would fail here, whatever the bends say.
                if (real != null)
                {
                    var rest = MotionSymmetry.RestModel(skeleton);
                    var index = MotionEvaluator.Index(skeleton);
                    var s = side == MotionSide.Left ? "_l" : "_r";
                    check(Vector3.Dot(rest[index["j_oya_a" + s]].Position - rest[index["j_hito_a" + s]].Position, Palm(skeleton, real).Palm) > 0.005f,
                        $"rig real: {prefix} {side} has the thumb on the palm side");
                }
            }
        }
    }

    private static string Failed(string? detail) => detail == null ? "" : $" ({detail})";

    private static SkeletonDescription Moved(SkeletonDescription skeleton, string name, Vector3 position)
    {
        var at = MotionEvaluator.Index(skeleton)[name];
        var bone = skeleton.Bones[at];
        return skeleton with { Bones = skeleton.Bones.SetItem(at, bone with { Reference = bone.Reference with { Position = position } }) };
    }

    /// <summary>Whether <paramref name="build"/> runs without throwing and gives no rig or one whose every axis is a number.</summary>
    private static bool Finite(Func<HandRig?> build)
    {
        static bool Number(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        try
        {
            return build() is not { } rig || Number(rig.AcrossAxis) &&
                rig.Chains.All(chain => Number(chain.SpreadAxis) && float.IsFinite(chain.SpreadSign) && chain.BendAxes.All(Number));
        }
        catch (ArithmeticException)
        {
            return false;
        }
    }

    /// <summary>
    /// The vanilla right hand's rest positions under a wrist turned as the game's, or its mirror for the
    /// left, optionally with tips and without some bones (their children hang from the bone above).
    /// </summary>
    private static SkeletonDescription TestHand(bool tips, bool left, params string[] without)
    {
        var half = MathF.Sqrt(.5f);
        var wrist = Quaternion.CreateFromRotationMatrix(new Matrix4x4(-half, -half, 0, 0, 0, 0, -1, 0, half, -half, 0, 0, 0, 0, 0, 1));
        var bones = new List<(string Name, string? Parent, Vector3 Position, Quaternion Rotation)>
        {
            ("n_hara", null, Vector3.Zero, Quaternion.Identity),
            ("j_te_r", "n_hara", new(-.6464f, 1.178f, -.0245f), wrist),
            ("j_hito_a_r", "j_te_r", new(.0937f, -.03f, -.0011f), Quaternion.Identity),
            ("j_hito_b_r", "j_hito_a_r", new(.0509f, 0, 0), Quaternion.Identity),
            ("j_naka_a_r", "j_te_r", new(.0938f, -.007f, -.0032f), Quaternion.Identity),
            ("j_naka_b_r", "j_naka_a_r", new(.0541f, 0, 0), Quaternion.Identity),
            ("j_kusu_a_r", "j_te_r", new(.0936f, .0161f, -.0021f), Quaternion.Identity),
            ("j_kusu_b_r", "j_kusu_a_r", new(.0519f, 0, 0), Quaternion.Identity),
            ("j_ko_a_r", "j_te_r", new(.0912f, .0384f, .0024f), Quaternion.Identity),
            ("j_ko_b_r", "j_ko_a_r", new(.0392f, 0, 0), Quaternion.Identity),
            // The game's own thumb turn: with none the thumb would lie along the fingers, where turning across the palm can't move it.
            ("j_oya_a_r", "j_te_r", new(.016f, -.0312f, .0192f), new(.9319431f, -.25743356f, .24743347f, -.063140385f)),
            ("j_oya_b_r", "j_oya_a_r", new(.078f, 0, 0), Quaternion.Identity),
        };
        if (tips)
            foreach (var finger in new[] { "hito", "naka", "kusu", "ko" })
                bones.Add(($"iv_{finger}_c_r", $"j_{finger}_b_r", new(.025f, 0, 0), Quaternion.Identity));
        var parentOf = bones.ToDictionary(bone => bone.Name, bone => bone.Parent);
        string? Kept(string? name) => name != null && without.Contains(name) ? Kept(parentOf[name]) : name;
        var kept = bones.Where(bone => !without.Contains(bone.Name)).Select(bone => bone with { Parent = Kept(bone.Parent) }).ToList();
        if (left)
            kept = kept.Select(bone => (Side(bone.Name), bone.Parent == null ? null : Side(bone.Parent), MotionMirror.Position(bone.Position), MotionMirror.Rotation(bone.Rotation))).ToList();
        var names = kept.Select(bone => bone.Name).ToList();
        return new SkeletonDescription("hand", "hand", [..kept.Select(bone => new SkeletonBone(bone.Name, (short)(bone.Parent == null ? -1 : names.IndexOf(bone.Parent)), 0,
            new BoneTransform(bone.Position, bone.Rotation, Vector3.One)))], [], [], []);

        static string Side(string name) => name.EndsWith("_r", StringComparison.Ordinal) ? name[..^2] + "_l" : name;
    }

    private static MotionTransform[] Turned(SkeletonDescription skeleton, int bone = -1, Vector3 axis = default, float degrees = 0)
    {
        var local = skeleton.Bones.Select(item => MotionTransform.From(item.Reference)).ToArray();
        if (bone >= 0)
            local[bone] = local[bone] with { Rotation = Quaternion.Normalize(local[bone].Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180)) };
        return local;
    }

    /// <summary>The chain's end point in <paramref name="local"/>: its last bone, plus 2 cm along that bone's rest direction.</summary>
    private static Vector3 End(SkeletonDescription skeleton, HandChain chain, MotionTransform[] local) =>
        MotionEvaluator.ModelSpace(skeleton, local)[chain.Bones[^1]].Position + Pointing(skeleton, chain, local) * .02f;

    /// <summary>The way the chain's last bone points in <paramref name="local"/>: its rest direction, turned with it.</summary>
    private static Vector3 Pointing(SkeletonDescription skeleton, HandChain chain, MotionTransform[] local)
    {
        var rest = MotionSymmetry.RestModel(skeleton);
        int last = chain.Bones[^1], before = chain.Bones[^2];
        var along = Vector3.Transform(Vector3.Normalize(rest[last].Position - rest[before].Position), Quaternion.Inverse(rest[last].Rotation));
        return Vector3.Transform(along, MotionEvaluator.ModelSpace(skeleton, local)[last].Rotation);
    }

    /// <summary>The palm's normal toward the body's middle and the index-side direction, worked out from the bone names.</summary>
    private static (Vector3 Palm, Vector3 Across) Palm(SkeletonDescription skeleton, HandRig rig)
    {
        var rest = MotionSymmetry.RestModel(skeleton);
        var index = MotionEvaluator.Index(skeleton);
        var s = rig.Side == MotionSide.Left ? "_l" : "_r";
        Vector3 At(string name) => rest[index[name + s]].Position;
        var across = Vector3.Normalize(At("j_hito_a") - At("j_ko_a"));
        var palm = Vector3.Normalize(Vector3.Cross(across, At("j_naka_b") - At("j_naka_a")));
        return (palm.X * rest[rig.Wrist].Position.X > 0 ? -palm : palm, across);
    }

    /// <summary>The first joint whose +20 degree bend does not bring its finger's end toward the palm, or null.</summary>
    private static string? Bends(SkeletonDescription skeleton, HandRig rig)
    {
        var (palm, _) = Palm(skeleton, rig);
        foreach (var chain in rig.Chains)
        {
            var before = End(skeleton, chain, Turned(skeleton));
            for (var i = 0; i < chain.Bones.Length; i++)
                if (!(Vector3.Dot(End(skeleton, chain, Turned(skeleton, chain.Bones[i], chain.BendAxes[i], 20)) - before, palm) > 1e-5f))
                    return skeleton.Bones[chain.Bones[i]].Name;
            if (chain.Finger == HandFinger.Thumb && !(Vector3.Dot(End(skeleton, chain, Turned(skeleton, chain.Bones[0], rig.AcrossAxis, 20)) - before, palm) > 1e-5f))
                return "thumb across";
        }
        return null;
    }

    /// <summary>Whether a +10 degree spread moves the index's end toward the index side and the little finger's away.</summary>
    private static bool Spreads(SkeletonDescription skeleton, HandRig rig)
    {
        var (_, across) = Palm(skeleton, rig);
        float Moved(HandFinger finger)
        {
            var chain = rig.Chains.Single(item => item.Finger == finger);
            return Vector3.Dot(End(skeleton, chain, Turned(skeleton, chain.Bones[0], chain.SpreadAxis * chain.SpreadSign, 10)) - End(skeleton, chain, Turned(skeleton)), across);
        }
        return Moved(HandFinger.Index) > 1e-5f && Moved(HandFinger.Little) < -1e-5f;
    }

    /// <summary>The first turn whose left-hand end is not the X-mirror of the right hand's after the same turn, or null.</summary>
    private static string? Mirrors(SkeletonDescription right, HandRig rightRig, SkeletonDescription left, HandRig leftRig)
    {
        foreach (var (r, l) in rightRig.Chains.Zip(leftRig.Chains))
        {
            var turns = Enumerable.Range(0, r.Bones.Length).Select(i => (i, r.BendAxes[i], l.BendAxes[i], 20f))
                .Append((0, r.SpreadAxis * r.SpreadSign, l.SpreadAxis * l.SpreadSign, 10f))
                .Concat(r.Finger == HandFinger.Thumb ? new[] { (0, rightRig.AcrossAxis, leftRig.AcrossAxis, 20f) } : []);
            foreach (var (i, rightAxis, leftAxis, degrees) in turns)
            {
                if (rightAxis == Vector3.Zero && leftAxis == Vector3.Zero)
                    continue;
                var mirrored = MotionMirror.Position(End(right, r, Turned(right, r.Bones[i], rightAxis, degrees)));
                if (l.Finger != r.Finger || leftAxis == Vector3.Zero || Vector3.Distance(mirrored, End(left, l, Turned(left, l.Bones[i], leftAxis, degrees))) > 1e-4f)
                    return $"{r.Finger} joint {i}";
            }
        }
        return null;
    }

    private static void Data(Action<bool, string> check, Action<Action, string> reject)
    {
        var layer = HandLayer(new MotionHand(MotionHandSide.Right, "fist"));
        MotionEvaluator.Validate(layer);
        MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Both, "x", 1, -1, true, true, Turns(1))));

        reject(() => MotionEvaluator.Validate(layer with { Fix = new MotionFix(MotionFixKind.HandShape) }), "a hand shape without a hand is refused");
        reject(() => MotionEvaluator.Validate(layer with { Fix = new MotionFix(MotionFixKind.SmoothJitter, Hand: new MotionHand(MotionHandSide.Left, "fist")) }),
            "a hand on another fix is refused");
        foreach (var bad in new[] { float.NaN, 1.01f, -1.01f, float.PositiveInfinity })
        {
            reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "fist", Curl: bad))), $"a hand with a curl of {bad} is refused");
            reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "fist", Spread: bad))), $"a hand with a spread of {bad} is refused");
        }
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand((MotionHandSide)3, "fist"))), "a hand with an unknown side is refused");
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, ""))), "a hand without a shape is refused");
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, new string('a', 41)))), "a hand with a shape name past 40 characters is refused");
        MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, new string('a', 40))));
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "x", Turns: Turns(1).SetItem("", Quaternion.Identity)))),
            "a hand turn without a bone name is refused");
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "x", Turns: Turns(1).SetItem("j_a", new Quaternion(float.NaN, 0, 0, 1))))),
            "a hand turn that isn't finite is refused");
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "x", Turns: Turns(1).SetItem("j_a", new Quaternion(0, 0, 0, 0))))),
            "a hand turn of no length is refused");
        MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "x", Turns: Turns(32))));
        reject(() => MotionEvaluator.Validate(HandLayer(new MotionHand(MotionHandSide.Left, "x", Turns: Turns(33)))), "a hand with more than 32 turns is refused");
        reject(() => MotionEvaluator.Validate(layer with { Fix = layer.Fix! with { Fade = 5 } }), "a hand shape with a game fade is refused");

        var temp = Path.Combine(Path.GetTempPath(), "ie-hand-shape-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_start.pap", "pose06_start.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "start", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var hand = HandLayer(new MotionHand(MotionHandSide.Both, "fist", .25f, -.5f, true, true, Turns(1)));
            var smooth = new MotionLayer("m2", MotionLayerKind.Fix, "Smooth jitter", 0, 30, 0, 0, .5f, PoseComponents.Rotation | PoseComponents.Position,
                MotionInterpolation.Smooth, Fix: new MotionFix(MotionFixKind.SmoothJitter));
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(true, 0, null, [hand, smooth]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            var read = loaded[0].Fix!.Hand!;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], hand) && read.Side == MotionHandSide.Both && read.Shape == "fist" && read.Curl == .25f &&
                  read.Spread == -.5f && read.KeepMotion && read.Tips && read.Turns!.Count == 1 && read.Turns["j_a0"] == Turns(1)["j_a0"],
                "hand journal: a hand layer keeps every field");
            check(loaded[1].Fix is { Hand: null }, "hand journal: a layer without a hand loads with none");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!["Fix"]!.AsObject().Remove("Hand"), "hand journal: a layer stores its hand as Hand");
            File.WriteAllText(path, json.ToJsonString());
            check(new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0].Fix is { Kind: MotionFixKind.HandShape, Hand: null },
                "hand journal: a journal without Hand loads with none");
        }
        finally { Directory.Delete(temp, true); }

        var doc = new TimelineDocument(40, 40, null, [], 1);
        var added = MotionEdits.AddFix(doc, layer.Fix!, out var key, out var error);
        check(error == null && MotionEdits.Find(added, key) is
              {
                  Kind: MotionLayerKind.Fix, Name: "Hand shape", Start: 0, End: 40, FadeIn: 0, FadeOut: 0, Amount: 1, Components: PoseComponents.Rotation,
              } made && made.Bones.IsEmpty && ReferenceEquals(made.Fix, layer.Fix),
            "hand add: the layer spans the clip at full amount, rotation only, on no named bones");

        var same = HandLayer(new MotionHand(MotionHandSide.Right, "fist", .5f, Turns: Turns(2)));
        check(MotionEdits.Same(same, HandLayer(new MotionHand(MotionHandSide.Right, "fist", .5f, Turns: Turns(2)))) &&
              !MotionEdits.Same(same, HandLayer(new MotionHand(MotionHandSide.Right, "fist", .6f, Turns: Turns(2)))) &&
              !MotionEdits.Same(same, HandLayer(new MotionHand(MotionHandSide.Right, "fist", .5f, Turns: Turns(2).SetItem("j_a0", new Quaternion(0, 1, 0, 0))))) &&
              !MotionEdits.Same(same, HandLayer(new MotionHand(MotionHandSide.Right, "fist", .5f))),
            "hand same: equal copies match; a different curl or turns don't");
    }

    private static MotionLayer HandLayer(MotionHand hand) =>
        new("m1", MotionLayerKind.Fix, "Hand shape", 0, 30, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Smooth,
            Fix: new MotionFix(MotionFixKind.HandShape, Hand: hand));

    private static ImmutableDictionary<string, Quaternion> Turns(int count) =>
        Enumerable.Range(0, count).ToImmutableDictionary(i => $"j_a{i}", _ => Quaternion.Identity);

    private static readonly string[] Ivcs = ["j_hito_a_l", "iv_hito_c_l", "iv_naka_c_l", "iv_kusu_c_l", "iv_ko_c_l", "iv_hito_c_r", "iv_naka_c_r", "iv_kusu_c_r", "iv_ko_c_r"];

    private static void Tips(Action<bool, string> check)
    {
        var left = Ivcs.Take(5);
        check(HandTipCheck.SkeletonHas(Ivcs, MotionSide.Left) && HandTipCheck.SkeletonHas(Ivcs, MotionSide.Right), "tips: an IVCS-style skeleton has all four tip bones on both sides");
        check(!HandTipCheck.SkeletonHas(left.Where(name => name != "iv_ko_c_l"), MotionSide.Left) && !HandTipCheck.SkeletonHas(left, MotionSide.Right) &&
              !HandTipCheck.SkeletonHas(["j_hito_a_l", "j_hito_b_l"], MotionSide.Left), "tips: a missing tip bone, the other side's tips or a game skeleton fail");

        // One vertex per tip bone, weighted to it and to a root bone.
        ModelMesh Hand(float weight, int tips = 4)
        {
            var bones = new[] { "j_te_l", "iv_hito_c_l", "iv_naka_c_l", "iv_kusu_c_l", "iv_ko_c_l" };
            var boneIndex = new List<int>();
            var weights = new List<float>();
            for (var tip = 1; tip <= tips; tip++)
            {
                boneIndex.AddRange([0, tip]);
                weights.AddRange([1 - weight, weight]);
            }
            var vertices = tips;
            var part = new ModelMeshPart(0, 0, new Vector3[vertices], new Vector3[vertices], new Vector2[vertices], [])
            { Skin = new ModelSkin(2, boneIndex.ToArray(), weights.ToArray()) };
            return new ModelMesh([part], [], []) { Bones = bones };
        }
        check(HandTipCheck.MeshWeighted(Hand(.5f), MotionSide.Left) && !HandTipCheck.MeshWeighted(Hand(.5f), MotionSide.Right), "tips: a mesh weighted to all four left tips passes on the left only");
        check(!HandTipCheck.MeshWeighted(Hand(.005f), MotionSide.Left) && !HandTipCheck.MeshWeighted(Hand(.5f, 3), MotionSide.Left), "tips: a weight of 0.005 or one unweighted tip fails");
        check(!HandTipCheck.MeshWeighted(new ModelMesh([], [], []), MotionSide.Left), "tips: a mesh without skin weights fails");

        const string clip = "Fingertips: the clip's skeleton has no tip bones. Repair it onto IVCS or YAS to bend them.";
        const string live = "Fingertips: your character's skeleton has no tip bones.";
        const string mesh = "Fingertips: your hands' model isn't weighted to tip bones.";
        check(HandTipCheck.Reason(false, false, false) == clip && HandTipCheck.Reason(false, true, true) == clip && HandTipCheck.Reason(true, false, false) == live &&
              HandTipCheck.Reason(true, false, true) == live && HandTipCheck.Reason(true, true, false) == mesh && HandTipCheck.Reason(true, true, true) == null,
            "tips reason: the first failing check's line, null when all pass");

        // The lines name the clip's skeleton and the hand model when they're known.
        var game = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game,
            new AnimationResource("chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb", "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb", "")),
            TestHand(false, false), 0, "");
        var modded = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Mod,
            new AnimationResource("chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb", @"D:\Mods\Some Skeleton\skl_c0801b0001.sklb", "", ModName: "Some Skeleton")),
            TestHand(false, false), 0, "");
        var rebuild = new RaceRebuild("c0801", new StandardSkeleton(SkeletonStandard.Vanilla, TestHand(false, false), "Game data"));
        check(HandTipCheck.SkeletonName(game, null) == "skl_c0801b0001.sklb from the game" &&
              HandTipCheck.SkeletonName(modded, null) == "skl_c0801b0001.sklb from Some Skeleton" &&
              HandTipCheck.SkeletonName(game, rebuild) == "the game's" && HandTipCheck.SkeletonName(null, null) == null,
            "tips skeleton name: the chosen skeleton's file and source, or a rebuild's standard");
        check(HandTipCheck.Reason(false, true, true, "skl_c0801b0001.sklb from the game") ==
              "Fingertips: the clip's skeleton (skl_c0801b0001.sklb from the game) has no tip bones. Repair it onto IVCS or YAS to bend them.",
            "tips reason: the clip's line names its skeleton");
        check(HandTipCheck.Models(["c0801e0000_glv.mdl", "C0801E0000_GLV.mdl", "", "c0801e6024_top.mdl"]) == "c0801e0000_glv.mdl, c0801e6024_top.mdl" &&
              HandTipCheck.Models([]) == null, "tips models: each hand model once, none is null");
        check(HandTipCheck.Reason(true, true, false, "skl_c0801b0001.sklb from the game", "c0801e0000_glv.mdl") ==
              "Fingertips: your hands' model (c0801e0000_glv.mdl) isn't weighted to tip bones." &&
              HandTipCheck.Reason(true, false, false, "x", "c0801e0000_glv.mdl") == live,
            "tips reason: the model's line names the model; the live line names nothing");
    }
}
