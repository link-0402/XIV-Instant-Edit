using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
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
            check(doc.Events.Length == 3 && doc.Events[1].Look is { Glow: 2, Colors.Length: 1 } look && look.Tint == new Vector3(1, 2, 3) &&
                  look.Colors[0].To == Vector3.UnitX && doc.Events[0].Values["SoundIndex"] == 1, "events come back with their values and looks");
            var keptSource = kept.Restored.Events[2].Source!;
            check(doc.Events[2].Source is { } source && source with { Files = keptSource.Files, Textures = keptSource.Textures } == keptSource &&
                  source.Files.OrderBy(p => p.Key).SequenceEqual(keptSource.Files.OrderBy(p => p.Key)) &&
                  source.Textures.OrderBy(p => p.Key).SequenceEqual(keptSource.Textures.OrderBy(p => p.Key)) &&
                  doc.Events[0].Source == null && doc.Events[1].Source == null,
                "a prop from a mod comes back with its source, and the other events without one");

            // An autosave written before events had a source.
            var older = JsonNode.Parse(written)!;
            foreach (var name in new[] { "Document", "Restored" })
                if (older[name]?["Events"] is JsonArray events)
                    foreach (var ev in events)
                        if (ev is JsonObject item && item.ContainsKey("Source") && item["Source"] is null)
                            item.Remove("Source");
            check(!older.ToJsonString().Contains("\"Source\":null", StringComparison.Ordinal), "the older autosave has no Source entries");
            older.AsObject().Remove("Format");
            File.WriteAllText(file, older.ToJsonString());
            var olderBack = store.LoadAutosave()!;
            check(olderBack.Restored.Events[0].Source == null && olderBack.Restored.Events[1].Source == null &&
                  olderBack.Restored.Events[2].Source is { Body: 9000 }, "an autosave without sources loads with none");
            check(back.Format == TimelineAutosave.PlacesFormat && olderBack.Format == 0,
                "kept edits say they keep places; an older build's, written without a format, read as format 0");
            check(doc.Events[1].Look is { Stop: 12 } placed && placed.Move == new Vector3(0, .25f, 0) && placed.Turn == new Vector3(0, MathF.PI / 2, 0) &&
                  doc.Events[2] is { Group: "p1", Place: { State: 1, OwnState: true, Bone: "n_buki_l", Size: 1.25f } prop } &&
                  prop.Move == new Vector3(.1f, 0, 0) && prop.Turn == new Vector3(0, 0, .5f),
                "an effect's move, turn and stop and a prop's group and place come back");
            check(doc.Motion is [{ Key: "m1", Keys: [{ Bones.Count: 1 }] }] && doc.NextMotionKey == 2 && doc.Cuts?.Parts.Length == 2 &&
                  doc.Cuts.Joins[0] == MotionJoin.ForCut, "bone layers and cuts come back");
            check(doc.Replacement is { Frames: 45, Report.Changed: 1, Pap: [4, 5, 6] } && doc.ExcludedBones is ["j_ude_a_l"] &&
                  doc.SoundCuts["t1"] && doc.Expression && doc.HeaderLength == 30 && doc.Length == 90 && doc.SourceLength == 100,
                  "the motion from Blender and the clip's settings come back");
            check(back.Imports is [{ GamePath: "instant_edit/step.scd", Rebuild: null } import] && import.Files["instant_edit/step.scd"] is [1, 2, 3] &&
                  back.FileHash == "file" && back.BaseHash == "base" && back.Capture.Clip.GamePath == Pap && back.Startup, "imports and the clip's identity come back");

            store.ClearAutosave();
            check(store.LoadAutosave() == null && !File.Exists(file), "forgetting the kept edits removes them");

            // A partner's workspace keeps its edits in a file of its own.
            var partner = Guid.NewGuid().ToString("N");
            check(AnimationJournalStore.AutosaveName(null) == "timeline-autosave.json" &&
                  AnimationJournalStore.AutosaveName(partner) == $"timeline-autosave-{partner}.json",
                "a partner workspace's kept edits are named by its preset, yours stay timeline-autosave.json");
            store.SaveAutosave(kept, partner);
            check(!File.Exists(file) && File.Exists(Path.Combine(dir, "AnimationEdits", AnimationJournalStore.AutosaveName(partner))),
                "a partner's kept edits leave yours alone");
            store.SaveAutosave(kept);
            store.ClearAutosave(partner);
            check(store.LoadAutosave(partner) == null && store.LoadAutosave() != null, "forgetting a partner's kept edits leaves yours");
            store.SaveAutosave(kept with { FileHash = "partner" }, partner);
            check(store.LoadAutosave(partner)?.FileHash == "partner" && store.LoadAutosave()?.FileHash == "file", "each workspace reads its own kept edits");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static TimelineAutosave Snapshot()
    {
        var clip = new AnimationClip(Pap, "loop", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("cap", 1, 1, Guid.NewGuid(), "Test", "Dance", clip, clip, [clip.GamePath],
            [new AnimationResource(clip.GamePath, "dance.pap", "hash")],
            DateTime.UtcNow, true);
        var layer = new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Smooth, ["j_kubi"],
            [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kubi",
                new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f), Vector3.One)))], true);
        var step = new TimelineEvent("t1", "C063", TimelineLane.Sound, 5, 0, false, "sound/foot/step.scd", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty.Add("SoundIndex", 1), Pin: "own0");
        var effect = new TimelineEvent("n1", "C173", TimelineLane.Effect, 10, 20, true, "vfx/fx.avfx", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty, Look: new TimelineEffectEdit
            {
                Tint = new Vector3(1, 2, 3), Glow = 2, Colors = [(Vector3.One, Vector3.UnitX)], Move = new Vector3(0, .25f, 0),
                Turn = new Vector3(0, MathF.PI / 2, 0), Stop = 12,
            });
        var source = new TimelinePropSource("Telephone", "Telephone", "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", 9001, 9000,
            ImmutableDictionary<string, string>.Empty
                .Add("chara/weapon/w9001/obj/body/b9000/model/w9001b9000.mdl", @"file:C:\mods\Telephone\m8044b0001.mdl")
                .Add("chara/weapon/w9001/obj/body/b9000/texture/d.tex", "game:chara/monster/m8044/obj/body/b0001/texture/d.tex"),
            ImmutableDictionary<string, string>.Empty.Add("chara/monster/m8044/obj/body/b0001/texture/d.tex", "chara/weapon/w9001/obj/body/b9000/texture/d.tex"));
        // A prop from a mod that also has a place: both come back.
        var prop = new TimelineEvent("n2", "C198", TimelineLane.Prop, 0, 60, true, "", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty.Add("Model", 9001).Add("Body", 9000).Add("SummonId", 1), Group: "p1",
            Place: new TimelinePropPlace(1, true, "n_buki_l", new Vector3(.1f, 0, 0), new Vector3(0, 0, .5f), 1.25f), Source: source);
        var sound = new TimelineSoundEdit([new TimelineSoundAlternative("own0", 0, null, 50, 0, 0, null, null),
            new TimelineSoundAlternative("new1", -1, "instant_edit/step.scd", 50, 0, 1, .8f, 100)], false, Volume: .5f);
        var doc = new TimelineDocument(90, 100, "emot/laugh", [step, effect, prop], 3)
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
        return new TimelineAutosave(capture, true, "file", "base", doc, [.. doc.SoundEdits.Select(p => (p.Key, p.Value))], [import], DateTime.UtcNow,
            TimelineAutosave.PlacesFormat);
    }
}
