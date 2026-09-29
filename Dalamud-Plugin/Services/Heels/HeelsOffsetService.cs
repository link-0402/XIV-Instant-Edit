using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Skeletons;
using Lumina.Data;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Heels;

/// <summary> A heels offset measured on a character's current feet model, with what it was measured on. </summary>
/// <param name="ModelFile">The drawn file, as <see cref="PainterVisibility.NormalizePath"/> writes it.</param>
/// <param name="Source">The mod the model comes from, or "game data".</param>
/// <param name="HeightScale">The character's height, which Simple Heels' offsets don't scale with.</param>
/// <param name="Scaling">The racial scaling the model was measured with, when it is another race's.</param>
internal sealed record HeelsOffsetResult(string Character, string ModelFile, string ItemName, ushort ModelId, string Source,
    HeelsMeasurement Measurement, float HeightScale, RacialScaling? Scaling, IReadOnlyList<string> Warnings)
{
    /// <summary> The offset for a Simple Heels entry, in world units: the model offset at the character's height. </summary>
    public float Offset => Measurement.Offset * HeightScale;

    /// <summary> What Simple Heels itself applies for the model's stored offset, in world units. </summary>
    public float? StoredOffset => Measurement.ModelOffset?.Value * HeightScale;
}

/// <summary>
/// Measures the heels offset of an On Screen character's shoes: the feet model it draws (read live, with the
/// parts and shapes its outfit turns on or off), measured unanimated after racial scaling. Customize+ and
/// animations are left out, like the offsets mod authors store.
/// </summary>
internal sealed class HeelsOffsetService : IDisposable
{
    // The feet slot among a human's models: head, top, hands, legs, feet.
    private const int FeetSlot = 4;
    private const long MaxFileBytes = 256L * 1024 * 1024;

    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IDataManager _data;
    private readonly ModelSkeletonResolver _skeletons;
    private readonly IPluginLog _log;
    private readonly Lock _lock = new();
    private Dictionary<ushort, List<string>>? _feetItems;

    public HeelsOffsetService(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects, IDataManager data,
        ModelSkeletonResolver skeletons, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _data = data;
        _skeletons = skeletons;
        _log = log;
        Ipc = new SimpleHeelsIpc(pi, log);
    }

    /// <summary> What Simple Heels applies to the local player now. </summary>
    public SimpleHeelsIpc Ipc { get; }

    public void Dispose() => Ipc.Dispose();

    private sealed record LiveFeet(string ModelFile, uint Attributes, uint Shapes, ushort ModelId, float HeightScale);

    public async Task<HeelsOffsetResult> MeasureAsync(OnScreenObject character, CancellationToken token)
    {
        var live = await _framework.RunOnFrameworkThread(() =>
        {
            var feet = ReadLive(character.ObjectIndex, character.Address);
            // While there, so the card shows what Simple Heels applies at the moment of measuring.
            Ipc.Refresh();
            return feet;
        }).ConfigureAwait(false);
        var node = HeelsMeasure.FeetNode(character.ResourceRoots, live.ModelFile)
            ?? throw new HeelsListOutdatedException();

        var warnings = new List<string>();
        var racePath = HeelsMeasure.RacePath(node);
        RacialScaling? scaling = null;
        var (source, problem) = await _skeletons.RacialScalingAsync(character.ObjectIndex, character.Address, token).ConfigureAwait(false);
        if (source is not null)
            scaling = source.For(racePath);
        else if (problem is not null && ModelSkeletonPaths.Parse(racePath) is { Human: true })
            warnings.Add($"Measured without racial scaling: {problem}.");

        return await Task.Run(() =>
        {
            var bytes = Read(node.ActualPath) ?? throw new IOException($"Could not read {Path.GetFileName(node.ActualPath)}.");
            token.ThrowIfCancellationRequested();
            var measurement = HeelsMeasure.Measure(bytes, scaling?.Deformer, live.Attributes, live.Shapes);
            var sourceLabel = node.SourceState == ResourceSourceState.LoadedMod && !string.IsNullOrEmpty(node.SourceModName)
                ? node.SourceModName
                : "game data";
            var name = SimpleHeels.FeetName(live.ModelId, FeetItems(live.ModelId));
            return new HeelsOffsetResult(character.Name, live.ModelFile, name, live.ModelId, sourceLabel, measurement, live.HeightScale,
                scaling, warnings);
        }, token).ConfigureAwait(false);
    }

    /// <summary> The drawn file of a character's current feet model, the parts and shapes turned on, and its height. </summary>
    private unsafe LiveFeet ReadLive(ushort objectIndex, nint address)
    {
        if (address == 0 || _objects[objectIndex] is not ICharacter character || character.Address != address)
            throw new HeelsListOutdatedException();
        var drawObject = ((Character*)address)->GetCharacterBase();
        if (drawObject == null || drawObject->GetModelType() != CharacterBase.ModelType.Human)
            throw new InvalidOperationException($"{character.Name.TextValue} isn't drawn as a person right now.");
        var model = drawObject->Models != null && drawObject->SlotCount > FeetSlot ? drawObject->Models[FeetSlot] : null;
        if (model == null || model->ModelResourceHandle == null)
            throw new InvalidOperationException($"{character.Name.TextValue} draws no feet model; the outfit may hide the feet.");
        var file = PainterVisibility.NormalizePath(model->ModelResourceHandle->FileName.ToString());
        var scale = drawObject->DrawObject.Object.Scale.Y;
        return new LiveFeet(file, model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask, ((Human*)drawObject)->Feet.Id,
            float.IsFinite(scale) && scale > 0 ? scale : 1);
    }

    private byte[]? Read(string path)
    {
        try
        {
            if (Path.IsPathRooted(path))
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length is > 0 and <= MaxFileBytes ? File.ReadAllBytes(info.FullName) : null;
            }
            return _data.GetFile<FileResource>(PathRules.NormalizeGamePath(path))?.Data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            _log.Debug(e, "Could not read {Path} for the heels offset.", path);
            return null;
        }
    }

    /// <summary> The feet items that use a model, in sheet order, as Simple Heels lists them (read once). </summary>
    private IReadOnlyList<string> FeetItems(ushort modelId)
    {
        lock (_lock)
        {
            if (_feetItems is null)
            {
                _feetItems = [];
                foreach (var item in _data.GetExcelSheet<Item>())
                {
                    if (item.ItemUICategory.RowId != SimpleHeels.FeetCategory || !item.EquipSlotCategory.IsValid ||
                        item.EquipSlotCategory.Value.Feet == 0)
                        continue;
                    var id = (ushort)item.ModelMain;
                    if (!_feetItems.TryGetValue(id, out var names))
                        _feetItems[id] = names = [];
                    names.Add(item.Name.ExtractText());
                }
            }
            return _feetItems.TryGetValue(modelId, out var found) ? found : [];
        }
    }
}

/// <summary> The On Screen list predates the character's current look; refreshing it fixes that. </summary>
internal sealed class HeelsListOutdatedException()
    : InvalidOperationException("Your character changed since the On Screen list was refreshed. It is refreshing now; measure again in a moment.");

/// <summary>
/// What Simple Heels applies to the local player's outfit, through its IPC. That IPC exists so sync
/// plugins can pass offsets on, and nothing in it sets an entry of the user's own, so the card only shows
/// the value. Updated by Simple Heels' LocalChanged message and by <see cref="Refresh"/>.
/// </summary>
internal sealed class SimpleHeelsIpc : IDisposable
{
    // Simple Heels 0.11's API is 2.x; a new major version may change these calls.
    private const int ApiMajor = 2;
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<string> _localPlayer;
    private readonly ICallGateSubscriber<string, object?> _localChanged;
    private readonly Lock _lock = new();
    private float? _offset;

    public SimpleHeelsIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _apiVersion = pi.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        _localPlayer = pi.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
        _localChanged = pi.GetIpcSubscriber<string, object?>("SimpleHeels.LocalChanged");
        _localChanged.Subscribe(OnLocalChanged);
    }

    /// <summary> Simple Heels' offset for the local player's outfit in world units, or null when it isn't running. </summary>
    public float? Offset
    {
        get { lock (_lock) return _offset; }
    }

    /// <summary> Asks Simple Heels again. Call on the framework thread: Simple Heels reads the object table. </summary>
    public void Refresh()
    {
        float? offset = null;
        try
        {
            if (_apiVersion.HasFunction && _apiVersion.InvokeFunc().Item1 == ApiMajor && _localPlayer.HasFunction)
                offset = SimpleHeels.CurrentOffset(_localPlayer.InvokeFunc());
        }
        catch (IpcNotReadyError)
        {
        }
        catch (Exception e)
        {
            _log.Debug(e, "Could not read Simple Heels' offset.");
        }
        lock (_lock)
            _offset = offset;
    }

    private void OnLocalChanged(string message)
    {
        if (SimpleHeels.CurrentOffset(message) is { } offset)
            lock (_lock)
                _offset = offset;
    }

    public void Dispose() => _localChanged.Unsubscribe(OnLocalChanged);
}
