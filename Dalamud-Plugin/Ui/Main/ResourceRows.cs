using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui.Main;

/// <summary>
/// What every resource table of the main window shares: the rows' colours, glyphs and source badges,
/// their action buttons and ⋯ menu, which hand a row to Blender, a texture session, Substance Painter
/// or the Send animation dialog. Used by On Screen, Game Files and the Animations tab.
/// </summary>
internal sealed class ResourceRows
{
    // No RowBg: rows are told apart by their kind and texture-role tints instead of stripes.
    public const ImGuiTableFlags ResourceTableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter |
                                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Hideable;
    public const string RowMenuPopup = "##row-menu";

    private readonly ModelSender _models;
    private readonly PainterPanel _painter;
    private readonly TextureSessionsTab _textureTab;
    private readonly AnimationSendDialog _animationSend;
    private readonly ModBrowserTab _modBrowser;
    private readonly PenumbraService _penumbra;
    private readonly IPluginLog _log;
    private readonly StatusFeed _feed;

    public ResourceRows(ModelSender models, PainterPanel painter, TextureSessionsTab textureTab, AnimationSendDialog animationSend,
        ModBrowserTab modBrowser, PenumbraService penumbra, IPluginLog log, StatusFeed feed)
    {
        _models = models;
        _painter = painter;
        _textureTab = textureTab;
        _animationSend = animationSend;
        _modBrowser = modBrowser;
        _penumbra = penumbra;
        _log = log;
        _feed = feed;
    }

    /// <summary> One tree column: an arrow or slot icon and the gap after it. </summary>
    public static float TreeStep => ImGui.GetFrameHeight() + Theme.Gap;

    private static FontAwesomeIcon KindGlyph(ResourceKinds kinds)
    {
        if ((kinds & ResourceKinds.Model) != 0) return FontAwesomeIcon.Cube;
        if ((kinds & ResourceKinds.Texture) != 0) return FontAwesomeIcon.Image;
        if ((kinds & ResourceKinds.Material) != 0) return FontAwesomeIcon.Palette;
        if ((kinds & ResourceKinds.Animation) != 0) return FontAwesomeIcon.Running;
        return FontAwesomeIcon.File;
    }

    /// <summary>
    /// The colour that marks a row: models and materials by kind, textures by role, with the
    /// same precedence as <see cref="KindGlyph"/>; null for other resources.
    /// </summary>
    private static Vector4? RowColour(ResourceView node)
    {
        if ((node.Kinds & ResourceKinds.Model) != 0) return Theme.ModelKind;
        if ((node.Kinds & ResourceKinds.Texture) != 0) return Theme.TextureRoleColour(node.TextureRole);
        if ((node.Kinds & ResourceKinds.Material) != 0) return Theme.MaterialKind;
        if ((node.Kinds & ResourceKinds.Animation) != 0) return Theme.AnimationKind;
        return null;
    }

    /// <summary> What the row's colour stands for, shown when hovering its glyph. </summary>
    private static string RowColourLabel(ResourceView node)
    {
        if ((node.Kinds & ResourceKinds.Model) != 0) return "Model";
        if ((node.Kinds & ResourceKinds.Texture) != 0) return TextureRoleClassifier.Label(node.TextureRole) + " texture";
        if ((node.Kinds & ResourceKinds.Material) != 0) return "Material";
        if ((node.Kinds & ResourceKinds.Animation) != 0) return "Animation";
        return ResourceViews.KindLabel(node.Type);
    }

    /// <summary> Washes the current table row in its colour; rows of other resources stay plain. </summary>
    public static void TintRow(ResourceView node)
    {
        if (RowColour(node) is { } colour)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.WithAlpha(colour, Theme.KindTintAlpha)));
    }

    /// <summary> The row's kind glyph in its colour, with a tooltip naming the kind or texture role. </summary>
    public static void DrawRowGlyph(ResourceView node)
    {
        Widgets.Icon(KindGlyph(node.Kinds), RowColour(node) ?? Theme.Muted);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(RowColourLabel(node));
    }

    /// <summary> A header row's background: an actor's line, or the character above the animation list. </summary>
    public static void DrawOpaqueRow()
    {
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()), ImGui.GetColorU32(Theme.RowAlt), Theme.Scaled(2));
    }

    /// <summary>
    /// Room for the widest row's buttons: a model's edit, its paint roller when Painter is on, and ⋯
    /// (see <see cref="DrawRowActions"/>). Icon buttons are as wide as their glyph plus frame padding.
    /// </summary>
    public float ActionsColumnWidth()
    {
        var padding = ImGui.GetStyle().FramePadding.X * 2;
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        float Button(FontAwesomeIcon icon) => ImGui.CalcTextSize(icon.ToIconString()).X + padding;
        var width = Button(FontAwesomeIcon.Pen) + Theme.Scaled(2) + Button(FontAwesomeIcon.EllipsisH);
        if (_painter.Enabled)
            width += Button(FontAwesomeIcon.PaintRoller) + Theme.Scaled(2);
        return MathF.Max(width, Theme.Scaled(58));
    }

    /// <summary>
    /// The row's source as a pill in the row's own colour, so the column matches the row. The
    /// text tone tells the sources apart: a mod is bright, game data muted, an unattributed file dim.
    /// </summary>
    public static void DrawSourceBadge(ResourceView node)
    {
        var colour = RowColour(node) ?? Theme.Muted;
        if (node.SourceModName.Length > 0)
        {
            if (Widgets.Badge(node.SourceModName, colour, Theme.Label))
                ImGui.SetTooltip(node.OptionMapping.Length > 0
                    ? $"Mod: {node.SourceModName}\nDirectory: {node.SourceModDirectory}\nOption: {node.OptionMapping}"
                    : $"Mod: {node.SourceModName}\nDirectory: {node.SourceModDirectory}");
        }
        else if (node.SourceState == ResourceSourceState.GameData)
        {
            if (Widgets.Badge("Game Data", colour, Theme.Muted))
                ImGui.SetTooltip("Loaded from the game's own files (no mod replaces it).");
        }
        else
        {
            if (Widgets.Badge(UiText.Safe(node.SourceLabel, "Unavailable"), colour, Theme.Hint))
                ImGui.SetTooltip("The file could not be attributed to a mod; it cannot be edited here.");
        }
    }

    public void DrawRowActions(ActorView actor, ResourceView node)
    {
        if (node.IsModel && ResourceViews.IsSafeModel(node))
        {
            if (Widgets.IconButton("##edit-model", FontAwesomeIcon.Pen, "Edit this model in Blender"))
                _models.TryEditNode(node, actor);
            ImGui.SameLine(0, Theme.Scaled(2));
            if (_painter.CanPaint(actor, node))
            {
                if (Widgets.IconButton("##paint-model", FontAwesomeIcon.PaintRoller, "Paint this model's textures in Substance Painter",
                        !_painter.Busy.IsBusy))
                    _painter.StartPainter(actor, node);
                ImGui.SameLine(0, Theme.Scaled(2));
            }
        }
        else if (TextureSessionsTab.IsTextureRow(node))
        {
            var available = TextureSessionsTab.TextureEditAvailable(node);
            if (Widgets.IconButton("##edit-texture", FontAwesomeIcon.PaintBrush,
                    available
                        ? "Open as a 32-bit TGA in your configured editor. Shared references to this texture will also change.\nOr drop an image on the row to replace the texture."
                        : TextureSessionsTab.TextureEditBlock(node) is { Length: > 0 } blocked ? blocked
                        : "This texture has no verified writable mod source.",
                    available && !_textureTab.Busy.IsBusy))
                _textureTab.StartTextureEdit(node, actor);
            ImGui.SameLine(0, Theme.Scaled(2));
        }
        else if (ResourceViews.IsAnimation(node))
        {
            var blocked = _animationSend.AnimationSendBlock(node);
            if (Widgets.IconButton("##send-animation", FontAwesomeIcon.Pen, blocked ?? "Send this animation to Blender", blocked is null))
                _animationSend.OpenAnimationSend(node);
            ImGui.SameLine(0, Theme.Scaled(2));
        }

        if (Widgets.GhostIconButton("##more", FontAwesomeIcon.EllipsisH, "More actions"))
            ImGui.OpenPopup(RowMenuPopup);
    }

    /// <summary>
    /// The row's context menu; opened by right-click on the name or the ⋯ button.
    /// <paramref name="extraItems"/> adds a view's own items after the edit actions.
    /// </summary>
    public void DrawRowMenu(ActorView actor, ResourceView node, Action? extraItems = null)
    {
        using var popup = ImRaii.Popup(RowMenuPopup);
        if (!popup.Success)
            return;

        var safeModel = node.IsModel && ResourceViews.IsSafeModel(node);
        var texture = TextureSessionsTab.IsTextureRow(node);
        if (safeModel && ImGui.MenuItem("Edit model in Blender"))
            _models.TryEditNode(node, actor);
        if (_painter.CanPaint(actor, node))
        {
            using var disabled = ImRaii.Disabled(_painter.Busy.IsBusy);
            if (ImGui.MenuItem("Paint textures in Substance Painter"))
                _painter.StartPainter(actor, node);
        }
        if (texture)
        {
            using var disabled = ImRaii.Disabled(!TextureSessionsTab.TextureEditAvailable(node) || _textureTab.Busy.IsBusy);
            if (ImGui.MenuItem("Edit texture"))
                _textureTab.StartTextureEdit(node, actor);
        }
        // Shown while the texture still holds an image dropped on it.
        if (texture && _textureTab.UndoableReplace(node, actor) is { Replacement: { } drop } replaced)
        {
            using (ImRaii.Disabled(_textureTab.Busy.IsBusy))
                if (ImGui.MenuItem("Undo replace"))
                    _textureTab.UndoTextureReplace(replaced);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Put back the texture from before {drop.File} was dropped on it.");
        }
        var animation = ResourceViews.IsAnimation(node);
        if (animation)
        {
            using var disabled = ImRaii.Disabled(_animationSend.AnimationSendBlock(node) is not null);
            if (ImGui.MenuItem("Send animation to Blender"))
                _animationSend.OpenAnimationSend(node);
        }
        extraItems?.Invoke();
        if (safeModel || texture || animation || extraItems is not null)
            ImGui.Separator();

        using (ImRaii.Disabled(node.GamePath.Length == 0))
            if (ImGui.MenuItem("Copy game path"))
                ImGui.SetClipboardText(node.GamePath);
        using (ImRaii.Disabled(node.ActualPath.Length == 0))
            if (ImGui.MenuItem("Copy resolved path"))
                ImGui.SetClipboardText(node.ActualPath);
        var folder = ContainingFolder(node);
        using (ImRaii.Disabled(folder is null))
            if (ImGui.MenuItem("Open containing folder") && folder is not null)
                OpenFolder(folder);

        if (node.SourceModDirectory.Length == 0)
            return;
        ImGui.Separator();
        if (ImGui.MenuItem("Open in Penumbra"))
            OpenInPenumbra(node);
        if (ImGui.MenuItem("Open mod"))
            _modBrowser.OpenModWindow(node.SourceModRootPath, node.SourceModDirectory);
    }

    private static string? ContainingFolder(ResourceView node)
    {
        if (node.ActualPath.Length == 0 || !Path.IsPathRooted(node.ActualPath))
            return null;
        try
        {
            var directory = Path.GetDirectoryName(node.ActualPath);
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void OpenFolder(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not open a mod folder.");
            _feed.SetStatus($"Could not open the folder: {e.Message}", FeedbackSeverity.Warning);
        }
    }

    private void OpenInPenumbra(ResourceView node)
    {
        try
        {
            _penumbra.OpenModInPenumbra(node.SourceModDirectory, node.SourceModName);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not open the mod in Penumbra.");
            _feed.SetStatus($"Could not open the mod in Penumbra: {e.Message}", FeedbackSeverity.Warning);
        }
    }
}
