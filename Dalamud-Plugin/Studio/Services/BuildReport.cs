using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

internal enum BuildOutcome { Ok, Skipped, Failed }

internal enum BuildCategory { Model, Texture, Variant, Material, Meta }

/// <summary>
/// One file (or one attempted file) the build produced. <see cref="SubjectKey"/>/<see cref="ItemName"/>
/// are 0/empty for pack-level entries (meta.json) that don't belong to any one item.
/// </summary>
internal sealed record BuildEntry(
    BuildOutcome  Outcome,
    BuildCategory Category,
    string        Label,
    string?       GamePath,
    string?       Detail,
    Target?       Source,
    uint          SubjectKey,
    string        ItemName);

/// <summary>Everything one item contributes to a build: its own models, materials and game-data lookups.</summary>
internal sealed class ItemBuildRequest
{
    /// <summary>Where the item goes in the mod (see <see cref="PackPlace"/>): its metadata, and its files unless they say otherwise.</summary>
    public string                           Option             { get; init; } = PackPlace.Default;

    public required PortSubject             Subject            { get; init; }
    public required List<RaceModelEntry>    Models             { get; init; }
    public required List<MaterialSetup>     Materials          { get; init; }
    public required List<ModelMaterialSlot> ModelMaterialSlots { get; init; }

    // Game data the build needs, read on the render thread before the worker starts, so
    // the worker only touches files on disk and never the game's data files.
    public required HashSet<RaceGender>     NativeRaces        { get; init; }

    /// <summary>
    /// The races a copy of each material is written for: for gear one per gender, named after that
    /// gender's base race, plus any race the game gives a material of its own; for hair every
    /// configured race; for anything else a single entry. Resolved on the render thread because the
    /// gear case reads the game's Eqdp tables.
    /// </summary>
    public IReadOnlyList<RaceGender?>       MaterialRaces      { get; init; } = new RaceGender?[] { null };

    /// <summary>
    /// The game's own Eqdp entry per race code, for this item's slot and set id. The build adds what
    /// it needs on top of these, and writes nothing where the game already grants it.
    /// </summary>
    public IReadOnlyDictionary<string, ushort> VanillaEqdp     { get; init; } = new Dictionary<string, ushort>();

    /// <summary>
    /// The race each model's materials are looked up under, by the model's own race code — what
    /// every model this build writes has to reference. Resolved on the render thread; a race that is
    /// missing here falls back to itself.
    /// </summary>
    public IReadOnlyDictionary<string, RaceGender> MaterialRaceByModel { get; init; } = new Dictionary<string, RaceGender>();
    public List<ModImcOverride>?            ImcOverrides       { get; init; }

    /// <summary>Equipment parameters the user changed from the game's own (gear only).</summary>
    public ModEqpOverride?                  EqpOverride        { get; init; }

    /// <summary>Extra skeleton entries the user set per race (hair only).</summary>
    public List<ModEstOverride>?            EstOverrides       { get; init; }

    /// <summary>
    /// The vanilla model each race whose entry is set to "dummy" builds its placeholder from,
    /// keyed by race code. Read on the render thread, since the worker never touches game data.
    /// </summary>
    public IReadOnlyDictionary<string, VanillaModelInfo> DummyBases { get; init; }
        = new Dictionary<string, VanillaModelInfo>();

    /// <summary>
    /// Vanilla materials to clone for materials whose shader has no bundled preset, keyed by
    /// <see cref="BuildRequest.TemplateKey"/>. A missing or null entry means "none found".
    /// </summary>
    public IReadOnlyDictionary<string, (string GamePath, MtrlInfo Material)?> VanillaTemplates { get; init; }
        = new Dictionary<string, (string GamePath, MtrlInfo Material)?>();
}

/// <summary>
/// Everything the build worker needs to write one combined mod from every item in the
/// modpack, deep-copied from the session on the render thread.
/// </summary>
internal sealed class BuildRequest
{
    public required string ModRoot         { get; init; }

    /// <summary>The mod's folder name (made safe for a folder, see <see cref="PortSession.SanitizeFolderName"/>).</summary>
    public required string ModName         { get; init; }
    public required string PluginDirectory { get; init; }

    /// <summary>The mod's name as the user typed it, for meta.json. Empty means <see cref="ModName"/>.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The mod folder the build makes: one that does not exist yet.</summary>
    public string ModPath => System.IO.Path.Combine(ModRoot, ModName);

    /// <summary>
    /// Where converted textures are kept between builds, so the same images are not compressed
    /// again every time (see <see cref="TextureCache"/>). Empty disables the cache.
    /// </summary>
    public string CacheDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Who converts the textures. Null falls back to the encoder bundled with this plugin, which is
    /// managed code and far slower at BC7 than the native one Penumbra offers.
    /// </summary>
    public TextureConversion? ConvertTexture { get; init; }
    public required ModMetaInfo Meta       { get; init; }
    public required List<ItemBuildRequest> Items { get; init; }

    /// <summary>The mod's groups of options. Every model, material and texture of <see cref="Items"/> says which option it goes into (resolved).</summary>
    public List<PackGroup> Groups { get; init; } = new();

    public static string TemplateKey(string materialName, RaceGender? race) => $"{materialName}|{race?.RaceCode}";
}

/// <summary>An immutable progress snapshot; the worker swaps in a new one per step.</summary>
internal sealed record BuildProgress(int Done, int Total, string Current)
{
    public float Fraction => Total <= 0 ? 0f : Math.Clamp(Done / (float)Total, 0f, 1f);
}

/// <summary>The structured result of one build: one mod made from every item that was in the pack.</summary>
internal sealed class BuildReport
{
    public required string ModName      { get; init; }
    public required string ModPath      { get; init; }
    public required string ItemsSummary { get; init; }

    public List<BuildEntry> Entries { get; } = new();

    public int VariantGroups  { get; set; }
    public int EqdpOverrides  { get; set; }
    public int ImcOverrides   { get; set; }
    public int EqpOverrides   { get; set; }
    public int EstOverrides   { get; set; }

    public TimeSpan Duration  { get; set; }
    public bool     Cancelled { get; set; }

    /// <summary>Set when the build stopped on an exception rather than a per-file failure.</summary>
    public string? FatalError { get; set; }

    /// <summary>
    /// Filled in on the render thread after the worker finishes: Penumbra's answer to adding the new mod.
    /// </summary>
    public PenumbraApiEc? AddModResult { get; set; }

    /// <summary>
    /// Which item <see cref="Add"/> should tag new entries with — set by the builder before
    /// processing each item's files, so every helper method doesn't need its own item parameter.
    /// </summary>
    public uint   CurrentSubjectKey { get; set; }
    public string CurrentItemName   { get; set; } = string.Empty;

    public int Ok      => Entries.Count(e => e.Outcome == BuildOutcome.Ok);
    public int Skipped => Entries.Count(e => e.Outcome == BuildOutcome.Skipped);
    public int Failed  => Entries.Count(e => e.Outcome == BuildOutcome.Failed);

    public bool Succeeded => FatalError == null && !Cancelled && Failed == 0;

    public bool RegisteredWithPenumbra => AddModResult is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged;

    public void Add(BuildOutcome outcome, BuildCategory category, string label, string? gamePath = null,
        string? detail = null, Target? source = null)
        => Entries.Add(new BuildEntry(outcome, category, label, gamePath, detail, source, CurrentSubjectKey, CurrentItemName));

    /// <summary>One-line result, as shown at the top of the report.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        if (FatalError != null)
            sb.Append("Build stopped: ").Append(FatalError).Append(". ");
        else if (Cancelled)
            sb.Append("Build cancelled. ");

        sb.Append($"{Ok} written, {Skipped} skipped, {Failed} failed");
        if (VariantGroups > 0) sb.Append($" · {VariantGroups} group(s) of options");
        if (EqdpOverrides > 0) sb.Append($" · {EqdpOverrides} race override(s)");
        if (ImcOverrides  > 0) sb.Append($" · {ImcOverrides} dye-variant override(s)");
        if (EqpOverrides  > 0) sb.Append($" · {EqpOverrides} equipment parameter(s)");
        if (EstOverrides  > 0) sb.Append($" · {EstOverrides} skeleton entr{(EstOverrides == 1 ? "y" : "ies")}");
        sb.Append($" · {Duration.TotalSeconds:0.0}s");
        return sb.ToString();
    }

    /// <summary>The whole report as plain text, for the copy button and the log.</summary>
    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"XIV Instant Edit build — {ItemsSummary}");
        sb.AppendLine($"Mod: {ModName}");
        sb.AppendLine($"Folder: {ModPath}");
        sb.AppendLine(Summary());
        if (AddModResult != null)
            sb.AppendLine(RegisteredWithPenumbra
                ? "Registered with Penumbra."
                : $"Penumbra did not pick it up automatically ({AddModResult}) — run a mod rediscovery.");
        sb.AppendLine();

        foreach (var itemGroup in Entries.GroupBy(e => (e.SubjectKey, e.ItemName)))
        {
            if (!string.IsNullOrEmpty(itemGroup.Key.ItemName))
                sb.AppendLine($"== {itemGroup.Key.ItemName} ==");

            foreach (var group in itemGroup.GroupBy(e => e.Category))
            {
                sb.AppendLine($"[{group.Key}]");
                foreach (var e in group)
                {
                    var tag = e.Outcome switch
                    {
                        BuildOutcome.Ok      => "ok     ",
                        BuildOutcome.Skipped => "skipped",
                        _                    => "FAILED ",
                    };
                    sb.Append("  ").Append(tag).Append("  ").Append(e.GamePath ?? e.Label);
                    if (!string.IsNullOrEmpty(e.Detail))
                        sb.Append("  — ").Append(e.Detail);
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }
}
