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
        var model = (node.Kinds & ResourceKinds.Model) != 0;
        if ((texture || material || model) && PreviewReadable(node) && Widgets.HoverDelay(hoverId))
        {
            if (texture)
                DrawTexturePreviewTooltip(node, presentation);
            else if (material)
                DrawMaterialHoverCard(node, presentation);
            else
                DrawModelHoverCard(node, presentation);
            return;
        }

        ImGui.SetTooltip($"{presentation}\nGame path: {(node.GamePath.Length == 0 ? "(none)" : node.GamePath)}");
    }

    private void DrawModelHoverCard(ResourceView node, string presentation)
    {
        var entry = _previews.GetModel(_previews.KeyFor(node.ActualPath, node.SourceState == ResourceSourceState.GameData));
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Label, presentation);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Hint, Safe(node.GamePath, "(no game path)"));
        ImGui.Separator();
        switch (entry.State)
        {
            case PreviewState.Failed:
                using (ImRaii.TextWrapPos(Theme.Scaled(360)))
                    ImGui.TextColored(Theme.Warning, $"Model unavailable: {entry.Error}");
                return;
            case PreviewState.Loading:
                ImGui.TextColored(Theme.Muted, "Reading model");
                return;
        }

        var preview = entry.Value!;
        var info = preview.Info;
        var box = new Vector2(Theme.Scaled(256));
        if (preview.Thumbnail is not null)
            Widgets.FittedImage(preview.Thumbnail, box);
        else if (preview.ThumbnailNote is not null)
        {
            using (ImRaii.TextWrapPos(Theme.Scaled(360)))
                ImGui.TextColored(Theme.Hint, preview.ThumbnailNote);
        }

        var meshes = info.MeshCount == 1 ? "1 mesh" : $"{info.MeshCount} meshes";
        var lods = info.LodCount == 1 ? "1 LOD" : $"{info.LodCount} LODs";
        ImGui.TextColored(Theme.Text, $"{info.VersionLabel} · {lods} · {meshes} · {info.Lod0Vertices:N0} verts / {info.Lod0Triangles:N0} tris");
        if (info.Partial)
            ImGui.TextColored(Theme.Warning, $"Partial read: {info.Note}");
        if (info.Materials.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Label, "Materials");
            for (var i = 0; i < info.Materials.Count; i++)
            {
                var (r, g, b) = ModelThumbnailRenderer.Palette[i % ModelThumbnailRenderer.Palette.Length];
                var chip = ImGui.GetCursorScreenPos();
                var chipSize = new Vector2(ImGui.GetTextLineHeight() * .8f);
                ImGui.GetWindowDrawList().AddRectFilled(chip + new Vector2(0, (ImGui.GetTextLineHeight() - chipSize.Y) / 2), chip + chipSize + new Vector2(0, (ImGui.GetTextLineHeight() - chipSize.Y) / 2),
                    ImGui.GetColorU32(new Vector4(r / 255f, g / 255f, b / 255f, 1)), Theme.Scaled(2));
                ImGui.Dummy(chipSize);
                ImGui.SameLine(0, Theme.Gap);
                ImGui.TextUnformatted(Path.GetFileName(info.Materials[i]));
                var textures = CountMaterialTextures(node, info.Materials[i]);
                if (textures >= 0)
                {
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextColored(Theme.Muted, textures == 1 ? "1 texture" : $"{textures} textures");
                }
            }
        }
        if (info.Attributes.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Label, "Attributes");
            using var wrap = ImRaii.TextWrapPos(Theme.Scaled(360));
            ImGui.TextColored(Theme.Muted, string.Join(" · ", info.Attributes));
        }
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, $"{info.BoneCount} bones · {info.ShapeCount} shapes · radius {info.Radius:0.##}");
    }

    /// <summary> Textures of the matching material row under this model in the tree, or -1 when the tree has no such row. </summary>
    private static int CountMaterialTextures(ResourceView model, string materialPath)
    {
        var fileName = Path.GetFileName(materialPath);
        foreach (var child in model.Children)
        {
            if ((child.Kinds & ResourceKinds.Material) == 0)
                continue;
            if (!string.Equals(child.GamePath, materialPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(child.GamePath), fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            var count = 0;
            foreach (var texture in child.Children)
                if ((texture.Kinds & ResourceKinds.Texture) != 0)
                    count++;
            return count;
        }
        return -1;
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
                ImGui.TextColored(Theme.Hint, entry.Value?.Alpha is null ? "Colour · alpha (building)" : "Colour · alpha");
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
                ImGui.TextColored(Theme.Muted, "Reading material");
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
                ImGui.TextColored(Theme.TextureRoleColour(TextureRoleClassifier.FromUsage(slot.Usage)), UsageLabel(slot.Usage));
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
            "diffuse" => "Base",
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
