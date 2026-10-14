using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models.Resources;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>A small labelled chip on a tile: an option, an origin, a count.</summary>
internal readonly record struct Badge(string Text, Vector4 Color, string? Tooltip = null);

/// <summary>How resources look: an icon per kind and section, a colour and a word per origin.</summary>
internal static class ResourceStyle
{
    public static FontAwesomeIcon Icon(ResourceKind kind) => kind switch
    {
        ResourceKind.Model          => FontAwesomeIcon.Cube,
        ResourceKind.Material       => FontAwesomeIcon.Palette,
        ResourceKind.Texture        => FontAwesomeIcon.Image,
        ResourceKind.Animation      => FontAwesomeIcon.Running,
        ResourceKind.Timeline       => FontAwesomeIcon.Stream,
        ResourceKind.Vfx            => FontAwesomeIcon.Magic,
        ResourceKind.VfxTexture     => FontAwesomeIcon.Fire,
        ResourceKind.Sound          => FontAwesomeIcon.Music,
        ResourceKind.Skeleton       => FontAwesomeIcon.Bone,
        ResourceKind.SkeletonParams => FontAwesomeIcon.Bone,
        ResourceKind.Physics        => FontAwesomeIcon.Wind,
        ResourceKind.Shader         => FontAwesomeIcon.Adjust,
        ResourceKind.Imc            => FontAwesomeIcon.Th,
        ResourceKind.Interface      => FontAwesomeIcon.WindowMaximize,
        _                           => FontAwesomeIcon.File,
    };

    public static FontAwesomeIcon SectionIcon(ResourceSection section) => section switch
    {
        ResourceSection.Models      => FontAwesomeIcon.Tshirt,
        ResourceSection.Animations  => FontAwesomeIcon.Running,
        ResourceSection.Vfx         => FontAwesomeIcon.Magic,
        ResourceSection.Sounds      => FontAwesomeIcon.Music,
        ResourceSection.Skeletons   => FontAwesomeIcon.Bone,
        ResourceSection.Interface   => FontAwesomeIcon.WindowMaximize,
        ResourceSection.Environment => FontAwesomeIcon.Home,
        ResourceSection.Metadata    => FontAwesomeIcon.SlidersH,
        _                           => FontAwesomeIcon.Folder,
    };

    public static Vector4 OriginColor(OriginKind origin) => origin switch
    {
        OriginKind.Added   => Theme.Good,
        OriginKind.Missing => Theme.Bad,
        OriginKind.Swap    => Theme.Accent,
        OriginKind.Vanilla => Theme.Faint,
        OriginKind.Shared  => Theme.Faint,
        _                  => Theme.Muted,
    };

    public static string OriginLabel(OriginKind origin) => origin switch
    {
        OriginKind.ModFile => "mod file",
        OriginKind.Swap    => "file swap",
        OriginKind.Vanilla => "game file",
        OriginKind.Added   => "added",
        OriginKind.Missing => "missing",
        OriginKind.Shared  => "shared",
        _                  => string.Empty,
    };

    public static string OriginHelp(OriginKind origin) => origin switch
    {
        OriginKind.ModFile => "A file of this mod.",
        OriginKind.Swap    => "A file swap: the game serves another game file at this path.",
        OriginKind.Vanilla => "The game's own file — this mod references it and does not replace it. Drop a file here to replace it.",
        OriginKind.Added   => "Added to the mod: a file of your own, or something made or reassigned here.",
        OriginKind.Missing => "Referenced, but neither this mod nor the game has it. Drop a file here to provide it.",
        OriginKind.Shared  => "A common material (skin, pubes, piercings) that the player's body mod provides, so it isn't missing. Drop a file here to put one in this mod.",
        _                  => string.Empty,
    };

    // ── Groups and options ───────────────────────────────────────────────────

    public static FontAwesomeIcon GroupIcon(Services.Project.ModGroupType type) => type switch
    {
        Services.Project.ModGroupType.Single    => FontAwesomeIcon.DotCircle,
        Services.Project.ModGroupType.Multi     => FontAwesomeIcon.CheckSquare,
        Services.Project.ModGroupType.Imc       => FontAwesomeIcon.Th,
        Services.Project.ModGroupType.Combining => FontAwesomeIcon.LayerGroup,
        _                                       => FontAwesomeIcon.ListUl,
    };

    /// <summary>How a group's options are picked, in a few words.</summary>
    public static string GroupTypeLabel(Services.Project.ModGroupType type) => type switch
    {
        Services.Project.ModGroupType.Single    => "pick one",
        Services.Project.ModGroupType.Multi     => "pick any",
        Services.Project.ModGroupType.Imc       => "IMC attributes",
        Services.Project.ModGroupType.Combining => "combining",
        _                                       => "unknown kind",
    };

    public static string GroupTypeHelp(Services.Project.ModGroupType type) => type switch
    {
        Services.Project.ModGroupType.Single    => "A single-select group: exactly one of its options is in use at a time.",
        Services.Project.ModGroupType.Multi     => "A multi-select group: any number of its options can be in use at once (at most 32).",
        Services.Project.ModGroupType.Imc       => "An IMC group: its options switch an item's attributes on and off. It carries no files.",
        Services.Project.ModGroupType.Combining => "A combining group: each combination of its options has files of its own.",
        _                                       => "A kind of group this version does not know.",
    };

    /// <summary>The icon of an option without a picture: the default files, or an option of a group of this type.</summary>
    public static FontAwesomeIcon OptionIcon(Services.Project.ModGroupType? type)
        => type == null ? FontAwesomeIcon.CheckCircle : GroupIcon(type.Value);

    // ── Badges ───────────────────────────────────────────────────────────────

    /// <summary>The width a badge takes.</summary>
    public static float BadgeWidth(Badge badge) => ImGui.CalcTextSize(badge.Text).X + Theme.S(12);

    /// <summary>Draws a badge at a screen position (no layout), returning its width.</summary>
    public static float DrawBadge(ImDrawListPtr draw, Vector2 pos, Badge badge)
    {
        var text = ImGui.CalcTextSize(badge.Text);
        var pad  = new Vector2(Theme.S(6), Theme.S(1));
        var size = text + pad * 2;
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(badge.Color with { W = 0.20f }), size.Y * 0.5f);
        draw.AddText(pos + pad, ImGui.GetColorU32(badge.Color), badge.Text);
        return size.X;
    }

    /// <summary>A badge laid out like any other item (with its tooltip).</summary>
    public static void BadgeItem(Badge badge)
    {
        Gui.Pill(badge.Text, badge.Color);
        Gui.Tooltip(badge.Tooltip);
    }

    /// <summary>Shortens <paramref name="text"/> with an ellipsis until it fits <paramref name="width"/>.</summary>
    public static string Fit(string text, float width)
    {
        if (width <= 0)
            return string.Empty;
        if (ImGui.CalcTextSize(text).X <= width)
            return text;

        int len = text.Length;
        while (len > 1 && ImGui.CalcTextSize(text[..len]).X > width)
            len = Math.Max(1, len * 9 / 10);
        return text[..len].TrimEnd();
    }

    /// <summary>Shortens a path from the left, so its file name stays readable.</summary>
    public static string FitPath(string path, float width)
    {
        if (width <= 0)
            return string.Empty;
        if (ImGui.CalcTextSize(path).X <= width)
            return path;

        int start = 0;
        while (start < path.Length - 1 && ImGui.CalcTextSize(path[start..]).X > width)
            start = Math.Min(path.Length - 1, start + Math.Max(1, (path.Length - start) / 10));
        return path[start..];
    }
}
