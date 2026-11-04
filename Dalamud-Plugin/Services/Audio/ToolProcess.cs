using System.Diagnostics;
using System.Text;

namespace InstantEdit.Services.Audio;

/// <summary>What a tool run printed: its exit code and the last lines of stdout and stderr.</summary>
internal sealed record ToolResult(int ExitCode, IReadOnlyList<string> Output, IReadOnlyList<string> Errors)
{
    /// <summary>Stdout then stderr, for parsers that look at both.</summary>
    public IEnumerable<string> Lines => Output.Concat(Errors);
}

/// <summary>
/// Runs a downloaded tool (yt-dlp, ffmpeg) hidden, with its arguments as a list so paths with spaces
/// or quotes stay whole. Stdout and stderr are read line by line as they come, so a full pipe never
/// stalls the tool. Cancelling or running past the timeout kills the whole process tree. Dalamud-free.
/// </summary>
internal static class ToolProcess
{
    // Enough to find an error line; yt-dlp's --newline progress would otherwise grow without bound.
    private const int KeptLines = 200;

    /// <summary>The per-job work folders: %TEMP%/InstantEdit/sound-import/&lt;guid&gt;.</summary>
    public static string WorkRoot => Path.Combine(Path.GetTempPath(), "InstantEdit", "sound-import");

    /// <summary>A new, empty work folder under <see cref="WorkRoot"/>.</summary>
    public static string NewWorkFolder()
    {
        var folder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Deletes a work folder and everything in it; a file still held open is left for the temp cleaner.</summary>
    public static void DeleteWorkFolder(string folder, Action<string>? log = null)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Couldn't delete the work folder {folder}: {e.Message}");
        }
    }

    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the process tree is killed.</exception>
    /// <exception cref="TimeoutException">The run took longer than <paramref name="timeout"/>; the process tree is killed.</exception>
    public static async Task<ToolResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? onOutput = null, Action<string>? onError = null, string? workingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        var output = new Queue<string>();
        var errors = new Queue<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Received(e.Data, output, onOutput);
        process.ErrorDataReceived += (_, e) => Received(e.Data, errors, onError);
        process.Start();
        // Tools that ask a question read EOF instead of waiting forever.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileNameWithoutExtension(executable)} took longer than {timeout.TotalMinutes:0.#} minutes.");
        }
        // Flushes the last redirected lines.
        process.WaitForExit();
        lock (output) lock (errors)
            return new ToolResult(process.ExitCode, output.ToArray(), errors.ToArray());
    }

    private static void Received(string? line, Queue<string> kept, Action<string>? callback)
    {
        if (line == null)
            return;
        lock (kept)
        {
            kept.Enqueue(line);
            if (kept.Count > KeptLines)
                kept.Dequeue();
        }
        callback?.Invoke(line);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            // Releases the tool's files before the work folder is deleted.
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
