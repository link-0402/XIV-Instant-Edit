using InstantEdit.Services;
using InstantEdit.Services.Actors;
using Penumbra.Api.Enums;

/// <summary>Partners: the player-name rule, a preset's name problems, the preset store, the spawn and despawn sequence and the plugin codes.</summary>
internal static class PartnerFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Names(check);
        Problems(check);
        Store(check);
        Service(check);
        Outcomes(check);
    }

    private static void Outcomes(Action<bool, string> check)
    {
        LinkOutcome applied = new(LinkStatus.Applied), missing = new(LinkStatus.Missing);
        check(PartnerCollections.Outcome(PenumbraApiEc.Success) == applied && PartnerCollections.Outcome(PenumbraApiEc.NothingChanged) == applied &&
              PartnerCollections.Outcome(PenumbraApiEc.CollectionMissing) == missing &&
              PartnerCollections.Outcome(PenumbraApiEc.InvalidIdentifier) == new LinkOutcome(LinkStatus.Failed, (int)PenumbraApiEc.InvalidIdentifier),
            "partner links: Penumbra's success and nothing changed apply, a missing collection is missing, anything else fails with its code");
        check(GlamourerIpc.Outcome(0) == applied && GlamourerIpc.Outcome(1) == applied && GlamourerIpc.Outcome(4) == missing &&
              GlamourerIpc.Outcome(6) == new LinkOutcome(LinkStatus.Failed, 6),
            "partner links: Glamourer's success and nothing done apply, a missing design is missing, anything else fails with its code");
        check(CustomizePlusProfiles.Outcome(0) == applied && CustomizePlusProfiles.Outcome(3) == missing &&
              CustomizePlusProfiles.Outcome(255) == new LinkOutcome(LinkStatus.Failed, 255),
            "partner links: Customize+'s success applies, a missing profile is missing, anything else fails with its code");
    }

    private static void Names(Action<bool, string> check)
    {
        var good = new[] { "Aria Rose", "Xb Yq", "Aria Rose-lee", "O'neil Rose", "Abcdefghij Klmnopqrst" };
        var bad = new[]
        {
            "Aria", "aria Rose", "XIVBridgeTest Partner", "A Rose", "Abcdefghijklmnop Rose", "Abcdefghijk Klmnopqrst",
            "Aria Ro--se", "Aria Ro-'se", "Aria  Rose", "Aria Rose Lee", "Aria Rose-Lee",
        };
        var wrong = good.Where(name => !PartnerNames.IsPlayerName(name)).Concat(bad.Where(PartnerNames.IsPlayerName)).ToArray();
        check(wrong.Length == 0, "partner names: player names are two capitalised parts of 2-15 letters, 21 characters at most" + Failed(string.Join('|', wrong)));
    }

    private static void Problems(Action<bool, string> check)
    {
        var id = Guid.NewGuid();
        var other = new PartnerPreset(Guid.NewGuid(), "Aria Rose", null, null, null);
        check(PartnerNames.Problem("  Aria Rose ", [], id, "Lucina Zero") == null, "partner problem: a trimmed free player name has no problem");
        check(PartnerNames.Problem("lucina zero", [], id, "Lucina Zero") == PartnerNames.Yours, "partner problem: your own name, case ignored, is refused");
        check(PartnerNames.Problem("aria rose", [other], id, null) == PartnerNames.Taken, "partner problem: another preset's name, case ignored, is taken");
        check(PartnerNames.Problem("Aria Rose", [other], other.Id, null) == null, "partner problem: a preset keeps its own name");
        check(PartnerNames.Problem("Aria", [], id, null) == PartnerNames.FirstLast, "partner problem: one name asks for a first and last name");
        check(PartnerNames.Taken == "Name is taken" && PartnerNames.Yours == "Use a name other than your character's" &&
              PartnerNames.FirstLast == "Use a first and last name, like Aria Rose",
            "partner problem: the messages read as the spec says");
    }

    private static void Store(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-partner-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var warnings = new List<string>();
            var file = Path.Combine(temp, PartnerPresetStore.FileName);
            var store = new PartnerPresetStore(temp, warnings.Add);
            check(store.Presets.IsEmpty && warnings.Count == 0, "partner store: a new store is empty");

            var preset = new PartnerPreset(Guid.NewGuid(), " Aria Rose ", new PartnerLink(Guid.NewGuid(), "Partner mods"),
                new PartnerLink(Guid.NewGuid(), "Summer look"), new PartnerLink(Guid.NewGuid(), "Tall"));
            var saved = store.Save(preset, "Lucina Zero", out var error);
            var kept = preset with { Name = "Aria Rose" };
            check(saved && error == null && store.Presets.SequenceEqual([kept]) && new PartnerPresetStore(temp, warnings.Add).Presets.SequenceEqual([kept]) && warnings.Count == 0,
                "partner store: a saved preset with all three links is trimmed and read back equal by a new store");

            var renamed = kept with { Name = "Mira Vale" };
            check(store.Save(renamed, null, out _) && store.Presets.SequenceEqual([renamed]) && new PartnerPresetStore(temp, warnings.Add).Presets.SequenceEqual([renamed]),
                "partner store: saving the same Id with a new name replaces it");
            check(!store.Save(renamed with { Name = "Aria" }, null, out var refused) && refused == PartnerNames.FirstLast && store.Presets.SequenceEqual([renamed]),
                "partner store: a name that isn't a player name is refused and nothing changes");
            check(!store.Save(new PartnerPreset(Guid.NewGuid(), "mira vale", null, null, null), null, out var taken) && taken == PartnerNames.Taken,
                "partner store: another preset's name is refused");

            check(store.Delete(renamed.Id) && !store.Delete(renamed.Id) && store.Presets.IsEmpty && new PartnerPresetStore(temp, warnings.Add).Presets.IsEmpty,
                "partner store: delete removes the preset from memory and the file, and again does nothing");

            var goodId = Guid.NewGuid();
            File.WriteAllText(file, $$"""{ "Version": 1, "Presets": [ { "Id": "{{goodId}}", "Name": "Aria Rose", "Collection": null, "Design": null, "Profile": null }, { "Name": "Mira Vale" } ], "OwnedAssignments": [] }""");
            warnings.Clear();
            var partial = new PartnerPresetStore(temp, warnings.Add);
            check(partial.Presets.Length == 1 && partial.Presets[0].Id == goodId && warnings.Count == 1 && File.Exists(file + ".bak"),
                "partner store load: an entry without an Id is skipped with one warning and the file is kept as .bak");

            File.Delete(file + ".bak");
            File.WriteAllText(file, "not json");
            warnings.Clear();
            var broken = new PartnerPresetStore(temp, warnings.Add);
            check(broken.Presets.IsEmpty && warnings.Count == 1 && File.Exists(file + ".bak") && File.ReadAllText(file + ".bak") == "not json",
                "partner store load: a file that isn't JSON loads empty, warns and is kept as .bak");

            broken.SetOwnsAssignment("Aria Rose", true);
            check(broken.OwnsAssignment("ARIA ROSE") && new PartnerPresetStore(temp, warnings.Add).OwnsAssignment("aria rose"),
                "partner store: an owned assignment survives a reload, case ignored");
            broken.SetOwnsAssignment("aria rose", false);
            check(!broken.OwnsAssignment("Aria Rose") && !new PartnerPresetStore(temp, warnings.Add).OwnsAssignment("Aria Rose"),
                "partner store: an assignment no longer owned is gone after a reload");

            var memory = new PartnerPresetStore(null);
            check(memory.Save(new PartnerPreset(Guid.NewGuid(), "Aria Rose", null, null, null), null, out _) && memory.Presets.Length == 1,
                "partner store memory: without a folder presets are kept in memory");
            check(warnings.Count == 1, "partner store: nothing past the bad files warned" + Failed(warnings.Count == 1 ? null : string.Join('|', warnings)));
        }
        finally { Directory.Delete(temp, true); }
    }

    /// <summary>A check whose detail (the calls so far) is shown only when it fails.</summary>
    private delegate void Check(bool ok, string message, string? detail = null);

    private static void Service(Action<bool, string> report)
    {
        Check check = (ok, message, detail) => report(ok, message + (ok ? "" : Failed(detail)));
        check(PartnerService.NotInGPose == "Enter GPose to spawn a partner." && PartnerService.TreatedAsYou == "Penumbra would treat it as you" &&
              PartnerService.DrawnLate == "Drawn late",
            "partner service: the messages read as the spec says");
        var duo = new PartnerLink(CollectionId, "Duo");
        check(PartnerService.ProblemText("Collection", "Penumbra", duo, new LinkOutcome(LinkStatus.NotLoaded)) == "Penumbra isn't loaded" &&
              PartnerService.ProblemText("Collection", "Penumbra", duo, new LinkOutcome(LinkStatus.Missing)) == "Collection Duo no longer exists" &&
              PartnerService.ProblemText("Design", "Glamourer", duo, new LinkOutcome(LinkStatus.Failed, 3)) == "Glamourer error 3",
            "partner service: a link that didn't apply reads as not loaded, no longer exists or the plugin's error code");

        Spawn_RefusedOutsideGPose(check);
        Spawn_RefusesYourName(check);
        Spawn_FullOrder(check);
        Spawn_ParentNotSelf(check);
        Despawn_UndoesExactly(check);
        Despawn_RestoresPrevious(check);
        Spawn_LeftoverTreatedAsOurs(check);
        Spawn_LinkFailuresContinue(check);
        Spawn_DrawnLate(check);
        Spawn_AlreadySpawned(check);
        Spawn_NoSlot(check);
        GPoseEnd_DespawnsAll(check);
        LostActor_CleansWhatItCan(check);
        Despawn_AfterRename_UsesSpawnName(check);
        Despawn_PenumbraGone_KeepsLeftover(check);
        Delete_DespawnsFirst(check);
        Dispose_DespawnsOnFramework(check);
        GPoseEnd_LostActor_TakesLostPath(check);
        Spawn_AssignThrows_DeletesActor(check);
        Despawn_ReleaseThrows_StillDeletes(check);
        Update_ApplyThrows_OthersGoOn(check);
        Dispose_Throws_StillUnsubscribes(check);
    }

    private static void GPoseEnd_LostActor_TakesLostPath(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        rig.Actors.Lost = true;
        rig.Actors.InGPose = false;
        var before = rig.Calls.Count;
        rig.Actors.Tick();
        check(rig.Since(before).SequenceEqual(["remove T"]) && rig.Store.OwnsAssignment("Aria Rose") && rig.Service.Find(preset.Id) == null,
            "partner despawn: an actor already gone when GPose ends only has its profile removed; its index is never touched", rig.Log);
    }

    private static void Spawn_AssignThrows_DeletesActor(Check check)
    {
        var rig = new Rig();
        rig.Collections.ThrowOnAssign = true;
        var preset = Aria();
        var threw = false;
        try { rig.Service.Spawn(preset); }
        catch (InvalidOperationException) { threw = true; }
        rig.Actors.Tick();
        check(threw && rig.Calls.SequenceEqual(["spawn Aria Rose", "parent 202", "current 202", "assign 202 C true true", "delete 202"]) &&
              rig.Service.Find(preset.Id) == null && !rig.Store.OwnsAssignment("Aria Rose") && rig.Warnings.Count == 1,
            "partner spawn: a call that throws during the spawn deletes the actor, tracks nothing, warns and rethrows", rig.Log + " / " + string.Join('|', rig.Warnings));
    }

    private static void Despawn_ReleaseThrows_StillDeletes(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        rig.Looks.ThrowOnRelease = true;
        var before = rig.Calls.Count;
        rig.Service.Despawn(preset.Id);
        check(rig.Since(before).SequenceEqual(["remove T", "release 202", "assign 202 null false true", "delete 202"]) && !rig.Store.OwnsAssignment("Aria Rose") &&
              rig.Service.Find(preset.Id) == null && rig.Warnings.Count == 1 && rig.Warnings[0].Contains("InvalidOperationException") && rig.Warnings[0].Contains("release"),
            "partner despawn: a step that throws is warned once and the later steps and the delete still run", rig.Log + " / " + string.Join('|', rig.Warnings));
    }

    private static void Update_ApplyThrows_OthersGoOn(Check check)
    {
        var rig = new Rig();
        var aria = Aria();
        var mira = Aria() with { Name = "Mira Vale" };
        rig.Service.Spawn(aria);
        rig.Service.Spawn(mira);
        // Mira, the last spawned, is stepped first each frame, so a throw there would starve Aria without the isolation.
        rig.Looks.ThrowOnApply = 203;
        rig.Actors.Tick();
        rig.Actors.Tick();
        var looksAfterTwo = rig.Calls.Count(call => call == "look 203 D");
        rig.Actors.Tick();
        var failed = rig.Service.Find(mira.Id);
        var other = rig.Service.Find(aria.Id);
        check(failed is { Stage: PartnerStage.Ready } && failed.Problems.SequenceEqual([PartnerService.LinksFailed]) &&
              other is { Stage: PartnerStage.Ready, TemporaryProfile: not null } && other.Problems.IsEmpty &&
              looksAfterTwo == 1 && rig.Calls.Count(call => call == "look 203 D") == 1 && !rig.Calls.Contains("profile 203 P") && rig.Warnings.Count == 1,
            "partner frame: a partner whose link call throws is Ready with Spawning failed: links, never retried, and the other still gets ready",
            rig.Log + " / " + string.Join('|', rig.Warnings));
        var before = rig.Calls.Count;
        rig.Service.Despawn(mira.Id);
        check(rig.Since(before).SequenceEqual(["assign 203 null false true", "delete 203"]),
            "partner despawn: a look that threw is not released", rig.Log);
    }

    private static void Dispose_Throws_StillUnsubscribes(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        rig.Actors.Deleting = () => throw new InvalidOperationException("delete");
        var threw = false;
        try { rig.Service.Dispose(); }
        catch (InvalidOperationException) { threw = true; }
        var before = rig.Calls.Count;
        rig.Actors.Tick();
        check(threw && !rig.Actors.Subscribed && rig.Calls.Count == before,
            "partner dispose: a despawn that throws still drops the frame callback, so later frames do nothing", rig.Log);
    }

    private static void Spawn_RefusedOutsideGPose(Check check)
    {
        var rig = new Rig();
        rig.Actors.InGPose = false;
        var preset = Aria();
        var refused = rig.Service.Spawn(preset);
        check(refused == PartnerService.NotInGPose && rig.Calls.Count == 0 && rig.Service.Find(preset.Id) == null,
            "partner spawn: out of GPose it is refused and nothing is called", rig.Log);
    }

    private static void Spawn_RefusesYourName(Check check)
    {
        var rig = new Rig();
        var preset = Aria() with { Name = "Lucina Zero" };
        var refused = rig.Service.Spawn(preset);
        check(refused == PartnerNames.Yours && rig.Calls.Count == 0 && rig.Service.Find(preset.Id) == null,
            "partner spawn: your own character's name is refused before any actor is made", rig.Log);
    }

    private static void Spawn_FullOrder(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        var refused = rig.Service.Spawn(preset);
        var waiting = rig.Service.Find(preset.Id)?.Stage;
        rig.Actors.Tick();
        var afterDraw = rig.Calls.ToArray();
        var drawn = rig.Service.Find(preset.Id)?.Stage;
        rig.Actors.Tick();
        rig.Actors.Tick();
        rig.Actors.Tick();
        string[] spawned = ["spawn Aria Rose", "parent 202", "current 202", "assign 202 C true true", "ready 202", "draw 202"];
        check(refused == null && afterDraw.SequenceEqual(spawned) && rig.Calls.SequenceEqual([.. spawned, "look 202 D", "profile 202 P"]),
            "partner spawn: spawn, parent check, previous assignment, assign, ready, draw, then the look and profile only once drawn", rig.Log);
        var view = rig.Service.Find(preset.Id);
        check(waiting == PartnerStage.Waiting && drawn == PartnerStage.Drawn && view is { Stage: PartnerStage.Ready, ObjectIndex: 202, Name: "Aria Rose" } &&
              view.PresetId == preset.Id && view.Problems.IsEmpty && view.TemporaryProfile == TemporaryId && rig.Store.OwnsAssignment("Aria Rose"),
            "partner spawn: the partner goes Waiting, Drawn, Ready with no problems, keeps the temporary profile and owns the new assignment");
    }

    private static void Spawn_ParentNotSelf(Check check)
    {
        var rig = new Rig();
        rig.Collections.Parent = 201;
        var preset = Aria();
        var refused = rig.Service.Spawn(preset);
        rig.Actors.Tick();
        check(refused == PartnerService.TreatedAsYou && rig.Calls.SequenceEqual(["spawn Aria Rose", "parent 202", "delete 202"]) &&
              rig.Service.Find(preset.Id) == null && !rig.Store.OwnsAssignment("Aria Rose"),
            "partner spawn: an actor Penumbra parents to you is deleted before any assignment", rig.Log);
    }

    private static void Despawn_UndoesExactly(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        var before = rig.Calls.Count;
        rig.Service.Despawn(preset.Id);
        var undone = rig.Since(before);
        rig.Service.Despawn(preset.Id);
        check(undone.SequenceEqual(["remove T", "release 202", "assign 202 null false true", "delete 202"]) && rig.Calls.Count == before + 4 &&
              !rig.Store.OwnsAssignment("Aria Rose") && rig.Service.Find(preset.Id) == null,
            "partner despawn: removes the profile, releases the look, removes our assignment, deletes the actor, and again does nothing", rig.Log);
    }

    private static void Despawn_RestoresPrevious(Check check)
    {
        var rig = new Rig();
        rig.Collections.Assignment = new CollectionAssignment(true, UsersId);
        var preset = Aria();
        rig.SpawnReady(preset);
        var ownedWhileSpawned = rig.Store.OwnsAssignment("Aria Rose");
        var before = rig.Calls.Count;
        rig.Service.Despawn(preset.Id);
        check(rig.Since(before).SequenceEqual(["remove T", "release 202", "assign 202 X false false", "delete 202"]) && !ownedWhileSpawned &&
              !rig.Store.OwnsAssignment("Aria Rose"),
            "partner despawn: the user's own assignment is put back and never recorded as ours", rig.Log);
    }

    private static void Spawn_LeftoverTreatedAsOurs(Check check)
    {
        var rig = new Rig();
        rig.Store.SetOwnsAssignment("Aria Rose", true);
        rig.Collections.Assignment = new CollectionAssignment(true, OldId);
        var preset = Aria();
        rig.SpawnReady(preset);
        var before = rig.Calls.Count;
        rig.Service.Despawn(preset.Id);
        check(rig.Since(before).SequenceEqual(["remove T", "release 202", "assign 202 null false true", "delete 202"]) && !rig.Store.OwnsAssignment("Aria Rose"),
            "partner despawn: an assignment left by a crash is removed, not put back", rig.Log);
    }

    private static void Spawn_LinkFailuresContinue(Check check)
    {
        var rig = new Rig();
        rig.Collections.Available = false;
        rig.Looks.Outcome = new LinkOutcome(LinkStatus.Missing);
        rig.Profiles.Outcome = new LinkOutcome(LinkStatus.Failed, 255);
        var preset = Aria();
        var refused = rig.SpawnReady(preset);
        var view = rig.Service.Find(preset.Id);
        check(refused == null && view is { Stage: PartnerStage.Ready, TemporaryProfile: null } &&
              view.Problems.SequenceEqual(["Penumbra isn't loaded", "Design Aria no longer exists", "Customize+ error 255"]) &&
              rig.Calls.SequenceEqual(["spawn Aria Rose", "ready 202", "draw 202", "look 202 D", "profile 202 P"]),
            "partner spawn: links that fail are listed and the next step still runs; without Penumbra no collection call is made",
            rig.Log + (view == null ? "" : " / " + string.Join('|', view.Problems)));
        var before = rig.Calls.Count;
        rig.Service.Despawn(preset.Id);
        check(rig.Since(before).SequenceEqual(["delete 202"]),
            "partner despawn: only what applied is undone, so a failed look, profile and collection leave just the delete", rig.Log);
    }

    private static void Spawn_DrawnLate(Check check)
    {
        var rig = new Rig();
        rig.Actors.NeverReady = true;
        rig.Actors.NeverDrawn = true;
        var preset = Aria();
        rig.Service.Spawn(preset);
        rig.Actors.Tick();
        var waiting = rig.Service.Find(preset.Id)?.Stage == PartnerStage.Waiting && !rig.Calls.Contains("draw 202");
        rig.Now = TimeSpan.FromSeconds(1.5);
        rig.Actors.Tick();
        var late = rig.Service.Find(preset.Id);
        var drawnLate = late is { Stage: PartnerStage.Drawn } && late.Problems.SequenceEqual([PartnerService.DrawnLate]) && rig.Calls[^1] == "draw 202";
        rig.Now = TimeSpan.FromSeconds(2);
        rig.Actors.Tick();
        var stillDrawn = rig.Service.Find(preset.Id)?.Stage == PartnerStage.Drawn && !rig.Calls.Contains("look 202 D");
        rig.Now = TimeSpan.FromSeconds(2.6);
        rig.Actors.Tick();
        var view = rig.Service.Find(preset.Id);
        check(waiting && drawnLate && stillDrawn && view is { Stage: PartnerStage.Ready } && view.Problems.SequenceEqual([PartnerService.DrawnLate]) &&
              rig.Calls.Contains("look 202 D") && rig.Calls.Contains("profile 202 P"),
            "partner spawn: an actor never ready is drawn after 1 s, one more second without a draw object applies the links anyway, and Drawn late shows once", rig.Log);
    }

    private static void Spawn_AlreadySpawned(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        var before = rig.Calls.Count;
        var again = rig.Service.Spawn(preset);
        check(again == null && rig.Calls.Count == before && rig.Service.Find(preset.Id)?.ObjectIndex == 202,
            "partner spawn: a preset already spawned spawns nothing more", rig.Log);
    }

    private static void Spawn_NoSlot(Check check)
    {
        var rig = new Rig();
        rig.Actors.SpawnProblem = "No free actor slot";
        var preset = Aria();
        var refused = rig.Service.Spawn(preset);
        rig.Actors.Tick();
        check(refused == "No free actor slot" && rig.Calls.SequenceEqual(["spawn Aria Rose"]) && rig.Service.Find(preset.Id) == null,
            "partner spawn: the actor's own refusal is returned and nothing else is called", rig.Log);
    }

    private static void GPoseEnd_DespawnsAll(Check check)
    {
        var rig = new Rig();
        var aria = Aria();
        var mira = Aria() with { Name = "Mira Vale" };
        rig.Service.Spawn(aria);
        rig.Service.Spawn(mira);
        rig.Actors.Tick();
        rig.Actors.Tick();
        var bothReady = rig.Service.Find(aria.Id)?.Stage == PartnerStage.Ready && rig.Service.Find(mira.Id) is { Stage: PartnerStage.Ready, ObjectIndex: 203 };
        rig.Actors.InGPose = false;
        var before = rig.Calls.Count;
        rig.Actors.Tick();
        var undone = rig.Since(before);
        check(bothReady && undone.Contains("delete 202") && undone.Contains("delete 203") && undone.Count(call => call.StartsWith("remove ")) == 2 &&
              rig.Service.Find(aria.Id) == null && rig.Service.Find(mira.Id) == null &&
              !rig.Store.OwnsAssignment("Aria Rose") && !rig.Store.OwnsAssignment("Mira Vale"),
            "partner despawn: the first frame out of GPose despawns every partner", rig.Log);
    }

    private static void LostActor_CleansWhatItCan(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        rig.Actors.Lost = true;
        var before = rig.Calls.Count;
        rig.Actors.Tick();
        rig.Service.Despawn(preset.Id);
        check(rig.Since(before).SequenceEqual(["remove T"]) && rig.Store.OwnsAssignment("Aria Rose") && rig.Service.Find(preset.Id) == null,
            "partner lost: an actor gone by itself only has its profile removed, and the assignment stays recorded as ours", rig.Log);
    }

    private static void Despawn_AfterRename_UsesSpawnName(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.Store.Save(preset, null, out _);
        rig.SpawnReady(preset);
        var owned = rig.Store.OwnsAssignment("Aria Rose");
        rig.Store.Save(preset with { Name = "Aria Lee" }, null, out _);
        var name = rig.Service.Find(preset.Id)?.Name;
        rig.Service.Despawn(preset.Id);
        check(owned && name == "Aria Rose" && !rig.Store.OwnsAssignment("Aria Rose") && !rig.Store.OwnsAssignment("Aria Lee") && rig.Calls[^1] == "delete 202",
            "partner despawn: a preset renamed while spawned clears the assignment of the name it spawned with", rig.Log);
    }

    private static void Despawn_PenumbraGone_KeepsLeftover(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        rig.Collections.AssignOutcome = new LinkOutcome(LinkStatus.NotLoaded);
        var before = rig.Calls.Count;
        rig.Service.Despawn(preset.Id);
        check(rig.Since(before).SequenceEqual(["remove T", "release 202", "assign 202 null false true", "delete 202"]) && rig.Store.OwnsAssignment("Aria Rose") &&
              rig.Service.Find(preset.Id) == null,
            "partner despawn: an assignment Penumbra couldn't remove stays recorded as ours and the actor is still deleted", rig.Log);
    }

    private static void Delete_DespawnsFirst(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.Store.Save(preset, null, out _);
        rig.SpawnReady(preset);
        int? listedAtDelete = null;
        rig.Actors.Deleting = () => listedAtDelete = rig.Store.Presets.Length;
        rig.Service.Delete(preset.Id);
        check(listedAtDelete == 1 && rig.Calls[^1] == "delete 202" && rig.Store.Presets.IsEmpty && rig.Service.Find(preset.Id) == null,
            "partner delete: a spawned preset is despawned, then removed from the store", rig.Log);
    }

    private static void Dispose_DespawnsOnFramework(Check check)
    {
        var rig = new Rig();
        var preset = Aria();
        rig.SpawnReady(preset);
        var subscribed = rig.Actors.Subscribed;
        var before = rig.Calls.Count;
        rig.Service.Dispose();
        check(subscribed && !rig.Actors.Subscribed &&
              rig.Since(before).SequenceEqual(["onframework", "remove T", "release 202", "assign 202 null false true", "delete 202"]) &&
              rig.Service.Find(preset.Id) == null,
            "partner dispose: every partner is despawned on the framework thread and the frame callback is dropped", rig.Log);
    }

    private static readonly Guid CollectionId = Guid.NewGuid(), DesignId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), TemporaryId = Guid.NewGuid(),
        UsersId = Guid.NewGuid(), OldId = Guid.NewGuid();

    /// <summary>A preset named Aria Rose with all three links: collection C "Duo", design D "Aria", profile P "Tall".</summary>
    private static PartnerPreset Aria() => new(Guid.NewGuid(), "Aria Rose", new PartnerLink(CollectionId, "Duo"), new PartnerLink(DesignId, "Aria"),
        new PartnerLink(ProfileId, "Tall"));

    /// <summary>How the calls name an id: C, D, P, T (the temporary profile), X (the user's collection), Old (a leftover), or null.</summary>
    private static string Label(Guid? id) =>
        id == null ? "null" : id == CollectionId ? "C" : id == DesignId ? "D" : id == ProfileId ? "P" : id == TemporaryId ? "T" : id == UsersId ? "X" : id == OldId ? "Old" : id.Value.ToString();

    private static string Flag(bool value) => value ? "true" : "false";

    /// <summary>A service over fakes that log every game and plugin call to one list, and a clock the scenario sets.</summary>
    private sealed class Rig
    {
        public readonly List<string> Calls = [];
        public readonly List<string> Warnings = [];
        public readonly PartnerPresetStore Store = new(null);
        public readonly FakeActors Actors;
        public readonly FakeCollections Collections;
        public readonly FakeLooks Looks;
        public readonly FakeProfiles Profiles;
        public readonly PartnerService Service;
        public TimeSpan Now;

        public Rig()
        {
            Actors = new FakeActors(Calls);
            Collections = new FakeCollections(Calls);
            Looks = new FakeLooks(Calls);
            Profiles = new FakeProfiles(Calls);
            Service = new PartnerService(Store, Actors, Collections, Looks, Profiles, () => Now, Warnings.Add);
        }

        /// <summary>Spawns, then runs the two frames that draw it and apply its links.</summary>
        public string? SpawnReady(PartnerPreset preset)
        {
            var refused = Service.Spawn(preset);
            Actors.Tick();
            Actors.Tick();
            return refused;
        }

        public string[] Since(int count) => Calls.Skip(count).ToArray();

        public string Log => string.Join('|', Calls);
    }

    /// <summary>Hands out object indices from 202; the draw object shows the frame after the draw.</summary>
    private sealed class FakeActors(List<string> calls) : IPartnerActors
    {
        public event Action? Updated;
        public bool InGPose { get; set; } = true;
        public string? PlayerName { get; set; } = "Lucina Zero";
        public string? SpawnProblem;
        public bool Lost, NeverReady, NeverDrawn;
        public Action? Deleting;
        private readonly Dictionary<ushort, int> drawnOn = [];
        private ushort next = 202;
        private int frame;

        public bool Subscribed => Updated != null;

        public void Tick()
        {
            frame++;
            Updated?.Invoke();
        }

        public (SpawnedActor? Actor, string? Problem) Spawn(string name)
        {
            calls.Add("spawn " + name);
            if (SpawnProblem != null)
                return (null, SpawnProblem);
            var index = next++;
            return (new SpawnedActor((uint)(index - 200), index, 0x10000 + index), null);
        }

        public bool Exists(SpawnedActor actor) => !Lost;

        public bool ReadyToDraw(SpawnedActor actor)
        {
            calls.Add($"ready {actor.ObjectIndex}");
            return !NeverReady;
        }

        public void Draw(SpawnedActor actor)
        {
            calls.Add($"draw {actor.ObjectIndex}");
            drawnOn[actor.ObjectIndex] = frame;
        }

        public bool IsDrawn(SpawnedActor actor) => !NeverDrawn && drawnOn.TryGetValue(actor.ObjectIndex, out var drawn) && frame > drawn;

        public void Delete(SpawnedActor actor)
        {
            Deleting?.Invoke();
            calls.Add($"delete {actor.ObjectIndex}");
        }

        public void OnFramework(Action action)
        {
            calls.Add("onframework");
            action();
        }
    }

    /// <summary>Parents each actor to itself and reports no individual assignment unless the scenario sets one.</summary>
    private sealed class FakeCollections(List<string> calls) : IPartnerCollections
    {
        public bool Available { get; set; } = true;
        public int? Parent;
        public CollectionAssignment? Assignment;
        public LinkOutcome AssignOutcome = new(LinkStatus.Applied);
        public bool ThrowOnAssign;

        public IReadOnlyList<PartnerLink> List() => [];

        public int ParentOf(int objectIndex)
        {
            calls.Add($"parent {objectIndex}");
            return Parent ?? objectIndex;
        }

        public CollectionAssignment? Current(int objectIndex)
        {
            calls.Add($"current {objectIndex}");
            return Assignment;
        }

        public LinkOutcome Assign(int objectIndex, Guid? collection, bool allowCreate, bool allowDelete)
        {
            calls.Add($"assign {objectIndex} {Label(collection)} {Flag(allowCreate)} {Flag(allowDelete)}");
            return ThrowOnAssign ? throw new InvalidOperationException("assign") : AssignOutcome;
        }
    }

    private sealed class FakeLooks(List<string> calls) : IPartnerLooks
    {
        public bool Available => true;
        public LinkOutcome Outcome = new(LinkStatus.Applied);
        public int? ThrowOnApply;
        public bool ThrowOnRelease;

        public IReadOnlyList<PartnerLink> List() => [];

        public LinkOutcome Apply(int objectIndex, Guid design)
        {
            calls.Add($"look {objectIndex} {Label(design)}");
            return objectIndex == ThrowOnApply ? throw new InvalidOperationException("look") : Outcome;
        }

        public void Release(int objectIndex)
        {
            calls.Add($"release {objectIndex}");
            if (ThrowOnRelease)
                throw new InvalidOperationException("release");
        }
    }

    /// <summary>Hands out the temporary profile T when the apply succeeds.</summary>
    private sealed class FakeProfiles(List<string> calls) : IPartnerProfiles
    {
        public bool Available => true;
        public LinkOutcome Outcome = new(LinkStatus.Applied);

        public IReadOnlyList<PartnerLink> List() => [];

        public (LinkOutcome Outcome, Guid? Temporary) Apply(ushort objectIndex, Guid profile)
        {
            calls.Add($"profile {objectIndex} {Label(profile)}");
            return (Outcome, Outcome.Status == LinkStatus.Applied ? TemporaryId : null);
        }

        public void Remove(Guid temporary) => calls.Add("remove " + Label(temporary));
    }

    private static string Failed(string? detail) => string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
}
