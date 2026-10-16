using InstantEdit.Studio.Services.Stash;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The package importer: where extractions go by default, the Unity .unitypackage reader against
/// hostile input, texture purposes, and the stash manifest each extraction leaves behind.
/// </summary>
internal static class PackageImportScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "PackageImport");
        Directory.CreateDirectory(root);

        OutputDefaultsToTheCache();
        OldTempDefaultMovesToTheCache();
        PickedFolderIsKept();
    }

    private const string CacheRoot = @"C:\c\XIV Instant Edit";

    private static void OutputDefaultsToTheCache()
    {
        Require(PackageImportPaths.DefaultOutput(CacheRoot) == @"C:\c\XIV Instant Edit\package-imports",
            "extractions default to package-imports inside the cache folder");
        Require(PackageImportPaths.ChooseOutput("", CacheRoot, @"C:\t\") == PackageImportPaths.DefaultOutput(CacheRoot),
            "no saved folder means the cache default");
    }

    private static void OldTempDefaultMovesToTheCache()
    {
        var expected = PackageImportPaths.DefaultOutput(CacheRoot);
        Require(PackageImportPaths.ChooseOutput(@"C:\t\InstantEdit\sims-import", CacheRoot, @"C:\t\") == expected,
            "the old %TEMP% default moves to the cache");
        Require(PackageImportPaths.ChooseOutput(@"c:\T\instantedit\SIMS-IMPORT\", CacheRoot, @"C:\t\") == expected,
            "the old default is recognised whatever its case and with a trailing slash");
    }

    private static void PickedFolderIsKept()
    {
        Require(PackageImportPaths.ChooseOutput(@"D:\mine", CacheRoot, @"C:\t\") == @"D:\mine",
            "a folder the user picked is kept");
    }
}
