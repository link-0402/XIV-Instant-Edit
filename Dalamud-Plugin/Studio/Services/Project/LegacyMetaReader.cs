using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// Reads the layout Penumbra used before meta.json version 4: the mod's descriptive fields in
/// meta.json, its default files in default_mod.json, and one group_NNN_name.json per option group.
/// They are merged into the single-document form, so the rest of the plugin only ever sees one
/// layout. Penumbra migrates such mods itself when it loads them, so only stragglers reach this.
///
/// Groups and options of that era had no ids. They get ids derived from the folder and their names
/// (not random ones), so references made to them stay valid between reads, until the merged form is
/// written and the ids become part of the file.
/// </summary>
internal static class LegacyMetaReader
{
    public const string DefaultModFile = "default_mod.json";

    public static bool HasLegacyLayout(string folder, JObject? meta)
    {
        if (meta != null && (meta["Groups"] != null || meta["DefaultData"] != null))
            return false;   // already the merged form, whatever else lies around
        try
        {
            return File.Exists(Path.Combine(folder, DefaultModFile))
                || Directory.EnumerateFiles(folder, "group_*.json").Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Builds the merged document. <paramref name="legacyFiles"/> lists the split files that went into it.</summary>
    public static JObject Merge(string folder, JObject? meta, out List<string> legacyFiles)
    {
        legacyFiles = new List<string>();
        var root = meta != null ? (JObject)meta.DeepClone() : new JObject();
        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (root["Identifier"] == null)
        {
            var identifier = new JProperty("Identifier", MetaIds.Derive(Guid.Empty, $"legacy/{folderName}").ToString());
            if (root.Property("FileVersion", StringComparison.Ordinal) is { } version)
                version.AddAfterSelf(identifier);
            else
                root.AddFirst(identifier);
        }

        var defaultPath = Path.Combine(folder, DefaultModFile);
        if (File.Exists(defaultPath))
        {
            legacyFiles.Add(DefaultModFile);
            var dd = TryRead(defaultPath);
            if (dd != null)
                root["DefaultData"] = Carrier(dd);
        }

        var groups = new JArray();
        var groupFiles = Directory.EnumerateFiles(folder, "group_*.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var file in groupFiles)
        {
            legacyFiles.Add(file);
            var g = TryRead(Path.Combine(folder, file));
            if (g == null)
                continue;

            // The whole group is kept, every key of it: a combining group's Containers, an IMC group's
            // Identifier and DefaultEntry, whatever a later format added. Only the ids are new.
            var name = (string?)g["Name"] ?? Path.GetFileNameWithoutExtension(file);
            var groupId = MetaIds.Derive(Guid.Empty, $"legacy/{folderName}/{name}");
            var group = (JObject)g.DeepClone();
            if (group["Type"] == null)
                group.AddFirst(new JProperty("Type", "Single"));
            if ((string?)group["Name"] == null)
                group["Name"] = name;
            if (Guid.TryParse(group["Id"]?.ToString(), out var existing))
                groupId = existing;
            else
                SetAfter(group, "Type", "Id", groupId.ToString());

            var options = new JArray();
            if (g["Options"] is JArray legacyOptions)
            {
                for (int i = 0; i < legacyOptions.Count; i++)
                {
                    if (legacyOptions[i] is not JObject o)
                        continue;
                    var optionName = (string?)o["Name"] ?? $"Option {i + 1}";
                    var option = (JObject)o.DeepClone();
                    if (!Guid.TryParse(option["Id"]?.ToString(), out _))
                    {
                        option.Remove("Id");
                        option.AddFirst(new JProperty("Id", MetaIds.Derive(groupId, $"{i}/{optionName}").ToString()));
                    }
                    if ((string?)option["Name"] == null)
                        option["Name"] = optionName;
                    options.Add(option);
                }
            }
            group["Options"] = options;
            groups.Add(group);
        }

        if (groups.Count > 0)
            root["Groups"] = groups;
        return root;
    }

    /// <summary>Sets <paramref name="key"/> right after the <paramref name="after"/> property (first when there is none).</summary>
    private static void SetAfter(JObject target, string after, string key, string value)
    {
        target.Remove(key);
        var property = new JProperty(key, value);
        if (target.Property(after, StringComparison.Ordinal) is { } anchor)
            anchor.AddAfterSelf(property);
        else
            target.AddFirst(property);
    }

    /// <summary>The Files / FileSwaps / Manipulations of a legacy default or option object.</summary>
    private static JObject Carrier(JObject source)
    {
        var carrier = new JObject();
        foreach (var key in new[] { "Files", "FileSwaps", "Manipulations" })
            if (source[key] != null)
                carrier[key] = source[key]!.DeepClone();
        return carrier;
    }

    private static JObject? TryRead(string path)
    {
        try
        {
            return PenumbraModMeta.ParseObject(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;   // a damaged split file contributes nothing; Penumbra would not load it either
        }
    }
}
