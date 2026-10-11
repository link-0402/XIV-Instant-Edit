using System.Collections.Immutable;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Timeline;

/// <summary>Sound edits and pins on the timeline document: the recipe, undo, saving and preview.</summary>
internal static class TimelineSoundFixture
{
    private const string Moan = "sound/moan.scd";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Edits(check, reject);
    }

    private static TimelineEvent Sound(string key, string path = Moan, int index = 0, string? pin = null) =>
        new(key, "C063", TimelineLane.Sound, 10, 0, false, path, TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty.Add("Loop", 1).Add("SoundIndex", index).Add("PositionFlags", 1).Add("BindId", 0), Pin: pin);

    private static TimelineDocument Doc(params TimelineEvent[] events) => new(100, 100, null, [.. events], 3);

    private static void Edits(Action<bool, string> check, Action<Action, string> reject)
    {
        var scd = ScdModelFixture.RandomSet(3);
        var ev1 = Sound("n1");
        var doc = Doc(ev1, Sound("n2"));
        var key = new TimelineSoundKey(Moan, 0);
        check(key.Equals(new TimelineSoundKey("Sound/Moan.SCD", 0)) && key.GetHashCode() == new TimelineSoundKey("Sound/Moan.SCD", 0).GetHashCode() &&
              !key.Equals(new TimelineSoundKey(Moan, 1)), "keys ignore case");
        check(TimelineSoundKey.Of(Sound("n9", "Sound/Moan.scd", 1)) == new TimelineSoundKey(Moan, 1) &&
              TimelineSoundKey.Of(Sound("n9") with { Magic = "C012", Lane = TimelineLane.Effect }) == null && TimelineSoundKey.Of(Sound("n9", "")) == null,
            "a sound event's key is its path and sound index");
        var edit = TimelineSoundEdits.Initial(ScdModel.Read(scd), 0);
        check(edit is { Custom: false, Alternatives: [{ Id: "o0", Share: 33 }, { Id: "o1" }, { Id: "o2", Share: 34 }] }, "the initial edit lists the file's alternatives");
        check(TimelineSoundEdits.Apply(scd, 0, edit, _ => null).Bytes.AsSpan().SequenceEqual(scd), "an unchanged edit writes the file back unchanged");
        var editor = new TimelineEditor(doc);
        editor.Replace(ev1 with { Pin = "o1" });
        editor.SetSound(key, edit with { Alternatives = [.. edit.Alternatives.Where(a => a.Id != "o1")] });
        check(TimelineEdits.Find(editor.Document, "n1")!.Pin == null, "removing an alternative clears the pins on it");
        editor.Undo();
        check(TimelineEdits.Find(editor.Document, "n1")!.Pin == "o1" && editor.Document.SoundEdits.IsEmpty, "undo brings the alternative and its pin back");
        // Review focus 5: a path written in another case shares the edit and loses its pin with it.
        var cased = new TimelineEditor(Doc(ev1 with { Pin = "o1" }, Sound("n2", "Sound/Moan.SCD", pin: "o1")));
        cased.SetSound(new TimelineSoundKey("SOUND/MOAN.scd", 0), edit with { Alternatives = [.. edit.Alternatives.Where(a => a.Id != "o1")] });
        check(cased.Document.Events.All(e => e.Pin == null) && cased.Document.SoundEdits.Count == 1, "a path in another case shares the edit and loses its pin with it");
        cased.Undo();
        check(cased.Document.Events.All(e => e.Pin == "o1"), "undo brings both pins back");
        check(TimelineEdits.Same(doc with { SoundEdits = doc.SoundEdits.Add(key, edit) }, doc with { SoundEdits = doc.SoundEdits.Add(new("SOUND/moan.scd", 0), edit with { }) }),
            "Same compares sound edits by content and ignores path case");
        check(!TimelineEdits.Same(doc with { SoundEdits = doc.SoundEdits.Add(key, edit) }, doc with { SoundEdits = doc.SoundEdits.Add(key, edit with { Volume = 0.5f }) }) &&
              !TimelineEdits.Same(doc, Doc(ev1 with { Pin = "o1" }, Sound("n2"))), "Same sees a changed sound edit and a changed pin");
        check(TimelineEdits.Replace(doc, ev1 with { Pin = "o1", Path = "sound/other.scd" }, null) is var r && TimelineEdits.Find(r, "n1")!.Pin == null,
            "a pin doesn't survive a change of file");
        check(TimelineEdits.Find(TimelineEdits.Replace(doc, ev1 with { Pin = "o1", Values = ev1.Values.SetItem("SoundIndex", 1) }, null), "n1")!.Pin == null,
            "a pin doesn't survive a change of sound index");
        check(TimelineEdits.Find(TimelineEdits.Replace(doc, ev1 with { Pin = "o1", Path = "Sound/Moan.scd" }, null), "n1")!.Pin == "o1",
            "a pin survives the same file written in another case");
        var removed = TimelineEdits.SetSound(doc with { SoundEdits = doc.SoundEdits.Add(key, edit) }, key, null);
        check(removed.SoundEdits.IsEmpty && ReferenceEquals(TimelineEdits.SetSound(removed, key, null), removed), "a sound edit can be dropped, and dropping none changes nothing");
        var added = edit with { Alternatives = [.. edit.Alternatives, new TimelineSoundAlternative("a1", 0, "sound/instant_edit/new.scd", 0, 0, 0, null, null)] };
        var pinnedAdded = TimelineEdits.SetSound(Doc(ev1 with { Pin = "a1" }, Sound("n2", pin: "o2")), key, added);
        check(TimelineEdits.SetSound(pinnedAdded, key, null).Events.Select(e => e.Pin).SequenceEqual([null, "o2"]),
            "dropping the edit clears pins on added sounds and keeps pins on the file's own");
        var tmb = TmbFileFixture.Sample();
        var tmbDoc = TimelineConvert.Read(TmbFile.Parse(tmb));
        var tmbSound = tmbDoc.Events.Single(e => e.Magic == "C063");
        check(ReferenceEquals(TimelineConvert.Apply(tmb, tmbDoc), tmb), "a timeline without sound edits saves as before");
        reject(() => TimelineConvert.Apply(tmb, tmbDoc with { SoundEdits = tmbDoc.SoundEdits.Add(TimelineSoundKey.Of(tmbSound)!.Value, edit) }), "an unbaked sound edit can't be saved");
        reject(() => TimelineConvert.Apply(tmb, tmbDoc with { Events = [.. tmbDoc.Events.Select(e => e == tmbSound ? e with { Pin = "o0" } : e)] }), "an unbaked pin can't be saved");
        var changed = edit with { Volume = 0.5f };
        check(ScdModel.Read(TimelineSoundEdits.Apply(scd, 0, changed, _ => null).Bytes).Sounds[0].Volume == 0.5f, "the recipe's file volume is applied");
        // The recipe's order: replace, add, remove, chances, volume and wait per sound.
        var clipFile = AudioFixture.Template();
        var recipe = edit with
        {
            Alternatives =
            [
                edit.Alternatives[0] with { Import = "sound/instant_edit/clip.scd", Entry = 0 },
                edit.Alternatives[2] with { Share = 40, Volume = 0.5f, WaitMs = 777 },
                new TimelineSoundAlternative("a1", 0, "sound/instant_edit/clip.scd", 30, 0, 0, null, null),
            ],
        };
        var (bytes, ids) = TimelineSoundEdits.Apply(scd, 0, recipe with { Alternatives = [recipe.Alternatives[0] with { Share = 30 }, .. recipe.Alternatives[1..]] },
            p => p == "sound/instant_edit/clip.scd" ? clipFile : null);
        var applied = ScdModel.Read(bytes);
        var clipSeconds = ScdFile.Read(clipFile).Audio[0].Seconds;
        check(ids.SequenceEqual(["o0", "o2", "a1"]) && ScdEdits.Shares(applied.Sounds[0]) is { Shares: [30, 40, 30], Nothing: 0 } &&
              applied.Audio[applied.Sounds[0].Entries[0].Audio].Seconds == clipSeconds && applied.Audio[applied.Sounds[0].Entries[2].Audio].Seconds == clipSeconds &&
              ScdEdits.TrackVolume(applied, applied.Sounds[0].Entries[1].Track) == 0.5f && ScdEdits.Wait(applied, applied.Sounds[0].Entries[1].Track) == 777,
            "the recipe replaces, adds, removes, and sets chances, volume and wait, in written order");
        reject(() => TimelineSoundEdits.Apply(scd, 0, recipe, _ => null), "a recipe whose added sound is missing is refused");
        reject(() => TimelineSoundEdits.Apply(scd, 0, edit with { Alternatives = [.. edit.Alternatives.Select(a => a with { Share = 50 })] }, _ => null),
            "chances over 100% can't be written");
        var oneShot = AudioFixture.Template();
        var made = TimelineSoundEdits.Initial(ScdModel.Read(oneShot), 0);
        var random = made with { Random = true, Alternatives = [made.Alternatives[0] with { Share = 50 }, new TimelineSoundAlternative("a1", 0, "sound/instant_edit/clip.scd", 50, 0, 0, null, null)] };
        check(ScdModel.Read(TimelineSoundEdits.Apply(oneShot, 0, random, _ => clipFile).Bytes).Sounds[0] is { Type: 12, Entries.Count: 2 },
            "a one-shot becomes a random set through the recipe");
    }
}
