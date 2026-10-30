using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InstantEdit.Studio.Services.Library;

/// <summary>A mod by folder name and the name Penumbra lists it under.</summary>
internal readonly record struct ModRef(string Folder, string Name);

/// <summary>
/// The game items the player's collection already changes, and the mods that change each one, for
/// the item browser's red tiles and its free set finder. Read on a worker from Penumbra: the items the
/// collection changes (its enabled mods' active options), and each enabled mod's changed items for
/// the names. Read again a moment after a mod or its settings change, and when the player's collection
/// does. Dalamud-free; the Penumbra calls come in as delegates and run on the worker.
/// </summary>
internal sealed class OccupiedItems
{
    /// <summary>How long settings must stay unchanged before they are read again (sliders send many changes).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>How long after Penumbra gave no answer it is asked again.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyList<ModRef> None = Array.Empty<ModRef>();

    private readonly Func<Guid, IReadOnlyCollection<string>?> _collectionItems;
    private readonly Func<Guid, IReadOnlyDictionary<string, bool>?> _enabledByMod;
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>>?> _modItems;
    private readonly Func<IReadOnlyDictionary<string, string>?> _modNames;
    private readonly Func<DateTime> _now;

    private long _generation;
    private long _changedAt;
    private (Guid Collection, long Generation)? _readFor;
    private DateTime _retryAt;
    private Task _running = Task.CompletedTask;
    private volatile Snapshot? _snapshot;
    private volatile bool _unknown;
    private int _revision;

    /// <summary>What was read for one collection: each changed item's mods by item name (see <see cref="Key"/>).</summary>
    internal sealed record Snapshot(Guid Collection, string CollectionName, IReadOnlyDictionary<string, IReadOnlyList<ModRef>> ByItem);

    /// <param name="collectionItems">The names of the items a collection changes; null when Penumbra can't say.</param>
    /// <param name="enabledByMod">A collection's mods by folder name and whether each is enabled.</param>
    /// <param name="modItems">Every mod's changed items by folder name.</param>
    /// <param name="modNames">Every mod's name by folder name.</param>
    /// <param name="now">The clock; tests set it.</param>
    public OccupiedItems(Func<Guid, IReadOnlyCollection<string>?> collectionItems, Func<Guid, IReadOnlyDictionary<string, bool>?> enabledByMod,
        Func<IReadOnlyDictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>>?> modItems, Func<IReadOnlyDictionary<string, string>?> modNames,
        Func<DateTime>? now = null)
    {
        _collectionItems = collectionItems;
        _enabledByMod    = enabledByMod;
        _modItems        = modItems;
        _modNames        = modNames;
        _now             = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Whether the player's collection has been read.</summary>
    public bool Ready => _snapshot != null;

    /// <summary>Penumbra gave no answer the last time it was asked.</summary>
    public bool Unknown => _unknown;

    /// <summary>The collection last read, or empty.</summary>
    public string CollectionName => _snapshot?.CollectionName ?? string.Empty;

    /// <summary>Goes up each time a read publishes, so a view knows to look again.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>The running read, or a finished task (tests wait on it).</summary>
    internal Task Idle => _running;

    /// <summary>A mod, or a mod's settings, changed: read again once things settle. Any thread.</summary>
    public void Invalidate()
    {
        Interlocked.Exchange(ref _changedAt, _now().Ticks);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Starts a read of <paramref name="collection"/> when it hasn't been read since the last change,
    /// or is not the collection read last. Called every frame the browser shows, from the thread that draws it.
    /// </summary>
    public void Need((Guid Id, string Name)? collection)
    {
        if (!_running.IsCompleted)
            return;
        if (collection is not { } asked)
        {
            _snapshot = null;
            _readFor = null;
            return;
        }

        var generation = Interlocked.Read(ref _generation);
        if (_readFor == (asked.Id, generation))
            return;
        var now = _now();
        bool sameCollection = _readFor?.Collection == asked.Id;
        if (sameCollection && now - new DateTime(Interlocked.Read(ref _changedAt)) < Settle)
            return;
        if (_unknown && sameCollection && now < _retryAt)
            return;

        if (!sameCollection)
            _snapshot = null;   // another collection's items aren't shown while this one is read
        _running = Task.Run(() => Read(asked, generation));
    }

    private void Read((Guid Id, string Name) collection, long generation)
    {
        try
        {
            if (collection.Id == Guid.Empty)
            {
                // Penumbra's empty collection ("None") changes nothing.
                Publish(new Snapshot(collection.Id, collection.Name, new Dictionary<string, IReadOnlyList<ModRef>>()), collection.Id, generation);
                return;
            }
            var enabled = _enabledByMod(collection.Id);
            var mods = _modItems();
            if (enabled == null || mods == null)
            {
                Failed(collection.Id);
                return;
            }
            var byItem = Build(_collectionItems(collection.Id), enabled, mods, _modNames());
            Publish(new Snapshot(collection.Id, collection.Name, byItem), collection.Id, generation);
        }
        catch (Exception)
        {
            Failed(collection.Id);
        }
    }

    /// <summary>No answer: what was shown stays, and the same collection is asked again after <see cref="Retry"/>.</summary>
    private void Failed(Guid collection)
    {
        _unknown = true;
        _retryAt = _now() + Retry;
        _readFor = (collection, -1);   // no generation matches, so a later Need reads again
        Interlocked.Increment(ref _revision);
    }

    private void Publish(Snapshot snapshot, Guid collection, long generation)
    {
        _snapshot = snapshot;
        _unknown = false;
        _readFor = (collection, generation);
        Interlocked.Increment(ref _revision);
    }

    /// <summary>
    /// Each changed item's mods, by item name: the enabled mods that list it among their changed items.
    /// With the collection's own list (<paramref name="collectionItems"/>), only what it changes counts,
    /// so an item only an option that is off touches stays free. Mods are sorted by name.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<ModRef>> Build(IReadOnlyCollection<string>? collectionItems,
        IReadOnlyDictionary<string, bool> enabled, IReadOnlyDictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>> modItems,
        IReadOnlyDictionary<string, string>? modNames)
    {
        var changed = collectionItems?.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byItem = new Dictionary<string, List<ModRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, items) in modItems)
        {
            if (!enabled.TryGetValue(folder, out var on) || !on)
                continue;
            var mod = new ModRef(folder, modNames != null && modNames.TryGetValue(folder, out var name) && !string.IsNullOrWhiteSpace(name) ? name : folder);
            foreach (var (itemName, kind) in items)
            {
                if (kind != ChangeKind.Item)
                    continue;
                var key = Key(itemName);
                if (key.Length == 0 || changed != null && !changed.Contains(key))
                    continue;
                if (!byItem.TryGetValue(key, out var list))
                    byItem[key] = list = new List<ModRef>();
                if (!list.Contains(mod))
                    list.Add(mod);
            }
        }
        return byItem.ToDictionary(p => p.Key, p => (IReadOnlyList<ModRef>)p.Value.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The mods other than <paramref name="except"/> (a folder name) that change an item, by its name; empty when none.</summary>
    public IReadOnlyList<ModRef> ModsChanging(string itemName, string? except = null)
    {
        if (_snapshot is not { } snapshot || !snapshot.ByItem.TryGetValue(Key(itemName), out var mods))
            return None;
        if (except == null || !mods.Any(m => string.Equals(m.Folder, except, StringComparison.OrdinalIgnoreCase)))
            return mods;
        return mods.Where(m => !string.Equals(m.Folder, except, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>Whether a mod other than <paramref name="except"/> changes an item.</summary>
    public bool IsChanged(string itemName, string? except = null) => ModsChanging(itemName, except).Count > 0;

    /// <summary>
    /// An item name as matched against Penumbra's: without soft hyphens, non-breaking spaces as spaces.
    /// Returns the name itself (no copy) when it has neither.
    /// </summary>
    internal static string Key(string name)
        => name.Replace("­", string.Empty).Replace(' ', ' ').Trim();
}
