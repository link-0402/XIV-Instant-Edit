using InstantEdit.Services.Audio;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// What the sound inspector's listen buttons play in game: the edited file cut down to the one sound
/// (or one alternative) as sound 0, served at a game path of its own. Dalamud-free.
/// </summary>
internal static class TimelineListen
{
    /// <summary>How long after its length a played sound's file stays served, in seconds.</summary>
    public const double ExtraSeconds = 2;

    /// <summary>A sound to play: the game path it is served at, its bytes, and how long to keep serving it.</summary>
    public sealed record Plan(string GamePath, byte[] Bytes, double Seconds);

    /// <summary>
    /// The plan for <paramref name="scd"/>, a file whose sound 0 plays: served at
    /// sound/instant_edit/listen_hash.scd, a path nothing else uses, for its length plus
    /// <see cref="ExtraSeconds"/>.
    /// </summary>
    public static Plan For(byte[] scd)
    {
        var info = ScdFile.Info(scd);
        if (info.SoundCount == 0)
            throw new InvalidDataException("This sound file has nothing to play.");
        var gamePath = $"sound/instant_edit/listen_{TimelineImports.Hash8(scd)}.scd";
        return new Plan(gamePath, scd, info.SoundSeconds[0] + ExtraSeconds);
    }

    /// <summary>
    /// Whether Penumbra serves the listen path from <paramref name="file"/>: <paramref name="resolved"/>,
    /// what it resolves the path to, is that file. Only then is the game asked to play it, as VFXEditor
    /// only plays a path whose file exists.
    /// </summary>
    public static bool Served(string? resolved, string file) =>
        !string.IsNullOrWhiteSpace(resolved) && Path.IsPathFullyQualified(resolved) && PathRules.SamePhysicalPath(resolved, file);

    /// <summary>The header's listen button: the whole sound <paramref name="sound"/> of the edited file, as sound 0.</summary>
    public static byte[] Whole(byte[] edited, int sound) => ScdEdits.KeepSound(ScdModel.Read(edited), sound).Write();

    /// <summary>A row's listen button: only alternative <paramref name="entry"/> of sound <paramref name="sound"/>, as sound 0.</summary>
    public static byte[] Row(byte[] edited, int sound, int entry) => ScdEdits.KeepOnly(ScdModel.Read(edited), sound, entry).Write();
}
