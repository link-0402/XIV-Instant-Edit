using Dalamud.Interface;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio;
using InstantEdit.Studio.Windows.Home;

namespace InstantEdit.Ui.Main;

/// <summary> The Mod Browser: Penumbra's mods as tiles, a window per open mod, and the modpack builder. </summary>
internal sealed class ModBrowserTab
{
    private readonly PenumbraIpc _penumbra;
    private readonly StatusFeed _feed;
    private StudioHost? _studio;
    private LibraryView? _library;

    public ModBrowserTab(PenumbraIpc penumbra, StatusFeed feed)
    {
        _penumbra = penumbra;
        _feed = feed;
    }

    /// <summary> Starts the tab on the plugin's Mod Browser; its mod windows hand their files to <paramref name="handoff"/>. </summary>
    public void Attach(StudioHost studio, IModHandoff handoff)
    {
        _studio = studio;
        _library = new LibraryView(studio);
        studio.Handoff = handoff;
    }

    /// <summary> Every Penumbra mod as a tile; a mod opens in a window of its own, Build opens the build window. </summary>
    public void Draw()
    {
        if (_library is null)
        {
            Widgets.EmptyState(FontAwesomeIcon.FolderOpen, "Mod Browser unavailable", "It could not be started. See the log for why.");
            return;
        }
        _library.Draw();
    }

    /// <summary>
    /// Opens a mod in a window of its own: its folder <paramref name="modRootPath"/>, or the
    /// folder <paramref name="modDirectory"/> in Penumbra's mod directory when the root is unknown.
    /// </summary>
    public void OpenModWindow(string modRootPath, string modDirectory)
    {
        if (_studio is null)
            return;
        var folder = modRootPath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            var root = _penumbra.GetModDirectory();
            if (string.IsNullOrWhiteSpace(root))
            {
                _feed.SetStatus("Penumbra's mod folder is unknown, so the mod can't be opened.", FeedbackSeverity.Warning);
                return;
            }
            folder = Path.Combine(root, modDirectory);
        }
        if (!_studio.OpenMod(folder, out var error))
            _feed.SetStatus($"Could not open the mod: {error}", FeedbackSeverity.Warning);
    }
}
