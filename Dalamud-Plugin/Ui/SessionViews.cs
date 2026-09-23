using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary> The state a texture edit session is shown in, in badge precedence order. </summary>
internal enum SessionState
{
    Watching,
    NeedsMod,
    Paused,
    Conflict,
}

/// <summary> Pure presentation helpers for texture edit sessions. </summary>
internal static class SessionViews
{
    public static SessionState StateOf(TextureEditSession session)
        => session.Conflict ? SessionState.Conflict
            : session.Paused ? SessionState.Paused
            : session.NeedsMod ? SessionState.NeedsMod
            : SessionState.Watching;

    /// <summary> "1024 × 1024 · BC7 (originally BC3) · Mipmaps" </summary>
    public static string Caption(TextureEditSession session)
    {
        var format = TextureFiles.FormatName(session.SavedFormat)
            + (session.SavedFormat == session.Format ? "" : $" (originally {TextureFiles.FormatName(session.Format)})");
        return $"{session.Width} × {session.Height} · {format} · {(session.MipMaps ? "Mipmaps" : "No mipmaps")}";
    }

    public static string DestinationLine(TextureEditSession session)
        => session.NeedsMod ? $"First save creates mod: {session.NewModName}" : $"Destination: {session.TargetFile}";

    /// <summary> The mod a session writes to (or will create), used to group the session list. </summary>
    public static string GroupKey(TextureEditSession session)
        => session.NeedsMod
            ? UiText.Safe(session.NewModName, "New mod")
            : UiText.Safe(session.ModDirectory, "Unknown mod");

    /// <summary> Sessions grouped by target mod, groups and members in first-seen order. </summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<TextureEditSession> Sessions)> Group(IReadOnlyList<TextureEditSession> sessions)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<TextureEditSession>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var key = GroupKey(session);
            if (!groups.TryGetValue(key, out var members))
            {
                members = [];
                groups[key] = members;
                order.Add(key);
            }
            members.Add(session);
        }

        return order.Select(key => (key, (IReadOnlyList<TextureEditSession>)groups[key])).ToArray();
    }
}
