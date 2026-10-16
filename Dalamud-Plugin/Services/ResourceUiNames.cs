using System.Text;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;

namespace InstantEdit.Services;

/// <summary>
/// Penumbra's UI names and icons of whole resource trees, kept between snapshots. Asking Penumbra for
/// them re-reads the character's shader packages on every call (about 150 ms and 95 MB on the framework
/// thread), while a tree without them takes a few milliseconds. So trees are fetched without names and
/// named here; only a tree not seen before is fetched again with them.
/// </summary>
/// <remarks>
/// A tree is matched by every node's type, paths and place in the hierarchy. Names are not cached per
/// node: an item's name follows its variant, which only the materials' paths tell apart, so any change
/// to the tree asks Penumbra again. Names are learned again after <see cref="MaxAgeMs"/> and on a
/// manual refresh (<see cref="Forget"/>), since a tree can keep its paths while its names change: one
/// read while a shader package was still loading, or a material edited in place.
/// </remarks>
internal sealed class ResourceUiNames
{
    /// <summary> Trees remembered: the player, minion and mount, through a few outfit changes. </summary>
    private const int Capacity = 16;

    /// <summary> How long learned names are used before Penumbra is asked again. </summary>
    private const long MaxAgeMs = 120_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ((string? Name, ChangedItemIcon Icon)[] Names, long Learned)> _byTree = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary> A tree fetched without UI data, with the names of the same tree seen before; null when it is new. </summary>
    public ResourceTreeDto? Named(ResourceTreeDto tree)
    {
        var signature = Signature(tree);
        (string? Name, ChangedItemIcon Icon)[]? names = null;
        lock (_lock)
            if (_byTree.TryGetValue(signature, out var entry) && Environment.TickCount64 - entry.Learned < MaxAgeMs)
                names = entry.Names;
        if (names is null)
            return null;
        var next = 0;
        return new ResourceTreeDto
        {
            Name = tree.Name,
            RaceCode = tree.RaceCode,
            Nodes = Copy(tree.Nodes)!,
        };

        // Pre-order, like Walk, so each node meets its own name.
        List<ResourceNodeDto>? Copy(List<ResourceNodeDto>? nodes)
            => nodes?.Select(node =>
            {
                var (name, icon) = names[next++];
                return new ResourceNodeDto
                {
                    Type = node.Type,
                    Icon = icon,
                    Name = name,
                    GamePath = node.GamePath,
                    ActualPath = node.ActualPath,
                    ObjectAddress = node.ObjectAddress,
                    ResourceHandle = node.ResourceHandle,
                    Children = Copy(node.Children)!,
                };
            }).ToList();
    }

    /// <summary> Remembers the names of a tree fetched with UI data. </summary>
    public void Learn(ResourceTreeDto tree)
    {
        var signature = Signature(tree);
        var names = Walk(tree.Nodes).Select(node => (node.Name, node.Icon)).ToArray();
        lock (_lock)
        {
            if (!_byTree.ContainsKey(signature))
            {
                _order.Enqueue(signature);
                while (_order.Count > Capacity)
                    _byTree.Remove(_order.Dequeue());
            }
            _byTree[signature] = (names, Environment.TickCount64);
        }
    }

    /// <summary> Drops every learned name, so the next snapshot asks Penumbra for all of them. </summary>
    public void Forget()
    {
        lock (_lock)
        {
            _byTree.Clear();
            _order.Clear();
        }
    }

    /// <summary> Every node's depth, type, paths and child count, in the order <see cref="Walk"/> visits them. </summary>
    internal static string Signature(ResourceTreeDto tree)
    {
        var builder = new StringBuilder(4096);
        Append(tree.Nodes, 0);
        return builder.ToString();

        void Append(List<ResourceNodeDto>? nodes, int depth)
        {
            foreach (var node in nodes ?? [])
            {
                builder.Append(depth).Append('|').Append((int)node.Type).Append('|')
                    .Append(node.GamePath).Append('|').Append(node.ActualPath).Append('|')
                    .Append(node.Children?.Count ?? 0).Append('\n');
                Append(node.Children, depth + 1);
            }
        }
    }

    private static IEnumerable<ResourceNodeDto> Walk(List<ResourceNodeDto>? nodes)
    {
        foreach (var node in nodes ?? [])
        {
            yield return node;
            foreach (var child in Walk(node.Children))
                yield return child;
        }
    }
}
