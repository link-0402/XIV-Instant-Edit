using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Previews;

namespace InstantEdit.Ui;

internal static partial class Widgets
{
    private static string? _hoverId;
    private static double _hoverSince;
    private static int _hoverFrame;

    /// <summary>
    /// True once the same item has been hovered for the delay (Shift skips the wait). Call every
    /// frame while the item is hovered; the timer restarts when the hovered item changes or a
    /// frame passes without a call.
    /// </summary>
    public static bool HoverDelay(string id, double delaySeconds = .3)
    {
        var now = ImGui.GetTime();
        var frame = ImGui.GetFrameCount();
        if (!string.Equals(id, _hoverId, StringComparison.Ordinal) || frame - _hoverFrame > 1)
        {
            _hoverId = id;
            _hoverSince = now;
        }
        _hoverFrame = frame;
        return ImGui.GetIO().KeyShift || now - _hoverSince >= delaySeconds;
    }

    /// <summary> A grey checkerboard, so transparent pixels read as transparent. </summary>
    public static void Checkerboard(Vector2 position, Vector2 size, float cell)
    {
        var drawList = ImGui.GetWindowDrawList();
        var dark = ImGui.GetColorU32(new Vector4(.16f, .16f, .18f, 1));
        var light = ImGui.GetColorU32(new Vector4(.27f, .27f, .3f, 1));
        drawList.AddRectFilled(position, position + size, dark);
        cell = Math.Max(2, cell);
        for (var y = 0f; y < size.Y; y += cell)
        {
            for (var x = 0f; x < size.X; x += cell)
            {
                if (((int)(x / cell) + (int)(y / cell)) % 2 == 0)
                    continue;
                var corner = position + new Vector2(x, y);
                var end = new Vector2(Math.Min(corner.X + cell, position.X + size.X), Math.Min(corner.Y + cell, position.Y + size.Y));
                drawList.AddRectFilled(corner, end, light);
            }
        }
    }

    /// <summary>
    /// Draws a texture preview fitted into a box: a spinner while it loads, the image over a
    /// checkerboard when ready, or a short notice when it failed. Always occupies the box.
    /// </summary>
    public static void PreviewImage(PreviewEntry<TexturePreview> entry, Vector2 box, PreviewLayer layer = PreviewLayer.Colour)
    {
        var position = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        switch (entry.State)
        {
            case PreviewState.Ready when entry.Value is { } preview:
                FittedImage(preview.Image(layer), box);
                break;
            case PreviewState.Failed:
                drawList.AddRect(position, position + box, ImGui.GetColorU32(Theme.WithAlpha(Theme.Muted, .4f)), Theme.Scaled(3));
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var glyph = FontAwesomeIcon.EyeSlash.ToIconString();
                    var glyphSize = ImGui.CalcTextSize(glyph);
                    drawList.AddText(position + (box - glyphSize) / 2, ImGui.GetColorU32(Theme.Inactive), glyph);
                }
                ImGui.Dummy(box);
                break;
            default:
                drawList.AddRect(position, position + box, ImGui.GetColorU32(Theme.WithAlpha(Theme.Muted, .25f)), Theme.Scaled(3));
                ImGui.SetCursorScreenPos(position + (box - new Vector2(ImGui.GetFrameHeight())) / 2);
                Spinner();
                ImGui.SetCursorScreenPos(position);
                ImGui.Dummy(box);
                break;
        }
    }

    /// <summary> Draws a texture wrap fitted into a box over a checkerboard. Always occupies the box. </summary>
    public static void FittedImage(Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap wrap, Vector2 box)
    {
        var position = ImGui.GetCursorScreenPos();
        var scale = Math.Min(box.X / Math.Max(1, wrap.Width), box.Y / Math.Max(1, wrap.Height));
        var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
        var origin = position + (box - size) / 2;
        Checkerboard(origin, size, Theme.Scaled(8));
        ImGui.SetCursorScreenPos(origin);
        ImGui.Image(wrap.Handle, size);
        ImGui.SetCursorScreenPos(position);
        ImGui.Dummy(box);
    }

    /// <summary> The caption under a preview: dimensions, format and mip count, or the load state. </summary>
    public static void PreviewCaption(PreviewEntry<TexturePreview> entry)
    {
        switch (entry.State)
        {
            case PreviewState.Ready when entry.Value is { } preview:
                var info = preview.Info;
                var mips = info.Mips == 1 ? "1 mip" : $"{info.Mips} mips";
                ImGui.TextColored(Theme.Muted, $"{info.SourceWidth} × {info.SourceHeight} · {TextureDecoder.FormatLabel(info.Format)} · {mips}{(info.Downscaled ? " · preview downscaled" : string.Empty)}");
                break;
            case PreviewState.Failed:
                using (ImRaii.TextWrapPos(Theme.Scaled(360)))
                    ImGui.TextColored(Theme.Warning, $"Preview unavailable: {entry.Error}");
                break;
            default:
                ImGui.TextColored(Theme.Muted, "Loading preview");
                break;
        }
    }
}
