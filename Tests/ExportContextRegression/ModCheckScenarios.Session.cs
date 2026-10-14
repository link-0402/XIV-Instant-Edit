using System.Diagnostics;
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
            "/mt_c0201b0001_piercings.mtrl", "/mt_c0201b0001_bibopube.mtrl", "/mt_c0201e0005_top_b.mtrl"]));
        var meta = PenumbraModMeta.Load(mod.Path);
        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod.Path, Files = meta.AllFiles().ToList(), Swaps = meta.AllSwaps().ToList() },
            new ModFileFactsCache(), GearGame(), CancellationToken.None);

        var origins = graph.Nodes.Values.Where(n => n.Kind == ResourceKind.Material).ToDictionary(n => Path.GetFileName(n.GamePath), n => n.Origin);
        Require(origins["mt_c0201b0001_bibo.mtrl"] == OriginKind.Shared && origins["mt_c0201b0001_piercings.mtrl"] == OriginKind.Shared
                && origins["mt_c0201b0001_bibopube.mtrl"] == OriginKind.Shared && origins["mt_c0201e0005_top_b.mtrl"] == OriginKind.Missing,
            "skin, pubes and piercings materials the body mod provides are shared; the item's own lacking material is missing");
        Require(graph.Unresolved == 1 && graph.Roots.Sum(r => r.MissingCount) == 1,
            "only the item's own material counts as missing");
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
}
