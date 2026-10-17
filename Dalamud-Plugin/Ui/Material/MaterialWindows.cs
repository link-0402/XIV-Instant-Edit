using System.Numerics;
using System.Text.Json.Nodes;
using Dalamud.Interface.Windowing;
using InstantEdit.Models;
using InstantEdit.Services.Materials;
using InstantEdit.Studio;

namespace InstantEdit.Ui.Material;

/// <summary>
/// The Material windows, one per open material, each with its own temporary-mod preview. A material a
/// window already holds opens there; otherwise a window with nothing to lose (empty, or closed with
/// no unsaved changes) takes it, or a new one opens beside the last.
/// </summary>
internal sealed class MaterialWindows : IDisposable
{
    private const float Cascade = 32f;

    private readonly WindowSystem system;
    private readonly Func<int, MaterialPreviewService> newPreview;
    private readonly Func<int, MaterialPreviewService, MaterialWindow> newWindow;
    private readonly Dictionary<int, MaterialPreviewService> previews = [];
    private readonly List<MaterialWindow> windows = [];
    // Opening happens while the window system draws, so new windows join it on the next frame.
    private readonly List<MaterialWindow> joining = [];
    private MaterialWindow? last;

    public MaterialWindows(WindowSystem system, IEnumerable<int> recordedSlots, Func<int, MaterialPreviewService> newPreview,
        Func<int, MaterialPreviewService, MaterialWindow> newWindow)
    {
        this.system = system;
        this.newPreview = newPreview;
        this.newWindow = newWindow;
        // A crash may have left previews of other slots behind; their services take them down at start.
        foreach (var slot in recordedSlots.Prepend(0).Distinct())
            previews[slot] = newPreview(slot);
        // The first window joins the system with AddJoining, like every later one.
        Create();
    }

    /// <summary>Opens a row's material in the window that holds it, a free one, or a new one.</summary>
    public void Open(ResourceView row, ActorView actor, IModFileCopies? copies)
    {
        var target = windows.FirstOrDefault(window => window.Holds(row.GamePath))
                     ?? windows.FirstOrDefault(window => window.Free && window.IsOpen)
                     ?? windows.FirstOrDefault(window => window.Free)
                     ?? Create();
        target.Open(row, actor, copies);
        target.BringToFront();
        last = target;
    }

    /// <summary>Adds windows made while drawing; call before the window system draws.</summary>
    public void AddJoining()
    {
        foreach (var window in joining)
            system.AddWindow(window);
        joining.Clear();
    }

    private MaterialWindow Create()
    {
        var slot = 0;
        while (windows.Any(window => window.Slot == slot))
            slot++;
        if (!previews.TryGetValue(slot, out var preview))
            previews[slot] = preview = newPreview(slot);
        var window = newWindow(slot, preview);
        if (last?.LastPosition is { } from)
        {
            window.Position = from + new Vector2(Cascade);
            window.PositionCondition = Dalamud.Bindings.ImGui.ImGuiCond.FirstUseEver;
        }
        windows.Add(window);
        joining.Add(window);
        return window;
    }

    /// <summary>The last opened window's state for the XIV Bridge test tool <c>material</c>; framework thread.</summary>
    internal JsonObject TestState(uint? constant)
    {
        var state = (last ?? windows[0]).TestState(constant);
        state["windows"] = windows.Count;
        state["openWindows"] = windows.Count(window => window.IsOpen);
        return state;
    }

    public void Dispose()
    {
        foreach (var window in windows)
        {
            if (!joining.Contains(window))
                system.RemoveWindow(window);
            window.Dispose();
        }
        foreach (var preview in previews.Values)
            preview.Dispose();
    }
}
