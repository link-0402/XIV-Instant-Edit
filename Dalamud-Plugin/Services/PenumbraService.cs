using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Services;

/// <summary> Result of applying an export to Penumbra. </summary>
public sealed record ExportResult(
    bool Success,
    ResultCode Code,
    string Message,
    IReadOnlyList<string>? Warnings = null,
    string? TargetFilePath = null,
    string? DestinationName = null,
    ModPathRemap? PathRemap = null,
    IReadOnlyList<string>? RequiredExternalMods = null,
    string? OutputModDirectory = null,
    string? OutputModRootPath = null,
    string? OutputTargetRelativePath = null,
    ResourceDependencyManifest? OutputResourceManifest = null,
    Guid? OutputModStableId = null)
{
    public ExportResult(bool success, string message)
        : this(success, success ? ResultCode.ExportApplied : ResultCode.ApplyFailed, message)
    {
    }

    public IReadOnlyList<string> WarningList => Warnings ?? Array.Empty<string>();
}

/// <summary>Physical paths changed by a successful in-place mod cleanup.</summary>
public sealed record ModPathRemap(
    string ModDirectory,
    string ModRoot,
    IReadOnlyDictionary<string, string> RelativePaths);

public sealed record VariantOptionTarget(
    string Id, string Name, string ModelPath, string? BackupTargetId = null, string? BackupDirectory = null);
public sealed record VariantGroupTarget(string Id, string Name, IReadOnlyList<VariantOptionTarget> Options);
public sealed record VariantTargetsResult(bool Success, ResultCode Code, string Message, IReadOnlyList<VariantGroupTarget> Groups);
public sealed record PenumbraCollectionTarget(Guid Id, string Name);
public sealed record NewModelModResult(
    ExportResult Result,
    string? ModRoot = null,
    string? TargetRelativePath = null,
    Guid? ModStableId = null);
public sealed record MashupContributor(InstantEditImportContext Context, IReadOnlyList<string> Materials);
public sealed record MashupMaterialAssignment(
    string ContextId,
    string ModelMaterial,
    string Alias,
    string GamePath,
    string? Slot);
public sealed record MashupPlanResult(
    bool Success,
    ResultCode Code,
    string Message,
    IReadOnlyList<MashupMaterialAssignment> Assignments,
    string? Fingerprint = null);
public sealed record MaterialCoverageMissing(
    string ContextId,
    string SourceModName,
    string ModelMaterial,
    string GamePath,
    string ResourceType = "material");
public sealed record MaterialCoverageResult(
    bool Available,
    bool Covered,
    ResultCode Code,
    string Message,
    IReadOnlyList<MaterialCoverageMissing> Missing);
public sealed record SourceOptionCapture(SourceOptionLocator? Locator, string Status, string? Warning = null);

/// <summary>A size's exported model: its option name and the staged file.</summary>
public sealed record SizeExportFile(string Name, string ExportedFile);

public sealed record PenumbraMod(string Directory, string Name, Guid? StableId = null);

public sealed record PenumbraModResource(
    string GamePath,
    string ActualPath,
    string RelativePath,
    string OptionMapping,
    IReadOnlyList<string> OptionMemberships);

public sealed record PenumbraModSnapshot(
    string Directory,
    string Name,
    string RootPath,
    IReadOnlyList<PenumbraModResource> Resources,
    Guid? StableId = null);

/// <summary>
/// An EST entry the exported meshes need, from the <c>xiv_est_hair</c>/<c>xiv_est_race</c> tags
/// Magic Fit's Hair Weights puts on hair it weights: the hair skeleton (<paramref name="Entry"/>,
/// <c>skl_c0801h0160.sklb</c> for 160) of race <see cref="Race"/> (<c>c0801</c>).
/// </summary>
public sealed class EstEntryRequest
{
    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("entry")]
    public int Entry { get; set; }

    [JsonPropertyName("race")]
    public string? Race { get; set; }
}

/// <summary>
/// XIV Instant Edit's way into Penumbra: builds the Penumbra IPC client and the mod services of
/// InstantEdit.Services.PenumbraMods around one export gate. Plugin hands each service to the code
/// that uses it; the work happens in the services.
/// </summary>
public sealed class PenumbraService : IDisposable
{
    /// <summary> One mod write at a time, across every service that writes into Penumbra's mods. </summary>
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public PenumbraService(
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log,
        IObjectTable objects,
        IDataManager data,
        ModelBackupStore backups,
        EstEntryStore estEntries)
    {
        // The services no longer check for these on each call, as the old optional ones were: a
        // missing one fails here, not halfway through an export.
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(estEntries);
        Ipc = new PenumbraIpc(pi, framework, log);
        var activation = new ModActivation(Ipc, framework, log, objects);
        Sources = new SourceMods(Ipc, framework, log);
        var writer = new ModelFileWriter(backups);
        var attributes = new AttributeGroupService(data, log);
        var est = new EstEntryService(estEntries, log);
        RaceExports = new RaceExportService(framework, log, _exportGate, data, backups, Sources, activation, writer, attributes, est);
        SizesExports = new SizesExportService(framework, log, _exportGate, Sources, activation, writer, attributes, est);
        ModelExports = new ModelExportService(Ipc, framework, log, _exportGate, backups, Sources, activation, writer, attributes,
            est, RaceExports);
        ModelBackups = new ModelBackupService(framework, log, _exportGate, backups, Sources, activation, writer, est, RaceExports);
        Mashups = new MashupService(Ipc, framework, log, _exportGate, data, Sources, activation, attributes, est);
        Scanner = new ModResourceScanner(framework, log, Sources);
        Textures = new TextureModBackend(Ipc, framework, objects, data, backups, _exportGate, Sources, activation);
        ModFiles = new ModFileService(framework, _exportGate, Sources, activation);
        Previews = new PreviewModWriter(Ipc, framework, log, objects, backups, _exportGate, Sources, activation);
        Animations = new AnimationModService(Ipc, framework, objects, _exportGate, activation);
        Physics = new PhysicsModService(Ipc, framework, log, _exportGate, activation);
    }

    internal PenumbraIpc Ipc { get; }
    internal SourceMods Sources { get; }
    internal ModelExportService ModelExports { get; }
    internal RaceExportService RaceExports { get; }
    internal SizesExportService SizesExports { get; }
    internal ModelBackupService ModelBackups { get; }
    internal MashupService Mashups { get; }
    internal ModResourceScanner Scanner { get; }
    internal TextureModBackend Textures { get; }
    internal ModFileService ModFiles { get; }
    internal PreviewModWriter Previews { get; }
    internal AnimationModService Animations { get; }
    internal PhysicsModService Physics { get; }

    public void Dispose() => Ipc.Dispose();
}
