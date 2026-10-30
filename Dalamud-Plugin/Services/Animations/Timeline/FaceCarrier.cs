using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// What plays a face opened on its own (Edit face), which has no expression of its own: a vanilla expression's
/// timeline, put in the preview's temporary mod with its one face entry naming the face and its face pack set to the
/// face's library. The expression is played in the face slot, so the face shows on its own and Live can run over it.
/// </summary>
internal static class FaceCarrier
{
    /// <summary>The vanilla expression a face opened on its own plays through.</summary>
    public const string Key = "facial/pose/f_umm";

    /// <summary>The carrier's timeline file, as the preview's temporary mod maps it.</summary>
    public static string GamePath => $"chara/action/{Key}.tmb";

    private static ushort? timeline;

    /// <summary>
    /// The carrier's timeline with its face entry playing <paramref name="motion"/> from <paramref name="pack"/> (null: an
    /// always-loaded face, no pack); its timing and everything else stay as the vanilla expression has them.
    /// </summary>
    public static byte[] Build(byte[] vanilla, string motion, string? pack)
    {
        var document = TimelineConvert.Read(TmbFile.Parse(vanilla), TimelineDocument.ExpressionLength) with { Expression = true };
        var faces = document.Events.Where(e => e.Magic == "C010" && e.Lane == TimelineLane.Expression).ToArray();
        if (faces.Length != 1)
            throw new InvalidDataException("The carrier expression must play exactly one face.");
        var carried = document with
        {
            Events = document.Events.Replace(faces[0], faces[0] with { Path = motion }),
            FacePack = pack,
        };
        return TimelineConvert.Apply(vanilla, carried);
    }

    /// <summary>The face library a face's file is (nonresident/&lt;pack&gt;.pap); null for the always-loaded face.pap.</summary>
    public static string? PackOf(string gamePath) => FaceLibraryIndex.TryParse(gamePath, out _, out _, out var pack) ? pack : null;

    /// <summary>The carrier's ActionTimeline row, read once; null when the game has none.</summary>
    public static ushort? TimelineId(IDataManager data)
    {
        if (timeline is { } known)
            return known;
        foreach (var row in data.GetExcelSheet<ActionTimeline>())
            if (row.LoadType == 0 && row.Key.ExtractText() == Key && row.RowId is > 0 and <= ushort.MaxValue)
                return timeline = (ushort)row.RowId;
        return null;
    }
}
