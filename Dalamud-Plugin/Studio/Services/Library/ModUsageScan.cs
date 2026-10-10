using System;
using System.Collections.Generic;

namespace InstantEdit.Studio.Services.Library;

/// <summary>The folder names of the mods enabled in at least one collection, and how many collections were read.</summary>
internal sealed record ModUsage(IReadOnlySet<string> Used, int Collections);

/// <summary>
/// Which mods are enabled anywhere. The caller's settings call decides what counts: Penumbra's
/// settings with inheritance on and temporary settings off, so a mod only another plugin turned on
/// for a while counts as unused. Mod Optimizer's usage scan, rebuilt here. Dalamud-free.
/// </summary>
internal static class ModUsageScan
{
    /// <param name="collections">Every collection by id.</param>
    /// <param name="enabledByMod">A collection's mods by folder name with whether each is enabled; null when it can't be read.</param>
    public static ModUsage From(IReadOnlyDictionary<Guid, string> collections, Func<Guid, IReadOnlyDictionary<string, bool>?> enabledByMod)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in collections.Keys)
        {
            if (enabledByMod(collection) is not { } settings)
                continue;
            foreach (var (mod, enabled) in settings)
                if (enabled)
                    used.Add(mod);
        }
        return new ModUsage(used, collections.Count);
    }
}
