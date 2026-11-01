using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Physics;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace InstantEdit.Services.Skeletons;

/// <summary> The head tracking a race was saved with (<see cref="Configuration.HeadTrackingRaces"/>). </summary>
public sealed class HeadTrackingSaved
{
    public ushort Race { get; set; }
    public bool LowerBack { get; set; }
    public float Back { get; set; } = 1;
    public float NeckHead { get; set; } = 1;
    public float Eyes { get; set; } = 1;
}

/// <summary>
/// The Head tracking card's work: your character's race, the game's skeleton parameters file for it edited with
/// <see cref="HeadTrackingEdit"/>, shown on your character through a temporary mod, and saved into one "Head tracking"
/// mod that maps every race you saved. Saving the game's settings takes your race out of the mod again.
/// </summary>
internal sealed class HeadTrackingService : IDisposable
{
    private const string ModName = "Head tracking";
    private const string ModDescription =
        "Head tracking made with XIV Instant Edit (Quick Actions > Head tracking). Changes how characters of these races turn toward what they look at, in every animation.";

    private readonly PenumbraIpc penumbra;
    private readonly PhysicsModService mods;
    private readonly PhysicsPreviewService preview;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly Func<string> cacheRoot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource life = new();
    private int objectIndex;

    public HeadTrackingService(PenumbraIpc penumbra, PhysicsModService mods, PreviewModWriter previewMods, IDataManager data, IFramework framework,
        IObjectTable objects, IClientState clientState, IPluginLog log, Configuration config, Action saveConfig, string configDirectory,
        Func<string> cacheRoot)
    {
        this.penumbra = penumbra;
        this.mods = mods;
        this.data = data;
        this.framework = framework;
        this.objects = objects;
        this.clientState = clientState;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
        this.cacheRoot = cacheRoot;
        preview = new PhysicsPreviewService(penumbra, previewMods, framework, log, configDirectory, cacheRoot, FilePreviewKind.HeadTracking);
    }

    /// <summary> Your character's race and sex skeleton (801), or null when it isn't loaded or isn't human. </summary>
    public ushort? Race { get; private set; }

    /// <summary> "Miqo'te female (c0801)". </summary>
    public string RaceLabel => Race is { } race ? $"{GameRaces.Label(race)} (c{race:D4})" : "";

    public bool Busy => gate.CurrentCount == 0;

    public bool Previewing => preview.Active;

    /// <summary> What the last action did, or null. </summary>
    public string? Status { get; private set; }

    /// <summary> Why the last action failed, or null. </summary>
    public string? Error { get; private set; }

    /// <summary> Another mod that changes your race's head tracking file in your collection, or null. </summary>
    public string? OtherMod { get; private set; }

    /// <summary> Your race changed (Glamourer, Fantasia); the preview has been ended. </summary>
    public event Action? RaceChanged;

    /// <summary> Reads your character's race; framework thread (the card calls it each time it draws). </summary>
    public unsafe void Refresh()
    {
        ushort? race = null;
        var index = 0;
        if (objects.LocalPlayer is ICharacter local && local.Address != 0)
        {
            var shown = PhysicsLiveReader.Shown(objects, local, clientState.IsGPosing);
            var drawObject = shown.Address == 0 ? null : ((GameCharacter*)shown.Address)->GetCharacterBase();
            if (drawObject != null && drawObject->GetModelType() == CharacterBase.ModelType.Human &&
                GameRaces.Find(((Human*)drawObject)->RaceSexId) is { } known)
            {
                race = known.Code;
                index = shown.ObjectIndex;
            }
        }
        // A warning the preview's check after its redraw left (Penumbra serving another file instead).
        if (preview.TakeNotice() is { Warning: { } warning })
            Status = warning;
        var (next, changed) = NextRace(Race, race);
        if (race != null)
            objectIndex = index;
        if (!changed)
            return;
        var hadRace = Race is not null;
        Race = next;
        OtherMod = null;
        // Straight to the preview, which waits for its own turn: a Preview or Save still running mustn't swallow the stop.
        if (hadRace && preview.Active)
            _ = Task.Run(() => StopAfterRaceChangeAsync(index));
        if (next is { } now)
            _ = Task.Run(() => FindOtherModAsync(now, index));
        RaceChanged?.Invoke();
    }

    private async Task StopAfterRaceChangeAsync(int index)
    {
        try { await preview.StopAsync(index).ConfigureAwait(false); }
        catch (Exception e) { log.Warning(e, "Could not end the head tracking preview after a race change."); }
    }

    /// <summary>
    /// The race after seeing <paramref name="seen"/>: a character without a drawn human body (mid-redraw, which every
    /// preview does) keeps the race it had, so only another race counts as a change.
    /// </summary>
    internal static (ushort? Race, bool Changed) NextRace(ushort? current, ushort? seen) =>
        seen is null ? (current, false) : (seen, seen != current);

    /// <summary> The folders among <paramref name="directories"/> whose mod maps <paramref name="gamePath"/>, except the mod with <paramref name="ours"/>. </summary>
    internal static List<string> ModsMapping(string root, IEnumerable<string> directories, string gamePath, Guid ours) =>
        directories.Where(directory => ModPaths.IsSafeModName(directory) && Path.Combine(root, directory) is var folder &&
                                       Directory.Exists(folder) && ModMetadata.ModMapsGamePath(folder, gamePath) &&
                                       (ours == Guid.Empty || ModMetadata.ReadModStableIdentifier(folder) != ours))
            .ToList();

    /// <summary> The settings your race was saved with, or the game's. </summary>
    public HeadTrackingSettings Saved(ushort race) =>
        config.HeadTrackingRaces.FirstOrDefault(saved => saved.Race == race) is { } found
            ? new HeadTrackingSettings(found.LowerBack, found.Back, found.NeckHead, found.Eyes)
            : HeadTrackingSettings.Game;

    /// <summary> Shows <paramref name="settings"/> on your character until <see cref="StopPreviewAsync"/>, a save or a race change. </summary>
    public Task PreviewAsync(HeadTrackingSettings settings) => Run(async () =>
    {
        var (race, index) = Current();
        var warning = await preview.ShowAsync(index, HeadTrackingEdit.GamePath(race), Edited(race, settings), life.Token).ConfigureAwait(false);
        Status = warning ?? "Previewing on your character.";
    });

    public Task StopPreviewAsync() => Run(async () =>
    {
        await preview.StopAsync(objectIndex).ConfigureAwait(false);
        Status = null;
    });

    /// <summary> Saves <paramref name="settings"/> for your race into the Head tracking mod, or takes your race out of it for the game's settings. </summary>
    public Task SaveAsync(HeadTrackingSettings settings) => Run(async () =>
    {
        var (race, index) = Current();
        var gamePath = HeadTrackingEdit.GamePath(race);
        settings = settings.Clamped();
        var bytes = Edited(race, settings);
        await preview.StopAsync(redrawObject: null).ConfigureAwait(false);
        var warnings = new List<string>();
        if (settings.IsGame)
        {
            if (config.HeadTrackingModDirectory.Length > 0)
            {
                var (_, warning) = await mods.RemoveFileModAsync(ModFileKind.SkeletonParams, config.HeadTrackingModDirectory,
                    config.HeadTrackingModIdentifier, gamePath).ConfigureAwait(false);
                if (warning != null)
                    warnings.Add(warning);
            }
            Remember(race, null);
            Status = "Your race uses the game's head tracking again.";
        }
        else
        {
            var name = await FreeNameAsync().ConfigureAwait(false);
            var saved = await mods.SaveFileModAsync(ModFileKind.SkeletonParams, config.HeadTrackingModDirectory, config.HeadTrackingModIdentifier,
                name, ModDescription, gamePath, bytes, index, life.Token).ConfigureAwait(false);
            config.HeadTrackingModDirectory = saved.Directory;
            config.HeadTrackingModIdentifier = saved.Identifier;
            warnings.AddRange(saved.Warnings);
            Remember(race, settings);
            Status = $"Saved in {saved.Directory}.";
        }
        if (await penumbra.RedrawObjectAsync(index).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        if (warnings.Count > 0)
            Status += " " + string.Join(" ", warnings);
        await FindOtherModAsync(race, index).ConfigureAwait(false);
    });

    private (ushort Race, int Index) Current() =>
        Race is { } race ? (race, objectIndex) : throw new IOException("Your character isn't loaded.");

    /// <summary> The game's own file for <paramref name="race"/>, edited; never the one mods resolve, so saves never stack. </summary>
    private byte[] Edited(ushort race, HeadTrackingSettings settings)
    {
        var vanilla = data.GetFile(HeadTrackingEdit.GamePath(race))?.Data
            ?? throw new FileNotFoundException("The game has no head tracking file for this race.");
        return HeadTrackingEdit.Apply(vanilla, settings);
    }

    private void Remember(ushort race, HeadTrackingSettings? settings)
    {
        var races = config.HeadTrackingRaces.Where(saved => saved.Race != race).ToList();
        if (settings != null)
            races.Add(new HeadTrackingSaved
            {
                Race = race, LowerBack = settings.LowerBack, Back = settings.Back, NeckHead = settings.NeckHead, Eyes = settings.Eyes,
            });
        config.HeadTrackingRaces = races;
        saveConfig();
    }

    /// <summary> "Head tracking", or "Head tracking (2)" when another mod has the name; only used when the mod is made. </summary>
    private async Task<string> FreeNameAsync()
    {
        var (directory, list) = await framework.RunOnFrameworkThread(() =>
            (penumbra.GetModDirectory(), penumbra.TryGetModList(out var mods) ? mods : null)).ConfigureAwait(false);
        var root = directory ?? "";
        var listed = list ?? throw new IOException("Could not retrieve the Penumbra mod list.");
        return PreviewModWriter.UniqueModName(ModName, name =>
            listed.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ||
            listed.Values.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) ||
            root.Length > 0 && (Directory.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, name))));
    }

    /// <summary>
    /// Names another mod enabled in your collection that maps your race's file, whether or not ours wins over it now:
    /// while ours is on, that's the head tracking ours replaces. Reads every mod's metadata, so it runs only when the
    /// race changes and after a save.
    /// </summary>
    private async Task FindOtherModAsync(ushort race, int index)
    {
        try
        {
            var collection = await penumbra.GetCollectionTargetAsync(index).ConfigureAwait(false);
            if (collection is null)
                return;
            var (root, list) = await framework.RunOnFrameworkThread(() =>
                (penumbra.GetModDirectory(), penumbra.TryGetModList(out var listed) ? listed : null)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root) || list is null)
                return;
            var mapping = await Task.Run(() => ModsMapping(root, list.Keys, HeadTrackingEdit.GamePath(race), config.HeadTrackingModIdentifier))
                .ConfigureAwait(false);
            var other = await framework.RunOnFrameworkThread(() => mapping.FirstOrDefault(directory =>
                penumbra.GetCurrentModSettings.Invoke(collection.Id, directory, string.Empty, false) is ({ } code, { } settings) &&
                code == Penumbra.Api.Enums.PenumbraApiEc.Success && settings.Item1)).ConfigureAwait(false);
            if (Race == race)
                OtherMod = other is null ? null : list.GetValueOrDefault(other) is { Length: > 0 } name ? name : other;
        }
        catch (Exception e)
        {
            log.Debug(e, "Could not check which mod changes head tracking.");
        }
    }

    /// <summary> One action at a time; its failure lands in <see cref="Error"/>. </summary>
    private Task Run(Func<Task> action, bool report = true) => Task.Run(async () =>
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            if (report)
                Error = null;
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (report)
                Error = e.Message;
            else
                log.Warning(e, "Head tracking failed.");
        }
        catch (Exception e)
        {
            log.Error(e, "Head tracking failed.");
            if (report)
                Error = "Head tracking failed. See the log for details.";
        }
        finally
        {
            gate.Release();
        }
    });

    public void Dispose()
    {
        life.Cancel();
        preview.Dispose();
    }
}
