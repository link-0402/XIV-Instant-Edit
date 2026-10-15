namespace InstantEdit.Services;

internal sealed class TextureConflictException(string message) : IOException(message);

/// <summary>A save that can't go through right now, such as while Penumbra loads; the session tries it again.</summary>
internal sealed class TextureWaitException(string message, bool modUnlisted = false) : IOException(message)
{
    /// <summary>Penumbra runs but doesn't list the mod, which may not end; the session pauses after a while.</summary>
    public bool ModUnlisted { get; } = modUnlisted;
}
