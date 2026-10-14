using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Plays a sound file in game for the Timeline's sound inspector, so it sounds as it will in game:
/// through the function the game plays a sound file by path with (VFXEditor's PlaySoundSig, a call
/// the signature's E8 leads to), with play = 1 on the framework thread, the path an ASCII string
/// freed right after the call as VFXEditor does. The file is served at a game path of its own
/// (<see cref="TimelineListen"/>) by a Penumbra temporary mod in every collection, which goes after
/// the sound's length, on the next play and on dispose. The only Dalamud file of Services/Audio.
/// </summary>
internal sealed class GameSoundPlayer : IDisposable
{
    internal const string PlaySoundSignature = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? FE C2";
    private const string Tag = "InstantEdit.Listen";
    private const int Priority = int.MaxValue;

    private readonly IFramework framework;
    private readonly PenumbraService penumbra;
    private readonly IPluginLog log;
    private readonly Func<string> cacheRoot;
    /// <summary>The play-sound function, <c>nint (nint path, byte play)</c>; 0 when not found.</summary>
    private readonly nint playSound;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? expiry;
    private string? session;
    // Read and written from the framework thread and from tasks: Dispose must see a mod being added.
    private volatile bool served;
    private volatile bool disposed;

    public GameSoundPlayer(ISigScanner scanner, IFramework framework, PenumbraService penumbra, IPluginLog log, Func<string> cacheRoot)
    {
        this.framework = framework;
        this.penumbra = penumbra;
        this.log = log;
        this.cacheRoot = cacheRoot;
        try
        {
            if (scanner.TryScanText(PlaySoundSignature, out var address) && address != nint.Zero)
                playSound = address;
            else
                log.Warning("The game's play-sound function wasn't found, so the Timeline can't play sounds.");
        }
        catch (Exception e)
        {
            log.Warning(e, "The game's play-sound function couldn't be looked up.");
        }
    }

    /// <summary>Why sounds can't be played now; null when they can.</summary>
    public string? Problem =>
        playSound == nint.Zero ? "Listening isn't available in this game version."
        : !penumbra.Available ? "Listening needs Penumbra."
        : null;

    /// <summary>Plays sound 0 of <paramref name="scd"/> in game; the previous sound's file stops being served.</summary>
    public async Task PlayAsync(byte[] scd, CancellationToken token)
    {
        if (Problem is { } problem)
            throw new InvalidOperationException(problem);
        var plan = TimelineListen.For(scd);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EndExpiry();
            await StopServingAsync();
            var file = Path.Combine(Session(), Path.GetFileName(plan.GamePath));
            await File.WriteAllBytesAsync(file, plan.Bytes, token);
            var added = await framework.RunOnFrameworkThread(() =>
            {
                // Dispose runs on the framework thread too: either it already ran, or it sees served and removes the mod.
                if (disposed)
                    return PenumbraApiEc.NothingChanged;
                served = true;
                return penumbra.AddTemporaryModAllOnFramework(Tag, new Dictionary<string, string> { [plan.GamePath] = file }, Priority);
            });
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (added != PenumbraApiEc.Success)
                    throw new IOException($"Penumbra could not serve the sound ({added}).");
                // Penumbra's collections take the new path on the frames after it was added.
                await framework.DelayTicks(2, token);
                await framework.RunOnFrameworkThread(() =>
                {
                    // Only a path Penumbra serves from the file just written is played, as VFXEditor only plays files that exist.
                    if (disposed || !TimelineListen.Served(penumbra.ResolveInterfacePathOnFramework(plan.GamePath), file))
                        throw new IOException("Penumbra doesn't serve the sound yet, so it wasn't played. Try again.");
                    Play(plan.GamePath);
                });
            }
            catch
            {
                // Nothing plays: the temporary mod goes now rather than on the next play. Dispose removes it itself.
                if (!disposed)
                    try { await StopServingAsync(); }
                    catch (Exception e) { log.Warning(e, "Could not stop serving the sound played from the Timeline."); }
                throw;
            }
            var cancel = expiry = new CancellationTokenSource();
            _ = ExpireAsync(TimeSpan.FromSeconds(plan.Seconds), cancel.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops serving the last sound now, as when the Timeline window closes.</summary>
    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            EndExpiry();
            if (!disposed)
                await StopServingAsync();
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not stop serving the sound played from the Timeline.");
        }
        finally
        {
            gate.Release();
        }
    }

    private void EndExpiry()
    {
        expiry?.Cancel();
        expiry?.Dispose();
        expiry = null;
    }

    private unsafe void Play(string gamePath)
    {
        var bytes = Encoding.ASCII.GetBytes(gamePath);
        var path = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, path, bytes.Length);
            Marshal.WriteByte(path + bytes.Length, 0);
            ((delegate* unmanaged<nint, byte, nint>)playSound)(path, 1);
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }
    }

    private async Task ExpireAsync(TimeSpan after, CancellationToken token)
    {
        try
        {
            await Task.Delay(after, token);
            await gate.WaitAsync(token);
            try
            {
                if (!disposed)
                    await StopServingAsync();
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not stop serving the sound played from the Timeline.");
        }
    }

    /// <summary>Removes the temporary mod and the files it served. Runs under <see cref="gate"/>.</summary>
    private async Task StopServingAsync()
    {
        if (served)
        {
            var (gone, error) = await framework.RunOnFrameworkThread(() => penumbra.RemoveTemporaryModAllOnFramework(Tag, Priority));
            if (!gone)
                throw new IOException(error ?? "Penumbra could not remove the sound.");
            served = false;
        }
        if (session != null)
            foreach (var file in Directory.EnumerateFiles(session))
                try { File.Delete(file); }
                catch (IOException) { }
    }

    /// <summary>The folder the served files are written to: a session of the animation preview's cache folder, cleaned like its own.</summary>
    private string Session()
    {
        if (session == null)
        {
            var root = AnimationPreviewRules.PreviewRoot(cacheRoot());
            session = AnimationPreviewRules.SessionFolder(root, AnimationPreviewRules.NewSessionName());
        }
        Directory.CreateDirectory(session);
        return session;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        expiry?.Cancel();
        if (served)
        {
            try
            {
                (bool Gone, string? Error) removed;
                if (framework.IsInFrameworkUpdateThread)
                    removed = penumbra.RemoveTemporaryModAllOnFramework(Tag, Priority);
                else
                {
                    var task = framework.RunOnFrameworkThread(() => penumbra.RemoveTemporaryModAllOnFramework(Tag, Priority));
                    removed = task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : (false, "Timed out.");
                }
                if (!removed.Gone)
                    log.Warning($"Could not remove the Timeline's listen sound: {removed.Error}");
            }
            catch (Exception e)
            {
                log.Warning(e, "Could not remove the Timeline's listen sound.");
            }
        }
        if (session != null)
            try { Directory.Delete(session, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
