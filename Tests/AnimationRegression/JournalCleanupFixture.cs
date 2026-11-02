using InstantEdit.Services.Animations;

/// <summary>
/// A job folder is made as soon as an edit starts; its record is written at the end. A job that fails or is
/// cancelled in between leaves staged files and no record.
/// </summary>
internal static class JournalCleanupFixture
{
    public static void Run(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-journal-cleanup-" + Guid.NewGuid().ToString("N"));
        var edits = Path.Combine(temp, "AnimationEdits");
        Directory.CreateDirectory(edits);
        try
        {
            string Job(TimeSpan age)
            {
                var folder = Path.Combine(edits, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "staged.wav"), new byte[64]);
                Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);
                return folder;
            }
            var stale = Job(TimeSpan.FromDays(30));
            var recent = Job(TimeSpan.FromMinutes(5));
            var notAJob = Path.Combine(edits, "keep-me");
            Directory.CreateDirectory(notAJob);
            Directory.SetLastWriteTimeUtc(notAJob, DateTime.UtcNow - TimeSpan.FromDays(30));

            _ = new AnimationJournalStore(temp);   // clean-up runs when the store opens
            check(!Directory.Exists(stale), "a job folder that never got its record is removed once it is older than the retention");
            check(Directory.Exists(recent), "and one that may still be running is left alone");
            check(Directory.Exists(notAJob), "folders that are not job folders are never touched");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
        }
    }
}
