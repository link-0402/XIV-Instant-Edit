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
        Baking(check, reject);
        Usage(check, reject);
        Listening(check, reject);
    }

    private static void Listening(Action<bool, string> check, Action<Action, string> reject)
    {
        var scd = ScdModelFixture.RandomSet(3);
        var plan = TimelineListen.For(scd);
        check(plan.GamePath == $"sound/instant_edit/listen_{TimelineImports.Hash8(scd)}.scd" && InstantEdit.Services.Animations.AnimationDependencies.SafeGamePath(plan.GamePath) &&
              ReferenceEquals(plan.Bytes, scd) && Math.Abs(plan.Seconds - (ScdFile.Info(scd).SoundSeconds[0] + 2)) < 1e-6,
            "a listen plan names a unique safe path and lasts the sound plus 2 s");
        reject(() => TimelineListen.For([1, 2, 3]), "a file that isn't a sound can't be listened to");
        var served = Path.Combine(Path.GetTempPath(), "listen_ab12cd34.scd");
        check(TimelineListen.Served(served, served) && TimelineListen.Served(served.ToUpperInvariant(), served) && TimelineListen.Served(served.Replace('\\', '/'), served) &&
              !TimelineListen.Served(plan.GamePath, served) && !TimelineListen.Served(null, served) && !TimelineListen.Served("", served) &&
              !TimelineListen.Served(Path.Combine(Path.GetTempPath(), "other.scd"), served),
            "a listen file plays only when Penumbra resolves its path to the file just written");
        var two = ScdModelFixture.TwoSoundsSharingAudio().Write();
        check(ScdModel.Read(TimelineListen.Whole(two, 1)) is { Sounds: [{ Type: 1 }] } && ScdModel.Read(TimelineListen.Whole(scd, 0)).Sounds[0].Entries.Count == 3 &&
              ScdModel.Read(TimelineListen.Row(scd, 0, 2)) is { Sounds: [{ Entries: [{ Limit: 100 }] }], Audio.Count: 1 } &&
              ScdModel.Read(TimelineListen.Row(scd, 0, 2)).Audio[0].Seconds == ScdModel.Read(scd).Audio[3].Seconds,
            "the header plays the whole sound as sound 0, a row only its alternative");
    }

    private static void Usage(Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(Path.GetTempPath(), "IE sound usage " + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            byte[] Naming(string path) => [.. new byte[37], .. System.Text.Encoding.ASCII.GetBytes(path), 0, .. new byte[11]];
            File.WriteAllBytes(Path.Combine(root, "a.pap"), Naming("sound/Moan.scd"));
            File.WriteAllBytes(Path.Combine(root, "b.tmb"), Naming("SOUND/MOAN.SCD"));
            File.WriteAllBytes(Path.Combine(root, "c.avfx"), Naming("sound/other.scd"));
            File.WriteAllBytes(Path.Combine(root, "d.pap"), Naming("sound/moan.scd"));
            File.WriteAllBytes(Path.Combine(root, "sub", "e.avfx"), Naming("sound/moan.scd"));
            File.WriteAllBytes(Path.Combine(root, "f.scd"), Naming("sound/moan.scd"));
            File.WriteAllBytes(Path.Combine(root, "g.pap"), Naming("sound/moan.scd2"[..13]));
            check(TimelineSoundUsage.OtherUsers(root, Moan, Path.Combine(root, "d.pap")).Order().SequenceEqual(["a.pap", "b.tmb", "sub/e.avfx"]),
                "other users are the mod's other animations and effects that name the sound, in any case");
        }
        finally { Directory.Delete(root, true); }
        var edited = new InstantEdit.Models.AnimationResource("chara/a.pap", @"C:\mods\M\a.pap", "papHash", "M", @"C:\mods\M", "a.pap");
        var moan = new InstantEdit.Models.AnimationResource(Moan, @"C:\mods\M\sound\moan.scd", "MOANHASH", "M", @"C:\mods\M", "sound/moan.scd");
        var outputs = ImmutableDictionary<string, byte[]>.Empty.Add("chara/a.pap", [1]);
        var replaced = new Dictionary<string, TimelineSoundReplacement> { [Moan] = new("MOANHASH", [2, 3]) };
        check(TimelineSoundUsage.ReplacedOutputs(outputs, [edited, moan], edited, replaced) is var all && all.Count == 2 && all[Moan].SequenceEqual(new byte[] { 2, 3 }),
            "an in-place sound joins the animation's outputs");
        reject(() => TimelineSoundUsage.ReplacedOutputs(outputs, [edited, moan with { Hash = "NEWER" }], edited, replaced), "a sound that changed while saving is refused");
        reject(() => TimelineSoundUsage.ReplacedOutputs(outputs, [edited, moan with { ModDirectory = "Other" }], edited, replaced), "a sound another mod provides is refused");
        reject(() => TimelineSoundUsage.ReplacedOutputs(outputs, [edited], edited, replaced), "a sound that wasn't read is refused");
        check(Refuses(() => TimelineSoundUsage.ReplacedOutputs(outputs, [edited, moan with { Hash = "NEWER" }], edited, replaced), "moan.scd changed while saving. Open the timeline again.") &&
              Refuses(() => TimelineSoundUsage.ReplacedOutputs(outputs, [edited, moan with { ModDirectory = "Other" }], edited, replaced), "moan.scd belongs to another mod. Save it as a copy."),
            "the refusals say what to do");
        // A pin copy of a file saved in place can't be mapped beside it: that file is being rewritten.
        var scd = ScdModelFixture.RandomSet(3);
        var both = Doc(Sound("n1", pin: "o1"), Sound("n2")) with
        {
            SoundEdits = ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit>.Empty.Add(new(Moan, 0), TimelineSoundEdits.Initial(ScdModel.Read(scd), 0) with { Volume = 0.5f }),
        };
        var inPlace = TimelineSoundEdits.Bake(both, p => p == Moan ? scd : null, _ => null, _ => TimelineSoundRoute.InPlace);
        check(inPlace.InPlace.Count == 1 && inPlace.Copies is [{ Beside: null, Rebuild: null }], "a pin copy of a file saved in place is mapped with the animation");
    }

    private static bool Refuses(Action action, string message)
    {
        try { action(); }
        catch (IOException e) { return e.Message == message; }
        return false;
    }

    private static void Baking(Action<bool, string> check, Action<Action, string> reject)
    {
        var scd = ScdModelFixture.RandomSet(3);
        byte[]? Bytes(string path) => path.Equals(Moan, StringComparison.OrdinalIgnoreCase) ? scd : null;
        byte[]? NoImport(string path) => null;
        var key = new TimelineSoundKey(Moan, 0);
        var louder = TimelineSoundEdits.Initial(ScdModel.Read(scd), 0) with { Volume = 0.5f };
        var docWithEdit = Doc(Sound("n1"), Sound("n2")) with { SoundEdits = ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit>.Empty.Add(key, louder) };
        TimelineDocument DocWithEditAndPin(string pinned, string pin) =>
            docWithEdit with { Events = [.. docWithEdit.Events.Select(e => e.Key == pinned ? e with { Pin = pin } : e)] };

        var baked = TimelineSoundEdits.Bake(docWithEdit, Bytes, NoImport, _ => TimelineSoundRoute.Copy);
        check(baked.Document.SoundEdits.IsEmpty && baked.Copies is [{ GamePath: var copy, Beside: var beside }] &&
              copy.StartsWith("sound/instant_edit/moan_") && beside![copy] == Moan && baked.Document.Events.All(e => e.Path == copy) &&
              ScdModel.Read(baked.Copies[0].Files[copy]).Sounds[0].Volume == 0.5f && baked.InPlace.IsEmpty,
            "a copied edit points every unpinned event at one copy, mapped beside the original");
        var inPlace = TimelineSoundEdits.Bake(docWithEdit, Bytes, NoImport, _ => TimelineSoundRoute.InPlace);
        check(inPlace.Copies.IsEmpty && inPlace.InPlace["SOUND/moan.scd"].BaseHash == InstantEdit.Services.Animations.AnimationPap.Hash(scd) &&
              inPlace.Document.Events.All(e => e.Path == Moan) && ScdModel.Read(inPlace.InPlace[Moan].Bytes).Sounds[0].Volume == 0.5f,
            "an in-place edit keeps the path and carries the original's hash");
        var unchanged = docWithEdit with { SoundEdits = docWithEdit.SoundEdits.SetItem(key, louder with { Volume = null }) };
        check(TimelineSoundEdits.Bake(unchanged, Bytes, NoImport, _ => TimelineSoundRoute.Copy) is { Copies.IsEmpty: true, InPlace.IsEmpty: true } same &&
              same.Document.Events.All(e => e.Path == Moan) && same.Document.SoundEdits.IsEmpty, "an edit that changes nothing keeps the file");
        // Review focus 4: one event pinned, one not, with an edit.
        var mixed = TimelineSoundEdits.Bake(DocWithEditAndPin("n1", "o2"), Bytes, NoImport, _ => TimelineSoundRoute.Copy);
        check(mixed.Copies.Length == 2 && TimelineEdits.Find(mixed.Document, "n1") is { Pin: null } p && p.Values["SoundIndex"] == 0 && p.Path.Contains("moan_3_") &&
              ScdModel.Read(mixed.Copies.Single(c => c.GamePath == p.Path).Files[p.Path]) is { Sounds: [{ Volume: 0.5f, Entries.Count: 1 }] } &&
              mixed.Copies.Single(c => c.GamePath == p.Path).Beside![p.Path] == Moan && TimelineEdits.Find(mixed.Document, "n2")!.Path.StartsWith("sound/instant_edit/moan_") &&
              !TimelineEdits.Find(mixed.Document, "n2")!.Path.Contains("moan_3_"),
            "a pinned event gets a keep-only copy cut from the edited file");
        var pinOnly = TimelineSoundEdits.Bake(Doc(Sound("n1", pin: "o1"), Sound("n2")), Bytes, NoImport, _ => TimelineSoundRoute.InPlace);
        check(pinOnly.Copies.Length == 1 && pinOnly.InPlace.IsEmpty && TimelineEdits.Find(pinOnly.Document, "n2")!.Path == Moan &&
              ScdModel.Read(pinOnly.Copies[0].Files[pinOnly.Copies[0].GamePath]).Sounds[0].Entries.Count == 1,
            "a pin without edits copies one alternative and leaves the file alone");
        reject(() => TimelineSoundEdits.Bake(Doc(Sound("n1", pin: "a7")), Bytes, NoImport, _ => TimelineSoundRoute.Copy), "a pin naming a sound the file lacks is refused");
        // Review focus 1: sound index 1 of a two-sound file.
        var twoBytes = ScdModelFixture.TwoSoundsSharingAudio().Write();
        var twoModel = ScdModel.Read(twoBytes);
        var key1 = new TimelineSoundKey(Moan, 1);
        var docOnSound1 = Doc(Sound("n1", index: 1, pin: "o0"), Sound("n2", index: 1)) with
        {
            SoundEdits = ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit>.Empty.Add(key1, TimelineSoundEdits.Initial(twoModel, 1) with { Volume = 0.5f }),
        };
        var two = TimelineSoundEdits.Bake(docOnSound1, path => path == Moan ? twoBytes : null, NoImport, _ => TimelineSoundRoute.Copy);
        var editedCopy = ScdModel.Read(two.Copies.Single(c => c.GamePath == TimelineEdits.Find(two.Document, "n2")!.Path).Files.Values.Single());
        var pinCopy = ScdModel.Read(two.Copies.Single(c => c.GamePath == TimelineEdits.Find(two.Document, "n1")!.Path).Files.Values.Single());
        check(editedCopy.Sounds[0].Volume == twoModel.Sounds[0].Volume && editedCopy.Sounds[1].Volume == 0.5f && TimelineEdits.Find(two.Document, "n2")!.Values["SoundIndex"] == 1 &&
              pinCopy.Sounds is [{ Volume: 0.5f, Type: 1 }] && TimelineEdits.Find(two.Document, "n1")!.Values["SoundIndex"] == 0,
            "an edit of sound 1 changes only sound 1, and its pin copy plays it as sound 0");
        // Review focus 3: an unreadable file without edits.
        var docWithoutEdits = Doc(Sound("n1"));
        check(TimelineSoundEdits.Bake(docWithoutEdits, _ => null, NoImport, _ => TimelineSoundRoute.Copy).Document == docWithoutEdits &&
              TimelineSoundEdits.Bake(docWithEdit with { SoundEdits = docWithEdit.SoundEdits.Add(new("sound/other.scd", 0), louder) }, Bytes, NoImport,
                  _ => TimelineSoundRoute.Copy).Copies.Length == 1,
            "a sound that can't be read never blocks a document without its edits, and an edit no event plays is dropped");
        reject(() => TimelineSoundEdits.Bake(docWithEdit, _ => null, NoImport, _ => TimelineSoundRoute.Copy), "an edited sound that can't be read is refused");
        var applied = 0;
        var (reused, reusedFiles) = TimelineSoundEdits.Redirectable(DocWithEditAndPin("n1", "o2"), Bytes, NoImport, (bytes, sound, edit) =>
        {
            applied++;
            return TimelineSoundEdits.Apply(bytes, sound, edit, NoImport);
        });
        check(applied == 1 && ScdModel.Read(reusedFiles[Moan]).Sounds[0].Volume == 0.5f && reused.SoundEdits.IsEmpty,
            "preview can reuse a recipe already written for the inspector");
        var (rest, files) = TimelineSoundEdits.Redirectable(DocWithEditAndPin("n1", "o2"), Bytes, NoImport);
        check(files.ContainsKey("SOUND/MOAN.SCD") && ScdModel.Read(files[Moan]).Sounds[0].Volume == 0.5f && rest.SoundEdits.IsEmpty && TimelineEdits.Find(rest, "n1")!.Pin == "o2",
            "preview points the file's own path at the edit and leaves pins for a rebuild");
        var removedFirst = DocWithEditAndPin("n1", "o2") with
        {
            SoundEdits = docWithEdit.SoundEdits.SetItem(key, louder with { Alternatives = [.. louder.Alternatives.Skip(1).Select(a => a with { Share = 50 })] }),
        };
        var (shifted, shiftedFiles) = TimelineSoundEdits.Redirectable(removedFirst, Bytes, NoImport);
        var rebuilt = TimelineSoundEdits.Bake(shifted, path => shiftedFiles.GetValueOrDefault(path) ?? Bytes(path), NoImport, _ => TimelineSoundRoute.Copy);
        check(TimelineEdits.Find(shifted, "n1")!.Pin == "o1" && rebuilt.Copies.Length == 1 && TimelineEdits.Find(rebuilt.Document, "n2")!.Path == Moan &&
              ScdModel.Read(rebuilt.Copies[0].Files.Values.Single()).Audio.Single().Seconds == ScdModel.Read(scd).Audio[3].Seconds,
            "a redirected file's pins name their alternative's place in it, so the rebuild cuts the right one");
        var twoSounds = docOnSound1 with { Events = [.. docOnSound1.Events.Select(e => e with { Pin = null })] };
        check(TimelineSoundEdits.Redirectable(twoSounds, path => path == Moan ? twoBytes : null, NoImport) is var (twoRest, twoFiles) && twoFiles.IsEmpty &&
              twoRest.SoundEdits.Count == 1, "a file with several sounds isn't redirected; its edit waits for a rebuild");
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
        var withModel = TimelineSoundEdits.ApplyModel(scd, 0, changed, _ => null);
        var unchangedModel = TimelineSoundEdits.ApplyModel(scd, 0, edit, _ => null);
        check(withModel.Model.Write().AsSpan().SequenceEqual(withModel.Bytes) && withModel.Model.Sounds[0].Volume == 0.5f &&
              ReferenceEquals(unchangedModel.Bytes, scd) && unchangedModel.Model.Write().AsSpan().SequenceEqual(scd),
            "applying a recipe also gives the file's model, so it needn't be read again");
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
