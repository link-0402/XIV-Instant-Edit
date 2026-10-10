using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Check;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Checks and repairs after the review's minor findings: when the fingerprint is taken, files changed after the check, ticks during an apply.</summary>
internal static partial class ModCheckScenarios
{
    public static void RunMinors(string testRoot)
    {
        TheReportsFingerprintIsTakenWithTheInput();
        FilesChangedAfterTheCheckAreNeverDeleted(testRoot);
        ATicksSnapshotIsntChangedByLaterTicks();
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

    private static void FilesChangedAfterTheCheckAreNeverDeleted(string testRoot)
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var files = Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "meta.json").ToList();
        var later = DateTime.UtcNow.AddMinutes(5);
        foreach (var file in files)
            File.SetLastWriteTimeUtc(file, later);

        var result = ApplyRecommended(mod, report, new FakeReloader(), testRoot, deleteReleased: true);
        Require(result.Ok && files.All(File.Exists), $"files changed after the check are never deleted ({result.Message})");
    }

    private static void ATicksSnapshotIsntChangedByLaterTicks()
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var selection = new RepairSelection(report) { DeleteReleasedFiles = true };
        var snapshot = selection.Snapshot();
        foreach (var repair in report.Repairs)
            selection.Set(repair.Id, false);
        selection.DeleteReleasedFiles = false;
        Require(snapshot.Count > 0 && snapshot.Count == new RepairSelection(report).Count && snapshot.DeleteReleasedFiles,
            "ticks changed while a repair is applied don't reach the apply under way");
    }
}
