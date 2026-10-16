using System;
using System.Collections.Generic;
using System.IO;

namespace InstantEdit.Studio.Services.Unity;

/// <summary>What an asset of a Unity package is, by its file extension.</summary>
internal enum UnityAssetKind { Texture, Model, Material, Prefab, Animation, Other }

/// <summary>One asset of a .unitypackage: its project path and what the scan learnt about it. Holds no file bytes.</summary>
internal sealed class UnityAsset
{
    public required string Guid { get; init; }

    /// <summary>The path in the Unity project, as the package's <c>pathname</c> file gives it ("Assets/...").</summary>
    public required string PackagePath { get; init; }

    public required UnityAssetKind Kind { get; init; }
    public long Size { get; init; }

    /// <summary>The thumbnail Unity stored beside the asset, if any.</summary>
    public byte[]? PreviewPng { get; init; }

    /// <summary>The texture importer's type: 1 for a normal map, -1 when unknown or not a texture.</summary>
    public int TextureType { get; init; } = -1;

    /// <summary>Ticked in the UI: extracted when set. Only textures and models are ever written.</summary>
    public bool Selected { get; set; } = true;

    public string FileName => Path.GetFileName(PackagePath);

    /// <summary>Whether extraction writes this kind of asset.</summary>
    public bool Extractable => Kind is UnityAssetKind.Texture or UnityAssetKind.Model;

    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".exr" };
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".fbx", ".obj", ".blend", ".dae" };

    public static UnityAssetKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (TextureExtensions.Contains(ext)) return UnityAssetKind.Texture;
        if (ModelExtensions.Contains(ext))   return UnityAssetKind.Model;
        return ext.ToLowerInvariant() switch
        {
            ".mat"                  => UnityAssetKind.Material,
            ".prefab"               => UnityAssetKind.Prefab,
            ".anim" or ".controller" => UnityAssetKind.Animation,
            _                       => UnityAssetKind.Other,
        };
    }
}

/// <summary>A Unity material: its name, shader reference and the texture each property uses (property → texture GUID).</summary>
internal sealed record UnityMaterial(string Guid, string Name, string Shader, IReadOnlyDictionary<string, string> Textures);

/// <summary>A prefab and the materials it references, in order. Packages often ship one prefab per colour variant.</summary>
internal sealed record UnityPrefab(string Guid, string Name, IReadOnlyList<string> MaterialGuids);

/// <summary>What a scan of a .unitypackage found.</summary>
internal sealed class UnityScanResult
{
    public required string PackagePath { get; init; }
    public List<UnityAsset> Assets { get; } = new();
    public List<UnityMaterial> Materials { get; } = new();
    public List<UnityPrefab> Prefabs { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>How many bytes of asset files the scan read into memory: material and prefab text only.</summary>
    public long BufferedAssetBytes { get; set; }

    public string PackageName => Path.GetFileNameWithoutExtension(PackagePath);
}

/// <summary>What an extraction wrote.</summary>
internal sealed class UnityExtraction
{
    public required string RootFolder { get; init; }
    public List<string> WrittenFiles { get; } = new();

    /// <summary>Asset GUID → the full path written for it.</summary>
    public Dictionary<string, string> FileByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Failed { get; set; }

    /// <summary>Package paths that would have landed outside the output folder, so were not written.</summary>
    public List<string> Refused { get; } = new();
}
