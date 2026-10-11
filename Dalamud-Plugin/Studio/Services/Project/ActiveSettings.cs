using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models.Project;

namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// How the open mod is set for the player right now: whether it is enabled in the collection the
/// player uses, and which of its options are picked — so the editor can say which files actually
/// apply. Asked of Penumbra every couple of seconds on the render thread.
/// </summary>
internal sealed class ActiveSettings
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    private readonly PenumbraIpcService _ipc;
    private DateTime _next;
    private string? _folder;
    private HashSet<OptionRef> _active = new();

    public ActiveSettings(PenumbraIpcService ipc) => _ipc = ipc;

    /// <summary>The collection the settings were read from, or null when they are not known.</summary>
    public string? Collection { get; private set; }

    /// <summary>Whether the mod is enabled in that collection; null when not known.</summary>
    public bool? Enabled { get; private set; }

    public void Update(ModProject? project)
    {
        if (project == null)
        {
            Reset();
            _folder = null;
            return;
        }

        if (!string.Equals(_folder, project.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            _folder = project.FolderName;
            _next = DateTime.MinValue;
        }

        if (DateTime.UtcNow < _next)
            return;
        _next = DateTime.UtcNow + Every;

        if (!_ipc.IsAvailable || _ipc.PlayerCollection() is not { } collection)
        {
            Reset();
            return;
        }

        var settings = _ipc.ModSettings(collection.Id, project.FolderName);
        if (settings == null)
        {
            Reset();
            return;
        }

        Collection = collection.Name;
        Enabled = settings.Value.Enabled;
        _active = Map(project.Meta, settings.Value.Options);
    }

    /// <summary>
    /// Whether files in <paramref name="option"/> apply to the player now: the default files while the
    /// mod is enabled, an option while it is also picked. Null when that is not known.
    /// </summary>
    public bool? IsOn(OptionRef option)
    {
        if (Enabled is not { } enabled)
            return null;
        return option.IsDefault ? enabled : enabled && _active.Contains(option);
    }

    private void Reset()
    {
        Collection = null;
        Enabled = null;
        _active = new HashSet<OptionRef>();
    }

    /// <summary>Penumbra names groups and options by name; the editor by id (a Combining group by container).</summary>
    internal static HashSet<OptionRef> Map(PenumbraModMeta meta, Dictionary<string, List<string>> picked)
    {
        var result = new HashSet<OptionRef>();
        foreach (var group in meta.Groups)
        {
            picked.TryGetValue(group.Name, out var names);

            // A Combining group always applies one container: the one of the picked options, or the
            // first ("c#0") when nothing is picked.
            if (group.Type == ModGroupType.Combining)
            {
                int mask = 0;
                for (int i = 0; names != null && i < group.Options.Count && i < 31; i++)
                    if (names.Contains(group.Options[i].Name, StringComparer.Ordinal))
                        mask |= 1 << i;
                result.Add(new OptionRef(group.Id, $"c#{mask}"));
                continue;
            }

            if (names == null || names.Count == 0)
                continue;

            foreach (var option in group.Options.Where(o => names.Contains(o.Name, StringComparer.Ordinal)))
                result.Add(option.Ref);
        }
        return result;
    }
}
