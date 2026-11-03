using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>Bone layers in the timeline editor: their edits, the angle fields, their rows and hit zones, and dragging them.</summary>
internal static class MotionEditsFixture
{
    private const string Hand = "j_te_l";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Adding(check);
        Dragging(check);
        Keys(check);
        Stack(check);
        Angles(check);
        Rows(check);
        Ordering(check);
        Gestures(check);
        BoneRows(check);
        KeyPicks(check);
        KeyTiming(check);
        Views(check);
        Symmetry(check);
    }

    private static void KeyTiming(Action<bool, string> check)
    {
        const string Arm = "j_ude_a_l";
        // m1 over the whole clip: keys at 30 (hand and arm, stepped), 41 (arm) and 50 (hand).
        var doc = Layered(Empty());
        doc = doc with { Motion = doc.Motion.SetItem(0, doc.Motion[0] with { Start = 0, End = 100, FadeIn = 0, FadeOut = 0 }) };
        doc = MotionEdits.SetKey(doc, "m1", 30, Arm, Turn(5));
        doc = MotionEdits.SetKey(doc, "m1", 41, Arm, Turn(15));
        doc = MotionEdits.SetEase(doc, "m1", 30, MotionInterpolation.Step);
        static int[] Frames(TimelineDocument doc) => [.. doc.Motion[0].Keys.Select(key => key.Frame)];
        static MotionKey KeyAt(TimelineDocument doc, int frame) => doc.Motion[0].Keys.Single(key => key.Frame == frame);
        static string[] BonesAt(TimelineDocument doc, int frame) => [.. KeyAt(doc, frame).Bones.Keys.Order(StringComparer.Ordinal)];
        KeyPick[] all = [new(30), new(41), new(50)];

        var half = MotionEdits.ScaleKeysAbout(doc, "m1", all, 30, 50, out var landed, out var error);
        check(error == null && Frames(half).SequenceEqual([30, 36, 40]) && landed.SequenceEqual([new KeyPick(30), new KeyPick(36), new KeyPick(40)]),
            "scale 50% about a playhead on the first key brings the rest closer, rounded to whole frames");
        check(Frames(MotionEdits.ScaleKeysAbout(doc, "m1", all, 45, 50, out _, out error)).SequenceEqual([37, 43, 48]) && error == null,
            "about a playhead inside the pick, the keys close in on it from both sides");
        var twice = MotionEdits.ScaleKeysAbout(doc, "m1", all, 10, 200, out _, out error);
        check(error == null && Frames(twice).SequenceEqual([50, 72, 90]) && KeyAt(twice, 50) is { Ease: MotionInterpolation.Step, Bones.Count: 2 },
            "200% about a playhead before the pick pushes the keys away from it, each with its bones and ease");
        check(Frames(MotionEdits.ScaleKeysAbout(doc, "m1", [new(41, Arm)], 30, 50, out landed, out _)).SequenceEqual([30, 36, 50]) &&
              landed.SequenceEqual([new KeyPick(36, Arm)]),
            "a picked bone's value scales on its own");
        check(ReferenceEquals(MotionEdits.ScaleKeysAbout(half, "m1", [new(30), new(36), new(40)], 30, 10, out _, out error), half) && error != null,
            "a scale that would put two keys on one frame is refused");
        check(ReferenceEquals(MotionEdits.ScaleKeysAbout(doc, "m1", [new(41)], 30, 182, out _, out error), doc) && error != null,
            "a scale that would put a key on one that isn't picked is refused");
        check(ReferenceEquals(MotionEdits.ScaleKeysAbout(doc, "m1", all, 0, 300, out _, out error), doc) && error != null,
            "a scale that would take a key past the clip's end is refused");
        var shorter = doc with { Motion = doc.Motion.SetItem(0, doc.Motion[0] with { End = 60 }) };
        check(ReferenceEquals(MotionEdits.ScaleKeysAbout(shorter, "m1", all, 10, 200, out _, out error), shorter) && error != null,
            "a key inside the layer is never scaled out of it");
        check(ReferenceEquals(MotionEdits.ScaleKeysAbout(doc, "m1", all, 30, 100, out _, out error), doc) && error == null, "100% changes nothing");

        var copied = MotionEdits.DuplicateKeys(doc, "m1", [new(30), new(41)], 60, out landed, out error);
        check(error == null && Frames(copied).SequenceEqual([30, 41, 50, 60, 71]) && landed.SequenceEqual([new KeyPick(60), new KeyPick(71)]) &&
              KeyAt(copied, 60) is { Ease: MotionInterpolation.Step } && BonesAt(copied, 60).SequenceEqual(BonesAt(doc, 30)),
            "duplicated keys land from the playhead on with their spacing, bones and ease, and the copies are picked");
        check(ReferenceEquals(MotionEdits.DuplicateKeys(doc, "m1", [new(30), new(41)], 39, out _, out error), doc) && error != null,
            "a duplicate that would land on a key is refused");
        var boneCopy = MotionEdits.DuplicateKeys(doc, "m1", [new(30, Hand)], 41, out landed, out error);
        check(error == null && BonesAt(boneCopy, 41).SequenceEqual([Hand, Arm]) && BonesAt(boneCopy, 30).SequenceEqual([Hand, Arm]) &&
              landed.SequenceEqual([new KeyPick(41, Hand)]),
            "a duplicated bone's value joins a key without that bone");

        var reversed = MotionEdits.ReverseKeys(doc, "m1", all, out landed, out error);
        check(error == null && Frames(reversed).SequenceEqual([30, 39, 50]) && KeyAt(reversed, 50) is { Ease: MotionInterpolation.Step } &&
              KeyAt(reversed, 50).Bones[Arm] == KeyAt(doc, 30).Bones[Arm] && BonesAt(reversed, 30).SequenceEqual([Hand]) && KeyAt(reversed, 30).Ease == null &&
              landed.SequenceEqual([new KeyPick(50), new KeyPick(39), new KeyPick(30)]),
            "reverse flips the picked keys between the first and the last; values and eases follow their keys");
        var swapped = MotionEdits.ReverseKeys(doc, "m1", [new(30, Hand), new(50, Hand)], out _, out error);
        check(error == null && Frames(swapped).SequenceEqual([30, 41, 50]) && KeyAt(swapped, 30).Bones[Hand] == KeyAt(doc, 50).Bones[Hand] &&
              KeyAt(swapped, 50).Bones[Hand] == KeyAt(doc, 30).Bones[Hand] && KeyAt(swapped, 30).Bones[Arm] == KeyAt(doc, 30).Bones[Arm] &&
              KeyAt(swapped, 30).Ease == MotionInterpolation.Step,
            "reversing two bone values swaps them and leaves the rest of their keys");
        check(ReferenceEquals(MotionEdits.ReverseKeys(doc, "m1", [new(30)], out _, out _), doc), "reversing one key changes nothing");

        // Rows: 6 the layer.
        var editor = new TimelineEditor(doc);
        editor.SetWidth(440);
        void Click(float frame, bool ctrl = false)
        {
            editor.Press(editor.HitTest(Geometry, At(frame, 6)), ctrl);
            editor.Release();
        }
        void PickAll()
        {
            Click(30);
            Click(41, true);
            Click(50, true);
        }
        editor.SelectLayer("m1", 30);
        editor.Playhead = 70;
        check(editor.Key(TimelineKey.Duplicate) && Frames(editor.Document).SequenceEqual([30, 41, 50, 70]) && editor.SelectedKeys.SetEquals([new KeyPick(70)]) &&
              editor.Document.Motion.Length == 2 && editor.History.UndoCount == 1,
            "Ctrl+D with a key picked copies it to the playhead and picks the copy, not the layer");
        editor.Undo();
        check(Frames(editor.Document).SequenceEqual([30, 41, 50]), "undo takes the copies away");

        PickAll();
        check(editor.SelectedKeys.Count == 3 && editor.ReverseTiming() && Frames(editor.Document).SequenceEqual([30, 39, 50]) &&
              editor.SelectedKeys.SetEquals([new KeyPick(30), new KeyPick(39), new KeyPick(50)]) && editor.History.UndoCount == 1,
            "Reverse timing keeps the keys picked, as one undo step");
        editor.Undo();
        check(Frames(editor.Document).SequenceEqual([30, 41, 50]) && KeyAt(editor.Document, 30).Ease == MotionInterpolation.Step, "undo puts them back");

        PickAll();
        editor.Playhead = 30;
        var before = editor.History.UndoCount;
        check(editor.Key(TimelineKey.ScaleTiming) && editor.Gesture == TimelineGesture.ScaleKeys, "S starts scaling the picked keys");
        editor.Drag(40);
        editor.Drag(50);
        check(Frames(editor.Document).SequenceEqual([30, 52, 70]) && editor.LastNotice == "Scale 200%",
            "the mouse twice as far from the playhead as where it began scales them 200%, and the status line says so");
        editor.Drag(35);
        editor.Release();
        check(Frames(editor.Document).SequenceEqual([30, 36, 40]) && editor.SelectedKeys.SetEquals([new KeyPick(30), new KeyPick(36), new KeyPick(40)]) &&
              editor.History.UndoCount == before + 1 && editor.Gesture == TimelineGesture.None,
            "a click applies the scale as one undo step, the keys still picked");
        editor.Undo();
        PickAll();
        before = editor.History.UndoCount;
        editor.Playhead = 30;
        editor.Key(TimelineKey.ScaleTiming);
        editor.Drag(40);
        editor.Drag(60);
        editor.Key(TimelineKey.Escape);
        check(Frames(editor.Document).SequenceEqual([30, 41, 50]) && editor.SelectedKeys.SetEquals(all) && editor.History.UndoCount == before &&
              editor.Gesture == TimelineGesture.None,
            "Escape puts the keys and the picks back");

        check(editor.ScaleTiming(50) && Frames(editor.Document).SequenceEqual([30, 36, 40]) && editor.History.UndoCount == before + 1,
            "Scale timing from the menu scales about the playhead as one undo step");
        check(!editor.ScaleTiming(10) && editor.LastError != null && Frames(editor.Document).SequenceEqual([30, 36, 40]),
            "a refused scale says why and changes nothing");
        editor.Undo();
        check(Frames(editor.Document).SequenceEqual([30, 41, 50]), "undo puts the scaled keys back");
    }

    private static void Symmetry(Action<bool, string> check)
    {
        var added = MotionEdits.Add(Empty(30), MotionLayerKind.Symmetry, 0, 30, out var key, out _);
        var layer = MotionEdits.Find(added, key)!;
        check(layer.Name == "Symmetry (Left → Right)" && layer.Components == PoseComponents.Rotation && layer.Amount == 1 && layer.From == MotionSide.Left &&
              MotionEdits.KindName(MotionLayerKind.Symmetry) == "Symmetry" && MotionEdits.MaxAmount(MotionLayerKind.Symmetry) == 1,
            "a new symmetry layer copies turns from the left at full blend");
        var flipped = MotionEdits.SetFrom(added, key, MotionSide.Right);
        check(MotionEdits.Find(flipped, key) is { From: MotionSide.Right, Name: "Symmetry (Right → Left)" },
            "flipping the side renames a layer that kept its default name");
        var named = MotionEdits.Replace(added, layer with { Name = "Hands" });
        check(MotionEdits.Find(MotionEdits.SetFrom(named, key, MotionSide.Right), key) is { From: MotionSide.Right, Name: "Hands" },
            "flipping keeps a name the user gave");
        check(ReferenceEquals(MotionEdits.SetFrom(added, key, MotionSide.Left), added), "setting the side it already has changes nothing");
        check(ReferenceEquals(MotionEdits.SetKey(added, key, 5, "j_sako_l", Turn(10)), added), "keys are never added to a symmetry layer");

        var made = MotionEdits.AddSymmetry(Empty(30), ["j_sako_l", "j_sako_l", ""], MotionSide.Right, out var madeKey, out var error);
        check(error == null && MotionEdits.Find(made, madeKey) is { Kind: MotionLayerKind.Symmetry, From: MotionSide.Right, Start: 0, End: 30 } symmetric &&
              symmetric.Bones.SequenceEqual(["j_sako_l"]) && symmetric.Name == "Symmetry (Right → Left)",
            "Make symmetrical adds one whole-clip layer with the picked bones and side");
        var refused = MotionEdits.AddSymmetry(Empty(30), [], MotionSide.Left, out _, out error);
        check(refused.Motion.IsEmpty && error == "Select a bone with a twin on the other side.", "Make symmetrical needs a bone");
        var editor = new TimelineEditor(Empty(30));
        check(editor.AddSymmetry(["j_sako_l"], MotionSide.Left) && editor.Document.Motion is [{ Kind: MotionLayerKind.Symmetry } only] &&
              editor.SelectedLayer == only.Key && editor.LastError == null,
            "Make symmetrical selects the layer it adds");
        check(!editor.AddSymmetry([], MotionSide.Left) && editor.Document.Motion.Length == 1 && editor.LastError == "Select a bone with a twin on the other side.",
            "Make symmetrical without a bone says why and adds nothing");

        // The clip animates the left custom bone only; copying it gives the right one a track no standard skeleton has.
        static BoneTransform Rest() => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var game = new SkeletonDescription("game", "game-print", [new("n_root", -1, 0, Rest()), new("j_kubi", 0, 0, Rest())], [], [], []);
        var skeleton = new SkeletonDescription("made", "made-print", [.. game.Bones, new("j_custom_l", 1, 0, Rest()), new("j_custom_r", 1, 0, Rest())], [], [], []);
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, new AnimationResource("chara/a.pap", "a.pap", "hash")), skeleton, 0, "");
        var clip = new AnimationClip("chara/a.pap", "loop", 0, 0, 1, "s.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [1, 2],
                Standards: [new StandardSkeleton(SkeletonStandard.Vanilla, game, "game")]));
        var copying = MotionEdits.AddSymmetry(Empty(30), ["j_custom_l"], MotionSide.Left, out _, out _);
        check(TimelineWindowViews.LayerBonesWarning(copying, clip, clip.Resolution?.Selected?.Skeleton) is { } warning && warning.Contains("j_custom_r", StringComparison.Ordinal),
            "a symmetry layer giving a non-standard bone a track warns like a keyed one");
        // Both custom bones tracked, so leaving the right one out is itself allowed.
        var bothTracked = clip with { Resolution = clip.Resolution! with { TrackBones = [1, 2, 3] } };
        check(TimelineWindowViews.BonesProblem(Empty(30) with { ExcludedBones = ["j_custom_r"] }, bothTracked, bothTracked.Resolution?.Selected?.Skeleton, null) == null &&
              TimelineWindowViews.BonesProblem(copying with { ExcludedBones = ["j_custom_r"] }, bothTracked, bothTracked.Resolution?.Selected?.Skeleton, null) is { } problem &&
              problem.StartsWith("Bone layers move", StringComparison.Ordinal) && problem.Contains("j_custom_r", StringComparison.Ordinal),
            "a symmetry layer copying onto a bone left out of the save refuses to save, as a keyed layer does");
        check(!MotionEdits.Same(copying.Motion[0], copying.Motion[0] with { From = MotionSide.Right }),
            "a layer copying the other side is a different layer");
        check(TimelineWindowViews.LayerContent(copying.Motion[0]) == "From the left · 1 bone",
            "a symmetry layer's tooltip says which side it copies and how many bones were picked");
    }

    private static void Views(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        check(TimelineWindowViews.LayerLabel(doc.Motion[0]) == "Offset 1" && TimelineWindowViews.LayerLabel(doc.Motion[1]) == "Amplitude 1 · 150%" &&
              TimelineWindowViews.LayerLabel(doc.Motion[0] with { Enabled = false }) == "Offset 1 · off" &&
              TimelineWindowViews.LayerLabel(doc.Motion[0] with { Amount = .5f }) == "Offset 1 · 50%",
            "a layer's bar names it, with its amount when that isn't full strength and off when it is turned off");
        check(TimelineWindowViews.KeyEaseText(doc.Motion[0], doc.Motion[0].Keys[0]) == "To the next key: Smooth (the layer's)" &&
              TimelineWindowViews.KeyEaseText(doc.Motion[0], doc.Motion[0].Keys[0] with { Ease = MotionInterpolation.EaseIn }) == "To the next key: Slow start" &&
              TimelineWindowViews.BoneKeysText(doc.Motion[0], Hand) == "2 keys in this layer" && !TimelineWindowViews.LayerEases.Contains(MotionInterpolation.Custom),
            "a key's tooltip says how it goes to the next key; a layer offers every way but a key's own curve");
        check(TimelineWindowViews.LayerContent(doc.Motion[0]) == "2 keys · 1 bone" && TimelineWindowViews.LayerContent(doc.Motion[1]) == "Every bone" &&
              TimelineWindowViews.LayerContent(doc.Motion[1] with { Bones = [Hand] }) == "1 bone",
            "a layer's tooltip counts its keys and bones");

        // The game's skeleton has the neck; the clip animates it and the hand.
        static BoneTransform Rest() => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var game = new SkeletonDescription("game", "game-print", [new("n_root", -1, 0, Rest()), new("j_kubi", 0, 0, Rest()), new(Hand, 0, 0, Rest())], [], [], []);
        var made = new SkeletonDescription("made", "made-print", [.. game.Bones, new("iv_ko_c_l", 2, 0, Rest()), new("j_custom", 2, 0, Rest())], [], [], []);
        var resource = new AnimationResource("chara/a.pap", "a.pap", "hash");
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, resource), made, 0, "");
        var clip = new AnimationClip("chara/a.pap", "loop", 0, 0, 1, "s.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [1, 2],
                Standards: [new StandardSkeleton(SkeletonStandard.Vanilla, game, "game")]));
        TimelineDocument Keyed(string bone, MotionLayerKind kind = MotionLayerKind.Offset) => Empty() with
        {
            Motion = [new("m1", kind, "L", 0, 100, 0, 0, kind == MotionLayerKind.Amplitude ? 2 : 1, PoseComponents.All, MotionInterpolation.Smooth,
                kind == MotionLayerKind.Amplitude ? [bone] : [],
                kind == MotionLayerKind.Amplitude ? [] : [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone, Turn(10)))])],
        };
        check(TimelineWindowViews.LayerBonesWarning(Keyed("j_custom"), clip, clip.Resolution?.Selected?.Skeleton) is { } warning && warning.Contains("j_custom", StringComparison.Ordinal),
            "layers moving a bone outside IVCS + YAS that the clip doesn't animate warn that sync plugins can refuse the file");
        check(TimelineWindowViews.LayerBonesWarning(Keyed("iv_ko_c_l"), clip, clip.Resolution?.Selected?.Skeleton) == null && TimelineWindowViews.LayerBonesWarning(Keyed("j_kubi"), clip, clip.Resolution?.Selected?.Skeleton) == null &&
              TimelineWindowViews.LayerBonesWarning(Keyed("j_custom", MotionLayerKind.Amplitude), clip, clip.Resolution?.Selected?.Skeleton) == null &&
              TimelineWindowViews.LayerBonesWarning(Keyed("j_custom") with { Motion = [Keyed("j_custom").Motion[0] with { Enabled = false }] }, clip, clip.Resolution?.Selected?.Skeleton) == null &&
              TimelineWindowViews.LayerBonesWarning(Keyed("j_custom"), clip with { Resolution = clip.Resolution! with { Standards = [] } }, clip.Resolution?.Selected?.Skeleton) == null,
            "standard bones, amplitudes, layers turned off and clips without the game's skeleton don't warn");

        // A match moves the loop's bones the clip leaves at rest: an untracked custom bone gets a track.
        TimelineDocument Matching(BoneTransform custom, ImmutableArray<string> limit = default)
        {
            var pose = ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kubi", Rest()).Add(Hand, Turn(5)).Add("j_custom", custom);
            return Empty() with
            {
                Motion = [new("m1", MotionLayerKind.Fix, "Match loop", 92, 100, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position,
                    MotionInterpolation.Smooth, limit, Fix: new MotionFix(MotionFixKind.MatchLoop, pose, pose))],
            };
        }
        check(TimelineWindowViews.LayerBonesWarning(Matching(Turn(10)), clip, clip.Resolution?.Selected?.Skeleton) is { } matchWarning && matchWarning.Contains("j_custom", StringComparison.Ordinal) &&
              TimelineWindowViews.LayerBonesWarning(Matching(Rest()), clip, clip.Resolution?.Selected?.Skeleton) == null && TimelineWindowViews.LayerBonesWarning(Matching(Turn(10), [Hand]), clip, clip.Resolution?.Selected?.Skeleton) == null,
            "a match warns for a non-standard bone the loop holds off rest, not one at rest or outside its limit");
    }

    private static TimelineDocument Empty(int length = 100) => new(length, length, null, [], 1);

    private static BoneTransform Turn(float degrees) =>
        new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180), Vector3.One);

    private static MotionKey Key(int frame, float degrees) =>
        new(frame, ImmutableDictionary<string, BoneTransform>.Empty.Add(Hand, Turn(degrees)));

    /// <summary>An offset from 20 to 60 fading 8 frames each way with keys at 30 and 50, then an amplitude over the whole clip.</summary>
    private static TimelineDocument Layered(TimelineDocument doc) => doc with
    {
        Motion =
        [
            new("m1", MotionLayerKind.Offset, "Offset 1", 20, 60, 8, 8, 1, PoseComponents.All, MotionInterpolation.Smooth, [], [Key(30, 10), Key(50, 20)]),
            new("m2", MotionLayerKind.Amplitude, "Amplitude 1", 0, 100, 0, 0, 1.5f, PoseComponents.Rotation, MotionInterpolation.Smooth),
        ],
        NextMotionKey = 3,
    };

    private static void Adding(Action<bool, string> check)
    {
        var whole = MotionEdits.Add(Empty(), MotionLayerKind.Offset, 0, 100, out var first, out var error);
        var layer = MotionEdits.Find(whole, first)!;
        check(first == "m1" && error == null && layer is { Name: "Offset 1", Start: 0, End: 100, FadeIn: 0, FadeOut: 0, Amount: 1, Components: PoseComponents.All } &&
              whole.NextMotionKey == 2,
            "a layer over the whole clip has no fades, so a loop stays seamless");
        var section = MotionEdits.Add(whole, MotionLayerKind.Offset, 40, 70, out var second, out _);
        check(MotionEdits.Find(section, second) is { Name: "Offset 2", FadeIn: 0, FadeOut: 0 } && section.Motion.Length == 2 && section.Motion[^1].Key == second,
            "a section has no fades either, is numbered per kind and applies after the others");
        var amplitude = MotionEdits.Add(Empty(), MotionLayerKind.Amplitude, 0, 100, out var amp, out _);
        var pose = MotionEdits.Add(Empty(), MotionLayerKind.Pose, 0, 100, out var posed, out _);
        check(MotionEdits.Find(amplitude, amp) is { Amount: 1.5f, Components: PoseComponents.Rotation | PoseComponents.Position } &&
              MotionEdits.Find(pose, posed) is { Amount: 1, Components: PoseComponents.Rotation | PoseComponents.Position },
            "an amplitude starts at 150% and a pose at full strength; both turn and move, scale only on request");
        var swing = MotionEdits.Add(Empty(), MotionLayerKind.Swing, 0, 100, out var swingKey, out _);
        var smooth = MotionEdits.Add(Empty(), MotionLayerKind.Smooth, 0, 100, out var smoothKey, out _);
        check(MotionEdits.Find(swing, swingKey) is { Name: "Swing 1", Amount: 1, Components: PoseComponents.Rotation | PoseComponents.Position, Window: 0 } s &&
              s.Swing == new MotionSwing() &&
              MotionEdits.Find(smooth, smoothKey) is { Name: "Smooth motion 1", Amount: 1, Window: MotionSmoothing.DefaultWindow, Swing: null },
            "a swing starts with the default swing and a smooth motion with a 10-frame window, both at full strength");
        check(TimelineWindowViews.LayerKinds.SequenceEqual([MotionLayerKind.Pose, MotionLayerKind.Offset, MotionLayerKind.LookAt, MotionLayerKind.Pin, MotionLayerKind.Balance,
                  MotionLayerKind.Amplitude, MotionLayerKind.Smooth, MotionLayerKind.Swing, MotionLayerKind.Noise, MotionLayerKind.Spring, MotionLayerKind.Repeat,
                  MotionLayerKind.Idle, MotionLayerKind.Symmetry, MotionLayerKind.Link]) &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Swing) == "Swing bones back and forth over the layer." &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Smooth) == "Even out sudden moves." &&
              TimelineUi.LayerIcon(MotionLayerKind.Swing) != TimelineUi.LayerIcon(MotionLayerKind.Idle) &&
              TimelineUi.LayerIcon(MotionLayerKind.Smooth) != TimelineUi.LayerIcon(MotionLayerKind.Swing) &&
              TimelineWindowViews.LayerColour(MotionLayerKind.Swing) != TimelineWindowViews.LayerColour(MotionLayerKind.Smooth),
            "the Add menu offers Swing and Smooth motion after Idle motion, each with its own hint, icon and colour");
        var swung = MotionEdits.Find(swing, swingKey)!;
        check(TimelineWindowViews.LayerContent(swung) == "No bones" && TimelineWindowViews.LayerContent(swung with { Bones = ["j_kosi", "j_sebo_a"] }) == "2 bones" &&
              TimelineWindowViews.LayerContent(MotionEdits.Find(smooth, smoothKey)!) == "Every bone" &&
              !TimelineLayout.Unfolds(swung) && !TimelineLayout.Unfolds(MotionEdits.Find(smooth, smoothKey)!),
            "a swing without bones says so, a smooth motion without bones smooths every bone, and neither unfolds into bone rows");
        var rest = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        check(ReferenceEquals(MotionEdits.SetKey(swing, swingKey, 10, "j_kosi", rest), swing) &&
              ReferenceEquals(MotionEdits.SetKey(smooth, smoothKey, 10, "j_kosi", rest), smooth),
            "swing and smooth motion layers take no keys");
        var noise = MotionEdits.Add(Empty(), MotionLayerKind.Noise, 0, 100, out var noiseKey, out _);
        var noisy = MotionEdits.Find(noise, noiseKey)!;
        check(noisy is { Name: "Noise 1", Amount: 1, Components: PoseComponents.Rotation | PoseComponents.Position, Window: 0, Swing: null } &&
              noisy.Noise == new MotionNoise() && MotionEdits.MaxAmount(MotionLayerKind.Noise) == MotionEvaluator.MaxAmount,
            "a noise starts with the default noise at full strength, up to 400%");
        check(TimelineWindowViews.LayerKindHint(MotionLayerKind.Noise) == "Random wobble on bones." &&
              TimelineUi.LayerIcon(MotionLayerKind.Noise) != TimelineUi.LayerIcon(MotionLayerKind.Smooth) &&
              TimelineUi.LayerIcons.Distinct().Count() == TimelineUi.LayerIcons.Length && TimelineUi.LayerIcons.Length == Enum.GetValues<MotionLayerKind>().Length &&
              Enum.GetValues<MotionLayerKind>().Where(kind => kind != MotionLayerKind.Noise).All(kind => TimelineWindowViews.LayerColour(kind) != TimelineWindowViews.LayerColour(MotionLayerKind.Noise)),
            "the Add menu offers Noise after Smooth motion, with its own hint, icon and colour");
        check(TimelineWindowViews.LayerContent(noisy) == "No bones" && TimelineWindowViews.LayerContent(noisy with { Bones = ["j_kosi"] }) == "1 bone" &&
              !TimelineLayout.Unfolds(noisy) && ReferenceEquals(MotionEdits.SetKey(noise, noiseKey, 10, "j_kosi", rest), noise),
            "a noise without bones says so, has no bone rows and takes no keys");
        var repeat = MotionEdits.Add(Empty(), MotionLayerKind.Repeat, 40, 100, out var repeatKey, out _);
        var repeated = MotionEdits.Find(repeat, repeatKey)!;
        check(repeated is { Name: "Repeat 1", Amount: 1, Components: PoseComponents.Rotation | PoseComponents.Position, Window: 0, Noise: null } &&
              repeated.Repeat == new MotionRepeat(10, 40) && MotionEdits.MaxAmount(MotionLayerKind.Repeat) == 1 &&
              MotionEdits.Find(MotionEdits.Add(Empty(), MotionLayerKind.Repeat, 10, 100, out var early, out _), early)!.Repeat == new MotionRepeat(0, 30),
            "a repeat starts on the 30 frames before it, from frame 0 at the earliest, at full strength");
        check(!MotionEdits.Same(repeated, repeated with { Repeat = repeated.Repeat! with { Blend = 2 } }) &&
              ReferenceEquals(MotionEdits.SetKey(repeat, repeatKey, 50, "j_kosi", rest), repeat),
            "layers differ by a repeat's blend, and a repeat takes no keys");
        check(MotionEdits.KindName(MotionLayerKind.Repeat) == "Repeat" &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Repeat) == "Play a stretch of the motion again and again." &&
              TimelineUi.LayerIcons.Distinct().Count() == TimelineUi.LayerIcons.Length && TimelineUi.LayerIcons.Length == Enum.GetValues<MotionLayerKind>().Length &&
              Enum.GetValues<MotionLayerKind>().Where(kind => kind != MotionLayerKind.Repeat).All(kind => TimelineWindowViews.LayerColour(kind) != TimelineWindowViews.LayerColour(MotionLayerKind.Repeat)),
            "the Add menu offers Repeat after Noise, with its own hint, icon and colour");
        check(TimelineWindowViews.LayerContent(repeated) == "Every bone" && TimelineWindowViews.LayerContent(repeated with { Bones = ["j_kosi"] }) == "1 bone" &&
              !TimelineLayout.Unfolds(repeated) && LayerPanel.Summary(repeated) == "Repeats every bone" &&
              LayerPanel.Summary(repeated with { Bones = ["j_kosi", "j_sebo_a"] }) == "Repeats 2 bones" && TimelineFace.Allows(MotionLayerKind.Repeat, null),
            "a repeat without bones repeats every bone, has no bone rows, and a face takes one");
        var spring = MotionEdits.Add(Empty(), MotionLayerKind.Spring, 0, 100, out var springKey, out _);
        var sprung = MotionEdits.Find(spring, springKey)!;
        check(sprung is { Name: "Spring 1", Amount: 1, Components: PoseComponents.Rotation, Window: 0, Repeat: null } && sprung.Spring == new MotionSpring() &&
              sprung.Bones.IsEmpty && MotionEdits.MaxAmount(MotionLayerKind.Spring) == 1,
            "a spring starts with the default spring, turning only, at full strength");
        check(!MotionEdits.Same(sprung, sprung with { Spring = sprung.Spring! with { Lag = 6 } }) &&
              ReferenceEquals(MotionEdits.SetKey(spring, springKey, 50, "j_kosi", rest), spring),
            "layers differ by a spring's lag, and a spring takes no keys");
        check(MotionEdits.KindName(MotionLayerKind.Spring) == "Spring" &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Spring) == "Make bones lag and overshoot the motion." &&
              TimelineUi.LayerIcons.Distinct().Count() == TimelineUi.LayerIcons.Length && TimelineUi.LayerIcons.Length == Enum.GetValues<MotionLayerKind>().Length &&
              Enum.GetValues<MotionLayerKind>().Where(kind => kind != MotionLayerKind.Spring).All(kind => TimelineWindowViews.LayerColour(kind) != TimelineWindowViews.LayerColour(MotionLayerKind.Spring)),
            "the Add menu offers Spring after Noise, with its own hint, icon and colour");
        check(TimelineWindowViews.LayerContent(sprung) == "No bones" && TimelineWindowViews.LayerContent(sprung with { Bones = ["j_kosi"] }) == "1 bone" &&
              !TimelineLayout.Unfolds(sprung) && LayerPanel.Summary(sprung with { Bones = ["j_kosi", "j_sebo_a"] }) == "Springs 2 bones" &&
              TimelineFace.Allows(MotionLayerKind.Spring, null),
            "a spring without bones says so, has no bone rows, and a face takes one");
        var balance = MotionEdits.Add(Empty(), MotionLayerKind.Balance, 0, 100, out var balanceKey, out _);
        var balanced = MotionEdits.Find(balance, balanceKey)!;
        check(balanced is { Name: "Balance 1", Amount: 1, Components: PoseComponents.Rotation | PoseComponents.Position, Window: 0, Spring: null } &&
              balanced.Balance == new MotionBalance() && balanced.Bones.IsEmpty && MotionEdits.MaxAmount(MotionLayerKind.Balance) == 1,
            "a balance starts with the default balance, turning and moving, at full strength");
        check(!MotionEdits.Same(balanced, balanced with { Balance = balanced.Balance! with { MaxShift = .2f } }) &&
              ReferenceEquals(MotionEdits.SetKey(balance, balanceKey, 50, "j_kosi", rest), balance) &&
              ReferenceEquals(MotionEdits.SetBones(balance, balanceKey, ["j_kosi"]), balance),
            "layers differ by a balance's max shift, and a balance takes no keys or bones");
        check(MotionEdits.KindName(MotionLayerKind.Balance) == "Balance" &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Balance) == "Keep the body's weight over the feet." &&
              TimelineUi.LayerIcons.Distinct().Count() == TimelineUi.LayerIcons.Length && TimelineUi.LayerIcons.Length == Enum.GetValues<MotionLayerKind>().Length &&
              Enum.GetValues<MotionLayerKind>().Where(kind => kind != MotionLayerKind.Balance).All(kind => TimelineWindowViews.LayerColour(kind) != TimelineWindowViews.LayerColour(MotionLayerKind.Balance)),
            "the Add menu offers Balance before Pin, with its own hint, icon and colour");
        var faced = MotionEdits.Add(TimelineFace.Open(Empty(), 0, 1, 100), MotionLayerKind.Balance, 0, 2, out _, out var faceError);
        check(faced.Motion.IsEmpty && faceError == "A face takes no balance layer." && !TimelineFace.Allows(MotionLayerKind.Balance, null) &&
              !TimelineLayout.Unfolds(balanced) && TimelineWindowViews.LayerContent(balanced) == "Hips and feet",
            "a face takes no balance, which has no bone rows and works on the hips and feet");
        var full = Empty() with { Motion = [.. Enumerable.Range(0, MotionEvaluator.MaxLayers).Select(i => layer with { Key = "m" + i })] };
        var refused = MotionEdits.Add(full, MotionLayerKind.Offset, 0, 10, out _, out var tooMany);
        check(ReferenceEquals(refused, full) && tooMany != null, "a layer past the most an animation may have is refused with a reason");
        check(MotionEdits.Find(MotionEdits.Add(Empty(), MotionLayerKind.Offset, 50, 50, out var thin, out _), thin) is { Start: 50, End: 51 },
            "a layer is always at least a frame long");
    }

    private static void Dragging(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        var moved = MotionEdits.Move(doc, "m1", 5);
        check(MotionEdits.Find(moved, "m1") is { Start: 25, End: 65 } layer && layer.Keys.Select(k => k.Frame).SequenceEqual([35, 55]),
            "moving a layer moves its keys with it");
        check(MotionEdits.Find(MotionEdits.Move(doc, "m1", -50), "m1") is { Start: 0, End: 40 } &&
              MotionEdits.Find(MotionEdits.Move(doc, "m1", 90), "m1") is { Start: 60, End: 100 },
            "a layer moves no further than the start or the end of the clip");
        check(ReferenceEquals(MotionEdits.Move(doc, "m1", 0), doc) && ReferenceEquals(MotionEdits.Move(doc, "missing", 3), doc),
            "a move that changes nothing keeps the same document");
        var shrunk = MotionEdits.Resize(doc, "m1", true, 35);
        check(MotionEdits.Find(shrunk, "m1") is { Start: 55, End: 60, FadeIn: 5, FadeOut: 0 },
            "a resize keeps a layer at least a frame long and shrinks its fades to fit");
        check(MotionEdits.Find(MotionEdits.Resize(doc, "m1", false, 100), "m1") is { End: 100 } &&
              MotionEdits.Find(MotionEdits.Resize(doc, "m1", true, -100), "m1") is { Start: 0 },
            "an edge stops at the clip's ends");
        check(MotionEdits.Find(MotionEdits.Fade(doc, "m1", true, 4), "m1") is { FadeIn: 12 } &&
              MotionEdits.Find(MotionEdits.Fade(doc, "m1", false, 3), "m1") is { FadeOut: 5 } &&
              MotionEdits.Find(MotionEdits.Fade(doc, "m1", true, 100), "m1") is { FadeIn: 32, FadeOut: 8 },
            "a fade's handle drags its fade, and both fades together never outlast the layer");
    }

    private static void Keys(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        var moved = MotionEdits.MoveKey(doc, "m1", 30, 100, out var landed);
        check(landed == 49 && MotionEdits.Find(moved, "m1")!.Keys.Select(k => k.Frame).SequenceEqual([49, 50]),
            "a key moves no further than the frame before its neighbour");
        MotionEdits.MoveKey(doc, "m1", 30, -100, out var lowest);
        check(lowest == 0, "the first key may move to the start of the timeline");
        var added = MotionEdits.SetKey(doc, "m1", 40, Hand, Turn(15));
        check(MotionEdits.Find(added, "m1")!.Keys.Select(k => k.Frame).SequenceEqual([30, 40, 50]), "a value at a frame without a key adds one in order");
        var updated = MotionEdits.SetKey(doc, "m1", 30, Hand, Turn(5));
        check(MotionEdits.Find(updated, "m1")!.Keys[0].Bones[Hand] == Turn(5) && ReferenceEquals(MotionEdits.SetKey(doc, "m1", 30, Hand, Turn(10)), doc),
            "a value at a key's frame replaces that key's value, and the same value changes nothing");
        var emptied = MotionEdits.SetKey(doc, "m1", 30, Hand, null);
        check(MotionEdits.Find(emptied, "m1")!.Keys.Select(k => k.Frame).SequenceEqual([50]), "taking a key's last bone out removes the key");
        var marked = MotionEdits.SetKey(doc, "m1", 30, Hand, Turn(10), moved: true);
        check(MotionEdits.Find(marked, "m1")!.Keys[0].Moved.SequenceEqual([Hand]) &&
              MotionEdits.Find(MotionEdits.SetKey(marked, "m1", 30, Hand, Turn(10), moved: false), "m1")!.Keys[0].Moved.IsEmpty,
            "a pose key marks and unmarks the bones whose position it sets");
        check(MotionEdits.Find(MotionEdits.ClearBone(doc, "m1", Hand), "m1")!.Keys.IsEmpty &&
              MotionEdits.Find(MotionEdits.DeleteKey(doc, "m1", 50), "m1")!.Keys.Select(k => k.Frame).SequenceEqual([30]),
            "a bone can be cleared from every key, and one key deleted");
    }

    private static void Stack(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        var reordered = MotionEdits.Reorder(doc, "m2", -1);
        check(reordered.Motion.Select(l => l.Key).SequenceEqual(["m2", "m1"]) && ReferenceEquals(MotionEdits.Reorder(doc, "m2", 1), doc),
            "a layer moves up and down the stack, never past either end");
        var copied = MotionEdits.Duplicate(doc, "m1", out var copy);
        check(copy == "m3" && copied.Motion.Select(l => l.Key).SequenceEqual(["m1", "m3", "m2"]) &&
              MotionEdits.Find(copied, copy) is { Name: "Offset 1 copy", Start: 20, End: 60 } dup && dup.Keys.Length == 2 && copied.NextMotionKey == 4,
            "a copy goes right after its layer, keys and all, with a new key");
        var whole = doc.Motion[0] with { Start = 0, End = 100, FadeIn = 30, FadeOut = 30 };
        var pasted = MotionEdits.Paste(Empty(50), [whole, doc.Motion[0] with { Start = 40, End = 90 }], 100, out var pastedKeys, out var pasteError);
        check(pasteError == null && pastedKeys.SequenceEqual(["m1", "m2"]) &&
              MotionEdits.Find(pasted, "m1") is { Start: 0, End: 50, FadeIn: 30, FadeOut: 20 } p && p.Keys.Length == 2 &&
              MotionEdits.Find(pasted, "m2") is { Start: 40, End: 50 },
            "pasted layers get new keys; a whole-clip layer covers the new clip, others are cut to it");
        MotionEdits.Paste(doc with { Motion = [.. Enumerable.Range(0, MotionEvaluator.MaxLayers).Select(i => doc.Motion[0] with { Key = $"m{i + 1}" })] },
            [whole], 100, out _, out var full);
        check(full != null, "pasting past the layer limit is refused");
        check(MotionEdits.Find(MotionEdits.SetEnabled(doc, "m1", false), "m1") is { Enabled: false } &&
              MotionEdits.Find(MotionEdits.SetBones(doc, "m2", [Hand, Hand, ""]), "m2")!.Bones.SequenceEqual([Hand]),
            "layers turn off, and a bone limit keeps each named bone once");
        var replaced = MotionEdits.Replace(doc, doc.Motion[1] with { Amount = 9, FadeIn = 80, FadeOut = 80, Name = "  Bigger  ", Kind = MotionLayerKind.Pose });
        check(MotionEdits.Find(replaced, "m2") is { Amount: MotionEvaluator.MaxAmount, FadeIn: 80, FadeOut: 20, Name: "Bigger", Kind: MotionLayerKind.Amplitude },
            "a replacement keeps the layer's kind and an amount, fades and name it can use");
        check(MotionEdits.Find(MotionEdits.Replace(doc, doc.Motion[0] with { Kind = MotionLayerKind.Pose, Amount = 3 }), "m1") is { Kind: MotionLayerKind.Offset, Amount: 3 } &&
              MotionEdits.Find(MotionEdits.Replace(Layered(Empty()) with { Motion = [doc.Motion[0] with { Kind = MotionLayerKind.Pose }] },
                  doc.Motion[0] with { Kind = MotionLayerKind.Pose, Amount = 3 }), "m1") is { Amount: 1 },
            "a pose blends at most fully; every other kind goes to 400%");
        check(ReferenceEquals(MotionEdits.Replace(doc, doc.Motion[0] with { }), doc) && MotionEdits.Same(doc.Motion[0], doc.Motion[0] with { Keys = [Key(30, 10), Key(50, 20)] }),
            "an unchanged replacement keeps the document; layers compare by content");
    }

    private static void Angles(Action<bool, string> check)
    {
        Vector3[] samples = [new(10, 20, 30), new(-45, 60, 170), new(0, 0, 0), new(90, -30, -90), new(179, 1, -179)];
        check(samples.All(angles =>
                  {
                      var back = MotionAngles.ToEuler(MotionAngles.FromEuler(angles));
                      var wrapped = MotionAngles.Wrap(back - angles);
                      return MathF.Abs(wrapped.X) < .01f && MathF.Abs(wrapped.Y) < .01f && MathF.Abs(wrapped.Z) < .01f;
                  }),
            "angles turn into a rotation and back");
        var rotations = new[]
        {
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 1.2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(-3, .5f, 2)), -2.5f),
        };
        check(rotations.All(q => 1 - MathF.Abs(Quaternion.Dot(MotionAngles.FromEuler(MotionAngles.ToEuler(q)), q)) < 1e-5f),
            "a rotation turns into angles and back, gimbal lock at 90 degrees included");
        check(Vector3.Distance(Vector3.Transform(Vector3.UnitY, MotionAngles.FromEuler(new Vector3(0, 0, 90))), -Vector3.UnitX) < 1e-5f,
            "each angle turns about its own axis");
        check(MotionAngles.Wrap(new Vector3(359, -181, 540)) == new Vector3(-1, 179, 180), "angles wrap into -180 to 180");
    }

    // A 100-frame document with a sound marker; fitted to 440 track pixels: 4 pixels per frame, frame 0 at x 100.
    // Rows: Motion 0, Expression 1, LipSync 2, Sound 3, Effect 4, Prop 5, then the layers in 6 and 7.
    private static readonly TimelineGeometry Geometry = new(Legend: 100, Ruler: 24, Row: 20, Edge: 5, Marker: 9);

    private static Vector2 At(float frame, int row, float dy = 10) => new(100 + frame * 4, 24 + row * 20 + dy);

    private static TimelineEditor Editor()
    {
        var doc = Layered(Empty() with { Events = [new("t1", "C063", TimelineLane.Sound, 50, 0, false, "sound/a.scd", TimelineAccess.Editable, null, false, ImmutableDictionary<string, double>.Empty)] });
        var editor = new TimelineEditor(doc);
        editor.SetWidth(440);
        return editor;
    }

    private static void Rows(Action<bool, string> check)
    {
        var editor = Editor();
        editor.SetUnfolded("m1", false);
        var layout = editor.Layout;
        check(layout.MotionFirstRow == 6 && layout.MotionRows == 3 && layout.LayerIndexAt(6) == 0 && layout.LayerIndexAt(7) == 1 && layout.RowCount == 9 &&
              layout.MotionRowAt(8) is { Layer: -1, Bone: null },
            "each bone layer gets a row below the event lanes, in the order they apply, and the row to add one from comes last");
        check(TimelineLayout.Build(Empty() with { Expression = true }).MotionRows == 0, "an expression's own timeline has no bone layer rows");
        TimelineHit Hit(Vector2 point) => editor.HitTest(Geometry, point);
        check(Hit(At(40, 6)) is { Part: TimelinePart.LayerBody, Layer: "m1" } &&
              Hit(At(20, 6)) is { Part: TimelinePart.LayerLeft, Layer: "m1" } &&
              Hit(At(60, 6)) is { Part: TimelinePart.LayerRight, Layer: "m1" },
            "a layer's bar moves it and its ends resize it");
        check(Hit(At(30, 6)) is { Part: TimelinePart.LayerKey, Layer: "m1", KeyFrame: 30 } && Hit(At(50.5f, 6)) is { Part: TimelinePart.LayerKey, KeyFrame: 50 },
            "a key's diamond grabs the key");
        check(Hit(At(28, 6, dy: 3)) is { Part: TimelinePart.LayerFadeIn, Layer: "m1" } && Hit(At(52, 6, dy: 3)) is { Part: TimelinePart.LayerFadeOut, Layer: "m1" } &&
              Hit(At(40, 6, dy: 3)) is { Part: TimelinePart.LayerBody },
            "the fade handles sit on the bar's top edge where the fades end");
        check(Hit(At(80, 6)) is { Part: TimelinePart.MotionLane } && Hit(new Vector2(50, 24 + 7 * 20 + 10)) is { Part: TimelinePart.Legend, Layer: "m2" } &&
              Hit(At(10, 8)) is { Part: TimelinePart.MotionLane, Layer: null } && Hit(At(10, 9)) is { Part: TimelinePart.MotionLane, Row: 8 },
            "empty space in a layer row is the bone lane, its legend names the layer, the row under the layers and the space below it add one");
        var none = new TimelineEditor(Empty());
        none.SetWidth(440);
        check(none.HitTest(Geometry, At(10, 6)) is { Part: TimelinePart.MotionLane, Layer: null }, "with no layers one empty bone row takes the first");
    }

    private static void Ordering(Action<bool, string> check)
    {
        var editor = Editor();
        static Vector2 Name(int row) => new(50, 24 + row * 20 + 10);
        string[] Order() => [.. editor.Document.Motion.Select(layer => layer.Key)];
        // Rows: m1 in 6 with its hand's row in 7, m2 in 8.
        editor.Press(editor.HitTest(Geometry, Name(8)));
        check(editor is { Gesture: TimelineGesture.LayerOrder, SelectedLayer: "m2" }, "pressing a layer's name selects it and starts moving it");
        editor.Drag(0, row: 7);
        check(Order().SequenceEqual(["m2", "m1"]), "dragged over a bone row of the layer above, it takes that layer's place");
        editor.Drag(0, row: 8);
        check(Order().SequenceEqual(["m1", "m2"]), "the places stay as they were at the press, so dragging back puts it back");
        editor.Drag(0, row: 2);
        editor.Drag(0, row: -1);
        check(Order().SequenceEqual(["m2", "m1"]), "above the layers it goes first, and off the rows it stays put");
        editor.Release();
        check(editor.History.UndoCount == 1 && editor.Gesture == TimelineGesture.None, "the drag is one undo step");

        // Now m2 in 6, m1 in 7 with its hand's row in 8.
        editor.Press(editor.HitTest(Geometry, Name(6)));
        editor.Drag(0, row: 30);
        check(Order().SequenceEqual(["m1", "m2"]), "below the layers it goes last");
        editor.Cancel();
        check(Order().SequenceEqual(["m2", "m1"]) && editor.History.UndoCount == 1, "Escape puts it back");
        editor.Press(editor.HitTest(Geometry, Name(6)));
        editor.Release();
        check(editor.History.UndoCount == 1, "a click without a drag changes nothing");
        editor.Press(editor.HitTest(Geometry, Name(8)));
        check(editor is { Gesture: TimelineGesture.Select, SelectedLayer: "m1" }, "a bone row's name only selects");
        editor.Release();
    }

    private static void BoneRows(Action<bool, string> check)
    {
        const string Arm = "j_ude_a_l";
        // m1: keys at 30 (hand and arm), 40 (arm) and 50 (hand).
        var doc = Layered(Empty());
        doc = MotionEdits.SetKey(doc, "m1", 30, Arm, Turn(5));
        doc = MotionEdits.SetKey(doc, "m1", 40, Arm, Turn(15));
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { [Arm] = 1, [Hand] = 2 };
        check(MotionEdits.KeyBones(doc.Motion[0]).SequenceEqual([Hand, Arm]) && MotionEdits.KeyBones(doc.Motion[0], order).SequenceEqual([Arm, Hand]),
            "a layer's bone rows list its keyed bones in the skeleton's order, by name while that is unknown");

        static int[] Frames(TimelineDocument doc) => [.. doc.Motion[0].Keys.Select(key => key.Frame)];
        static string[] BonesAt(TimelineDocument doc, int frame) => [.. doc.Motion[0].Keys.Single(key => key.Frame == frame).Bones.Keys.Order(StringComparer.Ordinal)];
        var moved = MotionEdits.MoveBoneKey(doc, "m1", Hand, 30, 5, out var landed);
        check(landed == 35 && Frames(moved).SequenceEqual([30, 35, 40, 50]) && BonesAt(moved, 30).SequenceEqual([Arm]) && BonesAt(moved, 35).SequenceEqual([Hand]),
            "moving one bone's key leaves the rest of the key where it was");
        var joined = MotionEdits.MoveBoneKey(doc, "m1", Hand, 30, 10, out landed);
        check(landed == 40 && Frames(joined).SequenceEqual([30, 40, 50]) && BonesAt(joined, 40).SequenceEqual([Hand, Arm]),
            "a bone's key joins the key it lands on");
        MotionEdits.MoveBoneKey(doc, "m1", Hand, 30, 40, out landed);
        check(landed == 49, "a bone's key never passes that bone's next key");
        var held = MotionEdits.SetEase(doc, "m1", 50, MotionInterpolation.Step);
        var alone = MotionEdits.MoveBoneKey(held, "m1", Hand, 50, -5, out landed);
        check(landed == 45 && Frames(alone).SequenceEqual([30, 40, 45]) && alone.Motion[0].Keys[2].Ease == MotionInterpolation.Step,
            "a key left empty goes, and a bone moved to a frame of its own keeps its key's ease");

        var eased = MotionEdits.SetEase(doc, "m1", 30, MotionInterpolation.Custom, new MotionCurve(2, 0, .5f, 3));
        check(eased.Motion[0].Keys[0] is { Ease: MotionInterpolation.Custom, Curve: { X1: 1, Y2: MotionCurve.MaxY } },
            "a key's own curve keeps its handles in range");
        check(ReferenceEquals(MotionEdits.SetEase(eased, "m1", 30, MotionInterpolation.Custom, eased.Motion[0].Keys[0].Curve), eased) &&
              MotionEdits.SetEase(eased, "m1", 30, null).Motion[0].Keys[0] is { Ease: null, Curve: null } &&
              !MotionEdits.Same(eased.Motion[0], doc.Motion[0]),
            "setting the same ease changes nothing, the layer's way drops the curve, and an ease counts as a change");

        var editor = new TimelineEditor(doc);
        editor.SetWidth(440);
        var geometry = Geometry with { Unfold = 16 };
        var legendY = (int row) => 24 + row * 20 + 10f;
        check(editor.IsUnfolded("m1") && editor.Layout.RowCount == 11, "layers start with their bone rows shown");
        editor.Press(editor.HitTest(geometry, new Vector2(92, legendY(6))));
        editor.Release();
        check(!editor.IsUnfolded("m1") && editor.Layout.RowCount == 9, "the arrow folds them away");
        check(editor.HitTest(geometry, new Vector2(92, legendY(6))) is { Part: TimelinePart.LayerUnfold, Layer: "m1" } &&
              editor.HitTest(geometry, new Vector2(92, legendY(7))) is { Part: TimelinePart.Legend, Layer: "m2" } &&
              editor.HitTest(Geometry, new Vector2(92, legendY(6))) is { Part: TimelinePart.Legend },
            "a layer with keys has an arrow at the right of its name; an amplitude has none");
        editor.Press(editor.HitTest(geometry, new Vector2(92, legendY(6))));
        editor.Release();
        var layout = editor.Layout;
        check(editor.IsUnfolded("m1") && layout.RowCount == 11 && layout.BoneAt(7) == (0, Hand) && layout.BoneAt(8) == (0, Arm) &&
              layout.LayerIndexAt(9) == 1 && layout.LayerRow(1) == 9 && layout.BoneRows(0) == 2 && layout.BoneRows(1) == 0 && layout.LayerIndexAt(7) == -1,
            "unfolding adds a row per bone under the layer and moves the next layer down");
        check(editor.History.UndoCount == 0, "folding is no edit");
        editor.BoneOrder = order;
        check(editor.Layout.BoneAt(7) == (0, Arm) && editor.Layout.BoneAt(8) == (0, Hand), "the bone rows follow the skeleton's order once it is known");
        check(editor.HitTest(geometry, At(40, 7)) is { Part: TimelinePart.BoneKey, Layer: "m1", Bone: Arm, KeyFrame: 40 } &&
              editor.HitTest(geometry, At(45, 7)) is { Part: TimelinePart.BoneLane, Layer: "m1", Bone: Arm } &&
              editor.HitTest(geometry, At(40, 8)) is { Part: TimelinePart.BoneLane, Bone: Hand } &&
              editor.HitTest(geometry, new Vector2(50, legendY(8))) is { Part: TimelinePart.Legend, Layer: "m1", Bone: Hand },
            "a bone's row grabs only that bone's keys, and its name names the bone");

        editor.Press(editor.HitTest(geometry, At(30, 8)));
        check(editor is { SelectedLayer: "m1", SelectedKey: 30, SelectedBone: Hand, Gesture: TimelineGesture.BoneKey, Playhead: 30 },
            "pressing a bone's key selects that bone's part of the key and moves the playhead there");
        editor.Drag(36, alt: true);
        editor.Release();
        check(Frames(editor.Document).SequenceEqual([30, 36, 40, 50]) && BonesAt(editor.Document, 36).SequenceEqual([Hand]) &&
              editor.SelectedKey == 36 && editor.SelectedBone == Hand && editor.History.UndoCount == 1,
            "dragging it moves that bone's value alone, as one undo step");
        editor.Key(TimelineKey.Right, alt: true);
        check(editor.SelectedKey == 37 && BonesAt(editor.Document, 37).SequenceEqual([Hand]), "Alt+arrows nudge the bone's key alone");
        editor.Key(TimelineKey.Delete);
        check(Frames(editor.Document).SequenceEqual([30, 40, 50]) && BonesAt(editor.Document, 30).SequenceEqual([Arm]) && editor.SelectedKey == null,
            "Delete takes out only the bone's value");
        editor.Press(editor.HitTest(geometry, At(30, 6)));
        editor.Release();
        check(editor is { SelectedKey: 30, SelectedBone: null }, "pressing the layer's key selects the whole key");
        editor.Undo();
        editor.Undo();
        editor.Undo();
        check(Frames(editor.Document).SequenceEqual([30, 40, 50]) && BonesAt(editor.Document, 30).SequenceEqual([Hand, Arm]), "undo puts the bone's key back");
        editor.SelectLayer("m1");
        editor.Key(TimelineKey.Delete);
        check(!editor.IsUnfolded("m1") && editor.Layout.LayerIndexAt(6) == 0 && editor.Layout.RowCount == 8, "a deleted layer's bone rows go with it");
    }

    private static void KeyPicks(Action<bool, string> check)
    {
        const string Arm = "j_ude_a_l";
        // m1: keys at 30 (hand and arm), 40 (arm) and 50 (hand).
        var doc = Layered(Empty());
        doc = MotionEdits.SetKey(doc, "m1", 30, Arm, Turn(5));
        doc = MotionEdits.SetKey(doc, "m1", 40, Arm, Turn(15));
        static int[] Frames(TimelineDocument doc) => [.. doc.Motion[0].Keys.Select(key => key.Frame)];
        static string[] BonesAt(TimelineDocument doc, int frame) => [.. doc.Motion[0].Keys.Single(key => key.Frame == frame).Bones.Keys.Order(StringComparer.Ordinal)];
        KeyPick[] wholeFirstTwo = [new(30), new(40)];

        var moved = MotionEdits.MoveKeys(doc, "m1", wholeFirstTwo, 5, out var landed, out var applied);
        check(Frames(moved).SequenceEqual([35, 45, 50]) && landed.SequenceEqual([new KeyPick(35), new KeyPick(45)]) && applied == 5,
            "picked keys move together");
        MotionEdits.MoveKeys(doc, "m1", wholeFirstTwo, 20, out landed, out applied);
        check(applied == 9 && landed.SequenceEqual([new KeyPick(39), new KeyPick(49)]), "picked keys stop together at a key that isn't picked");
        var mixed = MotionEdits.MoveKeys(doc, "m1", [new(30), new(50, Hand)], -5, out landed, out _);
        check(Frames(mixed).SequenceEqual([25, 40, 45]) && BonesAt(mixed, 25).SequenceEqual([Hand, Arm]) && BonesAt(mixed, 45).SequenceEqual([Hand]),
            "a whole key and a bone's value move together");
        var both = MotionEdits.MoveKeys(doc, "m1", [new(30, Hand), new(30, Arm)], 3, out _, out _);
        check(Frames(both).SequenceEqual([33, 40, 50]) && BonesAt(both, 33).SequenceEqual([Hand, Arm]), "two bones picked in one key land in one key");
        var joined = MotionEdits.MoveKeys(doc, "m1", [new(50, Hand)], -10, out _, out _);
        check(Frames(joined).SequenceEqual([30, 40]) && BonesAt(joined, 40).SequenceEqual([Hand, Arm]), "a picked bone's value joins a key it lands on");
        check(MotionEdits.Picks(doc.Motion[0], [new(30), new(30, Hand), new(45), new(40, Hand)]).SequenceEqual([new KeyPick(30)]),
            "a whole key's pick takes in its bones', and picks of missing keys or bones go");

        KeyPick[] all = [new(30), new(40), new(50)];
        var spread = MotionEdits.ScaleKeys(doc, "m1", all, 40, out landed, out var error);
        check(error == null && Frames(spread).SequenceEqual([30, 50, 70]) && landed.SequenceEqual([new KeyPick(30), new KeyPick(50), new KeyPick(70)]),
            "spreading picked keys over more frames plays them slower from the first");
        check(Frames(MotionEdits.ScaleKeys(doc, "m1", all, 10, out _, out error)).SequenceEqual([30, 35, 40]) && error == null,
            "over fewer frames they play faster");
        check(ReferenceEquals(MotionEdits.ScaleKeys(doc, "m1", all, 1, out _, out error), doc) && error != null,
            "a spread that would put two keys on one frame is refused");
        check(ReferenceEquals(MotionEdits.ScaleKeys(doc, "m1", [new(30), new(50)], 5, out _, out error), doc) && error != null &&
              Frames(MotionEdits.ScaleKeys(doc, "m1", [new(30), new(50)], 30, out _, out _)).SequenceEqual([30, 40, 60]),
            "a spread that would pass a key that isn't picked is refused; one that stays clear of it works");

        var deleted = MotionEdits.DeleteKeys(doc, "m1", [new(30, Hand), new(40)]);
        check(Frames(deleted).SequenceEqual([30, 50]) && BonesAt(deleted, 30).SequenceEqual([Arm]), "deleting picks takes out whole keys and single bones' values");

        var eased = MotionEdits.SetEase(doc, "m1", 30, MotionInterpolation.Step);
        var copy = MotionEdits.CopyKeys(eased, "m1", [new(30), new(50, Hand)]);
        check(copy is { Kind: MotionLayerKind.Offset, Keys: [{ Frame: 0, Ease: MotionInterpolation.Step }, { Frame: 20, Ease: null }] }, "copied keys count from the first");
        var pasted = MotionEdits.PasteKeys(eased, "m1", 70, copy!, out var picks);
        check(Frames(pasted).SequenceEqual([30, 40, 50, 70, 90]) && BonesAt(pasted, 70).SequenceEqual([Hand, Arm]) && pasted.Motion[0].Keys[3].Ease == MotionInterpolation.Step &&
              picks.SequenceEqual([new KeyPick(70), new KeyPick(90)]),
            "pasted keys land from the frame on, a whole key with its ease");
        check(ReferenceEquals(MotionEdits.PasteKeys(eased, "m2", 70, copy!, out _), eased), "keys paste only into a layer of the kind they came from");

        // Rows: 6 the layer, 7 the hand, 8 the arm.
        var editor = new TimelineEditor(doc);
        editor.SetWidth(440);
        void Click(float frame, int row, bool ctrl = false, bool shift = false)
        {
            editor.Press(editor.HitTest(Geometry, At(frame, row)), ctrl, shift);
            editor.Release();
        }
        Click(30, 6);
        Click(50, 6, ctrl: true);
        check(editor.SelectedKeys.SetEquals([new KeyPick(30), new KeyPick(50)]) && editor.SelectedKey == 50, "Ctrl+click adds a key to the picks");
        editor.Press(editor.HitTest(Geometry, At(30, 6)));
        check(editor.Gesture == TimelineGesture.LayerKeys, "pressing a picked key starts moving all of them");
        editor.Drag(33, alt: true);
        editor.Release();
        check(Frames(editor.Document).SequenceEqual([33, 40, 53]) && editor.SelectedKeys.SetEquals([new KeyPick(33), new KeyPick(53)]) &&
              editor.SelectedKey == 33 && editor.History.UndoCount == 1,
            "dragging picked keys moves them together as one undo step");
        editor.Press(editor.HitTest(Geometry, At(53, 7)));
        check(editor.Gesture == TimelineGesture.LayerKeys, "pressing a bone's value in a picked key drags the whole group");
        editor.Drag(40, alt: true);
        editor.Cancel();
        check(Frames(editor.Document).SequenceEqual([33, 40, 53]) && editor.SelectedKeys.SetEquals([new KeyPick(33), new KeyPick(53)]) && editor.History.UndoCount == 1,
            "Escape during a group drag puts the keys and the picks back");
        Click(53, 6);
        check(editor.SelectedKeys.SetEquals([new KeyPick(53)]), "a plain click on a picked key without a drag picks it alone");
        Click(33, 6);
        Click(53, 6, shift: true);
        check(editor.SelectedKeys.SetEquals([new KeyPick(33), new KeyPick(40), new KeyPick(53)]), "Shift+click picks the row's keys in between");
        editor.Key(TimelineKey.Right, alt: true);
        check(Frames(editor.Document).SequenceEqual([34, 41, 54]) && editor.SelectedKeys.SetEquals([new KeyPick(34), new KeyPick(41), new KeyPick(54)]),
            "Alt+arrows nudge every picked key");
        check(editor.SpreadKeys(40) && Frames(editor.Document).SequenceEqual([34, 48, 74]) && editor.SelectedKeys.Contains(new KeyPick(74)),
            "Spread over spreads the picks and keeps them picked");
        var before = editor.History.UndoCount;
        editor.BeginFieldEdit();
        editor.SpreadKeys(10);
        editor.SpreadKeys(60);
        editor.EndFieldEdit();
        check(Frames(editor.Document).SequenceEqual([34, 55, 94]) && editor.History.UndoCount == before + 1,
            "a field edit spreads the keys it began with, as one undo step");
        check(!editor.SpreadKeys(1) && editor.LastError != null && Frames(editor.Document).SequenceEqual([34, 55, 94]), "a colliding spread says why and changes nothing");
        editor.Key(TimelineKey.Delete);
        check(editor.Document.Motion[0].Keys.IsEmpty && editor.SelectedKeys.Count == 0, "Delete takes out every picked key");
        editor.Undo();
        editor.Undo();
        check(Frames(editor.Document).SequenceEqual([34, 48, 74]) && editor.SelectedKeys.All(pick => MotionEdits.KeyIndex(editor.Document.Motion[0], pick.Frame) >= 0),
            "undo brings the keys back and drops picks of keys it took away");
        Click(34, 6);
        Click(48, 6, ctrl: true);
        Click(74, 6, ctrl: true);
        editor.BeginFieldEdit();
        editor.SpreadKeys(1);
        editor.EndFieldEdit();
        Click(48, 6, ctrl: true);
        editor.BeginFieldEdit();
        editor.SpreadKeys(20);
        editor.EndFieldEdit();
        check(Frames(editor.Document).SequenceEqual([34, 48, 54]) && editor.SelectedKeys.SetEquals([new KeyPick(34), new KeyPick(54)]) &&
              editor.PickedKeys.Frames.Count == 2,
            "after a refused spread, the next field edit spreads the picks as they are now");

        var boxed = new TimelineEditor(doc);
        boxed.SetWidth(440);
        boxed.Press(boxed.HitTest(Geometry, At(25, 7)));
        check(boxed.Gesture == TimelineGesture.KeyBox && boxed.SelectedLayer == "m1", "pressing a bone row's empty space starts a key box");
        boxed.Drag(45, row: 8);
        check(boxed.KeyBox is { FirstRow: 7, LastRow: 8 } && boxed.SelectedKeys.SetEquals([new KeyPick(30, Hand), new KeyPick(30, Arm), new KeyPick(40, Arm)]),
            "the box picks the bones' values inside it");
        check(boxed.PickedKeys.Picks.Length == 3 && boxed.PickedKeys.Frames.SetEquals([30, 40]), "picked bone values count by frame");
        boxed.Drag(55, row: 6);
        boxed.Release();
        check(boxed.SelectedKeys.SetEquals([new KeyPick(30), new KeyPick(40), new KeyPick(50)]) && boxed.History.UndoCount == 0 && boxed.KeyBox == null,
            "over the layer's row it picks whole keys, and picking is no edit");
        check(boxed.Key(TimelineKey.Copy), "Ctrl+C copies the picked keys");
        boxed.Playhead = 70;
        check(boxed.Key(TimelineKey.Paste) && Frames(boxed.Document).SequenceEqual([30, 40, 50, 70, 80, 90]) &&
              boxed.SelectedKeys.SetEquals([new KeyPick(70), new KeyPick(80), new KeyPick(90)]),
            "Ctrl+V pastes them at the playhead and picks the copies");
        boxed.Press(boxed.HitTest(Geometry, At(50, 9)), ctrl: true);
        boxed.Drag(60, row: 9);
        check(boxed is { Gesture: TimelineGesture.KeyBox, SelectedLayer: "m2" }, "a Ctrl box on another layer's bar selects that layer");
        boxed.Cancel();
        check(boxed is { SelectedLayer: "m1", SelectedKey: 70 } && boxed.SelectedKeys.SetEquals([new KeyPick(70), new KeyPick(80), new KeyPick(90)]),
            "Escape during that box goes back to the layer and picks before it");

        var copying = Editor();
        copying.SelectAll();
        copying.Key(TimelineKey.Copy);
        copying.SelectLayer("m1", 30);
        copying.Key(TimelineKey.Copy);
        copying.ClearSelection();
        check(!copying.Key(TimelineKey.Paste) && copying.Document.Events.Length == 1, "after copying keys, Ctrl+V doesn't paste an event copied before");
    }

    private static void Gestures(Action<bool, string> check)
    {
        var editor = Editor();
        var hit = editor.HitTest(Geometry, At(40, 6));
        editor.Press(hit);
        check(editor.SelectedLayer == "m1" && editor.SelectedKey == null && editor.Selected.Count == 0 && editor.Gesture == TimelineGesture.LayerMove,
            "pressing a layer selects it and starts moving it");
        editor.Drag(45, alt: true);
        editor.Release();
        check(editor.SelectedLayerValue is { Start: 25, End: 65 } && editor.History.UndoCount == 1, "dragging a layer moves it as one undo step");
        editor.Undo();
        check(editor.SelectedLayerValue is { Start: 20, End: 60 }, "undo puts it back");

        editor.Press(editor.HitTest(Geometry, At(20, 6)));
        editor.Drag(15, alt: true);
        editor.Release();
        // The fade in now ends at 15 + 8.
        editor.Press(editor.HitTest(Geometry, At(23, 6, dy: 3)));
        editor.Drag(27, alt: true);
        editor.Release();
        check(editor.SelectedLayerValue is { Start: 15, FadeIn: 12 }, "an edge drag resizes the layer and a handle drag sets its fade");

        editor.Press(editor.HitTest(Geometry, At(30, 6)));
        check(editor.SelectedKey == 30 && editor.Playhead == 30 && editor.Gesture == TimelineGesture.LayerKey, "pressing a key selects it and moves the playhead there");
        editor.Drag(33, alt: true);
        check(editor.SelectedKey == 33, "a dragged key stays selected where it lands");
        editor.Key(TimelineKey.Escape);
        check(editor.SelectedKey == 30 && editor.SelectedLayerValue!.Keys[0].Frame == 30, "Escape puts a dragged key back");

        editor.Press(editor.HitTest(Geometry, At(30, 6)));
        editor.Release();
        editor.Key(TimelineKey.Right, alt: true);
        check(editor.SelectedKey == 31 && editor.SelectedLayerValue!.Keys[0].Frame == 31, "Alt+arrows nudge the selected key");
        editor.Key(TimelineKey.Delete);
        check(editor.SelectedLayerValue!.Keys.Length == 1 && editor.SelectedKey == null, "Delete takes out the selected key first");
        editor.Key(TimelineKey.Right, alt: true);
        check(editor.SelectedLayerValue is { Start: 16 }, "with no key selected, Alt+arrows nudge the layer");
        editor.Key(TimelineKey.Duplicate);
        check(editor.SelectedLayer == "m3" && editor.Document.Motion.Length == 3, "Ctrl+D copies the selected layer and selects the copy");
        editor.Key(TimelineKey.Delete);
        check(editor.Document.Motion.Length == 2 && editor.SelectedLayer == null, "Delete with no key selected takes out the layer");

        editor.Press(editor.HitTest(Geometry, At(50, 3)));
        check(editor.Selected.Contains("t1") && editor.SelectedLayer == null, "selecting an event clears the layer selection");
        editor.Release();
        editor.Press(editor.HitTest(Geometry, new Vector2(50, 24 + 6 * 20 + 10)));
        editor.Release();
        check(editor.SelectedLayer == "m1" && editor.Selected.Count == 0, "clicking a layer's name selects it and clears the event selection");

        check(editor.AddLayer(MotionLayerKind.Pose, 0, 100) && editor.SelectedLayerValue is { Name: "Pose 1", Kind: MotionLayerKind.Pose },
            "adding a layer selects it");
        var before = editor.History.UndoCount;
        var layer = editor.SelectedLayerValue!;
        editor.BeginFieldEdit();
        editor.Change(MotionEdits.Replace(editor.Document, layer with { Amount = .5f }));
        editor.Change(MotionEdits.Replace(editor.Document, layer with { Amount = .25f }));
        editor.EndFieldEdit();
        check(editor.SelectedLayerValue is { Amount: .25f } && editor.History.UndoCount == before + 1, "a field's changes until it is left are one undo step");
        editor.Undo();
        editor.Undo();
        check(editor.SelectedLayer == null && editor.Document.Motion.Length == 2, "undoing the add drops the selection of the layer it removed");
    }
}
