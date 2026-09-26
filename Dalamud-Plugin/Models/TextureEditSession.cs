using Penumbra.Api.Enums;

namespace InstantEdit.Models;

/// <param name="JobId">The Substance Painter project this texture belongs to; its vanilla textures share one new mod.</param>
public sealed record TextureEditRequest(string GamePath, string ActualPath, string ModDirectory,
    string ModRoot, string RelativePath, int? ObjectIndex, long ActorAddress, string NewModName = "", Guid? JobId = null);

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
    /// <summary>
    /// The Substance Painter project that opened this session. Its vanilla sessions share one new mod,
    /// whose Penumbra identifier is this id; the first commit creates it and later ones add their file.
    /// </summary>
    public Guid? JobId { get; init; }
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
/// <param name="MappingFingerprint">The mod's new metadata fingerprint when the commit changed its mappings; otherwise empty.</param>
internal sealed record TextureCommit(string Hash, string Backup, string Message, string MappingFingerprint = "");
/// <param name="ShowOption">The variant-group option to select in the session's collection, if any.</param>
internal sealed record TextureRefresh(TextureEditSession Session, Guid? ShowOption);

/// <summary> A texture Substance Painter exported for a session: a 32-bit TGA, or null to go back to the captured original. </summary>
internal sealed record ExternalTextureSave(Guid SessionId, byte[]? Tga);

internal enum ExternalTextureOutcome { Applied, Unchanged, Restored, Failed }

internal sealed record ExternalTextureResult(Guid SessionId, ExternalTextureOutcome Outcome, string Message);
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
    /// <summary>
    /// Reloads each affected mod once, applies collection setup and option selections, then redraws
    /// every captured actor and the player's entities once.
    /// </summary>
    Task<string> RefreshAsync(IReadOnlyList<TextureRefresh> items, CancellationToken token);
}
