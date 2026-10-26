using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// The Timeline window's kept unsaved edits: a document with every kind of edit, its sound edits and imports
/// come back from disk as they went in, and forgetting them leaves none. When they are kept, offered and
/// restored is the window's, checked in game.
/// </summary>
internal static class TimelineAutosaveFixture
{
    private const string Pap = "chara/human/c0801/animation/a0001/bt_common/emote/dance.pap";

    public static void Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ie-autosave-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AnimationJournalStore(dir);
            check(store.LoadAutosave() == null, "no kept edits at first");
            var kept = Snapshot();
            store.SaveAutosave(kept);
            var file = Path.Combine(dir, "AnimationEdits", "timeline-autosave.json");
            var written = File.ReadAllText(file);
            var back = store.LoadAutosave()!;
            store.SaveAutosave(back);
            check(File.ReadAllText(file) == written, "kept edits come back byte for byte");

            var doc = back.Restored;
            var sound = new TimelineSoundKey("sound/foot/step.scd", 1);
            check(doc.SoundEdits.TryGetValue(sound with { Path = "SOUND/foot/step.scd" }, out var edit) && edit.Volume == .5f &&
                  edit.Alternatives.Length == 2 && edit.Alternatives[1].Import == "instant_edit/step.scd", "sound edits come back by their key");
            check(doc.Events.Length == 2 && doc.Events[1].Look is { Glow: 2, Colors.Length: 1 } look && look.Tint == new Vector3(1, 2, 3) &&
                  look.Colors[0].To == Vector3.UnitX && doc.Events[0].Values["SoundIndex"] == 1, "events come back with their values and looks");
            check(doc.Motion is [{ Key: "m1", Keys: [{ Bones.Count: 1 }] }] && doc.NextMotionKey == 2 && doc.Cuts?.Parts.Length == 2 &&
                  doc.Cuts.Joins[0] == MotionJoin.ForCut, "bone layers and cuts come back");
            check(doc.Replacement is { Frames: 45, Report.Changed: 1, Pap: [4, 5, 6] } && doc.ExcludedBones is ["j_ude_a_l"] &&
                  doc.SoundCuts["t1"] && doc.Expression && doc.HeaderLength == 30 && doc.Length == 90 && doc.SourceLength == 100,
                  "the motion from Blender and the clip's settings come back");
            check(back.Imports is [{ GamePath: "instant_edit/step.scd", Rebuild: null } import] && import.Files["instant_edit/step.scd"] is [1, 2, 3] &&
                  back.FileHash == "file" && back.BaseHash == "base" && back.Capture.Clip.GamePath == Pap && back.Startup, "imports and the clip's identity come back");

            store.ClearAutosave();
            check(store.LoadAutosave() == null && !File.Exists(file), "forgetting the kept edits removes them");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static TimelineAutosave Snapshot()
    {
        var clip = new AnimationClip(Pap, "loop", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("cap", 1, 1, Guid.NewGuid(), "Test", "Dance", clip, clip, [clip.GamePath],
            [new AnimationResource(clip.GamePath, "dance.pap", "hash")], new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""),
            DateTime.UtcNow, true);
        var layer = new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Smooth, ["j_kubi"],
            [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kubi",
                new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f), Vector3.One)))], true);
        var step = new TimelineEvent("t1", "C063", TimelineLane.Sound, 5, 0, false, "sound/foot/step.scd", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty.Add("SoundIndex", 1), Pin: "own0");
        var effect = new TimelineEvent("n1", "C173", TimelineLane.Effect, 10, 20, true, "vfx/fx.avfx", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty, Look: new TimelineEffectEdit { Tint = new Vector3(1, 2, 3), Glow = 2, Colors = [(Vector3.One, Vector3.UnitX)] });
        var sound = new TimelineSoundEdit([new TimelineSoundAlternative("own0", 0, null, 50, 0, 0, null, null),
            new TimelineSoundAlternative("new1", -1, "instant_edit/step.scd", 50, 0, 1, .8f, 100)], false, Volume: .5f);
        var doc = new TimelineDocument(90, 100, "emot/laugh", [step, effect], 2)
        {
            Motion = [layer],
            NextMotionKey = 2,
            Cuts = new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]),
            Replacement = new MotionReplacement([4, 5, 6], "print", "hash", 45, "Walk", "Body", new BlenderMotionReport(4, 1, 0, false)),
            ExcludedBones = ["j_ude_a_l"],
            SoundCuts = ImmutableDictionary<string, bool>.Empty.Add("t1", true),
            SoundEdits = ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit>.Empty.Add(new TimelineSoundKey("sound/foot/step.scd", 1), sound),
            Expression = true,
            HeaderLength = 30,
        };
        var import = new TimelineImport("instant_edit/step.scd",
            ImmutableDictionary<string, byte[]>.Empty.Add("instant_edit/step.scd", [1, 2, 3]), 1.5f,
            Rebuild: ImmutableDictionary<string, Func<byte[], byte[]>>.Empty.Add("instant_edit/step.scd", b => b));
        return new TimelineAutosave(capture, true, "file", "base", doc, [.. doc.SoundEdits.Select(p => (p.Key, p.Value))], [import], DateTime.UtcNow);
    }
}
