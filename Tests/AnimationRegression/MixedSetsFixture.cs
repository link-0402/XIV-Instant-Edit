using System.Collections.Immutable;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Faces from several sets: each face and lip event remembers the set it was placed from, and a timeline whose
/// faces come from more than one is mixed, with every non-resident face listed once per set. Copying them and the
/// save row are later steps.
/// </summary>
internal static class MixedSetsFixture
{
    private static readonly IReadOnlySet<string> Resident = new HashSet<string>(StringComparer.Ordinal) { "cfxf_blink", "cfxl_lip_nor1" };

    public static void Run(Action<bool, string> check)
    {
        var one = Doc("set/a", Face("n1", "cfxf_grin"), Face("n2", "cfxf_wink", "SET/A"));
        var needs = MixedFaces.Needs(one, Resident);
        check(needs is { Known: true, Mixed: false, Faces: [{ Motion: "cfxf_grin", Set: "set/a" }, { Motion: "cfxf_wink", Set: "SET/A" }] },
            "faces from one set, named in any case, aren't mixed");

        var two = Doc("set/a", Face("n1", "cfxf_grin"), Face("n2", "cfxf_pout", "set/b"));
        check(MixedFaces.Needs(two, Resident) is { Known: true, Mixed: true, Faces: [{ Motion: "cfxf_grin", Set: "set/a" }, { Motion: "cfxf_pout", Set: "set/b" }] },
            "faces from two sets are mixed, and both are listed");

        var resident = Doc("set/a", Face("n1", "cfxf_blink", "set/b"), Lip("n2", "cfxl_lip_nor1", "set/b"), Face("n3", "cfxf_grin"));
        check(MixedFaces.Needs(resident, Resident) is { Mixed: false, Faces: [{ Motion: "cfxf_grin", Set: "set/a" }] },
            "resident faces and lips are never listed, nor count as a set");

        var lips = Doc("set/a", Face("n1", "cfxf_grin"), Lip("n2", "cfxl_lip_ah", "set/b"),
            new TimelineEvent("n3", "C063", TimelineLane.Sound, 0, 0, false, "sound/x.scd", TimelineAccess.Editable, null, false,
                ImmutableDictionary<string, double>.Empty, Pack: "set/c"));
        check(MixedFaces.Needs(lips, Resident) is { Mixed: true, Faces: [{ Motion: "cfxf_grin" }, { Motion: "cfxl_lip_ah", Set: "set/b" }] },
            "lip shapes count, other lanes don't");

        var twice = Doc("set/a", Face("n1", "cfxf_grin"), Face("n2", "cfxf_grin"), Face("n3", "cfxf_grin", "set/a"));
        check(MixedFaces.Needs(twice, Resident).Faces is [{ Motion: "cfxf_grin", Set: "set/a" }], "two events of one face are listed once");

        var both = Doc("set/a", Face("n1", "cfxf_grin"), Face("n2", "cfxf_grin", "set/b"));
        check(MixedFaces.Needs(both, Resident) is { Mixed: true, Faces: [{ Motion: "cfxf_grin", Set: "set/a" }, { Motion: "cfxf_grin", Set: "set/b" }] },
            "the same motion from two sets is listed for each");

        var unknown = MixedFaces.Needs(two, null);
        check(unknown is { Known: false, Mixed: false, Faces.Length: 0 }, "without the resident faces nothing is known yet");

        var lost = Doc(null, Face("n1", "cfxf_grin"), Face("n2", "cfxf_pout", "set/b"));
        check(MixedFaces.Needs(lost, Resident) is { Known: true, Mixed: false, Faces: [{ Motion: "cfxf_grin", Set: null }, { Motion: "cfxf_pout", Set: "set/b" }] },
            "a face in no set is listed without one, and isn't a set of its own");
        check(MixedFaces.Needs(new TimelineDocument(30, 30, null, default, 1), Resident) is { Known: true, Mixed: false, Faces.Length: 0 },
            "a timeline without events needs nothing");

        // Created events carry the request's set, faces and lips only.
        var face = TimelineTemplates.Create(new TimelineAddRequest(TimelineAddKind.Expression, 0, "cfxf_grin", Pack: "emot/laugh"));
        var lip = TimelineTemplates.Create(new TimelineAddRequest(TimelineAddKind.LipSync, 0, "cfxl_lip_ah", Pack: "emot/laugh"));
        var plain = TimelineTemplates.Create(new TimelineAddRequest(TimelineAddKind.Expression, 0, "cfxf_grin"));
        var sound = TimelineTemplates.Create(new TimelineAddRequest(TimelineAddKind.Sound, 0, "sound/x.scd", Pack: "emot/laugh"));
        var effect = TimelineTemplates.Create(new TimelineAddRequest(TimelineAddKind.Effect, 0, "vfx/x.avfx", Pack: "emot/laugh"));
        check(face is [{ Pack: "emot/laugh" }] && lip is [{ Pack: "emot/laugh" }] && plain is [{ Pack: null }] &&
              sound is [{ Pack: null }] && effect is [{ Pack: null }],
            "an added face or lip remembers its set, sounds and effects don't");

        var placed = Face("n1", "cfxf_grin", "set/a");
        check(!TimelineEdits.Same(placed, placed with { Pack = "set/b" }) && !TimelineEdits.Same(placed, placed with { Pack = null }) &&
              TimelineEdits.Same(placed, placed with { Values = placed.Values.ToImmutableDictionary() }),
            "events from different sets aren't the same");

        var dir = Path.Combine(Path.GetTempPath(), "ie-mixed-sets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AnimationJournalStore(dir);
            store.SaveAutosave(Snapshot(both));
            var back = store.LoadAutosave()!.Restored;
            check(back.Events is [{ Pack: null }, { Pack: "set/b" }] && MixedFaces.Needs(back, Resident).Mixed,
                "kept edits keep each event's set");

            // An autosave written before events had a set.
            var file = Path.Combine(dir, "AnimationEdits", "timeline-autosave.json");
            var older = JsonNode.Parse(File.ReadAllText(file))!;
            foreach (var name in new[] { "Document", "Restored" })
                if (older[name]?["Events"] is JsonArray events)
                    foreach (var ev in events)
                        if (ev is JsonObject item)
                            item.Remove("Pack");
            check(!older.ToJsonString().Contains("\"Pack\"", StringComparison.Ordinal), "the older autosave has no Pack entries");
            File.WriteAllText(file, older.ToJsonString());
            var olderBack = store.LoadAutosave()!.Restored;
            check(olderBack.Events is [{ Pack: null, Path: "cfxf_grin" }, { Pack: null, Path: "cfxf_grin" }] && olderBack.FacePack == "set/a",
                "an autosave without sets loads with none");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static TimelineDocument Doc(string? pack, params TimelineEvent[] events) => new(90, 90, pack, [.. events], events.Length + 1);

    private static TimelineEvent Face(string key, string motion, string? pack = null) =>
        new(key, "C010", TimelineLane.Expression, 0, 30, true, motion, TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty.Add(TmbEntrySchema.TimeControlField, 1), Pack: pack);

    private static TimelineEvent Lip(string key, string motion, string? pack = null) =>
        new(key, "C010", TimelineLane.LipSync, 0, 40, true, motion, TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty.Add(TmbEntrySchema.TimeControlField, 0), Pack: pack);

    private static TimelineAutosave Snapshot(TimelineDocument doc)
    {
        const string pap = "chara/human/c0801/animation/a0001/bt_common/emote/dance.pap";
        var clip = new AnimationClip(pap, "loop", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("cap", 1, 1, Guid.NewGuid(), "Test", "Dance", clip, clip, [clip.GamePath],
            [new AnimationResource(clip.GamePath, "dance.pap", "hash")], DateTime.UtcNow, true);
        return new TimelineAutosave(capture, false, "file", null, doc, [], [], DateTime.UtcNow, TimelineAutosave.PlacesFormat);
    }
}
