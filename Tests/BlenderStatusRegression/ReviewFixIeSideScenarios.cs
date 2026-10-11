using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data.Files;

/// <summary>Review fixes (IeSide group): drop scratch files and cleanup, and drops over unsaved working images.</summary>
internal static partial class TextureEditScenarios
{
    public static async Task RunReviewFixIeSideAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "instant-edit-textures-ieside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await DropCleanupAsync(Path.Combine(root, "drop-cleanup"));
            await DropKeepsUnsavedWorkingImageAsync(Path.Combine(root, "drop-unsaved"));
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test root.");
            Directory.Delete(root, true);
        }
    }

    /// <summary> bridge-1: a session a DDS drop or an Undo replace used is removed by automatic cleanup again. </summary>
    private static async Task DropCleanupAsync(string root)
    {
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        using (var f = new Fixture(Path.Combine(root, "dds"), (uint)TexFile.TextureFormat.BC7))
        {
            var dds = Path.Combine(images, "Painted.dds");
            File.WriteAllBytes(dds, [1, 2, 3, 4]);
            await f.Service.ReplaceAsync(f.Request, dds);
            var s = f.Service.Sessions.Single();
            var work = Path.Combine(s.Directory, "drop-work");
            Check(!Directory.Exists(work) || !Directory.EnumerateFiles(work).Any(), "ieside: a dropped DDS leaves no scratch files once converted");
            await f.Service.SetPausedAsync(s.Id, true);
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 1 && !Directory.Exists(s.Directory),
                "ieside: automatic cleanup removes a stale session a DDS drop used");
        }

        using (var f = new Fixture(Path.Combine(root, "undo"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.ReplaceAsync(f.Request, Png(Path.Combine(images, "Red.png"), 8, 8, 30));
            var s = f.Service.Sessions.Single();
            await f.Service.UndoReplaceAsync(s.Id);
            var work = Path.Combine(s.Directory, "drop-work");
            Check(!Directory.Exists(work) || !Directory.EnumerateFiles(work).Any(), "ieside: an Undo replace leaves no scratch files");
            await f.Service.SetPausedAsync(s.Id, true);
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 1 && !Directory.Exists(s.Directory),
                "ieside: automatic cleanup removes a stale session after an Undo replace");
        }

        using (var f = new Fixture(Path.Combine(root, "leftover"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            // A drop that stopped halfway, as when the game closed during its conversion.
            var work = Path.Combine(s.Directory, "drop-work");
            Directory.CreateDirectory(work);
            foreach (var name in new[] { "dropped.dds", "dropped.tex", "dropped.tga", "restored.tex" })
                File.WriteAllBytes(Path.Combine(work, name), [1]);
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 1 && !Directory.Exists(s.Directory),
                "ieside: drop scratch files left behind count as the service's own files");
        }
    }

    /// <summary>
    /// bridge-2: a drop over a working TGA holding edits the game doesn't have yet still reaches the game,
    /// but the TGA is kept and the session pauses, so the edits neither vanish nor go over the drop.
    /// </summary>
    private static async Task DropKeepsUnsavedWorkingImageAsync(string root)
    {
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        var png = Png(Path.Combine(images, "Red.png"), 8, 8, 30);

        // A paused session whose TGA was edited.
        using (var f = new Fixture(Path.Combine(root, "paused"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            var edited = Tga(8, 8, 55);
            File.WriteAllBytes(s.WorkingFile, edited);

            var result = await f.Service.ReplaceAsync(f.Request, png);
            s = f.Service.Sessions.Single();
            Check(result.Outcome == ExternalTextureOutcome.Applied && File.ReadAllBytes(s.TargetFile)[80] == 30,
                "ieside: a drop over an edited, paused working TGA still reaches the game");
            Check(File.ReadAllBytes(s.WorkingFile).SequenceEqual(edited) && s.Paused &&
                  result.Message.Contains("unsaved", StringComparison.Ordinal) && s.Status.Contains("unsaved", StringComparison.Ordinal),
                "ieside: the edited working TGA is kept and the session stays paused, which is said");
            await f.Service.ProcessPendingAsync(true);
            Check(File.ReadAllBytes(s.TargetFile)[80] == 30 && f.Backend.Commits == 1, "ieside: the kept edits don't go over the drop on their own");

            await f.Service.UndoReplaceAsync(s.Id);
            s = f.Service.Sessions.Single();
            Check(File.ReadAllBytes(s.TargetFile).SequenceEqual(f.Backend.Original) && File.ReadAllBytes(s.WorkingFile).SequenceEqual(edited) &&
                  s.Paused && s.Status.Contains("unsaved", StringComparison.Ordinal),
                "ieside: its Undo puts the texture back and keeps the edited working TGA too");

            await f.Service.SetPausedAsync(s.Id, false);
            await f.Service.ProcessPendingAsync(true);
            Check(File.ReadAllBytes(s.TargetFile)[80] == 55, "ieside: Resume saves the kept edits");
        }

        // An editor save the watcher hasn't taken yet (inside its settle time, or waiting).
        using (var f = new Fixture(Path.Combine(root, "settling"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            var edited = Tga(8, 8, 66);
            File.WriteAllBytes(s.WorkingFile, edited);
            var result = await f.Service.ReplaceAsync(f.Request, png);
            s = f.Service.Sessions.Single();
            Check(result.Outcome == ExternalTextureOutcome.Applied && File.ReadAllBytes(s.TargetFile)[80] == 30 &&
                  File.ReadAllBytes(s.WorkingFile).SequenceEqual(edited) && s.Paused,
                "ieside: a drop during an editor save applies the drop and keeps the save's TGA");
            await f.Service.ProcessPendingAsync(true);
            Check(File.ReadAllBytes(s.TargetFile)[80] == 30, "ieside: the pending editor save waits for Resume");
        }
    }
}
