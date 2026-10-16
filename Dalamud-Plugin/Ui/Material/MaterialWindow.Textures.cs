using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services;
using InstantEdit.Services.Materials;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Ui.Material;

/// <summary>The Textures section: one card per slot with its thumbnail, path and sampling, and adding or removing slots.</summary>
internal sealed partial class MaterialWindow
{
    private const uint NormalSampler = 0x0C5EC1F1;
    private const uint MaskSampler = 0x8A4E82B6;
    private const uint IndexSampler = 0x565F8FD8;
    private const uint DiffuseSampler = 0x115306BE;

    private readonly Dictionary<int, string> pathEdits = [];
    private Task<Dictionary<string, string?>>? resolving;
    private IReadOnlyList<string> resolveWanted = [];
    private Dictionary<string, string?> resolvedTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks Penumbra which file each texture path loads for the character, for the thumbnails.</summary>
    private void ResolveTextures(MaterialDocument doc)
    {
        var paths = doc.TexturePaths.Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        resolveWanted = paths;
        if (resolving != null)
            return;
        if (collection is not { } id)
        {
            resolvedTextures = paths.ToDictionary(path => path, path => (string?)path, StringComparer.OrdinalIgnoreCase);
            RefreshUsage(doc);
            return;
        }
        resolving = Task.Run(async () =>
        {
            var files = await penumbra.ResolveCollectionPathsAsync(id, paths).ConfigureAwait(false);
            return paths.Select((path, i) => (path, files[i])).ToDictionary(pair => pair.path, pair => pair.Item2, StringComparer.OrdinalIgnoreCase);
        });
    }

    private void PollTextures()
    {
        if (resolving is not { IsCompleted: true } done)
            return;
        resolving = null;
        if (done.IsCompletedSuccessfully)
            resolvedTextures = done.Result;
        else
            log.Debug(done.Exception!, "Could not resolve the material's textures.");
        if (resolveWanted.Any(path => !resolvedTextures.ContainsKey(path)) && session != null)
            ResolveTextures(session.Current);
        else if (session != null)
            RefreshUsage(session.Current);
    }

    private void DrawTextures(MaterialSession current, MaterialDocument doc)
    {
        Widgets.SectionHeader("Textures");
        var facts = ShaderPackages.Get(doc.ShaderPackage);
        for (var i = 0; i < doc.Slots.Count; i++)
            DrawSlot(current, doc, facts, i);
        if (facts == null || working != null)
            return;
        var missing = facts.Samplers.Where(sampler => doc.Slots.All(slot => slot.SamplerId != sampler.Id)).ToList();
        if (missing.Count == 0)
            return;
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##material-add-slot", "Add a texture slot");
        if (!combo.Success)
            return;
        foreach (var sampler in missing)
        {
            var read = facts.Samples(sampler.Id, doc.Keys);
            if (ImGui.Selectable($"{ShaderLabels.Sampler(sampler.Id)}{(read ? string.Empty : " (not read with these keys)")}##{sampler.Id:X8}"))
            {
                var path = SuggestedPath(doc, sampler.Id);
                _ = current.Apply(d => d.WithAddedSlot(sampler.Id, path), "Add texture slot");
                Say(FeedbackSeverity.Info, $"Added a {ShaderLabels.Sampler(sampler.Id)} slot at {path}. Change the path if that texture lives elsewhere.");
            }
        }
    }

    private void DrawSlot(MaterialSession current, MaterialDocument doc, ShaderPackageFacts? facts, int index)
    {
        var slot = doc.Slots[index];
        using var id = ImRaii.PushId($"slot-{index}");
        ImGui.Separator();
        var thumb = Theme.Scaled(56);
        var start = ImGui.GetCursorPosX();
        DrawThumbnail(slot.Path, thumb);
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Group())
        {
            ImGui.TextColored(SlotColour(slot.SamplerId), ShaderLabels.Sampler(slot.SamplerId));
            if (facts != null && !facts.Samples(slot.SamplerId, doc.Keys))
            {
                ImGui.SameLine(0, Theme.Gap);
                ImGui.TextColored(Theme.Warning, facts.DeclaresSampler(slot.SamplerId) ? "Not read with these keys" : "Not read by this shader");
            }
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.GetFrameHeight()));
            using (ImRaii.Disabled(working != null))
            {
                if (Widgets.GhostIconButton("##remove-slot", FontAwesomeIcon.Trash, "Remove this texture slot"))
                {
                    _ = current.Apply(d => d.WithoutSlot(index), "Remove texture slot");
                    pathEdits.Clear();
                    return;
                }
            }

            var text = pathEdits.TryGetValue(index, out var editing) ? editing : slot.Path;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##path", ref text, 512))
                pathEdits[index] = text;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                pathEdits.Remove(index);
                var path = PathRules.NormalizeGamePath(text.Trim());
                if (!ModPaths.IsSafeGameResourcePath(path, ".tex"))
                    Say(FeedbackSeverity.Warning, "A texture path is a game path ending in .tex, such as chara/equipment/e0001/texture/v01_c0101e0001_top_norm.tex.");
                else if (!string.Equals(path, slot.Path, StringComparison.Ordinal))
                    _ = current.Apply(d => d.WithSlot(index, slot with { Path = path }), "Texture path");
            }
            else if (!ImGui.IsItemActive())
                pathEdits.Remove(index);
            Tip(resolvedTextures.TryGetValue(slot.Path, out var file) && file != null && Path.IsPathRooted(file)
                ? $"The game path. The character loads it from:\n{file}"
                : "The game path. No mod replaces it for the character.");
        }

        using (ImRaii.Disabled(!doc.Editable))
        {
            ImGui.SetCursorPosX(start);
            var dx11 = slot.Dx11;
            if (ImGui.Checkbox("DX11##dx11", ref dx11))
                _ = current.Apply(d => d.WithSlot(index, slot with { Dx11 = dx11 }), "DX11 texture");
            Tip("The game asks for the texture's DX11 file (its name starts with --).");
            ImGui.SameLine(0, Theme.Gap * 2);
            DrawWrap(current, index, slot, "Across", slot.WrapU, wrap => slot.WithWrap(wrap, slot.WrapV));
            ImGui.SameLine(0, Theme.Gap);
            DrawWrap(current, index, slot, "Down", slot.WrapV, wrap => slot.WithWrap(slot.WrapU, wrap));
            ImGui.SameLine(0, Theme.Gap * 2);
            var bias = slot.LodBias;
            ImGui.SetNextItemWidth(-1);
            var changed = ImGui.SliderFloat("##lod", ref bias, -8f, 7.98f, "Blur %.2f");
            Track(current, changed, d => d.WithSlot(index, d.Slots[index].WithLodBias(bias)), "Texture blur");
            Tip("Below 0 is sharper, above 0 blurrier.");
        }
    }

    private static void DrawWrap(MaterialSession current, int index, MaterialSlot slot, string label, int wrap, Func<int, MaterialSlot> with)
    {
        ImGui.SetNextItemWidth(Theme.Scaled(84));
        using (var combo = ImRaii.Combo($"##wrap-{label}", ShaderLabels.WrapModes[Math.Clamp(wrap, 0, 3)]))
        {
            if (combo.Success)
                for (var mode = 0; mode < ShaderLabels.WrapModes.Length; mode++)
                    if (ImGui.Selectable(ShaderLabels.WrapModes[mode], mode == wrap) && mode != wrap)
                    {
                        var chosen = with(mode);
                        _ = current.Apply(d => d.WithSlot(index, chosen), "Texture wrap");
                    }
        }
        Tip($"How the texture repeats {label.ToLowerInvariant()}.");
    }

    private void DrawThumbnail(string path, float size)
    {
        var box = new Vector2(size);
        if (path.Length == 0 || !resolvedTextures.TryGetValue(path, out var file) || file == null)
        {
            var position = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRect(position, position + box, ImGui.GetColorU32(Theme.WithAlpha(Theme.Muted, .25f)), Theme.Scaled(3));
            ImGui.Dummy(box);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(resolving != null ? "Looking for the texture" : "The texture was not found.");
            return;
        }
        var entry = previews.GetTexture(previews.KeyFor(file, !Path.IsPathRooted(file)));
        Widgets.PreviewImage(entry, box);
        if (ImGui.IsItemHovered() && Widgets.HoverDelay("material-thumb:" + file))
        {
            using var tooltip = ImRaii.Tooltip();
            Widgets.PreviewImage(entry, new Vector2(Theme.Scaled(256)));
            Widgets.PreviewCaption(entry);
        }
    }

    private static Vector4 SlotColour(uint sampler) => sampler switch
    {
        NormalSampler => Theme.NormalTexture,
        MaskSampler => Theme.MaskTexture,
        IndexSampler => Theme.IndexTexture,
        DiffuseSampler => Theme.BaseTexture,
        _ => Theme.OtherTexture,
    };

    /// <summary>A path for a new slot beside the normal map: …_norm.tex becomes …_id.tex, …_mask.tex and so on.</summary>
    private static string SuggestedPath(MaterialDocument doc, uint sampler)
    {
        var normal = doc.Slots.FirstOrDefault(slot => slot.SamplerId == NormalSampler && slot.Path.Length > 0)?.Path
                     ?? doc.Slots.FirstOrDefault(slot => slot.Path.Length > 0)?.Path
                     ?? "chara/common/texture/new_norm.tex";
        var index = MaterialSession.IndexPathFor(normal);
        var stem = index[..^"_id.tex".Length];
        return sampler switch
        {
            IndexSampler => index,
            MaskSampler => stem + "_mask.tex",
            DiffuseSampler => stem + "_base.tex",
            NormalSampler => stem + "_norm.tex",
            0xFEA0F3D2 => stem + "_fx.tex",
            0xA7E197F6 => stem + "_flow.tex",
            _ => stem + $"_{ShaderLabels.Sampler(sampler).ToLowerInvariant().Replace(' ', '_')}.tex",
        };
    }
}
