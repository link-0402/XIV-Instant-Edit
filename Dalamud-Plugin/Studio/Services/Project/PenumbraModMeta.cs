using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Project;

/// <summary>One file of a mod: the game path it replaces, the file inside the mod folder, and which option carries it.</summary>
internal sealed record ModFileEntry(string GamePath, string RelPath, OptionRef Option, bool IsAbsolute);

/// <summary>One file swap of a mod: a game path redirected to another game path.</summary>
internal sealed record ModSwapEntry(string GamePath, string TargetGamePath, OptionRef Option);

internal enum ModGroupType { Single, Multi, Imc, Combining, Unknown }

/// <summary>One option of a group, or one file set of a Combining group, as the editor lists it.</summary>
internal sealed record ModOptionView(OptionRef Ref, string Name, int Index);

/// <summary>
/// One option group of a mod. <see cref="Options"/> are what the user picks between;
/// <see cref="FileSets"/> are what actually carries files — the options themselves, except for a
/// Combining group, whose files sit in one container per combination of its options.
/// </summary>
internal sealed record ModGroupView(string Id, string Name, ModGroupType Type, int Index,
    IReadOnlyList<ModOptionView> Options, IReadOnlyList<ModOptionView> FileSets);

/// <summary>How a meta.json is laid out, so a rewrite only changes what was edited.</summary>
internal sealed record JsonStyle(char IndentChar, int Indentation, string NewLine, bool TrailingNewLine)
{
    /// <summary>What Penumbra writes: tabs, LF, no newline at the end.</summary>
    public static readonly JsonStyle Penumbra = new('\t', 1, "\n", false);

    public static JsonStyle Detect(string text)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        char indentChar = '\t';
        int indentation = 1;
        int at = text.IndexOf('\n');
        if (at >= 0 && at + 1 < text.Length)
        {
            if (text[at + 1] == ' ')
            {
                indentChar = ' ';
                int n = 0;
                while (at + 1 + n < text.Length && text[at + 1 + n] == ' ') n++;
                indentation = Math.Max(1, n);
            }
        }
        return new JsonStyle(indentChar, indentation, newLine, text.EndsWith('\n'));
    }
}

/// <summary>
/// A Penumbra mod's meta.json, held as a JSON document so that everything this plugin does not
/// edit — option groups it never touches, manipulations of every kind, tags, the mod image, keys a
/// newer Penumbra adds — is written back exactly as it was read. Typed accessors cover what the
/// editor reads and writes; everything else stays in <see cref="Root"/>.
///
/// Not thread-safe: the render thread owns it, and a build works on a <see cref="Clone"/>.
/// </summary>
internal sealed class PenumbraModMeta
{
    public const string FileName     = "meta.json";
    public const string BackupSuffix = ".instant-edit-backup";

    private const string DefaultDataKey = "DefaultData";
    private const string GroupsKey      = "Groups";

    private readonly JsonStyle _style;
    private IReadOnlyList<ModGroupView>? _groups;

    private PenumbraModMeta(JObject root, JsonStyle style, bool wasLegacy, IReadOnlyList<string> legacyFiles)
    {
        Root        = root;
        _style      = style;
        WasLegacy   = wasLegacy;
        LegacyFiles = legacyFiles;
    }

    /// <summary>The document itself. Edit it through the methods below, which keep the cached views current.</summary>
    public JObject Root { get; }

    /// <summary>Read from the old split layout (meta.json + default_mod.json + group_*.json); saving writes one meta.json.</summary>
    public bool WasLegacy { get; private set; }

    /// <summary>The split-layout files merged in, renamed aside once the merged form has been written.</summary>
    public IReadOnlyList<string> LegacyFiles { get; private set; }

    /// <summary>Bumped on every edit, so views and the resource graph know to refresh.</summary>
    public int Revision { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Loading and saving
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Reads a mod folder's meta.json (merging the legacy split layout if that is what it has).</summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object.</exception>
    public static PenumbraModMeta Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        JObject? root = null;
        var style = JsonStyle.Penumbra;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            root  = ParseObject(text);
            style = JsonStyle.Detect(text);
        }

        if (LegacyMetaReader.HasLegacyLayout(folder, root))
        {
            var merged = LegacyMetaReader.Merge(folder, root, out var legacyFiles);
            return new PenumbraModMeta(merged, style, true, legacyFiles);
        }

        return new PenumbraModMeta(root ?? new JObject(), style, false, Array.Empty<string>());
    }

    /// <summary>Parses meta.json text without touching the disk (round-trip checks, tests).</summary>
    public static PenumbraModMeta Parse(string json) => new(ParseObject(json), JsonStyle.Detect(json), false, Array.Empty<string>());

    /// <summary>A brand-new mod's meta.json, laid out the way Penumbra writes one.</summary>
    public static PenumbraModMeta CreateNew(Guid identifier, string name, string author, string version, string website, string description = "")
    {
        var root = new JObject
        {
            ["FileVersion"] = 4,
            ["Identifier"]  = identifier.ToString(),
            ["LastWrite"]   = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["Name"]        = name,
        };
        if (!string.IsNullOrWhiteSpace(author))      root["Author"]      = author;
        if (!string.IsNullOrWhiteSpace(description)) root["Description"] = description;
        if (!string.IsNullOrWhiteSpace(version))     root["Version"]     = version;
        if (!string.IsNullOrWhiteSpace(website))     root["Website"]     = website;
        return new PenumbraModMeta(root, JsonStyle.Penumbra, false, Array.Empty<string>());
    }

    /// <summary>
    /// Parses JSON the way it has to be kept: dates stay strings (so their spelling survives) and
    /// fractions stay decimals (so "1.0" is written back as "1.0", not "1").
    /// </summary>
    internal static JObject ParseObject(string text)
    {
        using var sr = new StringReader(text);
        using var reader = new JsonTextReader(sr)
        {
            DateParseHandling  = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
        };
        var token = JToken.ReadFrom(reader, new JsonLoadSettings
        {
            CommentHandling    = CommentHandling.Ignore,
            LineInfoHandling   = LineInfoHandling.Ignore,
        });
        return token as JObject ?? throw new InvalidDataException("not a JSON object");
    }

    /// <summary>The document as text, in the layout it was read with.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder(4096);
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture) { NewLine = _style.NewLine })
        using (var jw = new JsonTextWriter(sw)
               {
                   Formatting  = Formatting.Indented,
                   IndentChar  = _style.IndentChar,
                   Indentation = _style.Indentation,
               })
        {
            Root.WriteTo(jw);
        }
        if (_style.TrailingNewLine)
            sb.Append(_style.NewLine);
        return sb.ToString();
    }

    /// <summary>
    /// Writes meta.json into <paramref name="folder"/>: to a temporary file first, then moved over the
    /// old one, so a crash never leaves half a file. With <paramref name="backupFirst"/>, the file
    /// as it was is kept beside it (once — an existing backup is never overwritten).
    /// </summary>
    public void Save(string folder, bool backupFirst)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        if (backupFirst && File.Exists(path) && !File.Exists(path + BackupSuffix))
            File.Copy(path, path + BackupSuffix);

        if (WasLegacy)
            Root["FileVersion"] = 4;
        SetOrInsert(Root, "LastWrite", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "Name");

        var tmp = path + ".instant-edit-tmp";
        File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);

        if (WasLegacy)
        {
            // The merged form now says everything the split files did; keep them aside rather than
            // deleting them, so nothing is lost if something read them differently.
            foreach (var file in LegacyFiles)
            {
                var full = Path.Combine(folder, file);
                try
                {
                    if (File.Exists(full) && !File.Exists(full + BackupSuffix))
                        File.Move(full, full + BackupSuffix);
                }
                catch (IOException) { /* left in place: Penumbra reads meta.json's groups first */ }
            }
            WasLegacy   = false;
            LegacyFiles = Array.Empty<string>();
        }
    }

    public PenumbraModMeta Clone()
        => new((JObject)Root.DeepClone(), _style, WasLegacy, LegacyFiles.ToList()) { Revision = Revision };

    // ─────────────────────────────────────────────────────────────────────────
    // Descriptive fields
    // ─────────────────────────────────────────────────────────────────────────

    public string Name        { get => Str("Name");        set => SetField("Name", value); }
    public string Author      { get => Str("Author");      set => SetField("Author", value); }
    public string Description { get => Str("Description"); set => SetField("Description", value); }
    public string Version     { get => Str("Version");     set => SetField("Version", value); }
    public string Website     { get => Str("Website");     set => SetField("Website", value); }

    /// <summary>The mod's preview image, relative to the mod folder ("" for none).</summary>
    public string Image       { get => Str("Image");       set => SetField("Image", value); }

    public Guid? Identifier
    {
        get => Guid.TryParse(Str("Identifier"), out var id) ? id : null;
        set
        {
            if (value is { } id) SetOrInsert(Root, "Identifier", id.ToString(), "Name");
            else Root.Remove("Identifier");
            Touch();
        }
    }

    public List<string> Tags
    {
        get => Root["ModTags"] is JArray tags ? tags.Select(t => t.ToString()).Where(t => t.Length > 0).ToList() : new List<string>();
        set
        {
            if (value.Count == 0 && Root["ModTags"] == null)
                return;
            SetOrInsert(Root, "ModTags", new JArray(value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray<object>()), DefaultDataKey, GroupsKey);
            Touch();
        }
    }

    private string Str(string key) => Root[key]?.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float
        ? Root[key]!.ToString()
        : string.Empty;

    /// <summary>
    /// Sets a top-level text field. Penumbra leaves empty fields out, so an empty value is not added
    /// where there was none; a field that exists is kept and emptied rather than removed.
    /// </summary>
    private void SetField(string key, string value)
    {
        if (Root[key] == null && string.IsNullOrEmpty(value))
            return;
        if (Root[key] is JValue existing && existing.Type == JTokenType.String && (string?)existing.Value == value)
            return;
        SetOrInsert(Root, key, value, DefaultDataKey, GroupsKey, "ModTags");
        Touch();
    }

    /// <summary>The value of a top-level key as JSON, for re-applying an edit onto a freshly read copy.</summary>
    public JToken? GetTopLevel(string key) => Root[key]?.DeepClone();

    public void SetTopLevel(string key, JToken? value)
    {
        if (value == null) Root.Remove(key);
        else SetOrInsert(Root, key, value.DeepClone(), DefaultDataKey, GroupsKey, "ModTags");
        Touch();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Groups and options
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ModGroupView> Groups => _groups ??= BuildGroupViews();

    private List<ModGroupView> BuildGroupViews()
    {
        var result = new List<ModGroupView>();
        if (Root[GroupsKey] is not JArray groups)
            return result;

        for (int gi = 0; gi < groups.Count; gi++)
        {
            if (groups[gi] is not JObject g)
                continue;
            var gid  = GroupId(g, gi);
            var name = (string?)g["Name"] is { Length: > 0 } n ? n : $"Group {gi + 1}";
            var type = ParseType((string?)g["Type"]);

            var options = new List<ModOptionView>();
            if (g["Options"] is JArray opts)
                for (int oi = 0; oi < opts.Count; oi++)
                    if (opts[oi] is JObject o)
                        options.Add(new ModOptionView(new OptionRef(gid, OptionId(o, oi)),
                            (string?)o["Name"] is { Length: > 0 } on ? on : $"Option {oi + 1}", oi));

            var fileSets = options;
            if (type == ModGroupType.Combining)
            {
                fileSets = new List<ModOptionView>();
                if (g["Containers"] is JArray containers)
                    for (int ci = 0; ci < containers.Count; ci++)
                        fileSets.Add(new ModOptionView(new OptionRef(gid, $"c#{ci}"), ContainerName(options, ci), ci));
            }
            else if (type == ModGroupType.Imc)
            {
                fileSets = new List<ModOptionView>();   // Imc options toggle attributes; they carry no files
            }

            result.Add(new ModGroupView(gid, name, type, gi, options, fileSets));
        }
        return result;
    }

    /// <summary>A Combining group's container is the combination of the options whose bits make up its index.</summary>
    private static string ContainerName(IReadOnlyList<ModOptionView> options, int index)
    {
        if (index == 0)
            return "(none)";
        var names = new List<string>();
        for (int bit = 0; bit < options.Count && bit < 31; bit++)
            if ((index & (1 << bit)) != 0)
                names.Add(options[bit].Name);
        return names.Count > 0 ? string.Join(" + ", names) : $"Combination {index}";
    }

    private static ModGroupType ParseType(string? type) => type switch
    {
        "Single"    => ModGroupType.Single,
        "Multi"     => ModGroupType.Multi,
        "Imc"       => ModGroupType.Imc,
        "Combining" => ModGroupType.Combining,
        _           => ModGroupType.Unknown,
    };

    private static string GroupId(JObject g, int index)
        => (string?)g["Id"] is { Length: > 0 } id ? id : $"#{index}";

    private static string OptionId(JObject o, int index)
        => (string?)o["Id"] is { Length: > 0 } id ? id : $"#{index}";

    /// <summary>"Default files", or "Group › Option".</summary>
    public string OptionLabel(OptionRef option)
    {
        if (option.IsDefault)
            return "Default files";
        var group = FindGroupView(option.Group);
        if (group == null)
            return "(missing option)";
        var set = group.FileSets.FirstOrDefault(o => o.Ref == option) ?? group.Options.FirstOrDefault(o => o.Ref == option);
        return set == null ? $"{group.Name} › (missing)" : $"{group.Name} › {set.Name}";
    }

    /// <summary>Just the option's own name ("Default" for the default files).</summary>
    public string OptionName(OptionRef option)
    {
        if (option.IsDefault)
            return "Default";
        var group = FindGroupView(option.Group);
        var set = group?.FileSets.FirstOrDefault(o => o.Ref == option) ?? group?.Options.FirstOrDefault(o => o.Ref == option);
        return set?.Name ?? "(missing)";
    }

    public ModGroupView? FindGroupView(string groupId)
        => Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an option (or the default files) still exists, i.e. can carry files.</summary>
    public bool OptionExists(OptionRef option) => option.IsDefault || Carrier(option, create: false) != null;

    /// <summary>Every place a file can go: the default files, then each option (or Combining container) in order.</summary>
    public IEnumerable<OptionRef> FileCarriers()
    {
        yield return OptionRef.Default;
        foreach (var g in Groups)
            foreach (var set in g.FileSets)
                yield return set.Ref;
    }

    private JObject? FindGroupObject(string groupId)
    {
        if (Root[GroupsKey] is not JArray groups)
            return null;
        for (int gi = 0; gi < groups.Count; gi++)
            if (groups[gi] is JObject g && string.Equals(GroupId(g, gi), groupId, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }

    /// <summary>The object that holds an option's Files/FileSwaps/Manipulations: DefaultData, an option, or a container.</summary>
    private JObject? Carrier(OptionRef option, bool create)
    {
        if (option.IsDefault)
        {
            if (Root[DefaultDataKey] is JObject dd)
                return dd;
            if (!create)
                return null;
            var created = new JObject();
            SetOrInsert(Root, DefaultDataKey, created, GroupsKey);
            return created;
        }

        var group = FindGroupObject(option.Group);
        if (group == null)
            return null;

        if (option.Option.StartsWith("c#", StringComparison.Ordinal))
        {
            return group["Containers"] is JArray containers
                && int.TryParse(option.Option.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ci)
                && ci >= 0 && ci < containers.Count
                    ? containers[ci] as JObject
                    : null;
        }

        if (group["Options"] is not JArray options)
            return null;
        for (int oi = 0; oi < options.Count; oi++)
            if (options[oi] is JObject o && string.Equals(OptionId(o, oi), option.Option, StringComparison.OrdinalIgnoreCase))
                return o;
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Files and swaps
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Every file of the mod, in every option: one entry per (option, game path).</summary>
    public IEnumerable<ModFileEntry> AllFiles()
    {
        foreach (var option in FileCarriers())
        {
            if (Carrier(option, create: false)?["Files"] is not JObject files)
                continue;
            foreach (var p in files.Properties())
            {
                if (p.Value.Type != JTokenType.String)
                    continue;
                var rel = (string)p.Value!;
                yield return new ModFileEntry(p.Name, rel, option, PathNormalizer.IsAbsolute(rel));
            }
        }
    }

    public IEnumerable<ModSwapEntry> AllSwaps()
    {
        foreach (var option in FileCarriers())
        {
            if (Carrier(option, create: false)?["FileSwaps"] is not JObject swaps)
                continue;
            foreach (var p in swaps.Properties())
                if (p.Value.Type == JTokenType.String)
                    yield return new ModSwapEntry(p.Name, (string)p.Value!, option);
        }
    }

    /// <summary>Counts of the manipulations in each option, by type, for the metadata summary.</summary>
    public IEnumerable<(OptionRef Option, string Type, int Count)> ManipulationCounts()
    {
        foreach (var option in FileCarriers())
        {
            if (Carrier(option, create: false)?["Manipulations"] is not JArray manips)
                continue;
            foreach (var g in manips.OfType<JObject>().GroupBy(m => (string?)m["Type"] ?? "?"))
                yield return (option, g.Key, g.Count());
        }
    }

    public bool TryGetFile(OptionRef option, string gamePath, out string relPath)
    {
        relPath = string.Empty;
        if (Carrier(option, create: false)?["Files"] is not JObject files)
            return false;
        var prop = FindProperty(files, gamePath);
        if (prop?.Value.Type != JTokenType.String)
            return false;
        relPath = (string)prop.Value!;
        return true;
    }

    /// <summary>Points <paramref name="gamePath"/> at <paramref name="relPath"/> in an option. Throws if the option no longer exists.</summary>
    public void SetFile(OptionRef option, string gamePath, string relPath)
    {
        var carrier = Carrier(option, create: true) ?? throw new InvalidOperationException($"Option {option} does not exist in this mod.");
        var files = carrier["Files"] as JObject;
        if (files == null)
        {
            files = new JObject();
            SetOrInsert(carrier, "Files", files, "FileSwaps", "Manipulations");
        }

        var existing = FindProperty(files, gamePath);
        if (existing != null)
        {
            if (existing.Value.Type == JTokenType.String && (string?)existing.Value == relPath)
                return;
            existing.Value = relPath;
        }
        else
        {
            files.Add(gamePath, relPath);
        }
        Touch();
    }

    public bool RemoveFile(OptionRef option, string gamePath)
    {
        if (Carrier(option, create: false)?["Files"] is not JObject files)
            return false;
        var prop = FindProperty(files, gamePath);
        if (prop == null)
            return false;
        prop.Remove();
        Touch();
        return true;
    }

    /// <summary>Every rel path any option points at, as comparison keys — what a file on disk must not be deleted under.</summary>
    public HashSet<string> ReferencedRelKeys()
        => AllFiles().Where(f => !f.IsAbsolute).Select(f => PathNormalizer.RelKey(f.RelPath)).ToHashSet(StringComparer.Ordinal);

    private static JProperty? FindProperty(JObject files, string gamePath)
    {
        var exact = files.Property(gamePath, StringComparison.Ordinal);
        if (exact != null)
            return exact;
        var key = PathNormalizer.GamePath(gamePath);
        return files.Properties().FirstOrDefault(p => PathNormalizer.GamePath(p.Name) == key);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Groups this plugin writes
    // ─────────────────────────────────────────────────────────────────────────

    public bool HasGroup(string id) => FindGroupObject(id) != null;

    /// <summary>
    /// Adds a group, or replaces the one with the same id. Fields a user may have changed in
    /// Penumbra's own editor — description, priority, page, which option is the default — are kept
    /// from the group being replaced.
    /// </summary>
    public void UpsertGroup(JObject group)
    {
        var id = (string?)group["Id"] ?? throw new ArgumentException("A group needs an Id.", nameof(group));
        if (Root[GroupsKey] is not JArray groups)
        {
            groups = new JArray();
            SetOrInsert(Root, GroupsKey, groups);
        }

        var existing = FindGroupObject(id);
        if (existing == null)
        {
            groups.Add(group);
        }
        else
        {
            foreach (var keep in new[] { "Description", "Priority", "Page", "DefaultSettings" })
                if (existing[keep] != null)
                    group[keep] = existing[keep]!.DeepClone();
            existing.Replace(group);
        }
        Touch();
    }

    public bool RemoveGroup(string id)
    {
        var existing = FindGroupObject(id);
        if (existing == null)
            return false;
        existing.Remove();
        Touch();
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Groups and options the user shapes
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>How many options Penumbra lets a group of this type have (a Multi group's choices are bits of one number).</summary>
    public static int MaxOptions(ModGroupType type) => type switch
    {
        ModGroupType.Single => int.MaxValue,
        ModGroupType.Multi  => 32,
        _                   => 0,
    };

    /// <summary>Whether options can be added to a group here: Single and Multi groups, while there is room.</summary>
    public static bool TakesOptions(ModGroupView group) => group.Options.Count < MaxOptions(group.Type);

    /// <summary>Why no option can be added to a group, or null when one can.</summary>
    public static string? WhyNoNewOption(ModGroupView group) => group.Type switch
    {
        ModGroupType.Single or ModGroupType.Multi when TakesOptions(group) => null,
        ModGroupType.Multi     => $"A multi-select group can have at most {MaxOptions(ModGroupType.Multi)} options.",
        ModGroupType.Imc       => "An IMC group switches attributes on and off; it carries no files.",
        ModGroupType.Combining => "A combining group's files belong to combinations of its options — add options to it in Penumbra.",
        _                      => "This kind of group is not known here.",
    };

    /// <summary>Adds an empty group at the end, laid out the way Penumbra writes one; returns its id.</summary>
    public string AddGroup(string name, ModGroupType type)
    {
        if (type is not (ModGroupType.Single or ModGroupType.Multi))
            throw new ArgumentException("Only single- and multi-select groups are made here.", nameof(type));
        if (Root[GroupsKey] is not JArray groups)
        {
            groups = new JArray();
            SetOrInsert(Root, GroupsKey, groups);
        }

        var id = Guid.NewGuid().ToString();
        groups.Add(new JObject
        {
            ["Type"]    = type == ModGroupType.Single ? "Single" : "Multi",
            ["Id"]      = id,
            ["Name"]    = name,
            ["Options"] = new JArray(),
        });
        Touch();
        return id;
    }

    /// <summary>
    /// Adds an empty option at the end of a Single or Multi group and returns it, or null when the group
    /// is gone or has no room. It gets an id of its own when the group's options have ids (as Penumbra
    /// gives them since meta.json version 4); otherwise it is addressed by position like the others.
    /// </summary>
    public OptionRef? AddOption(string groupId, string name)
    {
        if (FindGroupObject(groupId) is not { } group || FindGroupView(groupId) is not { } view || !TakesOptions(view))
            return null;
        if (group["Options"] is not JArray options)
        {
            options = new JArray();
            SetOrInsert(group, "Options", options);
        }

        bool ids = options.Count > 0 ? options.OfType<JObject>().Any(o => o["Id"] != null) : group["Id"] != null;
        var option = new JObject();
        if (ids)
            option["Id"] = Guid.NewGuid().ToString();
        option["Name"] = name;
        options.Add(option);
        Touch();
        return new OptionRef(view.Id, OptionId(option, options.Count - 1));
    }

    public bool RenameGroup(string groupId, string name)
    {
        if (FindGroupObject(groupId) is not { } group || (string?)group["Name"] == name)
            return false;
        SetOrInsert(group, "Name", name, "Description", "Options");
        Touch();
        return true;
    }

    public bool RenameOption(OptionRef option, string name)
    {
        if (option.IsDefault || option.Option.StartsWith("c#", StringComparison.Ordinal)
            || Carrier(option, create: false) is not { } carrier || (string?)carrier["Name"] == name)
            return false;
        SetOrInsert(carrier, "Name", name, "Description", "Files", "FileSwaps", "Manipulations");
        Touch();
        return true;
    }

    /// <summary>The description Penumbra shows for an option ("" for none).</summary>
    public string OptionDescription(OptionRef option)
        => option.IsDefault || option.Option.StartsWith("c#", StringComparison.Ordinal) ? string.Empty
         : (string?)Carrier(option, create: false)?["Description"] ?? string.Empty;

    public bool SetOptionDescription(OptionRef option, string description)
    {
        if (option.IsDefault || option.Option.StartsWith("c#", StringComparison.Ordinal) || Carrier(option, create: false) is not { } carrier)
            return false;
        if (((string?)carrier["Description"] ?? string.Empty) == description)
            return false;
        if (description.Length == 0 && carrier["Description"] != null) carrier.Remove("Description");
        else SetOrInsert(carrier, "Description", description, "Priority", "Files", "FileSwaps", "Manipulations");
        Touch();
        return true;
    }

    /// <summary>
    /// Takes an option of a Single or Multi group out, keeping which options are picked by default
    /// pointing at the same ones. <paramref name="moved"/> says where each option that stayed is
    /// addressed now: one addressed by position moves up when an option before it goes.
    /// </summary>
    public bool RemoveOption(OptionRef option, out Dictionary<OptionRef, OptionRef> moved)
    {
        moved = new Dictionary<OptionRef, OptionRef>();
        if (option.IsDefault || FindGroupObject(option.Group) is not { } group || FindGroupView(option.Group) is not { } view
            || view.Type is not (ModGroupType.Single or ModGroupType.Multi) || group["Options"] is not JArray options)
            return false;
        var index = view.Options.FirstOrDefault(o => o.Ref == option)?.Index ?? -1;
        if (index < 0 || index >= options.Count)
            return false;

        moved = Moves(() =>
        {
            options.RemoveAt(index);
            if (group["DefaultSettings"] is JValue { Type: JTokenType.Integer } value)
            {
                long settings = (long)value;
                group["DefaultSettings"] = view.Type == ModGroupType.Single
                    ? settings == index ? 0 : settings > index ? settings - 1 : settings
                    : (settings & ((1L << index) - 1)) | ((settings >> (index + 1)) << index);
            }
        });
        Touch();
        return true;
    }

    /// <summary>Takes a group out; <paramref name="moved"/> as for <see cref="RemoveOption"/> (a group addressed by position moves up).</summary>
    public bool RemoveGroup(string id, out Dictionary<OptionRef, OptionRef> moved)
    {
        moved = new Dictionary<OptionRef, OptionRef>();
        var existing = FindGroupObject(id);
        if (existing == null)
            return false;
        moved = Moves(existing.Remove);
        Touch();
        return true;
    }

    /// <summary>Runs a structural edit and says how the address of each option that survived it changed.</summary>
    private Dictionary<OptionRef, OptionRef> Moves(Action edit)
    {
        var before = CarrierObjects();
        edit();
        var after = new Dictionary<JObject, OptionRef>(ReferenceEqualityComparer.Instance);
        foreach (var (obj, now) in CarrierObjects())
            after[obj] = now;
        var moved = new Dictionary<OptionRef, OptionRef>();
        foreach (var (obj, was) in before)
            if (after.TryGetValue(obj, out var now) && now != was)
                moved[was] = now;
        return moved;
    }

    /// <summary>Every option and Combining container object, with how it is addressed now.</summary>
    private List<(JObject Obj, OptionRef Ref)> CarrierObjects()
    {
        var result = new List<(JObject, OptionRef)>();
        if (Root[GroupsKey] is not JArray groups)
            return result;
        for (int gi = 0; gi < groups.Count; gi++)
        {
            if (groups[gi] is not JObject g)
                continue;
            var gid = GroupId(g, gi);
            if (g["Options"] is JArray opts)
                for (int oi = 0; oi < opts.Count; oi++)
                    if (opts[oi] is JObject o)
                        result.Add((o, new OptionRef(gid, OptionId(o, oi))));
            if (g["Containers"] is JArray containers)
                for (int ci = 0; ci < containers.Count; ci++)
                    if (containers[ci] is JObject c)
                        result.Add((c, new OptionRef(gid, $"c#{ci}")));
        }
        return result;
    }

    /// <summary>The files one option (or the default files) carries: game path → path in the mod folder.</summary>
    public IEnumerable<(string GamePath, string RelPath)> FilesOf(OptionRef option)
    {
        if (Carrier(option, create: false)?["Files"] is not JObject files)
            yield break;
        foreach (var p in files.Properties())
            if (p.Value.Type == JTokenType.String)
                yield return (p.Name, (string)p.Value!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Manipulations
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a manipulation is about, independent of its value: its type and every field but the
    /// entry. Fields at their default (0, "Unknown", empty) are left out, since Penumbra omits them
    /// and other tools write them — both spellings name the same entry.
    /// </summary>
    public static string IdentityOf(JObject manipulation)
    {
        var sb = new StringBuilder((string?)manipulation["Type"] ?? "?");
        if (manipulation["Manipulation"] is JObject m)
        {
            foreach (var p in m.Properties().Where(p => p.Name != "Entry").OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var v = p.Value.Type is JTokenType.Object or JTokenType.Array ? p.Value.ToString(Formatting.None) : p.Value.ToString();
                if (v is "0" or "" or "Unknown" or "False")
                    continue;
                sb.Append('|').Append(p.Name).Append('=').Append(v);
            }
        }
        return sb.ToString();
    }

    public IEnumerable<JObject> Manipulations(OptionRef option)
        => Carrier(option, create: false)?["Manipulations"] is JArray manips ? manips.OfType<JObject>() : Enumerable.Empty<JObject>();

    public JObject? FindManipulation(OptionRef option, string identity)
        => Manipulations(option).FirstOrDefault(m => IdentityOf(m) == identity);

    /// <summary>Adds a manipulation to an option, or replaces the one about the same entry in place.</summary>
    public void UpsertManipulation(OptionRef option, JObject manipulation)
    {
        var carrier = Carrier(option, create: true) ?? throw new InvalidOperationException($"Option {option} does not exist in this mod.");
        var manips = carrier["Manipulations"] as JArray;
        if (manips == null)
        {
            manips = new JArray();
            SetOrInsert(carrier, "Manipulations", manips);
        }

        var identity = IdentityOf(manipulation);
        var existing = manips.OfType<JObject>().FirstOrDefault(m => IdentityOf(m) == identity);
        if (existing != null)
        {
            if (JToken.DeepEquals(existing, manipulation))
                return;
            existing.Replace(manipulation);
        }
        else
        {
            manips.Add(manipulation);
        }
        Touch();
    }

    public bool RemoveManipulation(OptionRef option, string identity)
    {
        var existing = Manipulations(option).FirstOrDefault(m => IdentityOf(m) == identity);
        if (existing == null)
            return false;
        existing.Remove();
        Touch();
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void Touch()
    {
        Revision++;
        _groups = null;
    }

    /// <summary>
    /// Sets <paramref name="key"/> on <paramref name="obj"/>. A key that exists keeps its place; a new
    /// one goes in front of the first of <paramref name="before"/> present (so descriptive fields stay
    /// above the file lists, the way Penumbra orders them), else at the end.
    /// </summary>
    private static void SetOrInsert(JObject obj, string key, JToken value, params string[] before)
    {
        var existing = obj.Property(key, StringComparison.Ordinal);
        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        foreach (var anchor in before)
        {
            var prop = obj.Property(anchor, StringComparison.Ordinal);
            if (prop != null)
            {
                prop.AddBeforeSelf(new JProperty(key, value));
                return;
            }
        }
        obj.Add(key, value);
    }
}

/// <summary>Stable ids for groups and options this plugin writes, so every update keeps the user's choices.</summary>
internal static class MetaIds
{
    /// <summary>
    /// A stable id for a child of <paramref name="parent"/>: the same parent and key always give the
    /// same id, so a rebuilt mod gives the same group and option the same id and Penumbra keeps the
    /// option the user picked.
    /// </summary>
    public static Guid Derive(Guid parent, string key)
    {
        var bytes = Encoding.UTF8.GetBytes($"{parent:N}/{key}");
        var hash  = SHA256.HashData(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }
}
