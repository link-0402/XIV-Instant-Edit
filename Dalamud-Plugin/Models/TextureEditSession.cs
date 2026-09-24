using Penumbra.Api.Enums;

namespace InstantEdit.Models;

public sealed record TextureEditRequest(string GamePath, string ActualPath, string ModDirectory,
    string ModRoot, string RelativePath, int? ObjectIndex, long ActorAddress, string NewModName = "");

public sealed record TextureEditSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string CacheRoot { get; init; } = "";
    public string GamePath { get; init; } = "";
    public string ResolvedGamePath { get; init; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModRoot { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string MappingFingerprint { get; set; } = "";
    public string NewModName { get; init; } = "";
    public bool NeedsMod { get; set; }
    public bool SetupPending { get; set; }
    public int? ObjectIndex { get; init; }
    public long ActorAddress { get; init; }
    public ulong ActorId { get; init; }
    public string ActorName { get; init; } = "";
    public Guid? CollectionId { get; init; }
    public string CollectionName { get; init; } = "";
    /// <summary>Format captured when the session opened; recompressed saves keep it.</summary>
    public uint Format { get; init; }
    /// <summary>Format of the texture currently committed to the destination.</summary>
    public uint SavedFormat { get; set; }
    /// <summary>Dimensions of the texture currently committed to the destination.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public bool MipMaps { get; init; }
    public string LastCommittedHash { get; set; } = "";
    public string PixelHash { get; set; } = "";
    public string WorkingHash { get; set; } = "";
    public bool Paused { get; set; }
    public bool Conflict { get; set; }
    public string Status { get; set; } = "Watching for saves";
    public string LastBackup { get; set; } = "";
    public DateTimeOffset? LastSaved { get; set; }
    /// <summary>The Single group holding this texture's variants, once the first variant is saved.</summary>
    public Guid? VariantGroupId { get; set; }
    /// <summary>The group's option that maps nothing, so the edited texture itself shows.</summary>
    public Guid? OriginalOptionId { get; set; }
    public List<TextureVariant> Variants { get; set; } = [];
    public string Directory => Path.Combine(CacheRoot, "texture-edits", Id.ToString("N"));
    public string WorkingFile => Path.Combine(Directory, Path.ChangeExtension(Path.GetFileName(GamePath), ".tga"));
    public string TargetFile => Path.Combine(ModRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
    public string VariantWorkingFile(TextureVariant variant) => Path.Combine(Directory, variant.Name + ".tga");
    public string VariantTargetFile(TextureVariant variant) => Path.Combine(ModRoot, variant.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// A TGA saved beside the working image under another name. It is committed to its own TEX in the
/// mod and published as an option, named after the file, that maps the session's game path to it.
/// </summary>
public sealed record TextureVariant
{
    /// <summary>The TGA file name without extension, which is also the Penumbra option name.</summary>
    public string Name { get; init; } = "";
    public string RelativePath { get; set; } = "";
    public Guid? OptionId { get; set; }
    public string LastCommittedHash { get; set; } = "";
    public string PixelHash { get; set; } = "";
    public string WorkingHash { get; set; } = "";
    public uint SavedFormat { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? LastSaved { get; set; }
}

internal sealed record TextureSource(byte[] Bytes, TextureEditSession Session);
internal sealed record TextureCommit(string Hash, string Backup, string Message);
internal sealed record TextureVariantCommit(string Hash, string Backup, string RelativePath,
    Guid GroupId, Guid OriginalOptionId, Guid OptionId, string MappingFingerprint);

internal interface ITextureEditBackend
{
    Task<TextureSource> CaptureAsync(TextureEditRequest request, CancellationToken token);
    Task ConvertAsync(string input, string output, TextureType format, bool mipMaps);
    Task<TextureCommit> CommitAsync(TextureEditSession session, byte[] tex, Func<bool> stillCurrent, CancellationToken token);
    /// <summary>Writes a variant TEX into the session's existing mod and maps it in the session's variant group.</summary>
    Task<TextureVariantCommit> CommitVariantAsync(TextureEditSession session, TextureVariant variant, byte[] tex,
        Func<bool> stillCurrent, CancellationToken token);
    /// <summary>Reloads and redraws; <paramref name="showOption"/> selects that variant-group option in the session's collection.</summary>
    Task<string> RefreshAsync(TextureEditSession session, Guid? showOption, CancellationToken token);
}
