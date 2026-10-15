using System.Diagnostics;
using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using static InstantEdit.TestSupport.Assertions;

/// <summary>A mod window's check: running it off the render thread, keeping the last report, and marking the Resources tree.</summary>
internal static partial class ModCheckScenarios
{
    public static void RunSession(string testRoot)
    {
        ACheckFinishesIntoTheReport();
        AFailedCheckKeepsTheLastReport();
        AReportGoesStaleWhenTheModChanges();
        MarkersPickTheWorstFindingPerNode();
        SharedBodyMaterialsAreNotMissing();
        OperationsAskForACheckAfter();
        OperationsReportProgressAndCanBeCancelled();
        TheReportsFingerprintIsTakenWithTheInput();
    }

    private static void WaitFor(CheckSession session)
    {
        var clock = Stopwatch.StartNew();
        while ((session.Running || session.Busy) && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(10);
            session.Update();
        }
        session.Update();
    }

    private static void ACheckFinishesIntoTheReport()
    {
        using var mod = StellarBladeMod();
        var session = new CheckSession();
        session.Start(CheckInput.ForFolder(mod.Path, StellarBladeGame(), StellarBladeItems(), 801));
        Require(session.Running, "a check runs in the background");
        WaitFor(session);
        Require(!session.Running && session.Report is { } report && report.Count(Severity.Error) > 0 && session.Error is null &&
                session.Selection is { } selection && report.Repairs.Where(r => r.Preselected).All(r => selection.IsTicked(r.Id)),
            "a finished check gives its report, with the certain repairs ticked");
    }

    private static void AFailedCheckKeepsTheLastReport()
    {
        using var mod = StellarBladeMod();
        var session = new CheckSession();
        session.Start(CheckInput.ForFolder(mod.Path, StellarBladeGame(), StellarBladeItems(), 801));
        WaitFor(session);
        var first = session.Report;

        session.Start(CheckInput.ForFolder(mod.Path, StellarBladeGame(), StellarBladeItems(), 801),
            () => throw new InvalidOperationException("Penumbra went away"));
        WaitFor(session);
        Require(ReferenceEquals(session.Report, first) && session.Error is { } error && error.Contains("Penumbra went away", StringComparison.Ordinal),
            "a check that fails keeps the last report and says why");
    }

    private static void AReportGoesStaleWhenTheModChanges()
    {
        using var mod = StellarBladeMod();
        var session = new CheckSession();
        session.Start(CheckInput.ForFolder(mod.Path, StellarBladeGame(), StellarBladeItems(), 801));
        WaitFor(session);
        var project = ModProject.Load(mod.Path);
        Require(!session.Stale(project), "a fresh report is not stale");
        mod.Meta(mod.ReadMeta().Replace("\"Author\": \"Luci\"", "\"Author\": \"Someone else\""));
        Require(session.Stale(project), "a report goes stale once meta.json changes");
    }

    private static void MarkersPickTheWorstFindingPerNode()
    {
        using var mod = GearMod();
        var meta = PenumbraModMeta.Load(mod.Path);
        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod.Path, Files = meta.AllFiles().ToList(), Swaps = meta.AllSwaps().ToList() },
            new ModFileFactsCache(), GearGame(), CancellationToken.None);
        var report = new ModCheckReport(mod.Path, MetaFingerprint.Of(mod.Path), DateTime.UtcNow,
        [
            new CheckFinding("a", "C009", CheckCategory.Materials, Severity.Info, "Material is from before Dawntrail", "", GearMaterial, OptionRef.Default),
            new CheckFinding("b", "C004", CheckCategory.Materials, Severity.Error, "Material sampler has no texture", "", GearMaterial, OptionRef.Default),
            new CheckFinding("c", "R002", CheckCategory.Races, Severity.Info, "1 race keeps its own model", ""),
        ], [], [], [], null, null, []);

        var markers = CheckMarkers.Build(report, graph);
        var node = graph.Find(GearMaterial, OptionRef.Default)!;
        Require(markers.Count == 1 && markers[node.Id].Severity == Severity.Error && markers[node.Id].Title == "Material sampler has no texture",
            "a file with findings is marked with the worst one; findings about no file mark nothing");
    }

    private static void SharedBodyMaterialsAreNotMissing()
    {
        using var mod = GearMod(model: ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl", "/mt_c0201b0001_bibo.mtrl",
            "/mt_c0201b0001_piercings.mtrl", "/mt_c0201b0001_bibopube.mtrl", "/mt_c0201e0005_top_b.mtrl", "/mt_c0201e0005_top_bibo.mtrl"]));
        var meta = PenumbraModMeta.Load(mod.Path);
        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod.Path, Files = meta.AllFiles().ToList(), Swaps = meta.AllSwaps().ToList() },
            new ModFileFactsCache(), GearGame(), CancellationToken.None);

        var materials = graph.Nodes.Values.Where(n => n.Kind == ResourceKind.Material).ToDictionary(n => Path.GetFileName(n.GamePath));
        Require(materials["mt_c0201b0001_bibo.mtrl"].Origin == OriginKind.Shared && materials["mt_c0201b0001_piercings.mtrl"].Origin == OriginKind.Shared
                && materials["mt_c0201b0001_bibopube.mtrl"].Origin == OriginKind.Shared && materials["mt_c0201e0005_top_b.mtrl"].Origin == OriginKind.Missing,
            "skin, pubes and piercings materials the body mod provides are shared; the item's own lacking material is missing");
        Require(materials["mt_c0201b0001_piercings.mtrl"].Note!.StartsWith("piercings", StringComparison.Ordinal)
                && materials["mt_c0201b0001_bibo.mtrl"].Note!.StartsWith("skin", StringComparison.Ordinal),
            "the name picks the note inside the body folder");
        Require(materials["mt_c0201e0005_top_bibo.mtrl"] is { Origin: OriginKind.Missing } gear && gear.GamePath.StartsWith("chara/equipment/", StringComparison.Ordinal),
            "a gear material the mod forgot is missing, even with a body mod's word in its name");
        Require(graph.Unresolved == 2 && graph.Roots.Sum(r => r.MissingCount) == 2,
            "only the item's own materials count as missing");
    }

    private static void OperationsAskForACheckAfter()
    {
        var session = new CheckSession();
        session.StartOperation("Applying fixes", _ => Task.FromResult((true, "Applied 2 fixes.")), checkAfter: true);
        Require(session.Busy, "an operation keeps the session busy");
        WaitFor(session);
        Require(session.LastOperation is { Ok: true, Message: "Applied 2 fixes." } && session.TakeCheckRequest() && !session.TakeCheckRequest(),
            "a finished operation says how it went and asks once for a new check");
    }

    private static void OperationsReportProgressAndCanBeCancelled()
    {
        var session = new CheckSession();
        var progress = session.OperationProgress;
        session.StartOperation("Changing textures", async token =>
        {
            progress.Report(new CheckProgress("Encoding", 1, 3));
            await Task.Delay(Timeout.Infinite, token);
            return (true, "done");
        }, checkAfter: false);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (session.Progress is null && waited.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(10);
        Require(session.Progress is { Phase: "Encoding", Current: 1, Total: 3 }, "an operation's progress shows");

        session.Cancel();
        while (session.Busy && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(10);
            session.Update();
        }
        Require(!session.Busy && session.LastOperation is (false, "Cancelled.") && session.Progress is null, "Cancel stops it");
    }

    private static void TheReportsFingerprintIsTakenWithTheInput()
    {
        using var mod = GearMod();
        var input = CheckInput.From(ModProject.Load(mod.Path), GearGame(), null, 201, null);
        var changed = Newtonsoft.Json.Linq.JObject.Parse(mod.ReadMeta());
        changed["Author"] = "Someone else";
        mod.Meta(changed.ToString());
        var report = ModCheck.Run(input, null, default);
        Require(report.Fingerprint != MetaFingerprint.Of(mod.Path),
            "a meta.json changed after the mod was copied for the check makes the report stale, not fresh");
    }
}
