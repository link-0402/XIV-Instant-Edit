using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using InstantEdit.Studio.Windows.Build;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows;

/// <summary>
/// The modpack being built: a new mod made by porting gear or character features from another
/// game, set up from your own files and built into Penumbra in one go (see <see cref="BuildScreen"/>).
/// The modpack is kept in the settings while the window is closed.
/// </summary>
internal sealed class BuildWindow : Window, IDisposable
{
    private readonly StudioHost _host;
    private readonly BuildScreen _screen;

    internal BuildWindow(StudioHost host, TextureThumbnailCache thumbnails)
        : base("Build a mod###ie-build", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _host   = host;
        _screen = new BuildScreen(host, thumbnails);

        Size            = new Vector2(1280, 760);
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(900, 500) };
    }

    public void Dispose() { }

    public override void OnClose() => _host.Pack.Flush();

    public override void Draw()
    {
        Theme.TileScale = Math.Clamp(_host.Configuration.TileScale, 0.75f, 1.6f);
        FileDrop.BeginExternalSource();
        _screen.Draw();
    }
}
