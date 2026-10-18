using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Materials;
using Lumina.Excel.Sheets;

namespace InstantEdit.Ui.Material;

/// <summary>
/// Dye rows: whether the table can be dyed, each row's template, channel and dyed fields, a preview
/// dye per channel (shown on the swatches and the character), and baking the preview into the rows.
/// </summary>
internal sealed partial class MaterialWindow
{
    private static readonly string[] DyeFields =
        ["Base", "Shine", "Glow", "Base brightness", "Metalness", "Roughness", "Sheen", "Sheen tint", "Sheen spread", "Anisotropy", "Reflection map", "Reflection"];

    // What the same bits dye on characterlegacy.shpk.
    private static readonly string[] LegacyDyeFields = ["Base", "Shine", "Glow", "Gloss", "Shine strength"];

    private Task<(StainTemplates? Templates, IReadOnlyList<(int Id, string Name, Vector4 Colour)> Names)>? dyeLoad;
    private StainTemplates? stains;
    private IReadOnlyList<(int Id, string Name, Vector4 Colour)> stainNames = [];
    private readonly int[] previewStains = [0, 0];

    /// <summary>Reads the dye templates and the dye names once, off the draw thread.</summary>
    private void EnsureDyeData()
    {
        if (dyeLoad != null || stains != null)
            return;
        dyeLoad = Task.Run(async () =>
        {
            var gud = (await data.GetFileAsync<Lumina.Data.FileResource>(StainTemplates.DawntrailPath, life.Token).ConfigureAwait(false))?.Data;
            var legacy = (await data.GetFileAsync<Lumina.Data.FileResource>(StainTemplates.LegacyPath, life.Token).ConfigureAwait(false))?.Data;
            var templates = gud == null ? null : StainTemplates.Read(gud, legacy);
            var names = data.GetExcelSheet<Stain>()
                .Where(stain => stain.RowId is > 0 and < StainTemplates.StainCount && !stain.Name.IsEmpty)
                .Select(stain => ((int)stain.RowId, stain.Name.ExtractText(), Colour(stain.Color)))
                .ToList();
            return (templates, (IReadOnlyList<(int, string, Vector4)>)names);
        });
    }

    private void PollDye()
    {
        if (dyeLoad is not { IsCompleted: true } done)
            return;
        if (done.IsCompletedSuccessfully)
            (stains, stainNames) = done.Result;
        else
        {
            log.Warning(done.Exception!, "Could not read the dye templates.");
            Say(FeedbackSeverity.Warning, "The dye templates couldn't be read, so dyes can't be previewed.");
        }
        dyeLoad = null;
    }

    private static Vector4 Colour(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    /// <summary>The table as the character shows it: dyed rows take the preview dyes.</summary>
    private ColorTable ShownTable(ColorTable table)
        => stains != null && table.Dye != null && previewStains.Any(stain => stain > 0) ? stains.Dyed(table, previewStains, LegacyShaderShown) : table;

    private bool LegacyShaderShown => session != null && LegacyConversion.IsLegacyShader(session.Current.ShaderPackage);

    /// <summary>The Dyeable switch, a preview dye per channel and Bake, above the pairs.</summary>
    private void DrawDyeBar(MaterialSession current, MaterialDocument doc, ColorTable table)
    {
        EnsureDyeData();
        var dyeable = table.Dye != null;
        if (ImGui.Checkbox("Dyeable##rows-dyeable", ref dyeable))
            _ = current.Apply(d => d.WithTable(d.Table!.WithDyeTable(dyeable)), dyeable ? "Make dyeable" : "Remove dye rows");
        Tip(dyeable ? "Rows can take dyes. Untick to remove the dye rows." : "Adds dye rows, so the material can be dyed.");
        if (!dyeable)
            return;
        for (var channel = 0; channel < previewStains.Length; channel++)
        {
            ImGui.SameLine(0, Theme.Gap * 2);
            DrawStainPicker(current, doc, channel);
        }
        ImGui.SameLine(0, Theme.Gap * 2);
        using (ImRaii.Disabled(stains == null || previewStains.All(stain => stain == 0)))
        {
            if (ImGui.Button("Bake##rows-bake") && stains != null)
            {
                var baked = stains.Dyed(table, previewStains, LegacyConversion.IsLegacyShader(doc.ShaderPackage));
                _ = current.Apply(d => d.WithTable(baked), "Bake dye");
                Say(FeedbackSeverity.Info, "The preview dyes are now the rows' own colours. The dye rows stay.");
            }
        }
        Tip("Writes the preview dyes into the rows, so the material looks dyed without a dye.");
    }

    private void DrawStainPicker(MaterialSession current, MaterialDocument doc, int channel)
    {
        var chosen = previewStains[channel];
        var name = chosen == 0 ? "No dye" : stainNames.FirstOrDefault(stain => stain.Id == chosen).Name ?? $"Dye {chosen}";
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, $"Dye {channel + 1}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Scaled(150));
        using var combo = ImRaii.Combo($"##stain-{channel}", name, ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
        {
            if (ComboWheel.Steps() is var steps and not 0)
            {
                var next = ComboWheel.Pick((int[])[0, .. stainNames.Select(stain => stain.Id)], chosen, steps);
                if (next != chosen)
                    SetPreviewStain(doc, channel, next);
            }
            return;
        }
        if (ImGui.Selectable("No dye##stain-none", chosen == 0))
            SetPreviewStain(doc, channel, 0);
        foreach (var (id, stainName, colour) in stainNames)
        {
            ImGui.ColorButton($"##swatch-{id}", colour, ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            if (ImGui.Selectable($"{stainName}##stain-{id}", chosen == id))
                SetPreviewStain(doc, channel, id);
        }
    }

    private void SetPreviewStain(MaterialDocument doc, int channel, int stain)
    {
        previewStains[channel] = stain;
        if (liveTable.Bound && doc.Table is { Kind: ColorTableKind.Dawntrail } table)
            liveTable.Set(ShownTable(table), null);
    }

    /// <summary>A row's dye: template, channel and the fields the dye replaces, with the preview dye's values.</summary>
    private void DrawRowDye(MaterialSession current, ColorTable table, int row, string name)
    {
        var dye = table.Dye![row];
        var legacyShader = LegacyShaderShown;
        var templates = (legacyShader ? stains?.LegacyTemplates : stains?.Templates) ?? [];
        if (RowLabel("Template", "Which dye template the row follows."))
        {
            var template = dye.Template;
            var preview = template == 0 ? "None" : $"Template {template}";
            using var combo = ImRaii.Combo("##dye-template", preview, ImGuiComboFlags.HeightLarge);
            if (combo.Success)
            {
                foreach (var option in (int[])[0, .. templates])
                    if (ImGui.Selectable(option == 0 ? "None##template-0" : $"Template {option}##template-{option}", option == template) && option != template)
                        _ = current.Apply(d => EditDye(d, row, r => r.WithTemplate(option)), $"{name} dye template");
            }
            else if (ComboWheel.Steps() is var steps and not 0)
            {
                var picked = ComboWheel.Pick((int[])[0, .. templates], template, steps);
                if (picked != template)
                    _ = current.Apply(d => EditDye(d, row, r => r.WithTemplate(picked)), $"{name} dye template");
            }
        }
        if (RowLabel("Dye channel", "Which of the item's dyes the row takes."))
        {
            var channel = dye.Channel;
            using var combo = ImRaii.Combo("##dye-channel", $"Dye {channel + 1}");
            if (combo.Success)
            {
                for (var option = 0; option < 2; option++)
                    if (ImGui.Selectable($"Dye {option + 1}", option == channel) && option != channel)
                        _ = current.Apply(d => EditDye(d, row, r => r.WithChannel(option)), $"{name} dye channel");
            }
            else if (ComboWheel.Scroll(ref channel, 2))
                _ = current.Apply(d => EditDye(d, row, r => r.WithChannel(channel)), $"{name} dye channel");
        }
        var stain = dye.Channel < previewStains.Length ? previewStains[dye.Channel] : 0;
        DyePack? pack = null;
        LegacyDyePack? legacyPack = null;
        if (stains != null && stain > 0 && !legacyShader && stains.TryGet(dye.Template, stain, out var found))
            pack = found;
        if (stains != null && stain > 0 && legacyShader && stains.TryGetLegacy(dye.Template, stain, out var foundLegacy))
            legacyPack = foundLegacy;
        var fields = legacyShader ? LegacyDyeFields : DyeFields;
        ImGui.TextColored(Theme.Label, "The dye changes");
        using var grid = ImRaii.Table("##dye-fields", 3, ImGuiTableFlags.SizingStretchSame);
        if (!grid.Success)
            return;
        for (var bit = 0; bit < fields.Length; bit++)
        {
            ImGui.TableNextColumn();
            var on = (dye.Flags & (1 << bit)) != 0;
            var flag = bit;
            if (ImGui.Checkbox($"{fields[bit]}##dye-{bit}", ref on))
                _ = current.Apply(d => EditDye(d, row, r => r.WithFlags((ushort)(on ? r.Flags | (1 << flag) : r.Flags & ~(1 << flag)))), $"{name} dye {fields[flag].ToLowerInvariant()}");
            if (pack != null && ImGui.IsItemHovered())
                ImGui.SetTooltip($"The preview dye gives {DyeValue(pack, bit)}.");
            if (legacyPack != null && ImGui.IsItemHovered())
                ImGui.SetTooltip($"The preview dye gives {LegacyDyeValue(legacyPack, bit)}.");
        }
    }

    private static string LegacyDyeValue(LegacyDyePack pack, int bit) => bit switch
    {
        0 => Rgb(pack.Diffuse),
        1 => pack.Specular == Vector3.Zero ? "no shine, so the row keeps its own" : Rgb(pack.Specular),
        2 => Rgb(pack.Emissive),
        3 => $"{pack.Gloss:0.#}",
        _ => $"{pack.SpecularMask * 100:0}%",
    };

    private static string DyeValue(DyePack pack, int bit) => bit switch
    {
        0 => Rgb(pack.Diffuse),
        1 => pack.Specular == Vector3.Zero ? "no shine, so the row keeps its own" : Rgb(pack.Specular),
        2 => Rgb(pack.Emissive),
        3 => $"{pack.Exposure:0.##}x",
        4 => $"{pack.Metalness * 100:0}%",
        5 => $"{pack.Roughness * 100:0}%",
        6 => $"{pack.SheenRate * 100:0}%",
        7 => $"{pack.SheenTint * 100:0}%",
        8 => $"{pack.SheenAperture:0.##}",
        9 => $"{pack.Anisotropy:0.##}",
        10 => $"{pack.SphereMapIndex}",
        _ => $"{pack.SphereMapMask * 100:0}%",
    };

    private static string Rgb(Vector3 colour) => $"{colour.X:0.###}, {colour.Y:0.###}, {colour.Z:0.###}";

    private static MaterialDocument EditDye(MaterialDocument doc, int row, Func<DtDyeRow, DtDyeRow> change)
    {
        if (doc.Table is not { Kind: ColorTableKind.Dawntrail, Dye: not null } source)
            return doc;
        var table = source.Clone();
        table.Dye![row] = change(table.Dye[row]);
        return doc.WithTable(table);
    }
}
