using System.Collections.Immutable;
using Lumina.Data.Files;

namespace InstantEdit.Services.GameFiles;

/// <summary>A vanilla model file found in the game's file index.</summary>
internal sealed record GameModelEntry(GameModelId Id, string GamePath)
{
    public static GameModelEntry For(GameModelId id) => new(id, GameModelPaths.ModelPath(id));
}

/// <summary>
/// Lists a category's vanilla model files. The game's file index stores only path hashes, so files
/// are found by asking whether candidate paths exist, which costs about a microsecond each:
/// every race code and part ID for character parts; for gear, the sets whose IMC file exists (every
/// set with models has one), then each slot the IMC has for every race code; for weapons, the
/// primary IDs that have a base skeleton, a first-body IMC or one of their first bodies, then every
/// body of those.
/// </summary>
/// <remarks>
/// The ranges come from brute-force scans of the 2026 game data: part IDs reach 251, gear sets
/// 9996 and weapon bodies 999, apart from one NPC weapon, w2701b9998, which the item seeds can't
/// name either. The skeleton and IMC test alone misses one primary, w3302, whose only body is b0002.
/// </remarks>
internal sealed class GameFileCatalogBuilder(Func<string, bool> exists, Func<string, byte[]?> read)
{
    public const int MaxPartId = 999;
    public const int MaxSetId = 9999;
    public const int MaxWeaponId = 9999;
    public const int MaxWeaponBody = 999;
    private const int FirstBodies = 9;

    /// <summary>How many paths the last enumeration asked about.</summary>
    public long Probes { get; private set; }

    /// <summary>
    /// The category's model files, in ID, race and slot order. <paramref name="weaponSeeds"/> adds
    /// the (primary, body) pairs the Item sheet names, so bodies above the probed range are kept.
    /// <paramref name="progress"/> gets the finished fraction now and then.
    /// </summary>
    public ImmutableArray<GameModelEntry> Enumerate(GameFileCategory category,
        IReadOnlyCollection<(ushort Primary, ushort Body)>? weaponSeeds = null, CancellationToken token = default,
        Action<float>? progress = null)
    {
        Probes = 0;
        var found = category switch
        {
            GameFileCategory.Equipment or GameFileCategory.Accessory => Gear(category, token, progress),
            GameFileCategory.Weapon => Weapons(weaponSeeds ?? [], token, progress),
            _ => Parts(category, token, progress),
        };
        progress?.Invoke(1);
        return [.. found.OrderBy(entry => entry.Id.Id).ThenBy(entry => entry.Id.SecondaryId)
            .ThenBy(entry => GameModelPaths.Slots(category).IndexOf(entry.Id.Slot)).ThenBy(entry => entry.Id.Race)];
    }

    private bool Exists(string path)
    {
        Probes++;
        return exists(path);
    }

    private List<GameModelEntry> Parts(GameFileCategory category, CancellationToken token, Action<float>? progress)
    {
        var found = new List<GameModelEntry>();
        var slots = GameModelPaths.Slots(category);
        for (var r = 0; r < GameRaces.All.Length; r++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke((float)r / GameRaces.All.Length);
            var race = GameRaces.All[r].Code;
            for (var id = 1; id <= MaxPartId; id++)
            {
                foreach (var slot in slots)
                {
                    var entry = GameModelEntry.For(new GameModelId(category, race, (ushort)id, 0, slot));
                    if (Exists(entry.GamePath)) found.Add(entry);
                }
            }
        }
        return found;
    }

    private List<GameModelEntry> Gear(GameFileCategory category, CancellationToken token, Action<float>? progress)
    {
        var found = new List<GameModelEntry>();
        var slots = GameModelPaths.Slots(category);
        for (var set = 0; set <= MaxSetId; set++)
        {
            if (set % 256 == 0)
            {
                token.ThrowIfCancellationRequested();
                progress?.Invoke((float)set / (MaxSetId + 1));
            }
            var imcPath = GameModelPaths.SetImcPath(category, (ushort)set);
            if (!Exists(imcPath)) continue;
            // The IMC names the slots the set has; without a readable one, try them all.
            var mask = PartMask(imcPath) ?? 0x1F;
            for (var part = 0; part < slots.Length; part++)
            {
                if ((mask & (1 << part)) == 0) continue;
                foreach (var race in GameRaces.All)
                {
                    var entry = GameModelEntry.For(new GameModelId(category, race.Code, (ushort)set, 0, slots[part]));
                    if (Exists(entry.GamePath)) found.Add(entry);
                }
            }
        }
        return found;
    }

    private int? PartMask(string imcPath)
    {
        try
        {
            return read(imcPath) is { Length: > 0 } bytes
                ? MaterialPreviewBundleBuilder.LooseLuminaFile.Load<ImcFile>(bytes).PartMask
                : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private List<GameModelEntry> Weapons(IReadOnlyCollection<(ushort Primary, ushort Body)> seeds, CancellationToken token,
        Action<float>? progress)
    {
        var primaries = new SortedSet<ushort>(seeds.Select(seed => seed.Primary));
        for (var primary = 0; primary <= MaxWeaponId; primary++)
        {
            if (primary % 256 == 0)
            {
                token.ThrowIfCancellationRequested();
                progress?.Invoke(0.2f * primary / MaxWeaponId);
            }
            var w = (ushort)primary;
            if (primaries.Contains(w)) continue;
            if (Exists($"chara/weapon/w{w:D4}/skeleton/base/b0001/skl_w{w:D4}b0001.sklb") ||
                Exists(GameModelPaths.ImcPath(new GameModelId(GameFileCategory.Weapon, 0, w, 1, ""))!))
            {
                primaries.Add(w);
                continue;
            }
            for (var body = 1; body <= FirstBodies; body++)
            {
                if (!Exists(GameModelPaths.ModelPath(new GameModelId(GameFileCategory.Weapon, 0, w, (ushort)body, "")))) continue;
                primaries.Add(w);
                break;
            }
        }

        var found = new Dictionary<string, GameModelEntry>(StringComparer.Ordinal);
        var done = 0;
        foreach (var primary in primaries)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(0.2f + 0.8f * done++ / Math.Max(1, primaries.Count));
            for (var body = 0; body <= MaxWeaponBody; body++)
                Add(new GameModelId(GameFileCategory.Weapon, 0, primary, (ushort)body, ""));
        }
        foreach (var (primary, body) in seeds.Where(seed => seed.Body > MaxWeaponBody))
            Add(new GameModelId(GameFileCategory.Weapon, 0, primary, body, ""));
        return [.. found.Values];

        void Add(GameModelId id)
        {
            var entry = GameModelEntry.For(id);
            if (!found.ContainsKey(entry.GamePath) && Exists(entry.GamePath)) found[entry.GamePath] = entry;
        }
    }
}
