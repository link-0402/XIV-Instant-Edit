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
    public uint Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool MipMaps { get; init; }
    public string LastCommittedHash { get; set; } = "";
    public string PixelHash { get; set; } = "";
    public string WorkingHash { get; set; } = "";
    public bool Paused { get; set; }
    public bool Conflict { get; set; }
    public string Status { get; set; } = "Watching for saves";
    public string LastBackup { get; set; } = "";
    public DateTimeOffset? LastSaved { get; set; }
    public string Directory => Path.Combine(CacheRoot, "texture-edits", Id.ToString("N"));
    public string WorkingFile => Path.Combine(Directory, Path.ChangeExtension(Path.GetFileName(GamePath), ".tga"));
    public string TargetFile => Path.Combine(ModRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
}

internal sealed record TextureSource(byte[] Bytes, TextureEditSession Session);
internal sealed record TextureCommit(string Hash, string Backup, string Message);

internal interface ITextureEditBackend
{
    Task<TextureSource> CaptureAsync(TextureEditRequest request, CancellationToken token);
    Task ConvertAsync(string input, string output, TextureType format, bool mipMaps);
    Task<TextureCommit> CommitAsync(TextureEditSession session, byte[] tex, Func<bool> stillCurrent, CancellationToken token, bool restoring = false);
    Task<string> RefreshAsync(TextureEditSession session, CancellationToken token);
}
