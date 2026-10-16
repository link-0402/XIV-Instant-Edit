using System;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Animation;

/// <summary>What the Build window can replace of an action timeline, and what its row is called.</summary>
internal static class EmoteCatalog
{
    private const string FacialPrefix = "facial/pose/";

    /// <summary>
    /// A timeline as the browser and set-up list it. Body timelines whose PAP sits in a race's
    /// bt_common folder (load types 0 and 2) can be replaced; an expression's face lives per face,
    /// and load type 1 timelines per weapon stance, so those are listed but not offered.
    /// </summary>
    public static AnimationTimelineRef Ref(ushort id, string key, byte loadType)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new AnimationTimelineRef(id, key, "?", false, "This timeline has no animation file.");
        if (key.StartsWith(FacialPrefix, StringComparison.Ordinal))
            return new AnimationTimelineRef(id, key, LabelFor(key), false, "Face animations live per face; not supported yet.");
        return loadType switch
        {
            0 or 2 => new AnimationTimelineRef(id, key, LabelFor(key), true, null),
            1      => new AnimationTimelineRef(id, key, LabelFor(key), false, "Weapon-specific animation; not supported yet."),
            _      => new AnimationTimelineRef(id, key, LabelFor(key), false, "Not a body animation."),
        };
    }

    /// <summary>"Start", "Loop" or "End" for an emote's parts, else the key's last part.</summary>
    public static string LabelFor(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..];
        if (name.EndsWith("_start", StringComparison.Ordinal)) return "Start";
        if (name.EndsWith("_loop", StringComparison.Ordinal))  return "Loop";
        if (name.EndsWith("_end", StringComparison.Ordinal))   return "End";
        return name;
    }
}
