using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

internal enum Severity { Info, Warning, Error }

/// <summary>One problem with the current set-up, pointing at the object that has it.</summary>
internal readonly record struct PortIssue(Severity Severity, Target Target, string Message);

/// <summary>
/// Checks every item in Build mode's modpack for everything the build would otherwise only discover
/// part-way through: missing files, empty variant folders, shaders the build has no template for,
/// two materials writing the same .mtrl, and two items overwriting each other. Severities mirror what
/// the build does — something it would skip is a warning, something it would fail on is an error.
///
/// Pure over the session; the main window re-runs it when the session changes and on a
/// slow timer (files on disk can appear or vanish without an edit).
/// </summary>
internal static class PortValidator
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".dds", ".tga", ".tex" };

    public static List<PortIssue> Validate(PortSession session, GameDataService gameData, MaterialTemplates templates,
        ModelFileCache models, ImageFileCache images, bool penumbraAvailable)
    {
        var issues = new List<PortIssue>();
        if (session.Items.Count == 0)
            return issues;

        if (!penumbraAvailable)
            issues.Add(new(Severity.Error, new Target(TargetKind.Build), "Penumbra is not available — the mod folder cannot be resolved."));

        // Place and game path → the item that writes it first, to catch two items overwriting each
        // other's files. Like the build, a file in one option and the same path in another are apart.
        var writtenBy = new Dictionary<string, PortItem>(StringComparer.OrdinalIgnoreCase);
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in session.Items)
            ValidateItem(session, item, gameData, templates, models, images, writtenBy, writtenPaths, issues);
        ValidateSharedTextures(session, writtenPaths, issues);
        ValidatePlaces(session, issues);

        issues.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return issues;
    }

    /// <summary>
    /// Where things go in the mod: a place that names an option no longer there falls back to what it
    /// belongs to; a variant file its group has no option for (a full multi-select group) is left out.
    /// </summary>
    private static void ValidatePlaces(PortSession session, List<PortIssue> issues)
    {
        const string Gone = "goes into an option that is no longer in the mod — it goes with what it belongs to instead. Pick another place in the inspector.";
        foreach (var group in session.Groups)
            if (group.Options.All(o => session.ContentsOf(PackPlace.Of(group.Id, o.Id)).Count == 0))
                issues.Add(new(Severity.Info, Target.ForPackGroup(group.Id),
                    $"Group {group.Name}: nothing goes into any of its options, so the build leaves it out."));
        foreach (var item in session.Items)
        {
            var who = item.Subject.DisplayName;
            if (!session.Exists(item.Meta.Option))
                issues.Add(new(Severity.Warning, new Target(TargetKind.Item, item.Key), $"{who} {Gone}"));
            for (int i = 0; i < item.Models.Count; i++)
            {
                var model = item.Models[i];
                var target = new Target(TargetKind.Model, item.Key, i);
                if (model.Option != null && !session.Exists(model.Option))
                    issues.Add(new(Severity.Warning, target, $"{who} / {model.RaceGender.DisplayName}: the model {Gone}"));
                if (model.UseVariants && !model.UseDummy && session.VariantGroupOf(model) is { } group)
                    foreach (var file in session.VariantFiles(model).Where(f => PortSession.OptionFor(group, f) == null && !group.IsLeftOut(PortSession.VariantKey(f))))
                        issues.Add(new(Severity.Warning, target, $"{who} / {model.RaceGender.DisplayName}: {Path.GetFileName(file)} has no option in {group.Name} and is left out."));
            }
            for (int m = 0; m < item.Materials.Count; m++)
            {
                var material = item.Materials[m];
                if (material.Option != null && !session.Exists(material.Option))
                    issues.Add(new(Severity.Warning, new Target(TargetKind.Material, item.Key, m), $"{who} / {material.Name}: the material {Gone}"));
                for (int t = 0; t < material.Textures.Count; t++)
                {
                    var tex = material.Textures[t];
                    var target = new Target(TargetKind.Texture, item.Key, m, t);
                    var what = $"{who} / {material.Name} / {TextureTypeInfo.DisplayName(tex.Type)}";
                    if (tex.Option != null && !session.Exists(tex.Option))
                        issues.Add(new(Severity.Warning, target, $"{what}: the texture {Gone}"));
                    if (tex.UseVariants && !tex.UseWhiteDummy && !tex.IsShared && session.VariantGroupOf(tex) is { } group)
                        foreach (var file in session.VariantFiles(tex).Where(f => PortSession.OptionFor(group, f) == null && !group.IsLeftOut(PortSession.VariantKey(f))))
                            issues.Add(new(Severity.Warning, target, $"{what}: {Path.GetFileName(file)} has no option in {group.Name} and is left out."));
                }
            }
        }
    }

    private static void ValidateItem(PortSession session, PortItem item, GameDataService gameData, MaterialTemplates templates,
        ModelFileCache models, ImageFileCache images, Dictionary<string, PortItem> writtenBy, HashSet<string> writtenPaths, List<PortIssue> issues)
    {
        var subject = item.Subject;
        var itemTarget = new Target(TargetKind.Item, item.Key);

        if (item.Animation is { } animation)
        {
            ValidateAnimation(item, animation, itemTarget, issues);
            ValidateAcrossItems(item, WrittenFiles(session, item, gameData), writtenBy, writtenPaths, issues);
            return;
        }

        // A feature port replaces a vanilla file; an id the game does not have replaces nothing.
        if (subject is FeatureSubject feature && !gameData.GetNativeRaces(subject).Contains(feature.Race))
            issues.Add(new(Severity.Error, itemTarget,
                $"The game has no {subject.DisplayName} — there is nothing for this item to replace."));

        bool hasModels = item.Models.Any(m => !string.IsNullOrWhiteSpace(m.SourcePath) || m.UseVariants || m.UseDummy) || subject.MultiRace && item.Models.Count > 0;
        if (!hasModels && item.Materials.Count == 0)
        {
            issues.Add(new(Severity.Info, itemTarget, $"{subject.DisplayName}: nothing set up yet — add a race model or a material."));
            return;
        }

        if (item.Models.Count == 0)
            issues.Add(new(Severity.Info, itemTarget,
                $"{subject.DisplayName}: no race models — the mod will only replace this {subject.KindLabel.ToLowerInvariant()}'s materials and textures."));

        ValidateModels(item, gameData, models, issues);
        ValidateMeta(item, gameData, issues);
        ValidateMaterials(item, gameData, templates, images, issues);
        ValidateWrittenMaterials(item, session, gameData, issues);
        ValidateAcrossItems(item, WrittenFiles(session, item, gameData), writtenBy, writtenPaths, issues);
    }

    /// <summary>An animation item: something to write, and every file it names is the kind its row takes.</summary>
    private static void ValidateAnimation(PortItem item, AnimationSetup animation, Target itemTarget, List<PortIssue> issues)
    {
        if (!animation.HasWork)
        {
            issues.Add(new(Severity.Warning, itemTarget, $"{item.Subject.DisplayName}: drop a .pap on at least one animation."));
            return;
        }
        foreach (var timeline in animation.Timelines)
        {
            if (!string.IsNullOrWhiteSpace(timeline.PapSource) && Animation.AnimationFileCheck.Problem(timeline.PapSource, tmb: false) is { } pap)
                issues.Add(new(Severity.Error, itemTarget, $"{item.Subject.DisplayName} / {timeline.Label} .pap: {pap}"));
            if (!string.IsNullOrWhiteSpace(timeline.TmbSource) && Animation.AnimationFileCheck.Problem(timeline.TmbSource, tmb: true) is { } tmb)
                issues.Add(new(Severity.Error, itemTarget, $"{item.Subject.DisplayName} / {timeline.Label} .tmb: {tmb}"));
        }
    }

    private static void ValidateModels(PortItem item, GameDataService gameData, ModelFileCache models, List<PortIssue> issues)
    {
        var subject = item.Subject;
        // Hair ported to a race that has no vanilla hair of this id: the game may never ask for it.
        var native = subject.Kind == SubjectKind.Hair ? gameData.GetNativeRaces(subject) : null;

        for (int i = 0; i < item.Models.Count; i++)
        {
            var model = item.Models[i];
            var target = new Target(TargetKind.Model, item.Key, i);
            var who = subject.HasRaces ? $"{subject.DisplayName} / {model.RaceGender.DisplayName}" : subject.DisplayName;

            if (native != null && !native.Contains(model.RaceGender))
                issues.Add(new(Severity.Warning, target,
                    $"{who}: has no vanilla {subject.KindLabel.ToLowerInvariant()} {((FeatureSubject)subject).Id}, so the game may not load this model for that race — check in game."));

            if (model.UseDummy)
            {
                if (item.Materials.Count == 0)
                    issues.Add(new(Severity.Error, target,
                        $"{who}: set to generate its model, but the item has no materials — each one becomes a mesh part."));
                else
                    issues.Add(new(Severity.Info, target,
                        $"{who}: the build generates this model ({item.Materials.Count} empty mesh part(s)) — model it in Blender before release."));
            }
            else if (model.UseVariants)
                ValidateModelVariants(item, model, gameData, models, target, who, issues);
            else if (string.IsNullOrWhiteSpace(model.SourcePath) && !subject.MultiRace)
                issues.Add(new(Severity.Info, target, $"{who}: no model file set — only materials and textures will be replaced."));
            else if (string.IsNullOrWhiteSpace(model.SourcePath))
                issues.Add(new(Severity.Warning, target, $"{who}: no model file set — this race will be skipped."));
            else if (!File.Exists(model.SourcePath))
                issues.Add(new(Severity.Error, target, $"{who}: model file not found."));
            else if (!model.SourcePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(Severity.Warning, target, $"{who}: source is not a .mdl file."));
            else
            {
                if (model.IsVanillaDummy)
                    issues.Add(new(Severity.Info, target, $"{who}: still the empty dummy — model it in Blender before release."));
                ValidateModelMaterials(item, model, model.SourcePath, model.MaterialLinks, gameData, models, target, who, issues);
            }
        }
    }

    /// <summary>
    /// Whether a model file's mesh parts end up on this item's materials. A model made anywhere else
    /// references the material names it was exported with, so a part left on one of those loads
    /// whatever the game already has at that name — never a .mtrl this mod writes.
    /// </summary>
    private static void ValidateModelMaterials(PortItem item, RaceModelEntry model, string path,
        IReadOnlyList<int>? links, GameDataService gameData, ModelFileCache models, Target target, string who, List<PortIssue> issues)
    {
        var facts = models.Read(path);
        var file = Path.GetFileName(path);

        if (!facts.Ok)
        {
            issues.Add(new(Severity.Warning, target,
                $"{who}: \"{file}\" does not read as a model ({facts.Error}) — it is copied into the mod as it is, so its materials cannot be assigned."));
            return;
        }

        var written = ModelMaterialLinks.WrittenNames(item.Subject, item.Materials,
            gameData.MaterialRaceFor(item.Subject, model.RaceGender));
        if (written.Count == 0 || facts.Materials.Count == 0)
            return;

        var kept = new List<string>();
        for (int slot = 0; slot < facts.Materials.Count; slot++)
        {
            var referenced = facts.Materials[slot];
            if (ModelMaterialLinks.IsSharedFeature(referenced))
                continue;   // a shared body material (bibo / bibopube / piercings, etc.) — not this item's to assign

            if (ModelMaterialLinks.Resolve(ModelMaterialLinks.Stored(links, slot), slot, facts.Materials, written) < 0
                && ModelMaterialLinks.IndexOfName(written, referenced) < 0)
                kept.Add($"{slot + 1} ({referenced.TrimStart('/')})");
        }

        if (kept.Count > 0)
            issues.Add(new(Severity.Warning, target,
                $"{who}: mesh part(s) {string.Join(", ", kept)} of \"{file}\" keep a material this mod does not write — " +
                "the game loads whatever it already has at that name. Assign one of this item's materials to them."));
    }

    /// <summary>The metadata overrides: EST skeletons per race. EQP and EST entries are meant to differ
    /// from the game's own — that is what setting one does — so a changed entry is not itself
    /// flagged here; only ones that would not actually work are.</summary>
    private static void ValidateMeta(PortItem item, GameDataService gameData, List<PortIssue> issues)
    {
        if (item.Subject is not FeatureSubject { Kind: SubjectKind.Hair } hair)
            return;

        for (int i = 0; i < item.Models.Count; i++)
        {
            var race = item.Models[i].RaceGender;
            var modelTarget = new Target(TargetKind.Model, item.Key, i);
            var vanilla = gameData.VanillaHairSkeleton(race, hair.Id);
            var skeleton = PortSession.EffectiveHairSkeleton(item, race, gameData);

            if (skeleton is { } id && !gameData.HasHairSkeleton(race, id))
                issues.Add(new(Severity.Warning, modelTarget,
                    $"{race.DisplayName}: skeleton h{id:D4} does not exist for this race, so the game cannot load it - " +
                    "pick one the race has, or leave the entry unset."));
            else if (skeleton == null)
                issues.Add(new(Severity.Info, modelTarget,
                    $"{race.DisplayName}: no skeleton entry is written for {hair.KindLabel.ToLowerInvariant()} {hair.Id}, so the game uses its " +
                    "default - set one under Metadata if this model has its own bones (hair physics)."));
            else if (vanilla != skeleton)
                issues.Add(new(Severity.Info, modelTarget,
                    $"{race.DisplayName}: the mod sets skeleton h{skeleton:D4} for this {hair.KindLabel.ToLowerInvariant()}" +
                    (vanilla == null ? " - the game has no entry of its own." : $", where the game has h{vanilla:D4}.")));
        }
    }

    private static void ValidateModelVariants(PortItem item, RaceModelEntry model, GameDataService gameData,
        ModelFileCache models, Target target, string who, List<PortIssue> issues)
    {
        if (model.VariantPaths.Count == 0)
        {
            issues.Add(new(Severity.Warning, target, $"{who}: no variant files set — this race will be skipped."));
            return;
        }

        for (int i = 0; i < model.VariantPaths.Count; i++)
        {
            var path = model.VariantPaths[i];
            if (!File.Exists(path))
                issues.Add(new(Severity.Error, target, $"{who}: variant file not found ({path})."));
            else if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(Severity.Warning, target, $"{who}: variant \"{Path.GetFileName(path)}\" is not a .mdl file."));
            else
                ValidateModelMaterials(item, model, path, model.VariantLinks(i), gameData, models, target, who, issues);
        }
    }

    private static void ValidateMaterials(PortItem item, GameDataService gameData, MaterialTemplates templates,
        ImageFileCache images, List<PortIssue> issues)
    {
        var subject = item.Subject;
        for (int m = 0; m < item.Materials.Count; m++)
        {
            var mat = item.Materials[m];
            var target = new Target(TargetKind.Material, item.Key, m);
            var who = string.IsNullOrWhiteSpace(mat.Name) ? $"Material {m + 1}" : mat.Name;

            if (string.IsNullOrWhiteSpace(mat.Name))
                issues.Add(new(Severity.Error, target, $"{who}: has no name, so it has no file name."));
            else if (!mat.Name.StartsWith("mt_", StringComparison.Ordinal))
                issues.Add(new(Severity.Warning, target, $"{who}: game material names start with \"mt_\" — the model's material slots must match this name exactly."));

            if (mat.Textures.Count == 0)
                issues.Add(new(Severity.Warning, target, $"{who}: has no textures yet — add texture slots or apply a preset."));

            var configuredTypes = mat.Textures.Select(t => t.Type).ToHashSet();
            if (MaterialPresetLibrary.FindPresetPath(mat.ShaderType, configuredTypes) == null)
            {
                var what = subject.KindLabel.ToLowerInvariant();
                issues.Add(gameData.HasVanillaMaterial(subject)
                    ? new(Severity.Info, target,
                        $"{who}: no bundled {ShaderInfo.DisplayName(mat.ShaderType)} preset — the {what}'s own vanilla material will be cloned instead.")
                    : new(Severity.Error, target,
                        $"{who}: no bundled {ShaderInfo.DisplayName(mat.ShaderType)} preset, and this {what} has no vanilla material to clone."));
            }

            // What building will do with the template: textures it cannot take are written but never read.
            var plan = templates.Plan(item, mat);
            if (plan != null)
            {
                foreach (var entry in plan.Textures.Where(e => e.Wiring == TextureWiring.NotWired))
                {
                    int t = mat.Textures.FindIndex(x => x.Type == entry.Type);
                    issues.Add(new(Severity.Warning, t >= 0 ? new Target(TargetKind.Texture, item.Key, m, t) : target,
                        $"{who} / {TextureTypeInfo.DisplayName(entry.Type)}: can't be wired into the material ({entry.Reason}) — it would be written, but nothing reads it."));
                }
            }

            var supported = ShaderInfo.SupportedTextures(mat.ShaderType);
            foreach (var type in configuredTypes.Where(t => !supported.Contains(t)))
            {
                if (plan != null && plan.NotWired.Contains(type)) continue;
                int t = mat.Textures.FindIndex(x => x.Type == type);
                issues.Add(new(Severity.Info, t >= 0 ? new Target(TargetKind.Texture, item.Key, m, t) : target,
                    $"{who} / {TextureTypeInfo.DisplayName(type)}: not a texture {ShaderInfo.DisplayName(mat.ShaderType)} normally uses" +
                    (type == TextureType.Specular && mat.ShaderType != ShaderType.CharacterLegacy ? " (since 7.0 the mask usually carries specular)" : "") +
                    " — it is added to the material, but the shader may ignore it."));
            }

            foreach (var dup in mat.Textures.GroupBy(t => t.Type).Where(g => g.Count() > 1))
                issues.Add(new(Severity.Warning, target, $"{who}: more than one {TextureTypeInfo.DisplayName(dup.Key)} texture — only the last is used by the material."));

            foreach (var dup in mat.Textures.GroupBy(t => t.Postfix, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add(new(Severity.Error, target, $"{who}: several textures use the postfix \"{dup.Key}\", so they overwrite each other."));

            for (int t = 0; t < mat.Textures.Count; t++)
            {
                var texTarget = new Target(TargetKind.Texture, item.Key, m, t);
                var texWho = $"{who} / {TextureTypeInfo.DisplayName(mat.Textures[t].Type)}";
                if (UsesMatcapLibrary(mat, mat.Textures[t]))
                    issues.Add(new(Severity.Info, texTarget, $"{texWho}: {MatcapLibraryNote}"));
                else
                    ValidateTexture(mat.Textures[t], images, texTarget, texWho, issues);
            }
        }
    }

    /// <summary>
    /// An empty matcap slot is not a missing texture: the Matcap preset then keeps its placeholder,
    /// which reads the MatCap Library mod's shared texture.
    /// </summary>
    internal static bool UsesMatcapLibrary(MaterialSetup mat, TextureSlot tex)
        => mat.ShaderType == ShaderType.CharacterReflection && tex.Type == TextureType.Reflection
           && !tex.UseWhiteDummy && !tex.IsShared && !tex.UseVariants && string.IsNullOrWhiteSpace(tex.SourcePath);

    internal const string MatcapLibraryNote =
        "no matcap image, so the material reads chara/common/texture/matcaplib_a.tex — players need the MatCap Library mod " +
        "(slot A) installed. Pick an image to ship your own instead.";

    private static void ValidateTexture(TextureSlot tex, ImageFileCache images, Target target, string who, List<PortIssue> issues)
    {
        if (tex.UseWhiteDummy || tex.IsShared)
            return;   // a shared texture writes nothing itself; see ValidateSharedTextures

        if (tex.UseVariants)
        {
            if (string.IsNullOrWhiteSpace(tex.SourcePath) || !Directory.Exists(tex.SourcePath))
            {
                issues.Add(new(Severity.Error, target, $"{who}: variant folder not found."));
                return;
            }

            bool anyImage;
            try
            {
                anyImage = Directory.EnumerateFiles(tex.SourcePath).Any(f => ImageExtensions.Contains(Path.GetExtension(f)));
            }
            catch (Exception)
            {
                anyImage = false;
            }

            if (!anyImage)
                issues.Add(new(Severity.Error, target, $"{who}: variant folder has no images."));
            return;
        }

        if (string.IsNullOrWhiteSpace(tex.SourcePath))
            issues.Add(new(Severity.Warning, target, $"{who}: no source file — this texture will be skipped."));
        else if (!File.Exists(tex.SourcePath))
            issues.Add(new(Severity.Error, target, $"{who}: source file not found."));
        else if (!ImageExtensions.Contains(Path.GetExtension(tex.SourcePath)))
            issues.Add(new(Severity.Error, target, $"{who}: unsupported file type (use png, jpeg, tga, dds or .tex)."));
        else
            ValidateTextureSize(tex, images.Read(tex.SourcePath), target, who, issues);
    }

    /// <summary>
    /// What a source image costs, which is the one thing about a texture that is easy to get wrong
    /// without noticing: an uncompressed 4096×4096 texture is 64 MiB the game carries around, and
    /// compressing that one with BC7 takes the better part of a minute the first time it is built.
    /// </summary>
    private static void ValidateTextureSize(TextureSlot tex, ImageFacts facts, Target target, string who, List<PortIssue> issues)
    {
        if (!facts.Ok)
            return;

        if (tex.Compression == TextureCompression.None && facts.Pixels >= 1024 * 1024)
            issues.Add(new(Severity.Warning, target,
                $"{who}: {facts.Size} written uncompressed is {ImageFacts.Mib(facts.UncompressedBytes)}, which the game then carries " +
                "in memory - either block format keeps it to a quarter of that."));
        else if (tex.Compression == TextureCompression.Bc7 && facts.Pixels >= 4096 * 2048)
            issues.Add(new(Severity.Info, target,
                $"{who}: {facts.Size} takes the better part of a minute to compress with BC7 the first time — later builds reuse it " +
                "unless the image changes. BC3 is far quicker, and the game's own textures of this kind are usually smaller."));
    }

    /// <summary>The .mtrl files an item writes: one per material, and per race copy for hair.</summary>
    private static void ValidateWrittenMaterials(PortItem item, PortSession session, GameDataService gameData, List<PortIssue> issues)
    {
        var subject = item.Subject;
        var races = gameData.MaterialRaces(subject, item.Models);

        // Materials are rarely looked up under the race wearing them: gear shares them per gender,
        // and a feature shares them across a group of races. The path is surprising enough to say
        // out loud, since it is also what any other mod touching that race group would replace.
        var shared = item.Models
            .Select(m => (Model: m.RaceGender, Material: gameData.MaterialRaceFor(subject, m.RaceGender)))
            .Where(x => x.Material != x.Model)
            .ToList();
        if (shared.Count > 0 && item.Materials.Count > 0)
        {
            var under = string.Join(", ", shared.Select(x => x.Material.DisplayName).Distinct());
            var forWhom = shared.Count > 3
                ? $"{shared.Count} of its races"
                : string.Join(", ", shared.Select(x => x.Model.DisplayName));
            issues.Add(new(Severity.Info, new Target(TargetKind.Item, item.Key),
                $"{subject.DisplayName}: its materials are written under {under} — that is where the game looks them up " +
                $"for {forWhom}, so every race sharing them sees this mod."));
        }
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int m = 0; m < item.Materials.Count; m++)
        {
            var name = item.Materials[m].Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;   // reported by ValidateMaterials
            var target = new Target(TargetKind.Material, item.Key, m);
            var place = session.PlaceOf(item, item.Materials[m]);

            // Each race copy of each material must land on its own file (in the option it goes into).
            foreach (var race in races)
            {
                var path = WrittenKey(place, subject.MaterialGamePath(subject.MaterialNameFor(name, race), race));
                if (byPath.TryGetValue(path, out var first) && first != m)
                {
                    issues.Add(new(Severity.Error, target,
                        $"{name} writes the same .mtrl as material {first + 1} ({item.Materials[first].Name}) — only one of them would survive."));
                    break;
                }
                byPath[path] = m;
            }
        }
    }

    /// <summary>
    /// The files an item writes, each with the place it goes into, as the build places them: a
    /// model, material or texture into its own place (else the item's), and a slot of variants into
    /// its variant group. Materials go under the races the build writes them for (the shared race of
    /// a feature, see <see cref="GameDataService.MaterialRaces"/>).
    /// </summary>
    private static List<(string Place, string Path, Target Target)> WrittenFiles(PortSession session, PortItem item, GameDataService gameData)
    {
        var subject = item.Subject;
        var files = new List<(string Place, string Path, Target Target)>();

        if (item.Animation is { } animation)
        {
            foreach (var plan in session.AnimationPlan(animation))
                files.Add((session.PlaceOf(item), plan.GamePath, new Target(TargetKind.Item, item.Key)));
            return files;
        }

        for (int i = 0; i < item.Models.Count; i++)
        {
            var model = item.Models[i];
            var place = model.UseVariants && !model.UseDummy ? VariantPlace(session.VariantGroupOf(model), item) : session.PlaceOf(item, model);
            files.Add((place, subject.ModelGamePath(model.RaceGender), new Target(TargetKind.Model, item.Key, i)));
        }

        var races = gameData.MaterialRaces(subject, item.Models);
        for (int m = 0; m < item.Materials.Count; m++)
        {
            var mat = item.Materials[m];
            if (string.IsNullOrWhiteSpace(mat.Name)) continue;
            var materialPlace = session.PlaceOf(item, mat);
            foreach (var race in races)
                files.Add((materialPlace, subject.MaterialGamePath(subject.MaterialNameFor(mat.Name, race), race), new Target(TargetKind.Material, item.Key, m)));
            for (int t = 0; t < mat.Textures.Count; t++)
            {
                var tex = mat.Textures[t];
                if (tex.IsShared) continue;   // points at another material's file, writes nothing
                var place = tex.UseVariants && !tex.UseWhiteDummy ? VariantPlace(session.VariantGroupOf(tex), item) : session.PlaceOf(item, mat, tex);
                files.Add((place, subject.TextureGamePath(MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix)),
                    new Target(TargetKind.Texture, item.Key, m, t)));
            }
        }
        return files;
    }

    /// <summary>Variant files go into the options of their group: two slots filling one group share those options.</summary>
    private static string VariantPlace(PackGroup? group, PortItem item)
        => group != null ? $"variants:{group.Id}" : $"variants:item:{item.Key}";

    /// <summary>A file's key: its place and its game path, the way the build tells files apart.</summary>
    internal static string WrittenKey(string place, string gamePath) => $"{place}|{gamePath}";

    /// <summary>Two items in the pack writing the same file into the same place (e.g. two gear items sharing a model id) overwrite each other.</summary>
    internal static void ValidateAcrossItems(PortItem item, IReadOnlyList<(string Place, string Path, Target Target)> files,
        Dictionary<string, PortItem> writtenBy, HashSet<string> writtenPaths, List<PortIssue> issues)
    {
        var subject = item.Subject;
        var reported = new HashSet<Target>();
        foreach (var (place, path, target) in files)
        {
            writtenPaths.Add(path);
            var key = WrittenKey(place, path);
            if (writtenBy.TryGetValue(key, out var owner))
            {
                if (owner != item && reported.Add(target))
                    issues.Add(new(Severity.Error, target,
                        $"{subject.DisplayName} and {owner.Subject.DisplayName} both write {path} — one overwrites the other."));
                continue;
            }
            writtenBy[key] = item;
        }
    }

    /// <summary>Counts issues of a severity whose target lives on a stage.</summary>
    public static int Count(IReadOnlyList<PortIssue> issues, StageId stage, Severity severity)
        => issues.Count(i => i.Target.Stage == stage && i.Severity == severity);

    /// <summary>
    /// A shared texture only points at a game path; something in the pack must still write it,
    /// or the material falls back to whatever the game has there (usually nothing).
    /// </summary>
    private static void ValidateSharedTextures(PortSession session, HashSet<string> writtenPaths, List<PortIssue> issues)
    {
        foreach (var item in session.Items)
        {
            for (int m = 0; m < item.Materials.Count; m++)
            {
                var mat = item.Materials[m];
                for (int t = 0; t < mat.Textures.Count; t++)
                {
                    var tex = mat.Textures[t];
                    if (!tex.IsShared || writtenPaths.Contains(tex.SharedGamePath)) continue;
                    issues.Add(new(Severity.Warning, new Target(TargetKind.Texture, item.Key, m, t),
                        $"{mat.Name} / {TextureTypeInfo.DisplayName(tex.Type)}: shares {tex.SharedGamePath}, but nothing in this modpack writes it " +
                        "(was the original renamed or removed?) — make it separate, or copy the material again."));
                }
            }
        }
    }

    /// <summary>The most severe issue on this target or anything under it in the tree, if any.</summary>
    public static Severity? WorstUnder(IReadOnlyList<PortIssue> issues, Target node)
    {
        Severity? worst = null;
        foreach (var issue in issues)
            if (issue.Target.IsWithin(node) && (worst == null || issue.Severity > worst.Value))
                worst = issue.Severity;
        return worst;
    }

    /// <summary>The most severe issue on exactly this target, if any.</summary>
    public static PortIssue? Worst(IReadOnlyList<PortIssue> issues, Target target)
    {
        PortIssue? worst = null;
        foreach (var issue in issues)
            if (issue.Target == target && (worst == null || issue.Severity > worst.Value.Severity))
                worst = issue;
        return worst;
    }
}
