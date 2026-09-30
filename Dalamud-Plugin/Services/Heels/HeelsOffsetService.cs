using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Services.Skeletons;
using Lumina.Data;
using Lumina.Excel.Sheets;
using RenderModel = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;

namespace InstantEdit.Services.Heels;

/// <summary> The model a character's feet are in, as the game draws it: its slot and file, and the parts and shapes turned on. </summary>
internal readonly record struct HeelsModelKey(HeelsSlot Slot, string ModelFile, uint Attributes, uint Shapes)
{
    /// <summary>
    /// Whether this is the same model as <paramref name="other"/> with the same shapes. The parts turned on
    /// don't count: a written offset attribute is turned on too, which changes them.
    /// </summary>
    public bool SameModel(HeelsModelKey other) => Slot == other.Slot && ModelFile == other.ModelFile && Shapes == other.Shapes;
}

/// <summary> An offset attribute of another model Simple Heels reads before the measured one's, so it wins. </summary>
internal sealed record HeelsEarlierOffset(HeelsSlot Slot, HeelsModelOffset Offset);

/// <param name="Source">The written file, with the hash of what the fix wrote, so Undo only writes over the fix.</param>
/// <param name="Original">The model's bytes before the fix.</param>
internal sealed record HeelsUndo(PreviewSource Source, byte[] Original, ushort ObjectIndex);

/// <summary> A measured model and what fixing its offset did. </summary>
/// <param name="Source">The mod the model comes from, or "game data".</param>
/// <param name="ModFile">Whether the model is a file of an installed mod, which a fix can write.</param>
/// <param name="HeightScale">The character's height, which Simple Heels multiplies model offsets by.</param>
/// <param name="Scaling">The racial scaling the model was measured with, when it is another race's.</param>
internal sealed record HeelsOffsetResult(HeelsModelKey Model, string ItemName, string Source, bool ModFile,
    HeelsMeasurement Measurement, HeelsFixPlan Plan, float HeightScale, RacialScaling? Scaling, HeelsEarlierOffset? Earlier,
    IReadOnlyList<string> Warnings)
{
    /// <summary> Whether the automatic fix made this result rather than Fix offset. </summary>
    public bool Automatic { get; init; }

    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    /// <summary> Whether the fix wrote the model. </summary>
    public bool Written { get; init; }

    /// <summary> Why a model that needs a fix wasn't written. </summary>
    public string? NotWritten { get; init; }

    /// <summary> What Undo writes back after the fix wrote the model; null once undone. </summary>
    internal HeelsUndo? Undo { get; init; }

    /// <summary> Whether Undo put the model back as it was. </summary>
    public bool Undone { get; init; }

    /// <summary> The offset the model sets for Simple Heels after the fix, in model units; null when it sets none. </summary>
    public float? ModelOffset => Written ? HeelsModelOffset.Find([Plan.Attribute])?.Value : Measurement.ModelOffset?.Value;
}

/// <summary>
/// Fixes the Simple Heels offset of the local player's shoes: measures the model the feet are in (the
/// feet, else the legs or a one-piece body when gear hides the feet) as it is drawn, unanimated after
/// racial scaling, and writes the offset into that model as its heels_offset attribute. Mod files are
/// written in place with a backup, then the mod is reloaded and the character redrawn. With the
/// automatic fix on, each new model is fixed once it has settled.
/// </summary>
internal sealed class HeelsOffsetService : IDisposable
{
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const int PollMilliseconds = 500;
    // Gear changes load for a moment; a model is fixed once it stayed the same this long.
    private const int SettleMilliseconds = 1500;

    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly ModelSkeletonResolver _skeletons;
    private readonly PenumbraService _penumbra;
    private readonly ResourceSourceAttributor _sources;
    private readonly IReadOnlyList<IPreviewModRegistry> _previews;
    private readonly Func<bool> _automatic;
    private readonly IPluginLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _lock = new();
    // Reading the item sheet takes a while; the card's reads of the last result must not wait for it.
    private readonly Lock _itemsLock = new();
    // The models the automatic fix leaves alone: each one checked, with its file's size and write time then.
    private readonly Dictionary<HeelsModelKey, FileStamp?> _handled = [];
    // Model files whose fix was undone: the automatic fix leaves them alone until Fix offset is pressed on them.
    private readonly HashSet<string> _undone = [];
    private Dictionary<(HeelsSlot, ushort), (List<string> Listed, List<string> All)>? _items;
    private int _busy;
    private long _nextPoll;
    private HeelsModelKey? _pending;
    private long _pendingSince;
    private HeelsModelKey? _live;
    private HeelsOffsetResult? _last;
    private string? _lastError;

    /// <param name="previews">The preview-mod stores whose files belong to their previews until those are applied.</param>
    /// <param name="automatic">Whether the automatic fix is on.</param>
    public HeelsOffsetService(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects, IClientState clientState, ICondition condition,
        IDataManager data, ModelSkeletonResolver skeletons, PenumbraService penumbra, ResourceSourceAttributor sources,
        IReadOnlyList<IPreviewModRegistry> previews, Func<bool> automatic, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _condition = condition;
        _data = data;
        _skeletons = skeletons;
        _penumbra = penumbra;
        _sources = sources;
        _previews = previews;
        _automatic = automatic;
        _log = log;
        Ipc = new SimpleHeelsIpc(pi, log);
        _framework.Update += OnUpdate;
    }

    /// <summary> What Simple Heels applies to the local player now. </summary>
    public SimpleHeelsIpc Ipc { get; }

    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary> The model the local player's feet are in now, read twice a second; null while none is drawn. </summary>
    public HeelsModelKey? LiveModel
    {
        get { lock (_lock) return _live; }
    }

    /// <summary> The last fix's result, by Fix offset or the automatic fix; null after an error. </summary>
    public HeelsOffsetResult? Last
    {
        get { lock (_lock) return _last; }
    }

    /// <summary> Why the last fix failed; null after a result. </summary>
    public string? LastError
    {
        get { lock (_lock) return _lastError; }
    }

    /// <summary> Raised on a worker when the automatic fix wrote a model. </summary>
    public event Action<HeelsOffsetResult>? AutomaticallyFixed;

    /// <summary> Raised on a worker when the automatic fix failed, with why. </summary>
    public event Action<string>? AutomaticFixFailed;

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _lifetime.Cancel();
        AutomaticallyFixed = null;
        AutomaticFixFailed = null;
        Ipc.Dispose();
    }

    /// <summary> Measures the local player's feet and fixes the model's offset. False while a fix is running. </summary>
    public bool Fix() => Start(automatic: false);

    /// <summary>
    /// Puts the model the last fix wrote back as it was, if its file still holds what the fix wrote. False
    /// while a fix is running or when there is nothing to undo.
    /// </summary>
    public bool UndoLast()
    {
        if (Last is not { Undo: { } undo } result || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                lock (_lock)
                    _undone.Add(result.Model.ModelFile);
                var warnings = await _penumbra.WritePreviewSourcesAsync([(undo.Source, undo.Original)], undo.ObjectIndex, "Nothing was undone")
                    .ConfigureAwait(false);
                var stamp = FileStamp.Of(undo.Source.ActualPath);
                lock (_lock)
                {
                    _handled[result.Model] = stamp;
                    _last = result with { Written = false, Undo = null, Undone = true, Warnings = [.. result.Warnings, .. warnings] };
                    _lastError = null;
                }
            }
            catch (Exception error)
            {
                _log.Warning(error, "Could not undo the heels offset fix.");
                lock (_lock)
                    _lastError = error.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
        return true;
    }

    private bool Start(bool automatic)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await FixAsync(automatic, _lifetime.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    _last = result;
                    _lastError = null;
                }
                if (automatic && result.Written)
                    AutomaticallyFixed?.Invoke(result);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not fix the heels offset.");
                lock (_lock)
                {
                    _last = null;
                    _lastError = error.Message;
                }
                if (automatic)
                    AutomaticFixFailed?.Invoke(error.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
        return true;
    }

    private sealed record DrawnModel(ushort ObjectIndex, nint Address, HeelsModelKey Key, ushort ModelId, float HeightScale,
        HeelsEarlierOffset? Earlier);

    private async Task<HeelsOffsetResult> FixAsync(bool automatic, CancellationToken token)
    {
        var (live, resolved) = await _framework.RunOnFrameworkThread(() =>
        {
            var model = ReadLive(out var problem) ?? throw new InvalidOperationException(problem);
            // While there, so the card shows what Simple Heels applies at the moment of fixing.
            Ipc.Refresh();
            return (model, _penumbra.GetResourcePaths(model.ObjectIndex));
        }).ConfigureAwait(false);
        if (!automatic)
            lock (_lock)
                _undone.Remove(live.Key.ModelFile);
        var slot = live.Key.Slot;
        var (actualPath, gamePath) = HeelsMeasure.Resolve(resolved, live.Key.ModelFile, slot)
            ?? (Path.IsPathRooted(live.Key.ModelFile)
                ? throw new InvalidOperationException($"Penumbra doesn't list the {slot.Name()} model your character draws. Redraw your character and try again.")
                : (live.Key.ModelFile, live.Key.ModelFile));
        try
        {
            return await MeasureAndWriteAsync(automatic, live, actualPath, gamePath, token).ConfigureAwait(false);
        }
        finally
        {
            // Whatever came of it, the automatic fix checks this model again only once its file changes.
            var stamp = FileStamp.Of(actualPath);
            lock (_lock)
                _handled[live.Key] = stamp;
        }
    }

    private async Task<HeelsOffsetResult> MeasureAndWriteAsync(bool automatic, DrawnModel live, string actualPath, string gamePath,
        CancellationToken token)
    {
        var slot = live.Key.Slot;
        var warnings = new List<string>();
        var racePath = gamePath.Length > 0 ? gamePath : Path.GetFileName(actualPath);
        RacialScaling? scaling = null;
        var (scalingSource, problem) = await _skeletons.RacialScalingAsync(live.ObjectIndex, live.Address, token).ConfigureAwait(false);
        if (scalingSource is not null)
            scaling = scalingSource.For(racePath);
        else if (problem is not null && ModelSkeletonPaths.Parse(racePath) is { Human: true })
            warnings.Add($"Measured without racial scaling: {problem}.");

        var source = _sources.AttributionFor(actualPath);
        var modFile = source.State == ResourceSourceState.LoadedMod && Path.IsPathRooted(actualPath);
        var bytes = await Task.Run(() => Read(actualPath, modFile), token).ConfigureAwait(false)
                    ?? throw new IOException($"Could not read {Path.GetFileName(actualPath)}.");
        var measurement = HeelsMeasure.Measure(bytes, scaling?.Deformer, live.Key.Attributes, live.Key.Shapes);
        var plan = HeelsFix.Plan(measurement, slot);
        var result = new HeelsOffsetResult(live.Key, SimpleHeels.ItemName(slot, live.ModelId, Items(slot, live.ModelId)),
            modFile ? source.ModName ?? "a mod" : "game data", modFile, measurement, plan, live.HeightScale, scaling, live.Earlier, warnings)
        {
            Automatic = automatic,
        };
        if (plan.Action != HeelsFixAction.Write)
            return result;
        if (!modFile)
            return result with
            {
                NotWritten = source.State == ResourceSourceState.GameData
                    ? "The model is one of the game's own files, which Instant Edit doesn't change."
                    : "The model isn't a file of an installed mod, so there is no file to write the offset into.",
            };
        if (source.ModDirectory is { } directory && _previews.Any(store => store.HoldsMod(directory)))
            return result with { NotWritten = $"The model is part of the preview mod {directory}. Apply or discard that preview first." };

        token.ThrowIfCancellationRequested();
        var patched = HeelsModelTag.Apply(bytes, plan.Attribute);
        // The written model must measure as before, with the new offset as its only one.
        var before = HeelsMeasure.Measure(bytes, scaling?.Deformer, null, live.Key.Shapes);
        var after = HeelsMeasure.Measure(patched, scaling?.Deformer, null, live.Key.Shapes);
        if (after.Lowest != before.Lowest || after.DrawnParts != before.DrawnParts || after.OffsetAttributes != 1 ||
            after.ModelOffset?.Attribute != plan.Attribute)
            throw new InvalidDataException("Writing the offset would have changed more of the model than its offset, so the model was left alone.");
        var file = new PreviewSource
        {
            GamePath = gamePath,
            ActualPath = actualPath,
            State = source.State,
            ModName = source.ModName ?? string.Empty,
            ModDirectory = source.ModDirectory ?? string.Empty,
            ModRootPath = source.ModRootPath ?? string.Empty,
            RelativePath = (source.RelativePath ?? string.Empty).Replace('\\', '/'),
            ModStableId = source.ModStableId,
            Sha256 = PreviewSource.Hash(bytes),
        };
        warnings.AddRange(await _penumbra.WritePreviewSourcesAsync([(file, patched)], live.ObjectIndex, "Fix the offset again").ConfigureAwait(false));
        return result with { Written = true, Undo = new HeelsUndo(file with { Sha256 = PreviewSource.Hash(patched) }, bytes, live.ObjectIndex) };
    }

    /// <summary> Watches the local player's feet, and with the automatic fix on, fixes each new model once it has settled. </summary>
    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now < _nextPoll)
            return;
        _nextPoll = now + PollMilliseconds;
        HeelsModelKey? key = null;
        try
        {
            if (_clientState.IsLoggedIn)
                key = ReadLive(out _)?.Key;
        }
        catch (Exception e)
        {
            _log.Debug(e, "Could not read the local player's feet model.");
        }
        lock (_lock)
            _live = key;

        if (!_automatic() || key is not { } current || Waits())
        {
            _pending = null;
            return;
        }
        if (_pending != current)
        {
            _pending = current;
            _pendingSince = now;
            return;
        }
        if (now - _pendingSince < SettleMilliseconds || Busy)
            return;
        // A checked model is checked again when its file changed since, as a Quick Export from Blender changes it.
        var stamp = FileStamp.Of(current.ModelFile);
        lock (_lock)
        {
            if (_undone.Contains(current.ModelFile) ||
                (_handled.TryGetValue(current, out var checkedStamp) && (checkedStamp is null || checkedStamp == stamp)))
                return;
            _handled[current] = null;
        }
        if (!Start(automatic: true))
            lock (_lock)
                _handled.Remove(current);
    }

    /// <summary> GPose, cutscenes, zoning and combat, where the redraw after a fix would get in the way. </summary>
    private bool Waits()
        => _clientState.IsGPosing || _condition[ConditionFlag.InCombat] || _condition[ConditionFlag.BetweenAreas] ||
           _condition[ConditionFlag.BetweenAreas51] || _condition[ConditionFlag.OccupiedInCutSceneEvent] ||
           _condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.WatchingCutscene78];

    /// <summary>
    /// The local player's model that holds its feet, the parts and shapes it turns on, its height, and an
    /// offset a model Simple Heels reads first sets. Call on the framework thread. Null, with why, when none is drawn.
    /// </summary>
    private unsafe DrawnModel? ReadLive(out string problem)
    {
        problem = string.Empty;
        if (_objects.LocalPlayer is not { Address: not 0 } player)
        {
            problem = "Your character isn't loaded.";
            return null;
        }
        var drawObject = ((Character*)player.Address)->GetCharacterBase();
        if (drawObject == null || drawObject->GetModelType() != CharacterBase.ModelType.Human)
        {
            problem = "Your character isn't drawn as a person right now.";
            return null;
        }
        var human = (Human*)drawObject;
        foreach (var slot in HeelsSlots.FeetOrder)
        {
            var model = SlotModel(drawObject, slot);
            if (model == null)
                continue;
            var key = new HeelsModelKey(slot, PainterVisibility.NormalizePath(model->ModelResourceHandle->FileName.ToString()),
                model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask);
            var modelId = slot switch
            {
                HeelsSlot.Top => human->Top.Id,
                HeelsSlot.Legs => human->Legs.Id,
                _ => human->Feet.Id,
            };
            HeelsEarlierOffset? earlier = null;
            foreach (var before in slot.ReadBefore())
            {
                if (SlotModel(drawObject, before) is var other && other != null && OffsetOf(other) is { } offset)
                {
                    earlier = new HeelsEarlierOffset(before, offset);
                    break;
                }
            }
            var scale = drawObject->DrawObject.Object.Scale.Y;
            return new DrawnModel(player.ObjectIndex, player.Address, key, modelId,
                float.IsFinite(scale) && scale > 0 ? scale : 1, earlier);
        }
        problem = "Your character draws no feet, legs or body model right now.";
        return null;
    }

    private static unsafe RenderModel* SlotModel(CharacterBase* drawObject, HeelsSlot slot)
    {
        var index = (int)slot;
        if (drawObject->Models == null || drawObject->SlotCount <= index)
            return null;
        var model = drawObject->Models[index];
        return model != null && model->ModelResourceHandle != null ? model : null;
    }

    /// <summary> The offset a loaded model sets, from its attributes, the way Simple Heels reads it. </summary>
    private static unsafe HeelsModelOffset? OffsetOf(RenderModel* model)
    {
        var names = new List<string>();
        foreach (var (name, _) in model->ModelResourceHandle->Attributes)
            names.Add(name.ToString());
        return HeelsModelOffset.Find(names);
    }

    private byte[]? Read(string path, bool modFile)
    {
        try
        {
            if (modFile)
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length is > 0 and <= MaxFileBytes ? File.ReadAllBytes(info.FullName) : null;
            }
            return Path.IsPathRooted(path) ? null : _data.GetFile<FileResource>(PathRules.NormalizeGamePath(path))?.Data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            _log.Debug(e, "Could not read {Path} for the heels offset.", path);
            return null;
        }
    }

    /// <summary>
    /// The items that use a model, in sheet order: the ones Simple Heels lists in its Equipment Offsets (feet
    /// items, and body and legs items that take the feet slot too), else all of the slot's. Read once.
    /// </summary>
    private IReadOnlyList<string> Items(HeelsSlot slot, ushort modelId)
    {
        lock (_itemsLock)
        {
            if (_items is null)
            {
                _items = [];
                foreach (var item in _data.GetExcelSheet<Item>())
                {
                    // The Item sheet's UI categories Simple Heels lists body, legs and feet items under.
                    HeelsSlot? itemSlot = item.ItemUICategory.RowId switch
                    {
                        35 => HeelsSlot.Top,
                        36 => HeelsSlot.Legs,
                        38 => HeelsSlot.Feet,
                        _ => null,
                    };
                    if (itemSlot is not { } key || !item.EquipSlotCategory.IsValid)
                        continue;
                    var id = (ushort)item.ModelMain;
                    if (!_items.TryGetValue((key, id), out var names))
                        _items[(key, id)] = names = ([], []);
                    var name = item.Name.ExtractText();
                    names.All.Add(name);
                    if (item.EquipSlotCategory.Value.Feet != 0)
                        names.Listed.Add(name);
                }
            }
            return _items.TryGetValue((slot, modelId), out var found) ? found.Listed.Count > 0 ? found.Listed : found.All : [];
        }
    }
}

/// <summary>
/// What Simple Heels applies to the local player's outfit, through its IPC. That IPC exists so sync
/// plugins can pass offsets on, and nothing in it sets an offset, so the card only shows it. Updated by
/// Simple Heels' LocalChanged message and by <see cref="Refresh"/>.
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
