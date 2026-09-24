using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Previews;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private readonly PreviewService _previews;

    private static bool PreviewReadable(ResourceView node)
        => node.ActualPath.Length > 0 && node.SourceState is ResourceSourceState.GameData or ResourceSourceState.LoadedMod;

    /// <summary>
    /// The hover behaviour of a resource row: textures show an image after a short delay,
    /// materials list their textures, everything else keeps the plain text tooltip.
    /// </summary>
    private void DrawRowHover(ResourceView node, string presentation, string hoverId)
    {
        var texture = (node.Kinds & ResourceKinds.Texture) != 0;
        var material = (node.Kinds & ResourceKinds.Material) != 0;
        if ((texture || material) && PreviewReadable(node) && Widgets.HoverDelay(hoverId))
        {
            if (texture)
                DrawTexturePreviewTooltip(node, presentation);
            else
                DrawMaterialHoverCard(node, presentation);
            return;
        }

        ImGui.SetTooltip($"{presentation}\nGame path: {(node.GamePath.Length == 0 ? "(none)" : node.GamePath)}");
    }

    private void DrawTexturePreviewTooltip(ResourceView node, string presentation)
    {
        var entry = _previews.GetTexture(_previews.KeyFor(node.ActualPath, node.SourceState == ResourceSourceState.GameData));
        var large = ImGui.GetIO().KeyShift;
        if (large)
            _previews.RequestAlpha(entry);
        var box = new Vector2(Theme.Scaled(large ? 512 : 256));

        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Label, presentation);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Hint, Safe(node.GamePath, "(no game path)"));
        Widgets.PreviewImage(entry, box);
        if (large)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.PreviewImage(entry, box, alpha: true);
        }
        Widgets.PreviewCaption(entry);
        if (large)
        {
            if (entry.State == PreviewState.Ready)
                ImGui.TextColored(Theme.Hint, entry.Value?.Alpha is null ? "Colour · alpha (building…)" : "Colour · alpha");
        }
        else
        {
            ImGui.TextColored(Theme.Hint, "Hold Shift for a larger view with the alpha channel.");
        }
    }

    private void DrawMaterialHoverCard(ResourceView node, string presentation)
    {
        var entry = _previews.GetMaterial(_previews.KeyFor(node.ActualPath, node.SourceState == ResourceSourceState.GameData));
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Label, presentation);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Hint, Safe(node.GamePath, "(no game path)"));
        ImGui.Separator();
        switch (entry.State)
        {
            case PreviewState.Failed:
                using (ImRaii.TextWrapPos(Theme.Scaled(360)))
                    ImGui.TextColored(Theme.Warning, $"Material unavailable: {entry.Error}");
                return;
            case PreviewState.Loading:
                ImGui.TextColored(Theme.Muted, "Reading material…");
                return;
        }

        var slots = entry.Value?.Textures ?? Array.Empty<MaterialTextureSlot>();
        if (slots.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "This material references no textures.");
            return;
        }

        var thumb = new Vector2(Theme.Scaled(56));
        foreach (var slot in slots)
        {
            var child = FindTextureChild(node, slot.GamePath);
            using var group = ImRaii.Group();
            if (child is not null && PreviewReadable(child))
                Widgets.PreviewImage(_previews.GetTexture(_previews.KeyFor(child.ActualPath, child.SourceState == ResourceSourceState.GameData)), thumb);
            else
                ImGui.Dummy(thumb);
            ImGui.SameLine(0, Theme.Gap);
            using (ImRaii.Group())
            {
                ImGui.TextColored(Theme.Label, UsageLabel(slot.Usage));
                ImGui.TextColored(Theme.Muted, Path.GetFileName(slot.GamePath));
                if (child is null)
                    ImGui.TextColored(Theme.Hint, "not loaded for this actor");
                else if (child.SourceModName.Length > 0)
                    ImGui.TextColored(Theme.ModSource, child.SourceModName);
                else if (child.SourceState == ResourceSourceState.GameData)
                    ImGui.TextColored(Theme.GameSource, "Game Data");
            }
        }
    }

    private static ResourceView? FindTextureChild(ResourceView material, string gamePath)
    {
        foreach (var child in material.Children)
            if (string.Equals(child.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                return child;
        var fileName = Path.GetFileName(gamePath);
        if (fileName.Length == 0)
            return null;
        foreach (var child in material.Children)
            if ((child.Kinds & ResourceKinds.Texture) != 0 && string.Equals(Path.GetFileName(child.GamePath), fileName, StringComparison.OrdinalIgnoreCase))
                return child;
        return null;
    }

    private static string UsageLabel(string usage)
        => usage switch
        {
            "diffuse" => "Diffuse",
            "normal" => "Normal",
            "mask" => "Mask",
            "index" => "Index",
            "specular" => "Specular",
            "occlusion" => "Occlusion",
            "flow" => "Flow",
            "decal" => "Decal",
            _ => "Texture",
        };

    /// <summary> A session's committed texture (or the vanilla source before its first save). </summary>
    private void DrawSessionThumbnail(TextureEditSession session)
    {
        PreviewKey key;
        if (!session.NeedsMod && session.TargetFile.Length > 0 && Path.IsPathRooted(session.TargetFile))
            key = _previews.KeyFor(session.TargetFile, false);
        else
            key = _previews.KeyFor(Safe(session.ResolvedGamePath, session.GamePath), true);
        var entry = _previews.GetTexture(key);
        Widgets.PreviewImage(entry, new Vector2(Theme.ThumbSize));
        if (!ImGui.IsItemHovered())
            return;
        using var tooltip = ImRaii.Tooltip();
        Widgets.PreviewImage(entry, new Vector2(Theme.Scaled(256)));
        Widgets.PreviewCaption(entry);
    }
}
