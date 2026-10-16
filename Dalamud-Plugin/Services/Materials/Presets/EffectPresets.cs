using System.Numerics;
using InstantEdit.Studio.Services;
using static InstantEdit.Services.Materials.Presets.PresetChecks;

namespace InstantEdit.Services.Materials.Presets;

/// <summary>The presets the Material window offers, in the order it shows them. Adding one is adding a class here.</summary>
public static class EffectPresets
{
    public static IReadOnlyList<MaterialEffectPreset> All { get; } =
    [
        new Metal(), new Glow(), new ScrollingPattern(), new Glass(), new SeeThrough(), new Matcap(), new Dyeable(), new DetailPattern(),
        new Velvet(), new CleanUp(),
    ];

    /// <summary>Builds a result, adding the row target's note to the warnings.</summary>
    private static PresetResult Result(MaterialDocument doc, List<string> changes, List<string> warnings, string? note = null, params PresetTexture[] textures)
    {
        if (note != null)
            warnings.Insert(0, note);
        return new PresetResult(doc, changes, warnings, textures);
    }

    /// <summary>Switches the shader and fits the material to it; the fitting notes become changes.</summary>
    private static MaterialDocument Switch(MaterialDocument doc, string package, List<string> changes)
    {
        if (doc.ShaderPackage.Equals(package, StringComparison.OrdinalIgnoreCase) || ShaderPackages.Get(package) is not { } facts)
            return doc;
        var (switched, notes) = MaterialSession.Reconcile(doc.WithShaderPackage(package), facts);
        changes.Add($"Shader is now {package}.");
        changes.AddRange(notes);
        return switched;
    }

    private sealed class Metal : MaterialEffectPreset
    {
        public override string Name => "Metal";
        public override string Description => "Rows reflect like metal, in their own colour.";
        public override bool NeedsRows => true;
        public override string Requirement => "Needs Dawntrail colour rows.";
        public override bool Applies(PresetContext context) => HasRows(context);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var (rows, note) = Rows(context, target);
            var doc = EditRows(context.Doc, rows, row => row with { Metalness = 1f, Roughness = 0.2f, Specular = row.Diffuse, SheenRate = 0f });
            var warnings = new List<string>();
            if (context.Mask is { } mask)
            {
                var (red, green) = MaskMeans(mask);
                if (green > 0.6f)
                    warnings.Add("The mask's green channel is rough; the metal will look dull. Darken it in the mask.");
                if (red < 0.3f)
                    warnings.Add("The mask's red channel is dark; the metal will barely shine. Brighten it in the mask.");
            }
            else
                warnings.Add("The mask texture couldn't be read, so it wasn't checked.");
            return Result(doc, [$"Metalness 100% and roughness 20% on {RowCount(rows.Length)}; the shine takes the base colour."], warnings, note);
        }
    }

    private sealed class Glow : MaterialEffectPreset
    {
        public override string Name => "Glow";
        public override string Description => "Rows (or the glow colour) light up.";
        public override bool NeedsRows => true;
        public override string Requirement => "Needs colour rows or a shader that reads the glow colour.";
        public override bool Applies(PresetContext context) => HasRows(context) || Reads(context, EmissiveColor);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var doc = context.Doc;
            var changes = new List<string>();
            var warnings = new List<string>();
            string? note = null;
            if (HasRows(context))
            {
                (var rows, note) = Rows(context, target);
                doc = EditRows(doc, rows, row => row with { Emissive = row.Diffuse * 2f, Exposure = 2f });
                changes.Add($"Glow at twice the base colour and 1 stop brighter on {RowCount(rows.Length)}.");
            }
            if (Reads(context, EmissiveColor))
            {
                doc = doc.WithConstant(EmissiveColor, [1f, 1f, 1f]);
                changes.Add("Glow colour is white.");
            }
            else if (doc.ShaderPackage.Equals("character.shpk", StringComparison.OrdinalIgnoreCase))
                warnings.Add("On this shader only the rows glow. The glow colour value does nothing.");
            return Result(doc, changes, warnings, note);
        }
    }

    private sealed class ScrollingPattern : MaterialEffectPreset
    {
        public const uint ReflectionSource = 0xF886E10E;
        public const uint Catchlight = 0x9A8A46F5;

        public override string Name => "Scrolling pattern";
        public override string Description => "A pattern scrolls across the rows, like RGB hair.";
        public override bool NeedsRows => true;
        public override string Requirement => "Needs normal, mask and index textures.";

        public override bool Applies(PresetContext context)
            => new[] { NormalSampler, MaskSampler, IndexSampler }.All(sampler => context.Doc.Slots.Any(slot => slot.SamplerId == sampler && slot.Path.Length > 0));

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var changes = new List<string>();
            var doc = Switch(context.Doc, "characterscroll.shpk", changes);
            doc = doc.WithKey(ReflectionSource, Catchlight);
            var existing = doc.Slots.FirstOrDefault(slot => slot.SamplerId == CatchlightSampler && slot.Path.Length > 0);
            var textures = new List<PresetTexture>();
            if (existing == null)
            {
                var path = BesideMaterial(context.MaterialGamePath, "_fx.tex");
                doc = WithSamplerPath(doc, CatchlightSampler, path);
                textures.Add(new PresetTexture("Scroll pattern", path, ScrollPattern()));
                changes.Add($"Added the pattern texture {path}. Replace it with your own image later.");
            }
            doc = doc.WithConstant(0x738A241C, [0.2f]).WithConstant(0x71CC9A45, [0.2f]).WithConstant(0x43345395, [1.5f]).WithConstant(0x4172EDCC, [1.5f]);
            changes.Add("Reflections come from the pattern; layer 1 scrolls at 0.2 and repeats 1.5 times.");
            var (rows, note) = Rows(context with { Doc = doc }, target);
            doc = EditRows(doc, rows, row => row with { ScrollLayer = 1f });
            changes.Add($"{RowCount(rows.Length)} show layer 1.");
            return Result(doc, changes, [], note, [.. textures]);
        }
    }

    private sealed class Glass : MaterialEffectPreset
    {
        public override string Name => "Glass";
        public override string Description => "See-through glass that bends what is behind it.";
        public override bool Applies(PresetContext context) => context.Facts.SamplesColorTable;
        public override string Requirement => "Needs a gear shader.";

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var changes = new List<string>();
            var doc = Switch(context.Doc, "characterglass.shpk", changes);
            doc = doc.WithConstant(0x7801E004, [1.5f]).WithConstant(0xC4647F37, [0.1f]).WithFlags(doc.ShaderFlags | Translucent);
            changes.Add("Refraction 1.5, glass thickness 0.1, translucent.");
            return Result(doc, changes, []);
        }
    }

    private sealed class SeeThrough : MaterialEffectPreset
    {
        public override string Name => "See-through";
        public override string Description => "The material's alpha shows through.";
        public override string Requirement => "Translucent stockings crash the game.";
        public override bool Applies(PresetContext context) => !context.Doc.ShaderPackage.Equals("characterstockings.shpk", StringComparison.OrdinalIgnoreCase);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var doc = context.Doc.WithFlags(context.Doc.ShaderFlags | Translucent);
            var changes = new List<string> { "Translucent." };
            var warnings = new List<string>();
            foreach (var (id, value, label) in new[] { (0x29AC0223u, 0.5f, "alpha cutoff 50%"), (0xD62BF368u, 2f, "alpha sharpness 2"), (0xD07A6A65u, 0f, "alpha offset 0") })
            {
                doc = doc.WithConstant(id, [value]);
                changes.Add(label[..1].ToUpperInvariant() + label[1..] + ".");
            }
            if (!Reads(context, 0x29AC0223))
                warnings.Add("This shader ignores the alpha values; only the translucent flag applies.");
            return Result(doc, changes, warnings);
        }
    }

    private sealed class Matcap : MaterialEffectPreset
    {
        public override string Name => "Matcap";
        public override string Description => "A painted lighting ball from the matcap library shades it.";
        public override string Requirement => "Needs a normal map.";
        public override bool Applies(PresetContext context) => context.Doc.Slots.Any(slot => slot.SamplerId == NormalSampler && slot.Path.Length > 0);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var changes = new List<string>();
            var doc = Switch(context.Doc, "characterreflection.shpk", changes);
            doc = WithSamplerPath(doc, CatchlightSampler, MaterialPatcher.MatcapLibraryDefault);
            changes.Add($"The catchlight texture is {MaterialPatcher.MatcapLibraryDefault}. Pick another from the matcap library in Textures.");
            return Result(doc, changes, []);
        }
    }

    private sealed class Dyeable : MaterialEffectPreset
    {
        private const ushort Fields = 0x37; // base, shine, glow, metalness, roughness

        public override string Name => "Dyeable";
        public override string Description => "Rows take the dye's colour, shine, glow, metalness and roughness.";
        public override bool NeedsRows => true;
        public override string Requirement => "Needs Dawntrail colour rows.";
        public override bool Applies(PresetContext context) => HasRows(context);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var (rows, note) = Rows(context, target);
            var table = context.Doc.Table!.WithDyeTable(true).Clone();
            foreach (var row in rows)
                table.Dye![row] = new DtDyeRow(0).WithFlags(Fields).WithTemplate(context.DyeTemplate).WithChannel(0);
            var warnings = new List<string>();
            if (context.DyeTemplate == 0)
                warnings.Add("Pick each row's dye template in its Dye section.");
            return Result(context.Doc.WithTable(table), [$"{RowCount(rows.Length)} take dye channel 1."], warnings, note);
        }
    }

    private sealed class DetailPattern : MaterialEffectPreset
    {
        public override string Name => "Detail pattern";
        public override string Description => "A fine fabric-like pattern over the rows (or the skin).";
        public override bool NeedsRows => true;
        public override string Requirement => "Needs colour rows or a skin shader.";
        public override bool Applies(PresetContext context) => HasRows(context) || Reads(context, TileIndex);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var doc = context.Doc;
            var changes = new List<string>();
            string? note = null;
            if (HasRows(context))
            {
                (var rows, note) = Rows(context, target);
                doc = EditRows(doc, rows, row => row with { TileIndex = 41, TileAlpha = 1f, TileUU = 16f, TileUV = 0f, TileVU = 0f, TileVV = 16f });
                changes.Add($"Detail pattern 41 at full strength, 16 times across and down, on {RowCount(rows.Length)}.");
            }
            if (Reads(context, TileIndex))
            {
                doc = doc.WithConstant(TileIndex, [41f]).WithConstant(TileScale, [16f, 16f]).WithConstant(TileAlpha, [1f]);
                changes.Add("Detail pattern 41 at full strength, 16 times across and down.");
            }
            return Result(doc, changes, [], note);
        }
    }

    private sealed class Velvet : MaterialEffectPreset
    {
        public override string Name => "Velvet";
        public override string Description => "Soft sheen at the edges, like velvet.";
        public override bool NeedsRows => true;
        public override string Requirement => "Needs colour rows or a shader that reads sheen.";
        public override bool Applies(PresetContext context) => HasRows(context) || Reads(context, SheenRate);

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var doc = context.Doc;
            var changes = new List<string>();
            string? note = null;
            if (HasRows(context))
            {
                (var rows, note) = Rows(context, target);
                doc = EditRows(doc, rows, row => row with { SheenRate = 0.3f, SheenTint = 0.5f, SheenAperture = 5f });
                changes.Add($"Sheen 30%, sheen tint 50%, sheen spread 5 on {RowCount(rows.Length)}.");
            }
            if (Reads(context, SheenRate))
            {
                doc = doc.WithConstant(SheenRate, [0.3f]).WithConstant(SheenTint, [0.5f]).WithConstant(SheenAperture, [5f]);
                changes.Add("Sheen 30%, sheen tint 50%, sheen spread 5.");
            }
            return Result(doc, changes, [], note);
        }
    }

    private sealed class CleanUp : MaterialEffectPreset
    {
        private const uint VertexColour = 0xF52CCF05;
        private const uint UsualVertexColour = 0xA7D2FF60;

        public override string Name => "Clean up";
        public override string Description => "Removes keys and values this shader ignores.";
        public override bool Applies(PresetContext context) => context.Doc.Editable;

        public override PresetResult Apply(PresetContext context, RowTarget target)
        {
            var doc = context.Doc;
            var facts = context.Facts;
            var changes = new List<string>();
            foreach (var (id, value) in doc.Keys)
            {
                var key = facts.Key(id);
                if (key != null && key.Values.Contains(value))
                    continue;
                doc = doc.WithoutKey(id);
                changes.Add(key == null
                    ? $"Removed {ShaderLabels.Key(id).Label}: this shader doesn't have it."
                    : $"Removed {ShaderLabels.Key(id).Label} = {ShaderLabels.KeyValue(id, value)}: this shader has no such value.");
            }
            foreach (var constant in context.Doc.Constants)
            {
                if (facts.Reads(constant.Id, doc.Keys))
                    continue;
                doc = doc.WithoutConstant(constant.Id);
                changes.Add($"Removed {ShaderLabels.Constant(constant.Id).Label}: this shader ignores it.");
            }
            if (facts.Key(VertexColour) is { } vertex && vertex.Values.Contains(UsualVertexColour) && doc.KeyValue(VertexColour) != UsualVertexColour)
            {
                doc = doc.WithKey(VertexColour, UsualVertexColour);
                changes.Add("Vertex colour is now the usual one for gear.");
            }
            if (changes.Count == 0)
                changes.Add("Nothing to clean up.");
            return Result(doc, changes, []);
        }
    }
}
