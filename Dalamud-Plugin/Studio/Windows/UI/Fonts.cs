using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// The larger fonts tiles and headers use: the game's own Axis face at two sizes. Built by
/// Dalamud's font atlas in the background; until a font is ready, text simply draws in the default
/// font.
/// </summary>
/// <remarks>
/// Each font is locked once a frame and pushed straight into ImGui: a game font handle's own Push
/// (and even its Available) allocates several hundred bytes, and lists push a font for every tile.
/// </remarks>
internal static class Fonts
{
    private static Slot? _title;
    private static Slot? _heading;

    public static void Initialize(IUiBuilder ui)
    {
        _title   = new Slot(ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis14)));
        _heading = new Slot(ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18)));
    }

    /// <summary>Tile titles: a little larger than body text.</summary>
    public static IDisposable Title() => Push(_title);

    /// <summary>Header tiles and the library's heading.</summary>
    public static IDisposable Heading() => Push(_heading);

    private static IDisposable Push(Slot? slot)
    {
        if (slot?.Current() is not { } font)
            return Nothing.Instance;
        ImGui.PushFont(font.ImFont);
        return Pop.Instance;
    }

    public static void Dispose()
    {
        _title?.Dispose();
        _heading?.Dispose();
        _title = _heading = null;
    }

    /// <summary> A font handle and its lock for the current frame. </summary>
    private sealed class Slot(IFontHandle handle) : IDisposable
    {
        private ILockedImFont? _locked;
        private int _frame = -1;

        /// <summary> The font locked for this frame; null while it isn't built. Locked again each frame, so a rebuilt atlas is picked up. </summary>
        public ILockedImFont? Current()
        {
            var frame = ImGui.GetFrameCount();
            if (frame == _frame)
                return _locked;
            _frame = frame;
            _locked?.Dispose();
            _locked = null;
            try
            {
                if (handle.Available)
                    _locked = handle.Lock();
            }
            catch (Exception)
            {
                // Not built yet, or failed to build: text draws in the default font.
            }
            return _locked;
        }

        public void Dispose()
        {
            _locked?.Dispose();
            _locked = null;
            handle.Dispose();
        }
    }

    private sealed class Pop : IDisposable
    {
        public static readonly Pop Instance = new();
        public void Dispose() => ImGui.PopFont();
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
