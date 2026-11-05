using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations;

/// <summary>One of the game's own idles: its name as /changepose numbers it and the PAP the race loads for it.</summary>
internal sealed record GameIdle(string Name, string GamePath);

/// <summary>A standing idle loop a mod replaces in the character's collection: "Loop 2 - Mod name", and the game path it replaces.</summary>
internal sealed record ModdedIdle(string Name, string GamePath);

internal sealed partial class AnimationEditService
{
    // /changepose idles so far go up to pose07; a few spare numbers let a new one appear by itself.
    private const int MostIdlePoses = 20;

    /// <summary>
    /// The race the character playing <paramref name="source"/> is, by the body skeleton it loaded (e.g. "c0101"); else
    /// the race the clip was made for. Null for a clip of no playable race.
    /// </summary>
    internal static string? CharacterRace(AnimationTimelineSource source) =>
        source.Capture.LoadedResourcePaths.Select(path => (Path: path, Race: AnimationSkeletonIndex.ModelFromPath(path)))
            .FirstOrDefault(loaded => loaded.Race != null && loaded.Path == VanillaIdlePaths(loaded.Race).Skeleton).Race
        ?? source.FileModel ?? AnimationSkeletonIndex.ModelFromPath(source.Clip.SkeletonPath);

    /// <summary>
    /// The game's standing idle for <paramref name="race"/> and each /changepose idle (emote/poseNN_loop), at the
    /// path the game loads for that race (<paramref name="resolve"/>, the PAP load table) when <paramref name="exists"/>.
    /// </summary>
    internal static ImmutableArray<GameIdle> GameIdles(string race, Func<string, string> resolve, Func<string, bool> exists)
    {
        var idles = ImmutableArray.CreateBuilder<GameIdle>();
        void Add(string name, string key)
        {
            var path = resolve($"chara/human/{race}/animation/a0001/bt_common/{key}.pap");
            if (exists(path))
                idles.Add(new GameIdle(name, path));
        }
        Add("Standing idle", "resident/idle");
        for (var number = 1; number <= MostIdlePoses; number++)
            Add($"Idle pose {number.ToString(CultureInfo.InvariantCulture)}", $"emote/pose{number:D2}_loop");
        return idles.ToImmutable();
    }

    /// <summary>The game's own idles for <paramref name="race"/> (see <see cref="GameIdles(string, Func{string, string}, Func{string, bool})"/>).</summary>
    public Task<ImmutableArray<GameIdle>> GameIdlesAsync(string race, CancellationToken token)
    {
        var table = PapTable;
        return Task.Run(() => GameIdles(race, path => table?.Resolve(path) ?? path, resources.GameFileExists), token);
    }

    /// <summary>
    /// Decodes <paramref name="idle"/>, the game's own file whatever mods replace it, on the game skeleton of the race it
    /// was made for (its PAP header's, else its path's): another race's copy when the race borrows one. Nothing in game changes.
    /// </summary>
    public Task<MotionClip> DecodeGameIdleAsync(AnimationTimelineSource source, GameIdle idle, Action<string> status, CancellationToken token) =>
        DecodeGameFileAsync(idle.GamePath, null, source.Capture, status, token);

    /// <summary>
    /// The loops of <paramref name="winners"/> a mod replaces, each named by its slot and that mod (resident/idle is
    /// Loop 0, emote/pose03_loop Loop 3, as <see cref="AnimationSlots"/> numbers them), in slot order. Startups are left out.
    /// </summary>
    internal static ImmutableArray<ModdedIdle> ModdedIdles(IEnumerable<(GameIdle Idle, string? Mod)> winners)
    {
        var idles = new List<(int Slot, ModdedIdle Idle)>();
        foreach (var (idle, mod) in winners)
            if (!string.IsNullOrWhiteSpace(mod) && AnimationSlots.Describe(idle.GamePath) is { Startup: false } slot)
                idles.Add((slot.Index, new ModdedIdle($"Loop {slot.Index.ToString(CultureInfo.InvariantCulture)} - {mod}", idle.GamePath)));
        return [.. idles.OrderBy(i => i.Slot).Select(i => i.Idle)];
    }

    /// <summary>
    /// The standing idle loops the collection of the character playing <paramref name="source"/> takes from a mod right
    /// now (see <see cref="ModdedIdles"/>), at the path the game loads for its race; none without a race or collection.
    /// </summary>
    public async Task<ImmutableArray<ModdedIdle>> ModdedIdlesAsync(AnimationTimelineSource source, CancellationToken token)
    {
        if (CharacterRace(source) is not { } race || source.Capture.CollectionId == Guid.Empty)
            return [];
        var idles = await GameIdlesAsync(race, token).ConfigureAwait(false);
        var mods = await resources.ModNamesAsync(source.Capture.CollectionId, [.. idles.Select(idle => idle.GamePath)]).ConfigureAwait(false);
        return ModdedIdles(idles.Zip(mods));
    }

    /// <summary>
    /// Decodes <paramref name="idle"/> as the character's collection loads it now, on that collection's skeleton of the race
    /// it was made for, so an idle made for a skeleton mod reads too. Nothing in game changes.
    /// </summary>
    public async Task<MotionClip> DecodeModdedIdleAsync(AnimationTimelineSource source, ModdedIdle idle, Action<string> status, CancellationToken token)
    {
        var collection = source.Capture.CollectionId;
        var (_, pap) = await resources.ReadAsync(collection, idle.GamePath, token).ConfigureAwait(false);
        return await DecodePapAsync(pap, idle.GamePath, null, source.Capture, async path => (await resources.ReadAsync(collection, path, token)).Bytes,
            status, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The standing idle's entry in resident/idle.pap. Every race's file lists an additive damage flinch
    /// (cbna_add_dmg_f) before it.
    /// </summary>
    internal const string StandingIdleEntry = "cbnm_id0";

    /// <summary>
    /// The entry of <paramref name="entries"/> to decode: the one named <paramref name="entryName"/>, else the first
    /// body entry that isn't additive (cbna_add_*, a motion added onto another), else the first body entry.
    /// </summary>
    internal static AnimationPap.Entry? DonorEntry(IReadOnlyList<AnimationPap.Entry> entries, string? entryName) =>
        entries.FirstOrDefault(e => e.Name == entryName)
        ?? entries.FirstOrDefault(e => e.Face == 0 && !e.Name.Contains("_add_", StringComparison.Ordinal))
        ?? entries.FirstOrDefault(e => e.Face == 0);

    /// <summary>
    /// Decodes the game's own PAP at <paramref name="papPath"/> on its race's game skeleton: the entry
    /// <see cref="DonorEntry"/> picks.
    /// </summary>
    private async Task<MotionClip> DecodeGameFileAsync(string papPath, string? entryName, AnimationCapture capture, Action<string> status,
        CancellationToken token)
    {
        var pap = await resources.ReadGameAsync(papPath, token)
            ?? throw new FileNotFoundException($"The game has no {Path.GetFileNameWithoutExtension(papPath)} animation for this race.");
        return await DecodePapAsync(pap, papPath, entryName, capture, path => resources.ReadGameAsync(path, token), status, token);
    }

    /// <summary>
    /// Decodes <paramref name="pap"/>, the file at <paramref name="papPath"/>, on the body skeleton of the race its header
    /// names (else its path's), as <paramref name="readSkeleton"/> reads it: the entry <see cref="DonorEntry"/> picks.
    /// </summary>
    private async Task<MotionClip> DecodePapAsync(byte[] pap, string papPath, string? entryName, AnimationCapture capture,
        Func<string, Task<byte[]?>> readSkeleton, Action<string> status, CancellationToken token)
    {
        var file = new AnimationPap(pap);
        var race = (file.ModelType == 0 ? AnimationSkeletonIndex.ModelCode(file.ModelId) : null) ?? AnimationSkeletonIndex.ModelFromPath(papPath)
            ?? throw new InvalidOperationException("The animation isn't made for a playable race.");
        var skeletonPath = VanillaIdlePaths(race).Skeleton;
        var skeleton = await readSkeleton(skeletonPath)
            ?? throw new FileNotFoundException($"The game has no skeleton for {AnimationBones.RaceName(race)}.");
        var entry = DonorEntry(file.Entries, entryName)
            ?? throw new InvalidDataException("The game's animation holds no body motion.");
        var description = await framework.RunOnFrameworkThread(() => AnimationSkeleton.Inspect(skeleton));
        var clip = new AnimationClip(papPath, entry.Name, entry.Binding, 0, 0, skeletonPath, description.Fingerprint);
        var work = Path.Combine(Path.GetTempPath(), "XIV Instant Edit motion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try { return await baker.DecodeAsync(pap, skeleton, clip, capture, work, status, token, () => { }); }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }
}
