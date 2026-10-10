using System;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// The larger fonts tiles and headers use: the game's own Axis face at two sizes. Built by
/// Dalamud's font atlas in the background; until a font is ready, text simply draws in the default
/// font.
/// </summary>
internal static class Fonts
{
    private static IFontHandle? _title;
    private static IFontHandle? _heading;

    public static void Initialize(IUiBuilder ui)
    {
        _title   = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis14));
        _heading = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
    }

    /// <summary>Tile titles: a little larger than body text.</summary>
    public static IDisposable Title() => Push(_title);

    /// <summary>Header tiles and the library's heading.</summary>
    public static IDisposable Heading() => Push(_heading);

    private static IDisposable Push(IFontHandle? handle)
    {
        try
        {
            return handle is { Available: true } ? handle.Push() : Nothing.Instance;
        }
        catch (Exception)
        {
            return Nothing.Instance;
        }
    }

    public static void Dispose()
    {
        _title?.Dispose();
        _heading?.Dispose();
        _title = _heading = null;
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
