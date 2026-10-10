namespace InstantEdit.Studio.Services.Check;

/// <summary>Penumbra's reload and mod list for repairs and clean-up, asked on the framework thread (inline when already on it).</summary>
internal sealed class PenumbraModReloader : IModReloader
{
    private readonly PenumbraIpcService _ipc;

    public PenumbraModReloader(PenumbraIpcService ipc) => _ipc = ipc;

    public bool Reload(string folderName)
        => StudioServices.Framework.RunOnFrameworkThread(() => _ipc.ReloadMod(folderName) is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
            .GetAwaiter().GetResult();

    public bool IsPresent(string folderName)
        => StudioServices.Framework.RunOnFrameworkThread(() => _ipc.GetModList()?.ContainsKey(folderName) ?? false).GetAwaiter().GetResult();
}
