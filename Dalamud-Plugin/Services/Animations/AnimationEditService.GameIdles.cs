using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations;

/// <summary>One of the game's own idles: its name as /changepose numbers it and the PAP the race loads for it.</summary>
internal sealed record GameIdle(string Name, string GamePath);

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
        var file = new AnimationPap(pap);
        var race = (file.ModelType == 0 ? AnimationSkeletonIndex.ModelCode(file.ModelId) : null) ?? AnimationSkeletonIndex.ModelFromPath(papPath)
            ?? throw new InvalidOperationException("The animation isn't made for a playable race.");
        var skeletonPath = VanillaIdlePaths(race).Skeleton;
        var skeleton = await resources.ReadGameAsync(skeletonPath, token)
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
