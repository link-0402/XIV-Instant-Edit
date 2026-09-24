using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary>
/// Which resource kinds the browser shows. With nothing selected it shows every editable
/// kind as a tree; with one or more kinds selected it shows only those, flattened.
/// </summary>
internal sealed class ResourceKindSelection
{
    public ResourceKinds Selected { get; private set; } = ResourceKinds.None;

    /// <summary> True when no explicit kind is selected (the tree view). </summary>
    public bool IsAll => Selected == ResourceKinds.None;

    /// <summary> True when the browser flattens rows to the selected kinds. </summary>
    public bool IsFlat => !IsAll;

    /// <summary> The kinds a row must intersect to be shown. </summary>
    public ResourceKinds Admitted => IsAll ? ResourceKinds.Editable : Selected;

    public bool Contains(ResourceKinds kind) => (Selected & kind) != 0;

    /// <summary> Selects exactly this kind (None returns to the tree view). </summary>
    public void Set(ResourceKinds kind) => Selected = kind;

    public void Clear() => Selected = ResourceKinds.None;

    /// <summary> Adds or removes a kind; removing the last one returns to the tree view. </summary>
    public void Toggle(ResourceKinds kind)
        => Selected = Contains(kind) ? Selected & ~kind : Selected | kind;

    /// <summary> The row itself is one of the admitted kinds. </summary>
    public bool Admits(ResourceView node) => (node.Kinds & Admitted) != 0;

    /// <summary> The row or any descendant is admitted, so the subtree is worth drawing. </summary>
    public bool AdmitsSubtree(ResourceView node) => (node.SubtreeKinds & Admitted) != 0;

    /// <summary> Some descendant is admitted, so the row can expand. </summary>
    public bool AdmitsChild(ResourceView node) => (node.DescendantKinds & Admitted) != 0;
}

/// <summary> The search box: memoised, case-insensitive matching over a row and its descendants. </summary>
internal sealed class ResourceSearch
{
    private readonly Dictionary<ResourceView, bool> _matches = new(ReferenceEqualityComparer.Instance);
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            var text = UiText.Safe(value);
            if (string.Equals(text, _text, StringComparison.Ordinal))
                return;
            _text = text;
            _matches.Clear();
        }
    }

    public bool Active => _text.Length > 0;

    /// <summary> Forgets memoised results, for example after a new snapshot replaced the rows. </summary>
    public void Invalidate() => _matches.Clear();

    public bool ActorIdentityMatches(ActorView actor)
        => actor.Category.Contains(_text, StringComparison.OrdinalIgnoreCase)
        || actor.Name.Contains(_text, StringComparison.OrdinalIgnoreCase);

    /// <summary> Search match for a row or any descendant, memoised per search text and snapshot. </summary>
    public bool Matches(ResourceView node)
    {
        if (!Active)
            return true;
        if (_matches.TryGetValue(node, out var matches))
            return matches;

        var filter = _text;
        matches = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.SourceLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.SourceModName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.SourceRelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.GamePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.ActualPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Children.Any(Matches);
        _matches[node] = matches;
        return matches;
    }
}

/// <summary> Row counts per kind group, recomputed only when the displayed actors change. </summary>
internal sealed class ResourceTypeCounter
{
    private ActorView[] _actors = [];
    private int[] _counts = [];

    public int[] Count(IReadOnlyList<ActorView> actors, IReadOnlyList<ResourceKinds> groups)
    {
        var unchanged = actors.Count == _actors.Length && _counts.Length == groups.Count;
        for (var i = 0; unchanged && i < actors.Count; i++)
            unchanged = ReferenceEquals(actors[i], _actors[i]);
        if (unchanged)
            return _counts;

        var counts = new int[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            counts[i] = actors.SelectMany(x => x.Roots).SelectMany(root => root.Flattened)
                .Count(node => (node.Kinds & group) != 0);
        }

        _actors = actors.ToArray();
        _counts = counts;
        return counts;
    }
}

/// <summary>
/// Which actors, sections and rows are expanded. Rows in the tree view start collapsed and
/// remember what was opened; rows in the flat view start expanded and remember what was closed.
/// </summary>
internal sealed class ExpansionState
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    public bool IsExpanded(string key, bool defaultExpanded, bool forceExpanded = false)
        => forceExpanded || (defaultExpanded ? !_collapsed.Contains(key) : _expanded.Contains(key));

    public void Toggle(string key, bool expanded, bool defaultExpanded)
    {
        var set = defaultExpanded ? _collapsed : _expanded;
        if (expanded)
        {
            if (defaultExpanded) set.Add(key); else set.Remove(key);
        }
        else
        {
            if (defaultExpanded) set.Remove(key); else set.Add(key);
        }
    }

    /// <summary> The stable id of an actor header. </summary>
    public static string ActorKey(OnScreenObject entity)
        => UiText.SafeId($"actor:{entity.Address:X}:{entity.ObjectIndex}");

    /// <summary> The stable id of a section under an actor. </summary>
    public static string SectionKey(string actorKey, ResourceSection section)
        => actorKey + section switch
        {
            ResourceSection.CharacterFeatures => ":features",
            ResourceSection.Gear => ":gear",
            _ => ":other",
        };

    /// <summary> The stable id of a tree row; must not change, or open rows would close on reload. </summary>
    public static string NodeKey(string scope, ResourceView node)
    {
        var type = UiText.Safe(node.Type, "Resource");
        var name = UiText.Safe(node.Name, "Unnamed resource");
        var gamePath = UiText.Safe(node.GamePath);
        var actualPath = UiText.Safe(node.ActualPath);
        return UiText.SafeId($"{scope}:{type}:{name}:{gamePath}:{actualPath}");
    }
}

/// <summary> The Mod Browser's mod list filter, memoised per list and search text. </summary>
internal sealed class ModListFilter
{
    private IReadOnlyList<PenumbraMod>? _source;
    private string _text = string.Empty;
    private IReadOnlyList<PenumbraMod> _result = [];

    public IReadOnlyList<PenumbraMod> Apply(IReadOnlyList<PenumbraMod> mods, string text)
    {
        text = UiText.Safe(text);
        if (ReferenceEquals(mods, _source) && string.Equals(text, _text, StringComparison.Ordinal))
            return _result;

        _source = mods;
        _text = text;
        _result = text.Length == 0
            ? mods
            : mods.Where(mod => mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                             || mod.Directory.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return _result;
    }
}
