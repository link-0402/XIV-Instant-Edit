using System;
using System.Collections.Generic;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Check;

/// <summary>Item names for a model and IMC variant, for reports such as "variant 1: Birdsong Coat". Safe to call from a worker.</summary>
internal interface IItemVariants
{
    IReadOnlyList<string> Names(GameModelKind kind, ushort primaryId, ushort secondaryId, EquipSlot? slot, int variant);
}

/// <summary>The player's collection as a check saw it: its name, and whether (and at what priority) the mod is on in it.</summary>
internal sealed record CollectionContext(string Name, bool? ModEnabled, int? Priority, string? Note);

/// <summary>
/// The player's Penumbra collection, for finding what other mods provide and which of this mod's
/// files another mod overrides. Captured on the framework thread before a check starts.
/// </summary>
internal interface ICollectionView
{
    CollectionContext Context { get; }

    /// <summary>For each game path, the file or game path the collection resolves it to, or null when nothing redirects it.</summary>
    IReadOnlyDictionary<string, string?> Resolve(IReadOnlyList<string> gamePaths);

    /// <summary>The mod folder a resolved file belongs to, or null when it is not inside Penumbra's mod folder.</summary>
    string? ModOf(string resolvedPath);
}

/// <summary>Everything a check reads, copied on the render thread so the check can run on a worker.</summary>
internal sealed class CheckInput
{
    public required string Folder { get; init; }

    /// <summary>A copy of the mod's meta.json; the check never writes it.</summary>
    public required PenumbraModMeta Meta { get; init; }

    /// <summary>meta.json's fingerprint when <see cref="Meta"/> was copied, so an edit made before the check runs makes its report stale.</summary>
    public MetaFingerprint? Fingerprint { get; init; }

    public required IVanillaFiles Game { get; init; }
    public IItemVariants? Items { get; init; }

    /// <summary>The player's race code (101 for c0101), for highlighting it.</summary>
    public ushort? PlayerRace { get; init; }

    public ICollectionView? Collection { get; init; }

    /// <summary>Files this plugin wrote into the mod (the sidecar's ledger), as comparison keys: never offered for deletion.</summary>
    public IReadOnlySet<string> LedgerRelKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The same input, checked against a collection.</summary>
    public CheckInput WithCollection(ICollectionView? collection) => new()
    {
        Folder = Folder, Meta = Meta, Fingerprint = Fingerprint, Game = Game, Items = Items, PlayerRace = PlayerRace, Collection = collection, LedgerRelKeys = LedgerRelKeys,
    };

    public static CheckInput From(ModProject project, IVanillaFiles game, IItemVariants? items, ushort? player, ICollectionView? collection)
    {
        var ledger = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in project.Sidecar.Ledger.Files)
            ledger.Add(PathNormalizer.RelKey(file.RelPath));
        return new CheckInput
        {
            Folder        = project.FolderPath,
            Meta          = project.Meta.Clone(),
            Fingerprint   = MetaFingerprint.Of(project.FolderPath),
            Game          = game,
            Items         = items,
            PlayerRace    = player,
            Collection    = collection,
            LedgerRelKeys = ledger,
        };
    }

    /// <summary>A check of a mod folder as it is on disk (tests, and the parity harness).</summary>
    public static CheckInput ForFolder(string folder, IVanillaFiles game, IItemVariants? items = null, ushort? player = null,
        ICollectionView? collection = null)
        => new()
        {
            Folder      = folder,
            Fingerprint = MetaFingerprint.Of(folder),
            Meta        = PenumbraModMeta.Load(folder),
            Game       = game,
            Items      = items,
            PlayerRace = player,
            Collection = collection,
        };
}
