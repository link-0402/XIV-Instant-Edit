using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>What one part of the mod carries: its default files, or one option — files (game path → path in the mod folder) and manipulations.</summary>
public sealed class ModCarrier
{
    public List<KeyValuePair<string, string>> Files { get; } = new();
    public List<ModEqdpOverride> Eqdp { get; } = new();
    public List<ModImcOverride>  Imc  { get; } = new();
    public List<ModEqpOverride>  Eqp  { get; } = new();
    public List<ModEstOverride>  Est  { get; } = new();

    public bool HasManipulations => Eqdp.Count + Imc.Count + Eqp.Count + Est.Count > 0;
    public bool IsEmpty => Files.Count == 0 && !HasManipulations;
}

/// <summary>One group of options as written into meta.json. Ids are kept from build to build, so Penumbra keeps what the player picked.</summary>
public sealed class ModGroupOut
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool Multi { get; init; }
    public List<ModOptionOut> Options { get; } = new();
}

public sealed class ModOptionOut
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public ModCarrier Contents { get; init; } = new();
}

/// <summary>
/// An Eqdp (Equipment Deformer Parameter) override: tells the game that a race has files of its
/// own for this item — a model, a material, or both (see <see cref="EqdpInfo"/>). Needed whenever a
/// port writes a model for a race the game gives none, and whenever it writes a material under a
/// base race the game has no material entry for.
/// </summary>
public sealed class ModEqdpOverride
{
    public required RaceGender RaceGender { get; init; }
    public required EquipSlot  Slot       { get; init; }
    public required ushort     SetId      { get; init; }

    /// <summary>The slot's bits as they should end up: what the game already grants, plus what this port needs.</summary>
    public required ushort     Entry      { get; init; }
}

/// <summary>
/// An Imc override: forces a single dye/recolor variant's MaterialId back to 1 (the
/// "v0001" material folder this tool always writes to), while preserving every other
/// field of that variant's own vanilla entry (decal, VFX, animation, attributes, sound).
/// One of these is written per variant an item has, so the mod's material shows up
/// regardless of which specific recolor variant the equipped item instance uses.
/// </summary>
public sealed class ModImcOverride
{
    public required EquipSlot Slot                { get; init; }
    public required ushort    SetId                { get; init; }
    public required byte      Variant              { get; init; }
    public required byte      DecalId              { get; init; }
    public required byte      VfxId                { get; init; }
    public required byte      MaterialAnimationId  { get; init; }
    public required ushort    AttributeMask        { get; init; }
    public required byte      SoundId              { get; init; }
}

/// <summary>
/// An Eqp (Equipment Parameter) override: what a piece hides or shows on the rest of the
/// character. <see cref="Entry"/> is the whole 64-bit entry, as Penumbra serialises it; only the
/// bytes of the item's own slot are meaningful, the rest are carried over from the game's entry.
/// </summary>
public sealed class ModEqpOverride
{
    public required EquipSlot Slot  { get; init; }
    public required ushort    SetId { get; init; }
    public required ulong     Entry { get; init; }
}

/// <summary>
/// An Est (Extra Skeleton Table) override: which skeleton a race uses for a hair id, which is
/// what lets a ported hairstyle keep its physics on a race the game has no entry for.
/// </summary>
public sealed class ModEstOverride
{
    public required RaceGender RaceGender { get; init; }
    public required ushort     SetId      { get; init; }
    public required ushort     SkeletonId { get; init; }

    /// <summary>Penumbra's EstType member name; only hair is offered for now.</summary>
    public string Slot { get; init; } = "Hair";
}

/// <summary>The mod's identity and descriptive fields as written into meta.json.</summary>
public sealed class ModMetaInfo
{
    /// <summary>Mod identifier: new for every mod a build makes.</summary>
    public Guid   Identifier  { get; init; } = Guid.NewGuid();
    public string Author      { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version     { get; init; } = "1.0";
    public string Website     { get; init; } = string.Empty;
}

/// <summary>
/// Writes the Penumbra <c>meta.json</c> (FileVersion 4, as of Penumbra 1.7+) for a
/// created mod: its default files, then its groups of options, each option with its own
/// files and manipulations. The layout mirrors what Penumbra's ModSerialization/GroupSerialization
/// write. Since 1.7, groups and default options live in meta.json again (no more
/// separate group_*.json / default_mod.json files), and every mod/group/option gets
/// a stable GUID identifier.
/// </summary>
public static class ModMetaWriter
{
    /// <summary>
    /// Writes meta.json into <paramref name="modPath"/>. Files map game paths to mod-relative
    /// paths (a default file usually sits at its game path; an option's in a folder of its own).
    /// </summary>
    public static void Write(string modPath, string modName, ModCarrier defaults, IReadOnlyList<ModGroupOut> groups, ModMetaInfo? info = null)
    {
        info ??= new ModMetaInfo();

        // Written next to it and then moved over it, so meta.json is never seen half written.
        var path = Path.Combine(modPath, "meta.json");
        var tmp  = path + ".tmp";
        try
        {
            using (var stream = File.Create(tmp))
            using (var j = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                WriteMeta(j, modName, defaults, groups, info);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* a stray .tmp is harmless */ }
        }
    }

    private static void WriteMeta(Utf8JsonWriter j, string modName, ModCarrier defaults, IReadOnlyList<ModGroupOut> groups, ModMetaInfo info)
    {
        j.WriteStartObject();
        j.WriteNumber("FileVersion", 4);
        j.WriteString("Identifier", info.Identifier);
        j.WriteString("LastWrite", DateTime.UtcNow);
        j.WriteString("Name", modName);
        j.WriteString("Author", info.Author);
        j.WriteString("Description", info.Description);
        j.WriteString("Version", info.Version);
        j.WriteString("Website", info.Website);

        if (!defaults.IsEmpty)
        {
            j.WriteStartObject("DefaultData");
            WriteContents(j, defaults);
            j.WriteEndObject();
        }

        var written = groups.Where(g => g.Options.Count > 0).ToList();
        if (written.Count > 0)
        {
            j.WriteStartArray("Groups");
            foreach (var group in written)
                WriteGroup(j, group);
            j.WriteEndArray();
        }

        j.WriteEndObject();
    }

    private static void WriteGroup(Utf8JsonWriter j, ModGroupOut group)
    {
        j.WriteStartObject();
        j.WriteString("Type", group.Multi ? "Multi" : "Single");
        j.WriteString("Id", group.Id.ToString());
        j.WriteString("Name", group.Name);
        j.WriteString("Description", group.Description);
        j.WriteNumber("Priority", 0);
        j.WriteNumber("Page", 0);
        j.WriteNumber("DefaultSettings", 0); // single: the first option; multi: none picked

        j.WriteStartArray("Options");
        foreach (var option in group.Options)
        {
            j.WriteStartObject();
            j.WriteString("Id", option.Id.ToString());
            j.WriteString("Name", option.Name);
            j.WriteString("Description", option.Description);
            WriteContents(j, option.Contents);
            j.WriteEndObject();
        }
        j.WriteEndArray();

        j.WriteEndObject();
    }

    private static void WriteContents(Utf8JsonWriter j, ModCarrier contents)
    {
        if (contents.Files.Count > 0)
        {
            j.WriteStartObject("Files");
            foreach (var (gamePath, relPath) in contents.Files)
                j.WriteString(gamePath, relPath);
            j.WriteEndObject();
        }
        if (contents.HasManipulations)
            WriteManipulations(j, contents.Eqdp, contents.Imc, contents.Eqp, contents.Est);
    }

    private static void WriteManipulations(Utf8JsonWriter j, IReadOnlyList<ModEqdpOverride> eqdpOverrides,
        IReadOnlyList<ModImcOverride> imcOverrides, IReadOnlyList<ModEqpOverride> eqpOverrides,
        IReadOnlyList<ModEstOverride> estOverrides)
    {
        j.WriteStartArray("Manipulations");

        foreach (var o in eqpOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Eqp");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", o.Entry);
            j.WriteNumber("SetId", o.SetId);
            j.WriteString("Slot", SlotInfo.LabelMap[o.Slot]);
            j.WriteEndObject();
            j.WriteEndObject();
        }

        foreach (var o in estOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Est");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", o.SkeletonId);
            j.WriteString("Gender", o.RaceGender.PenumbraGender);
            j.WriteString("Race", o.RaceGender.PenumbraRace);
            j.WriteNumber("SetId", o.SetId);
            j.WriteString("Slot", o.Slot);
            j.WriteEndObject();
            j.WriteEndObject();
        }

        foreach (var o in eqdpOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Eqdp");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", o.Entry);
            j.WriteString("Gender", o.RaceGender.PenumbraGender);
            j.WriteString("Race", o.RaceGender.PenumbraRace);
            j.WriteNumber("SetId", o.SetId);
            j.WriteString("Slot", SlotInfo.LabelMap[o.Slot]);
            j.WriteEndObject();
            j.WriteEndObject();
        }

        foreach (var o in imcOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Imc");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("PrimaryId", o.SetId);
            j.WriteNumber("SecondaryId", 0);
            j.WriteNumber("Variant", o.Variant);
            j.WriteString("ObjectType", SlotInfo.IsAccessory(o.Slot) ? "Accessory" : "Equipment");
            j.WriteString("EquipSlot", SlotInfo.LabelMap[o.Slot]);
            j.WriteString("BodySlot", "Unknown");
            j.WriteStartObject("Entry");
            j.WriteNumber("MaterialId", 1); // always force the v0001 folder this tool writes to
            j.WriteNumber("DecalId", o.DecalId);
            j.WriteNumber("VfxId", o.VfxId);
            j.WriteNumber("MaterialAnimationId", o.MaterialAnimationId);
            j.WriteNumber("AttributeMask", o.AttributeMask);
            j.WriteNumber("SoundId", o.SoundId);
            j.WriteEndObject();
            j.WriteEndObject();
            j.WriteEndObject();
        }

        j.WriteEndArray();
    }
}
