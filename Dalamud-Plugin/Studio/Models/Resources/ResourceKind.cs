using System;
using System.Collections.Generic;
using System.IO;

namespace InstantEdit.Studio.Models.Resources;

/// <summary>What a game file is, by its extension (and, for textures, where it lives).</summary>
public enum ResourceKind
{
    Model,
    Material,
    Texture,
    Animation,
    Timeline,
    Vfx,
    VfxTexture,
    Sound,
    Skeleton,
    SkeletonParams,
    Physics,
    Shader,
    Imc,
    Interface,
    Other,
}

/// <summary>The groups the contents pane lists a mod's resources under.</summary>
public enum ResourceSection
{
    Models,
    Animations,
    Vfx,
    Sounds,
    Skeletons,
    Interface,
    Environment,
    Other,
    Metadata,
}

/// <summary>Classification of game paths, and what may replace each kind of file.</summary>
public static class ResourceKinds
{
    private static readonly string[] ImageSources   = { ".png", ".jpg", ".jpeg", ".dds", ".tga", ".tex" };
    private static readonly string[] VfxTexSources  = { ".png", ".jpg", ".jpeg", ".dds", ".tga", ".tex", ".atex" };

    public static ResourceKind FromGamePath(string gamePath)
    {
        var path = gamePath.Replace('\\', '/').ToLowerInvariant();
        return Path.GetExtension(path) switch
        {
            ".mdl"             => ResourceKind.Model,
            ".mtrl"            => ResourceKind.Material,
            ".tex"             => path.StartsWith("ui/", StringComparison.Ordinal) ? ResourceKind.Interface : ResourceKind.Texture,
            ".atex"            => ResourceKind.VfxTexture,
            ".pap"             => ResourceKind.Animation,
            ".tmb"             => ResourceKind.Timeline,
            ".avfx"            => ResourceKind.Vfx,
            ".scd"             => ResourceKind.Sound,
            ".sklb" or ".eid"  => ResourceKind.Skeleton,
            ".skp"             => ResourceKind.SkeletonParams,
            ".phyb"            => ResourceKind.Physics,
            ".shpk"            => ResourceKind.Shader,
            ".imc"             => ResourceKind.Imc,
            ".uld" or ".amb"   => ResourceKind.Interface,
            _                  => ResourceKind.Other,
        };
    }

    public static ResourceSection SectionFor(ResourceKind kind, string gamePath)
    {
        var path = gamePath.Replace('\\', '/').ToLowerInvariant();
        bool environment = path.StartsWith("bg/", StringComparison.Ordinal) || path.StartsWith("bgcommon/", StringComparison.Ordinal);
        return kind switch
        {
            ResourceKind.Model or ResourceKind.Material or ResourceKind.Texture
                => environment ? ResourceSection.Environment : ResourceSection.Models,
            ResourceKind.Animation or ResourceKind.Timeline => ResourceSection.Animations,
            ResourceKind.Vfx or ResourceKind.VfxTexture     => ResourceSection.Vfx,
            ResourceKind.Sound                              => ResourceSection.Sounds,
            ResourceKind.Skeleton or ResourceKind.SkeletonParams or ResourceKind.Physics => ResourceSection.Skeletons,
            ResourceKind.Interface                          => ResourceSection.Interface,
            _                                               => environment ? ResourceSection.Environment : ResourceSection.Other,
        };
    }

    public static string Label(ResourceKind kind) => kind switch
    {
        ResourceKind.Model          => "Model",
        ResourceKind.Material       => "Material",
        ResourceKind.Texture        => "Texture",
        ResourceKind.Animation      => "Animation",
        ResourceKind.Timeline       => "Timeline",
        ResourceKind.Vfx            => "VFX",
        ResourceKind.VfxTexture     => "VFX texture",
        ResourceKind.Sound          => "Sound",
        ResourceKind.Skeleton       => "Skeleton",
        ResourceKind.SkeletonParams => "Skeleton parameters",
        ResourceKind.Physics        => "Physics",
        ResourceKind.Shader         => "Shader",
        ResourceKind.Imc            => "Variant table",
        ResourceKind.Interface      => "Interface",
        _                           => "File",
    };

    public static string SectionLabel(ResourceSection section) => section switch
    {
        ResourceSection.Models      => "Models",
        ResourceSection.Animations  => "Animations",
        ResourceSection.Vfx         => "VFX",
        ResourceSection.Sounds      => "Sounds",
        ResourceSection.Skeletons   => "Skeletons & physics",
        ResourceSection.Interface   => "Interface",
        ResourceSection.Environment => "Housing & environment",
        ResourceSection.Metadata    => "Metadata",
        _                           => "Other files",
    };

    /// <summary>Whether a game path holds a texture in the game's own format (so images can be converted into it).</summary>
    public static bool IsTexturePath(string gamePath)
    {
        var ext = Path.GetExtension(gamePath).ToLowerInvariant();
        return ext is ".tex" or ".atex";
    }

    /// <summary>The local file types that can replace or add a file at <paramref name="gamePath"/>.</summary>
    public static IReadOnlyList<string> AcceptedSources(string gamePath)
    {
        var ext = Path.GetExtension(gamePath).ToLowerInvariant();
        return ext switch
        {
            ".tex"  => ImageSources,
            ".atex" => VfxTexSources,
            ""      => Array.Empty<string>(),
            _       => new[] { ext },
        };
    }

    /// <summary>An ImGuiFileDialog filter for the files that can replace <paramref name="gamePath"/>.</summary>
    public static string DialogFilter(string gamePath)
    {
        var accepted = AcceptedSources(gamePath);
        if (accepted.Count == 0)
            return "All files{.*}";
        var label = IsTexturePath(gamePath) ? "Images and textures" : $"{Label(FromGamePath(gamePath))} files";
        return $"{label}{{{string.Join(',', accepted)}}}";
    }

    /// <summary>Whether a local file can stand in for <paramref name="gamePath"/>.</summary>
    public static bool Accepts(string gamePath, string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        foreach (var accepted in AcceptedSources(gamePath))
            if (accepted == ext)
                return true;
        return false;
    }
}
