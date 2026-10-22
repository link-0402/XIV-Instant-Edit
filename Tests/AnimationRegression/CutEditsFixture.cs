using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Cutting a timeline document: the cut list it keeps, and the ripple that moves, shortens or
/// removes its events and bone layer keys when frames go or come back.
/// </summary>
internal static class CutEditsFixture
{
    private static readonly ImmutableDictionary<string, double> NoValues = ImmutableDictionary<string, double>.Empty;

    private static TimelineEvent Ev(string key, TimelineLane lane, int start, int duration = 0, bool resizable = false,
        TimelineAccess access = TimelineAccess.Editable, string magic = "C063") =>
        new(key, magic, lane, start, duration, resizable, "", access, access == TimelineAccess.Editable ? null : "Kept as is", false, NoValues);

    /// <summary>
    /// A 100-frame clip: its motion (locked, the whole length), sounds at 10, 45 and 70, an effect over
    /// 30 to 60, a locked entry at 50, and an offset layer over 20 to 90 with keys at 20, 40, 50, 60 and 90.
    /// </summary>
    internal static TimelineDocument Doc(int length)
    {
        var keys = new[] { 20, 40, 50, 60, 90 }
            .Select(f => new MotionKey(f, ImmutableDictionary<string, BoneTransform>.Empty))
            .ToImmutableArray();
        var layer = new MotionLayer("m1", MotionLayerKind.Offset, "Offset", 20, 90, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [], keys);
        return new TimelineDocument(length, length, null,
        [
            Ev("t1", TimelineLane.Motion, 0, length, access: TimelineAccess.Locked, magic: "C009"),
            Ev("t2", TimelineLane.Sound, 10),
            Ev("t3", TimelineLane.Sound, 45),
            Ev("t4", TimelineLane.Effect, 30, 30, resizable: true, magic: "C012"),
            Ev("t5", TimelineLane.Sound, 70),
            Ev("t6", TimelineLane.Other, 50, access: TimelineAccess.Locked, magic: "C031"),
        ], 1) { Motion = [layer] };
    }

    internal static TimelineEvent? Find(TimelineDocument doc, string key) => TimelineEdits.Find(doc, key);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Ripple(check);
        Editor(check);
        Blends(check);
        Bake(check);
        Stretched(check);
    }

    /// <summary>Cut edits on a clip whose middle part plays at a third of the speed: frames convert through the piecewise map.</summary>
    private static void Stretched(Action<bool, string> check)
    {
        var three = CutEdits.CutAt(CutEdits.CutAt(Doc(100), 40), 60);    // parts 0-40, 40-60, 60-100
        var cuts = three.Cuts!;
        var slowDoc = three with { Length = 140, Cuts = cuts with { Parts = cuts.Parts.SetItem(1, cuts.Parts[1] with { Stretch = 3 }) } };
        check(CutEdits.CutAt(slowDoc, 70).Cuts!.Parts is [_, { From: 40, To: 50, Stretch: 3 }, { From: 50, To: 60, Stretch: 3 }, _],
            "C keeps the stretch on both pieces");
        check(CutEdits.TrimPart(slowDoc, 1, left: false, -9, out _) is { Length: 131, Cuts.Parts: [_, { To: 57, Stretch: 3 }, _] },
            "trimming a part at a third of the speed takes a source frame per 3 output frames");
        check(CutEdits.RemoveRange(slowDoc, 30, 70, out _) is { Length: 100, Cuts.Parts: [{ To: 30 }, { From: 50, To: 60, Stretch: 3 }, _] },
            "removing frames across a normal and a stretched part removes the output frames shown");
        var gapped = CutEdits.RemoveRange(slowDoc, 100, 110, out _);               // the last part now starts at source frame 70
        check(CutEdits.TrimPart(gapped, 2, left: true, -10, out _).Cuts!.Parts is [_, { To: 60, Stretch: 3 }, { From: 60 }],
            "a part trimmed back to a stretched neighbour's frames meets it but doesn't merge with it");
        check(CutEdits.SetBlend(slowDoc, 1, true) is { Length: 140, Cuts.Parts: [_, { Frames: 60, Blend: true, Stretch: 1 }, _] },
            "a stretched part turned into a blend keeps its output length");
        check(TimelineSoundCuts.Spans(CutEdits.RemoveRange(slowDoc, 100, 110, out _), 0, 10).Length > 0, "sound cuts still find the removed frames");

        // At stretch 1 the conversions round exactly as Kept / Length did, .5 ties included.
        var tie = Doc(44) with { Cuts = new MotionCuts(44, [new MotionPart(0, 30)], []) };                 // Kept 30, Length 44
        check(CutEdits.TrimPart(tie, 0, left: false, -11, out _).Cuts!.Parts is [{ To: 22 }], "-11 output frames at 30/44 round to -8 source frames");
        var tieIn = Doc(100) with { Length = 75, Cuts = new MotionCuts(100, [new MotionPart(0, 2)], []) };  // Kept 2, Length 75
        check(CutEdits.TrimPart(tieIn, 0, left: false, 112, out _) is { Length: 188, Cuts.Parts: [{ To: 5 }] },
            "3 source frames brought back at 75/2 take 113 output frames");
        var tiny = CutEdits.CutAt(CutEdits.CutAt(Doc(100), 40), 41);                                     // parts 0-40, 40-41, 41-100
        var tinyCuts = tiny.Cuts!;
        var tinyBlend = CutEdits.SetBlend(tiny with { Cuts = tinyCuts with { Parts = tinyCuts.Parts.SetItem(1, tinyCuts.Parts[1] with { Stretch = 0.25 }) } }, 1, true);
        check(tinyBlend.Cuts!.Parts is [_, { Frames: 1, Blend: true, Stretch: 1 }, _] && Validates(tinyBlend.Cuts!),
            "a one-frame part at four times the speed turned into a blend keeps a frame");

        // SetPartLength: t3 at 45, t5 at 70, layer keys at 20, 40, 50, 60 and 90.
        var slowed = CutEdits.SetPartLength(three, 1, 60, out _);
        check(slowed is { Length: 140, Cuts.Parts: [{ Stretch: 1 }, { Stretch: 3 }, { Stretch: 1 }] } &&
              slowed.Cuts!.PartStart(1, 140) == 40 && slowed.Cuts.PartEnd(1, 140) == 100 && slowed.Cuts.PartEnd(0, 140) == 40,
            "playing the middle part over 60 frames triples its length; the parts around it keep their speed");
        check(Find(slowed, "t3")!.Start == 55 && Find(slowed, "t5")!.Start == 110 && Find(slowed, "t2")!.Start == 10 &&
              slowed.Motion[0].Keys.Select(k => k.Frame).SequenceEqual([20, 40, 70, 100, 130]),
            "events and keys inside the part stretch with it, those after it move, those before stay");
        var squeezed = CutEdits.SetPartLength(three, 1, 5, out var merged);
        check(squeezed.Length == 85 && squeezed.Motion[0].Keys.Select(k => k.Frame).SequenceEqual([20, 40, 43, 45, 75]) && merged == new CutRemoval(0, 0),
            "squeezing keys that land on frames of their own merges none of them");
        BoneTransform Moved(float x) => new(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);
        var close = three with
        {
            Motion = [three.Motion[0] with
            {
                Keys = [new MotionKey(50, ImmutableDictionary<string, BoneTransform>.Empty.Add("a", Moved(1)).Add("b", Moved(1))),
                        new MotionKey(52, ImmutableDictionary<string, BoneTransform>.Empty.Add("a", Moved(2)))],
            }],
        };
        var joined = CutEdits.SetPartLength(close, 1, 5, out var joinedAway);                // 50 and 52 both land on 43
        check(joined.Motion[0].Keys is [{ Frame: 43 } key] && key.Bones["a"] == Moved(2) && key.Bones["b"] == Moved(1) && joinedAway.Keys == 1,
            "keys a squeeze puts on one frame merge, the later key's values winning, and the merge is counted");
        var merging = new TimelineEditor(close);
        check(merging.SetPartLength(1, 5) && merging.LastRemoval == new CutRemoval(0, 1), "Plays over says how many keys merged");
        check(CutEdits.SetPartLength(three, 1, 1, out _).Cuts!.Parts[1].Stretch == MotionPart.MinStretch &&
              CutEdits.SetPartLength(three, 1, 999, out _).Cuts!.Parts[1].Stretch == MotionPart.MaxStretch, "the stretch clamps");
        CutEdits.SetPartLength(three, 1, 999, out _).Cuts!.Validate();
        check(CutEdits.SetPartLength(slowed, 1, 20, out _) is { Length: 100 } back && back.Cuts!.Parts[1].Stretch == 1 &&
              Find(back, "t5")!.Start == 70, "back to normal speed, everything is where it was");
        // Normal speed: the same as SetPartLength back to the part's own frames while the clip's even stretch hasn't changed.
        var restored = CutEdits.SetPartLength(slowed, 1, 20, out _);
        check(CutEdits.ResetPartSpeed(slowed, 1, out _) is { Length: 100 } reset && reset.Cuts!.Parts[1].Stretch == 1 &&
              Find(reset, "t5")!.Start == 70 && Find(reset, "t3")!.Start == Find(restored, "t3")!.Start &&
              reset.Motion[0].Keys.Select(k => k.Frame).SequenceEqual(restored.Motion[0].Keys.Select(k => k.Frame)),
            "normal speed puts everything back where it was");
        // After the clip's length changed with a part slowed (Span 140, Length 150), round(L / S) through SetPartLength
        // can't land on 1; ResetPartSpeed sets it exactly and keeps the clip's even stretch (Length 150 * 100 / 140 = 107).
        var regrown = slowed with { Length = 150 };
        check(CutEdits.ResetPartSpeed(regrown, 1, out _) is { Length: 107 } normal && normal.Cuts!.Parts[1].Stretch == 1 && Validates(normal.Cuts!),
            "normal speed is exactly 1 after the clip's length changed");
        var blended = CutEdits.SetBlend(three, 1, true);
        check(ReferenceEquals(CutEdits.ResetPartSpeed(three, 1, out _), three) && ReferenceEquals(CutEdits.ResetPartSpeed(blended, 1, out _), blended) &&
              ReferenceEquals(CutEdits.ResetPartSpeed(slowed, 5, out _), slowed), "normal speed leaves a normal part, a blend part and a missing part alone");
        var blendPart = CutEdits.SetBlend(three, 1, true);
        check(ReferenceEquals(CutEdits.SetPartLength(blendPart, 1, 40, out _), blendPart), "a blend part has no speed");
        check(AnimationBakeRules.ChangesMotion(slowed) && AnimationEditService.RetimeSeconds(slowed) == 140 / 30f, "a slowed part bakes at the new length");

        var none = ImmutableDictionary<string, BoneTransform>.Empty;
        // Normal speed on the part slowed to 60 frames puts keys 49 and 50 both on 43.
        var resetting = new TimelineEditor(slowed with { Motion = [slowed.Motion[0] with { Keys = [new MotionKey(49, none), new MotionKey(50, none)] }] });
        check(resetting.ResetPartSpeed(1) && resetting.LastRemoval == new CutRemoval(0, 1), "Normal speed says how many keys merged");

        // Typing 1 and then 60 into Plays over: each keystroke counts from the document the field edit began on.
        var typing = new TimelineEditor(three);
        var typingSteps = typing.History.UndoCount;
        typing.BeginFieldEdit();
        typing.SetPartLength(1, 1);
        typing.SetPartLength(1, 60);
        typing.EndFieldEdit();
        var typed = CutEdits.SetPartLength(three, 1, 60, out _);
        check(typing.Document.Length == typed.Length && typing.Document.Cuts!.Parts[1].Stretch == typed.Cuts!.Parts[1].Stretch &&
              typing.Document.Events.Select(ev => ev.Start).SequenceEqual(typed.Events.Select(ev => ev.Start)) &&
              typing.Document.Motion[0].Keys.Select(k => k.Frame).SequenceEqual(typed.Motion[0].Keys.Select(k => k.Frame)) &&
              typing.History.UndoCount == typingSteps + 1,
            "typing a number into Plays over keys by key is the same as setting it once, as one undo step");

        // Typing 3 and then 30 into Blend frames: the 3 must not ripple the sound at 45 out of the 40-60 blend for good.
        var blendTyping = new TimelineEditor(CutEdits.SetBlend(three, 1, true));
        var blendSteps = blendTyping.History.UndoCount;
        blendTyping.BeginFieldEdit();
        blendTyping.SetBlendLength(1, 3);
        blendTyping.SetBlendLength(1, 30);
        blendTyping.EndFieldEdit();
        var blendTyped = CutEdits.SetBlendLength(CutEdits.SetBlend(three, 1, true), 1, 30, out _);
        check(blendTyping.Document.Length == blendTyped.Length && Find(blendTyping.Document, "t3")?.Start == 45 &&
              blendTyping.Document.Events.Select(ev => ev.Start).SequenceEqual(blendTyped.Events.Select(ev => ev.Start)) &&
              blendTyping.Document.Motion[0].Keys.Select(k => k.Frame).SequenceEqual(blendTyped.Motion[0].Keys.Select(k => k.Frame)) &&
              blendTyping.History.UndoCount == blendSteps + 1,
            "typing a number into Blend frames keys by key is the same as setting it once, as one undo step");

        // Parts 0-40, 40-55 played over 65 frames, 55-100: the last part starts at output 105, a float sum away from it.
        var boundary = CutEdits.SetPartLength(CutEdits.CutAt(CutEdits.CutAt(Doc(100), 40), 55), 1, 65, out _);
        var boundaryCuts = boundary.Cuts!;
        var removing = new TimelineEditor(boundary) { Playhead = 105 };
        check(boundary.Length == 150 && boundaryCuts.PartAt(boundaryCuts.PartStart(2, 150), 150) == 2 && boundaryCuts.PartAt(105, 150) == 2 &&
              !removing.Key(TimelineKey.RemoveBefore) && ReferenceEquals(removing.Document, boundary),
            "a part's start belongs to it with stretched parts before it, so Q there removes nothing of the part before");

        // Parts 0-20, 20-40 at a third of the speed, a blend over 40-60, 60-100: Length and Span 140.
        var rollCuts = new MotionCuts(100, [new MotionPart(0, 20), new MotionPart(20, 40, Stretch: 3), new MotionPart(40, 60, Blend: true), new MotionPart(60, 100)],
            [MotionJoin.ForCut, MotionJoin.ForCut, MotionJoin.ForCut]);
        var rolledSlow = CutEdits.TrimPart(Doc(100) with { Length = 140, Cuts = rollCuts }, 1, left: false, 9, out _);
        var rolledCuts = rolledSlow.Cuts!;
        check(rolledSlow.Length == 140 && rolledCuts.Span == 140 && rolledCuts.Parts is [_, { To: 43, Stretch: 3 }, { Frames: 11, Blend: true }, _] &&
              rolledCuts.PartEnd(0, 140) == 20 && rolledCuts.PartStart(3, 140) == 100 && rolledCuts.PartEnd(3, 140) == 140 && Validates(rolledCuts),
            "rolling a slowed part's end into a blend takes the blend's frames at the part's speed, so no other part moves");
        // Parts 0-40, a blend over 40-60, 60-80 at a third of the speed, 80-100: Length and Span 140.
        var rollLeftCuts = new MotionCuts(100, [new MotionPart(0, 40), new MotionPart(40, 60, Blend: true), new MotionPart(60, 80, Stretch: 3), new MotionPart(80, 100)],
            [MotionJoin.ForCut, MotionJoin.ForCut, MotionJoin.ForCut]);
        var rolledLeft = CutEdits.TrimPart(Doc(100) with { Length = 140, Cuts = rollLeftCuts }, 2, left: true, -9, out _);
        var rolledLeftCuts = rolledLeft.Cuts!;
        check(rolledLeft.Length == 140 && rolledLeftCuts.Span == 140 && rolledLeftCuts.Parts is [_, { Frames: 11, Blend: true }, { From: 57, Stretch: 3 }, _] &&
              rolledLeftCuts.PartEnd(0, 140) == 40 && rolledLeftCuts.PartStart(3, 140) == 120 && Validates(rolledLeftCuts),
            "rolling a slowed part's start into a blend does the same");

        // The last part slowed to 120 frames (Length 180), with a sound and a one-frame layer keyed at its last frame, 179.
        var slowEnd = CutEdits.SetPartLength(three, 2, 120, out _);
        var lastFrame = slowEnd with
        {
            Events = [.. slowEnd.Events, Ev("t7", TimelineLane.Sound, 179)],
            Motion = [.. slowEnd.Motion, new MotionLayer("m2", MotionLayerKind.Offset, "Offset", 179, 180, 0, 0, 1, PoseComponents.All,
                MotionInterpolation.Linear, [], [new MotionKey(179, none)])],
        };
        foreach (var squeezedEnd in (TimelineDocument[])[CutEdits.ResetPartSpeed(lastFrame, 2, out _), CutEdits.SetPartLength(lastFrame, 2, 40, out _)])
            check(squeezedEnd.Length == 100 && Find(squeezedEnd, "t7")!.Start == 99 && squeezedEnd.Motion[1] is { Start: 99, End: 100, Keys: [{ Frame: 99 }] },
                "squeezing the last part keeps what started in its last frame inside the clip");

        // Part 1 of this one plays 28.5 frames, shown as 29: typing 30 or 28 shows that number.
        var half = CutEdits.CutAt(CutEdits.CutAt(Doc(100), 41), 60) with { Length = 150 };
        int Shown(TimelineDocument d) => TimelineSnap.Round(d.Cuts!.PartEnd(1, d.Length) - d.Cuts.PartStart(1, d.Length));
        check(Shown(half) == 29 && Shown(CutEdits.SetPartLength(half, 1, 30, out _)) == 30 && Shown(CutEdits.SetPartLength(half, 1, 28, out _)) == 28,
            "Plays over shows the number typed into it, from a part of a half-frame length too");
    }

    private static bool Validates(MotionCuts cuts)
    {
        try
        {
            cuts.Validate();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void Blends(Action<bool, string> check)
    {
        var three = CutEdits.CutAt(CutEdits.CutAt(Doc(100), 40), 60);    // parts 0-40, 40-60, 60-100
        var blended = CutEdits.SetBlend(three, 1, true);
        check(blended is { Length: 100, Cuts.Parts: [{ Blend: false }, { From: 40, To: 60, Blend: true }, { Blend: false }] } &&
              blended.Events.SequenceEqual(three.Events) && CutEdits.SetBlend(blended, 1, false).Cuts!.Parts.SequenceEqual(three.Cuts!.Parts),
            "a part replaced with a blend keeps its frames, the length and the events; back again it is as before");
        var four = CutEdits.SetBlend(CutEdits.CutAt(three, 50), 1, true); // 0-40, 40-50 blend, 50-60, 60-100
        check(ReferenceEquals(CutEdits.SetBlend(three, 0, true), three) && ReferenceEquals(CutEdits.SetBlend(three, 2, true), three) &&
              ReferenceEquals(CutEdits.SetBlend(four, 2, true), four), "the first and last part, and a part next to a blend part, can't be one");
        check(ReferenceEquals(CutEdits.CutAt(blended, 50), blended), "C doesn't cut a blend part in two");
        var rolled = CutEdits.TrimPart(blended, 0, left: false, -5, out _);
        var rolledFar = CutEdits.TrimPart(blended, 2, left: true, -30, out _);
        check(rolled is { Length: 100, Cuts.Parts: [{ From: 0, To: 35 }, { From: 35, To: 60, Blend: true }, { From: 60, To: 100 }] } &&
              rolledFar is { Length: 100, Cuts.Parts: [{ To: 40 }, { From: 40, To: 41, Blend: true }, { From: 41, To: 100 }] },
            "a neighbour's edge at a blend part rolls: the blend shrinks or grows, each keeps a frame, and the length stays");
        var grownEnd = CutEdits.TrimPart(blended, 1, left: false, 10, out _);
        var grownStart = CutEdits.TrimPart(blended, 1, left: true, -5, out _);
        var shrunk = CutEdits.TrimPart(blended, 1, left: false, -5, out _);
        check(grownEnd is { Length: 110, Cuts.Parts: [{ To: 40 }, { From: 40, To: 70, Blend: true }, { From: 60, To: 100 }] } &&
              Find(grownEnd, "t5")!.Start == 80 && Find(grownEnd, "t2")!.Start == 10 &&
              grownStart is { Length: 105, Cuts.Parts: [{ To: 40 }, { Frames: 25, Blend: true }, { From: 60 }] } && Find(grownStart, "t3")!.Start == 50 &&
              shrunk is { Length: 95, Cuts.Parts: [_, { From: 40, To: 55, Blend: true }, { From: 60 }] } && Find(shrunk, "t5")!.Start == 65,
            "a blend part's own edges make it longer or shorter, and what follows moves");
        var thirty = CutEdits.SetBlendLength(blended, 1, 30, out _);
        thirty.Cuts!.Validate();
        check(thirty is { Length: 110, Cuts.Parts: [_, { Frames: 30, Blend: true }, { From: 60, To: 100 }] }, "Blend frames sets its length and moves what follows");
        var rolledIntoLong = CutEdits.TrimPart(thirty, 0, left: false, 25, out _);
        rolledIntoLong.Cuts!.Validate();
        check(rolledIntoLong is { Length: 110, Cuts.Parts: [{ To: 60 }, { Frames: 10, Blend: true }, { From: 60 }] },
            "rolling into a blend longer than the frames it replaced stops at the next part's source frames");
        check(CutEdits.SetBlend(thirty, 1, false) is { Length: 100, Cuts.Parts: [_, { From: 40, To: 60, Blend: false }, { From: 60 }] } back && back.Cuts!.Parts.Length == 3,
            "a blend longer than the source frames between its neighbours gives up the rest when it plays its motion again");
        var leftLast = CutEdits.RemovePart(thirty, 2, out _);
        leftLast.Cuts!.Validate();
        check(leftLast.Cuts!.Parts is [_, { From: 40, To: 70, Blend: false }], "a long blend part left last plays its own motion from where it started");
        check(CutEdits.RemovePart(blended, 2, out _).Cuts!.Parts is [_, { Blend: false }], "a blend part left last plays its own motion again");
        check(MotionCutter.Window(blended.Cuts!, 0, 100) == (0, 0) && MotionCutter.Window(blended.Cuts!, 1, 100) == (0, 0) &&
              MotionCutter.Window(three.Cuts!, 0, 100) != (0, 0), "the joins at a blend part's edges blend nothing");
        var editor = new TimelineEditor(three) { Loops = false };
        editor.SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, 1));
        check(editor.Key(TimelineKey.Blend) && editor.Document.Cuts!.Parts[1].Blend && editor.Key(TimelineKey.Blend) && !editor.Document.Cuts!.Parts[1].Blend &&
              editor.Undo() && editor.Document.Cuts!.Parts[1].Blend, "B turns the selected part into a blend and back, one undo step each");
    }

    private static void Bake(Action<bool, string> check)
    {
        var doc = Doc(100);
        var cutDoc = CutEdits.RemoveRange(doc, 40, 60, out _);
        check(AnimationBakeRules.ChangesMotion(cutDoc) && AnimationEditService.RetimeSeconds(cutDoc) == 80 / 30f &&
              AnimationEditService.RetimeSeconds(CutEdits.SetSeam(doc, MotionJoin.ForSeam)) == 100 / 30f &&
              AnimationEditService.RetimeSeconds(doc) == null && !AnimationBakeRules.ChangesMotion(doc),
            "cuts bake and always carry the output length; an untouched document bakes nothing");
        check(AnimationBakeRules.ChangesMotion(CutEdits.SetEase(doc, 30)) && AnimationEditService.RetimeSeconds(CutEdits.SetEase(doc, 30)) == 100 / 30f,
            "an eased start bakes at the same length");
        // A save that cuts or stretches leaves a timeline in other frames than the motion from before it: the
        // window reads the saved file back as it is, and Edit again brings the cuts and layers back.
        check(AnimationBakeRules.KeepsLayersOverSave(doc) && !AnimationBakeRules.KeepsLayersOverSave(cutDoc) &&
              !AnimationBakeRules.KeepsLayersOverSave(doc with { Length = 120 }) && !AnimationBakeRules.KeepsLayersOverSave(CutEdits.SetEase(doc, 30)) &&
              !AnimationBakeRules.KeepsLayersOverSave(doc with { ExcludedBones = ["j_kosi"] }),
            "only a save that keeps the motion's frames goes on editing its layers on the motion from before");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var journalJson = JsonSerializer.Serialize(new AnimationTimelineEdit(false, 0, 80 / 30f, [], null, cutDoc.Cuts), options);
        check(JsonSerializer.Deserialize<AnimationTimelineEdit>(journalJson, options)!.Cuts!.Parts.Length == 2 &&
              JsonSerializer.Deserialize<AnimationTimelineEdit>("{\"Startup\":false,\"EntryIndex\":0,\"DurationSeconds\":null}", options)!.Cuts == null,
            "journals carry cuts, and older ones load without");
        var cutSounds = ImmutableDictionary<string, string>.Empty.Add("sound/instant_edit/song_cut_ab.scd", "sound/test/song.scd");
        var withSounds = JsonSerializer.Serialize(new AnimationTimelineEdit(false, 0, 80 / 30f, [], null, cutDoc.Cuts, cutSounds), options);
        check(JsonSerializer.Deserialize<AnimationTimelineEdit>(withSounds, options)!.CutSounds!["sound/instant_edit/song_cut_ab.scd"] == "sound/test/song.scd" &&
              JsonSerializer.Deserialize<AnimationTimelineEdit>(journalJson, options)!.CutSounds == null,
            "journals carry the sounds a save cut and what they were cut from, and older ones load without");
    }

    private static void Editor(Action<bool, string> check)
    {
        var doc = Doc(100);
        var g = new TimelineGeometry(100, 20, 20, 5, 9, 0, 0, 30);
        var view = new TimelineView(0, 4, 400);                          // 4 px per frame
        var two = CutEdits.RemoveRange(doc, 40, 60, out _);              // boundary at output 40 = x 260
        TimelineHit At(TimelineDocument d, float x, float y = 35, bool loops = false) =>
            g.HitTest(d, TimelineLayout.Build(d), view, new Vector2(x, y), loops);
        check(At(two, 150) is { Part: TimelinePart.ClipPart, Cut: 0 } && At(two, 258) is { Part: TimelinePart.ClipRight, Cut: 0 } &&
              At(two, 262) is { Part: TimelinePart.ClipLeft, Cut: 1 }, "parts and their edges");
        check(At(two, 260 - 24) is { Part: TimelinePart.JoinEdge, Cut: 0 } && At(two, 245) is { Part: TimelinePart.Join, Cut: 0 },
            "a centered 12-frame join: its edge 6 frames out, its body");
        var looped = CutEdits.SetSeam(two, MotionJoin.ForSeam);         // Before, frames 68-80 = x 372-420
        check(At(looped, 372, loops: true).Part == TimelinePart.SeamEdge && At(looped, 385, loops: true).Part == TimelinePart.Seam &&
              At(looped, 385, loops: false).Part == TimelinePart.ClipPart && At(looped, 419, loops: true) is { Part: TimelinePart.ClipRight, Cut: 1 },
            "the seam is hit only on a looping clip; the clip's end stays a trim edge");
        check(g.RowTop(0) == 50 && At(two, 150, 55).Part != TimelinePart.ClipPart, "rows start under the clip band");
        var editor = new TimelineEditor(doc) { Loops = false };
        editor.Playhead = 30;
        check(editor.Key(TimelineKey.Cut) && editor.Document.Cuts!.Parts.Length == 2, "C cuts at the playhead");
        editor.Playhead = 70;
        check(editor.Key(TimelineKey.RemoveAfter) && editor.Document.Length == 70, "W removes from the playhead to the part's end");
        check(!editor.SetSeam(MotionJoin.ForSeam) && !editor.FixLoop(new SeamReport(new JoinGap(0, 0, "", 0, 0), 1, null, SeamState.Clean)) &&
              !editor.SetEase(30), "a clip that doesn't loop takes no seam");
        editor.SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, 0));
        check(editor.Key(TimelineKey.Delete) && editor.Document.Length == 40 && editor.Undo() && editor.Document.Length == 70, "Delete removes a part; undo brings it back");
        // Q at 50, on the part playing 30 to 70, removes 30 to 50: the events starting at 30 and 45, and the key at 40.
        editor.Playhead = 50;
        check(editor.Key(TimelineKey.RemoveBefore) && editor.Document.Length == 50 && editor.LastRemoval == new CutRemoval(2, 1),
            "Q removes from the part's start to the playhead and says what went");
        editor.Press(At(editor.Document, 150, 200));
        check(editor.LastRemoval == null, "the next click forgets what the last cut removed");

        var dragging = new TimelineEditor(two) { Snap = false };
        dragging.Press(At(two, 419));                                     // the clip's end, frame 80
        dragging.Drag(70);
        dragging.Release();
        check(dragging.Document is { Length: 70, Cuts.Parts: [_, { To: 90 }] } && dragging.Undo() && dragging.Document.Length == 80,
            "dragging the clip's end trims it as one undo step");
        dragging.Press(At(two, 236));                                     // the join's left edge, frame 34
        dragging.Drag(31);
        dragging.Release();
        check(dragging.Document.Cuts!.Joins[0].Frames == 18 && dragging.SelectedCut == new TimelineCutSelection(TimelineCutTarget.Join, 0),
            "dragging a centred blend's edge 3 frames out makes it 6 frames longer");
        var clicking = new TimelineEditor(two);
        void Click(float x) { clicking.Press(At(two, x)); clicking.Release(); }
        Click(245);                                                       // the join's body, frame 36 of part 0
        var first = clicking.SelectedCut;
        Click(245);
        var second = clicking.SelectedCut;
        Click(245);
        var third = clicking.SelectedCut;
        Click(270);                                                       // the join's body, frame 42 of part 1
        check(first == new TimelineCutSelection(TimelineCutTarget.Join, 0) && second == new TimelineCutSelection(TimelineCutTarget.Part, 0) &&
              third == first && clicking.SelectedCut == new TimelineCutSelection(TimelineCutTarget.Part, 1),
            "a selected blend clicked again selects the part under the pointer, and once more the blend");

        var three = CutEdits.CutAt(CutEdits.CutAt(Doc(100), 40), 60);    // parts 0-40, 40-60, 60-100
        var stretching = new TimelineEditor(three) { Snap = false };
        stretching.Press(At(three, 339), shift: true);   // just left of the boundary at frame 60: the middle part's right edge
        stretching.Drag(100);
        stretching.Release();
        check(stretching.Document is { Length: 140, Cuts.Parts: [_, { Stretch: 3 }, _] } && stretching.Undo() && stretching.Document.Length == 100,
            "Shift+drag on a part's end plays it slower, as one undo step");
        var cancelled = new TimelineEditor(three) { Snap = false };
        cancelled.Press(At(three, 339), shift: true);
        cancelled.Drag(100);
        cancelled.Cancel();
        check(cancelled.Document.Length == 100, "Escape cancels the stretch");
        // Keys at 50 and 52 both land on 43 when the middle part's end is dragged from 60 to 45.
        var closeKeys = three with
        {
            Motion = [three.Motion[0] with { Keys = [new MotionKey(50, ImmutableDictionary<string, BoneTransform>.Empty), new MotionKey(52, ImmutableDictionary<string, BoneTransform>.Empty)] }],
        };
        var squeezing = new TimelineEditor(closeKeys) { Snap = false };
        squeezing.Press(At(closeKeys, 339), shift: true);
        squeezing.Drag(45);
        squeezing.Release();
        check(squeezing.Document.Length == 85 && squeezing.LastRemoval == new CutRemoval(0, 1), "Shift+drag says how many keys merged");
        var trimming = new TimelineEditor(three) { Snap = false };
        trimming.Press(At(three, 339));
        trimming.Drag(50);
        trimming.Release();
        check(trimming.Document.Cuts!.Parts[1].Stretch == 1, "without Shift the edge trims as before");
        // Played 1.5 times as long, part 1 (source 41-60) runs from output 61.5 to 90: 28.5 frames.
        var wide = CutEdits.CutAt(CutEdits.CutAt(Doc(100), 41), 60) with { Length = 150 };
        var wideCuts = CutEdits.CutsOf(wide);
        var still = new TimelineEditor(wide) { Snap = false };
        var stillSteps = still.History.UndoCount;
        still.Press(At(wide, 459), shift: true);                          // frame 89.75, part 1's right edge
        still.Drag(89.75f);
        still.Release();
        check(wideCuts.PartEnd(1, 150) - wideCuts.PartStart(1, 150) == 28.5 && At(wide, 459) is { Part: TimelinePart.ClipRight, Cut: 1 } &&
              ReferenceEquals(still.Document, wide) && still.History.UndoCount == stillSteps,
            "a Shift press on a part's end that doesn't move changes nothing, even on a part of a half-frame length");
        check(CutEdits.StretchPart(three, 1, 40, out _) is { Length: 140, Cuts.Parts: [_, { Stretch: 3 }, _] } sp &&
              CutEdits.SetPartLength(three, 1, 60, out _) is { } sl && sl.Length == sp.Length && sl.Cuts!.Parts[1].Stretch == sp.Cuts!.Parts[1].Stretch,
            "stretching a part by 40 frames is the same edit as setting it to play over 60");

        var eased = CutEdits.SetEase(two, 20);                            // frames 0-20 = x 100-180
        check(At(eased, 180, loops: true).Part == TimelinePart.EaseEdge && At(eased, 150, loops: true).Part == TimelinePart.Seam &&
              At(eased, 180).Part == TimelinePart.ClipPart, "the ease's edge and body are hit only on a looping clip");
        var easing = new TimelineEditor(eased) { Snap = false, Loops = true };
        var steps = easing.History.UndoCount;
        easing.Press(At(eased, 180, loops: true));
        easing.Drag(26);
        easing.Release();
        check(easing.Document.Cuts!.Ease == 26 && easing.History.UndoCount == steps + 1 &&
              easing.SelectedCut == new TimelineCutSelection(TimelineCutTarget.Seam, -1),
            "dragging the ease's edge sets its length as one undo step and selects the seam");
        var endEased = CutEdits.SetEase(two, 20, atEnd: true);            // output frames 60-80 = x 340-420
        check(At(endEased, 340, loops: true).Part == TimelinePart.EaseEdge && At(endEased, 380, loops: true).Part == TimelinePart.Seam,
            "an end's ease is hit at the end");
        var easingEnd = new TimelineEditor(endEased) { Snap = false, Loops = true };
        easingEnd.Press(At(endEased, 340, loops: true));
        easingEnd.Drag(56);
        easingEnd.Release();
        check(easingEnd.Document.Cuts is { Ease: 24, EaseEnd: true }, "dragging an end's ease edge left makes it longer");
    }

    private static void Ripple(Action<bool, string> check)
    {
        var doc = Doc(100);
        var cut = CutEdits.RemoveRange(doc, 40, 60, out var gone);
        check(cut.Length == 80 && cut.Cuts is { Parts: [{ From: 0, To: 40 }, { From: 60, To: 100 }], Joins: [var j] } && j == MotionJoin.ForCut,
            "removing 40-60 leaves two parts and a default join");
        check(Find(cut, "t1")!.Duration == 80 && Find(cut, "t2")!.Start == 10 && Find(cut, "t3") == null &&
              Find(cut, "t4") is { Start: 30, Duration: 10 } && Find(cut, "t5")!.Start == 50 && Find(cut, "t6")!.Start == 40,
            "events ripple: inside goes, spanning shortens, after moves left, locked moves to the cut");
        check(cut.Motion[0] is { Start: 20, End: 70 } m && m.Keys.Select(k => k.Frame).SequenceEqual([20, 40, 70]) && gone == new CutRemoval(1, 2),
            "a key at the range's start goes, the key at its end lands on it");
        check(ReferenceEquals(CutEdits.CutAt(cut, 40), cut) && CutEdits.CutAt(doc, 30).Cuts!.Parts.Length == 2 && CutEdits.CutAt(doc, 30).Length == 100,
            "a cut on a boundary changes nothing; a new cut keeps the length");
        var back = CutEdits.TrimPart(cut, 1, left: true, -20, out _);
        check(back.Cuts == null && back.Length == 100 && Find(back, "t5")!.Start == 70, "trimming back over the removed frames merges the parts");
        var half = CutEdits.TrimPart(cut, 1, left: true, -10, out _);
        check(half.Cuts!.Parts[1] == new MotionPart(50, 100) && half.Length == 90 && Find(half, "t5")!.Start == 60, "a partial trim back inserts frames");
        var stretched = CutEdits.RemoveRange(doc with { Length = 200 }, 80, 120, out _);
        check(stretched.Length == 160 && stretched.Cuts!.Parts[1].From == 60, "a stretch maps output frames to source frames");
        var slowEnd = CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", .2f, 1.6f), 1.4f, null, SeamState.Stalls));
        check(slowEnd is { Length: 100, Cuts: { Parts: [{ From: 0, To: 100 }], Seam: null, Ease: MotionCuts.DefaultEase } },
            "Fix loop eases the start of a loop that arrives slower than it leaves, trimming nothing");
        var held = CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", 0, 1.6f), 1.4f, 88, SeamState.Stalls));
        check(held is { Length: 88, Cuts: { Parts: [{ To: 88 }], Seam: null, Ease: MotionCuts.DefaultEase } },
            "Fix loop trims an end that holds frame 0's pose, then eases the start");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(20, 30, "x", 3, 3), 3, null, SeamState.Pops)) is
              { Length: 100, Cuts: { Seam: not null, Ease: 0 } },
            "a pose jump gets a blend and keeps the length");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(20, 30, "x", .2f, 3), 3, null, SeamState.Stalls)) is
              { Cuts: { Seam: not null, Ease: 0 } },
            "a pose jump into a faster start gets only the blend, which matches the speeds too");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", 3, .5f), 1.4f, null, SeamState.Stalls)) is
              { Length: 100, Cuts: { Ease: MotionCuts.DefaultEase, EaseEnd: true, Seam: null } } &&
              slowEnd.Cuts!.EaseEnd == false, "a loop that arrives faster than it leaves eases its end instead");
        // A blend and an ease fight over the speed at the seam: Fix loop keeps one of them.
        check(CutEdits.FixLoop(slowEnd, new SeamReport(new JoinGap(20, 30, "x", 3, 3), 3, null, SeamState.Pops)).Cuts is { Seam: not null, Ease: 0 } &&
              CutEdits.FixLoop(CutEdits.SetSeam(doc, MotionJoin.ForSeam), new SeamReport(new JoinGap(0, 0, "", .2f, 1.6f), 1.4f, null, SeamState.Stalls)).Cuts is
                  { Seam: null, Ease: MotionCuts.DefaultEase },
            "Fix loop sets a blend or an ease, never both");
        check(CutEdits.SetEase(doc, 90).Cuts!.Ease == 60 && CutEdits.SetEase(CutEdits.SetEase(doc, 20), 0).Cuts == null &&
              CutEdits.SetEase(doc, 20, atEnd: true).Cuts is { Ease: 20, EaseEnd: true } && CutEdits.SetEase(slowEnd, 30, atEnd: true).Cuts!.EaseEnd,
            "an ease clamps to 60, takes a side, and none leaves the motion whole");
        check(ReferenceEquals(CutEdits.RemoveRange(doc, 1, 100, out _), doc), "a cut leaving under 2 frames is refused");
        var expression = doc with { Expression = true };
        check(ReferenceEquals(CutEdits.RemoveRange(expression, 10, 20, out _), expression), "an expression's timeline is never cut");
        check(CutEdits.SetJoin(cut, 0, new MotionJoin(90, JoinPlacement.After)).Cuts!.Joins[0] == new MotionJoin(60, JoinPlacement.After), "blends clamp to 60");
        check(cut is { MotionLength: 100, KeptFrames: 80, Cut: true, Stretched: false } && doc is { Cut: false, Stretched: false, KeptFrames: 100 } &&
              stretched is { KeptFrames: 80, Stretched: true }, "a document knows its motion's length, the frames it keeps and whether it is cut or stretched");
    }
}
