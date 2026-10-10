using System.Text.Json.Serialization;
using Penumbra.Api.Enums;

namespace InstantEdit.Models;

/// <param name="JobId">The Substance Painter project this texture belongs to; its vanilla textures share one new mod.</param>
/// <param name="CollectionObjectIndex">
/// For a vanilla texture edited without an actor (from Game Files): the object whose collection
/// gets the new mod. The session keeps no actor, so the texture needn't be one the object renders.
/// </param>
public sealed record TextureEditRequest(string GamePath, string ActualPath, string ModDirectory,
    string ModRoot, string RelativePath, int? ObjectIndex, long ActorAddress, string NewModName = "", Guid? JobId = null,
    int? CollectionObjectIndex = null);

public sealed record TextureEditSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string CacheRoot { get; init; } = "";
    public string GamePath { get; init; } = "";
    public string ResolvedGamePath { get; init; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModRoot { get; set; } = "";
    public string RelativePath { get; set; } = "";
    /// <summary>
    /// The game paths the mod maps to the destination file, hashed. A save stops with a conflict
    /// when they change; the rest of the mod's metadata may change freely.
    /// </summary>
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
    /// <summary>The last save could not go through yet and is tried again; Status says why.</summary>
    [JsonIgnore]
    public bool Waiting { get; set; }
    public string Status { get; set; } = "Watching for saves";
    public string LastBackup { get; set; } = "";
    public DateTimeOffset? LastSaved { get; set; }
    /// <summary>The Single group holding this texture's variants, once the first variant is saved.</summary>
    public Guid? VariantGroupId { get; set; }
    /// <summary>The group's option that maps nothing, so the edited texture itself shows.</summary>
    public Guid? OriginalOptionId { get; set; }
    public List<TextureVariant> Variants { get; set; } = [];
    /// <summary>The image last dropped over this texture, kept until it is undone.</summary>
    public TextureReplacement? Replacement { get; set; }
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

/// <summary>
/// An image file dropped over a texture in On Screen. Undo puts back what it replaced while the
/// destination still holds what the drop committed (<see cref="Hash"/>); any later save ends that.
/// </summary>
public sealed record TextureReplacement
{
    /// <summary>The dropped file's name.</summary>
    public string File { get; init; } = "";
    /// <summary>The backup of the texture the drop replaced; empty when the drop created the mod, so the captured original was there.</summary>
    public string Backup { get; init; } = "";
    public string Hash { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

internal sealed record TextureSource(byte[] Bytes, TextureEditSession Session);
internal sealed record TextureCommit(string Hash, string Backup, string Message);
/// <summary>The destination as it is now, which a conflicted session takes as its last save when it reconnects.</summary>
/// <param name="VariantHashes">The hash of each variant's file by variant name; a variant whose file is gone is left out.</param>
internal sealed record TextureRebase(string Hash, string MappingFingerprint, uint Format, int Width, int Height,
    IReadOnlyDictionary<string, string> VariantHashes);
/// <param name="ShowOption">The variant-group option to select in the session's collection, if any.</param>
internal sealed record TextureRefresh(TextureEditSession Session, Guid? ShowOption);

/// <summary>
/// A texture Substance Painter exported for a session: a 32-bit TGA, or null to go back to the captured original.
/// <paramref name="DroppedFile"/> names the image file instead when the TGA is one dropped over the texture.
/// </summary>
internal sealed record ExternalTextureSave(Guid SessionId, byte[]? Tga, string? DroppedFile = null);

internal enum ExternalTextureOutcome { Applied, Unchanged, Restored, Failed }

internal sealed record ExternalTextureResult(Guid SessionId, ExternalTextureOutcome Outcome, string Message);
internal sealed record TextureVariantCommit(string Hash, string Backup, string RelativePath,
    Guid GroupId, Guid OriginalOptionId, Guid OptionId, string MappingFingerprint);

internal interface ITextureEditBackend
{
    Task<TextureSource> CaptureAsync(TextureEditRequest request, CancellationToken token);
    /// <summary>
    /// Checks, before an image is encoded, that a save can go through now. Throws a
    /// <c>TextureConflictException</c> when it never will, and a <c>TextureWaitException</c> when it can later.
    /// </summary>
    Task CheckAsync(TextureEditSession session, CancellationToken token);
    /// <summary>Validates a conflicted session's destination again and reads it as it is now.</summary>
    Task<TextureRebase> RebaseAsync(TextureEditSession session, CancellationToken token);
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
