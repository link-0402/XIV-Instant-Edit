using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using ComboWheel = InstantEdit.Ui.ComboWheel;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// Edits one texture slot — where its image comes from (a file, a folder of variants, or a
/// placeholder), BC7, and its name. <see cref="DrawCompact"/> is the one-line version in the
/// model tree; <see cref="DrawFull"/> is the inspector card. Both edit the same slot through
/// the same rules, so they never disagree.
/// </summary>
internal sealed class TextureSlotEditor
{
    private enum SourceMode { File, Variants, Dummy, Alpha, Diffuse, Maps }

    private static readonly string[] SourceModeLabels = { "File", "Variants", "Dummy", "Add alpha", "From diffuse", "From maps" };

    private const string ImageFilter = "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}";

    private readonly ISetUpSession _session;
    private readonly TextureThumbnailCache _thumbnails;
    private (TextureSlot? Slot, float Value) _strengthDrag;

    public TextureSlotEditor(ISetUpSession session, TextureThumbnailCache thumbnails)
    {
        _session    = session;
        _thumbnails = thumbnails;
    }

    private static SourceMode ModeOf(TextureSlot tex)
        => tex.UseWhiteDummy ? SourceMode.Dummy : tex.UseVariants ? SourceMode.Variants : tex.UseAlphaOverlay ? SourceMode.Alpha
         : tex.UseDiffuseNormal ? SourceMode.Diffuse : tex.UseMadeMask ? SourceMode.Maps : SourceMode.File;

    private static void SetMode(TextureSlot tex, SourceMode mode)
    {
        // From diffuse puts a diffuse in SourcePath: the slot's own file waits aside until it is left.
        if (mode == SourceMode.Diffuse && !tex.UseDiffuseNormal)
            (tex.NormalSourcePath, tex.SourcePath) = (tex.SourcePath, string.Empty);
        else if (mode != SourceMode.Diffuse && tex.UseDiffuseNormal)
            (tex.SourcePath, tex.NormalSourcePath) = (tex.NormalSourcePath, string.Empty);

        tex.UseVariants      = mode == SourceMode.Variants;
        tex.UseWhiteDummy    = mode == SourceMode.Dummy;
        tex.UseAlphaOverlay  = mode == SourceMode.Alpha;
        tex.UseDiffuseNormal = mode == SourceMode.Diffuse;
        tex.UseMadeMask      = mode == SourceMode.Maps;
    }

    /// <summary>
    /// Add alpha is offered on the normal slot of a hair or gear material, From diffuse on the normal
    /// slot of a gear material, From maps on the mask slot of a gear material; each is kept on a slot
    /// that already uses it.
    /// </summary>
    private static bool Offers(MaterialSetup? mat, TextureSlot tex, SourceMode mode) => mode switch
    {
        SourceMode.Alpha   => tex.UseAlphaOverlay || tex.Type == TextureType.Normal
                              && (mat?.ShaderType == ShaderType.Hair || GameNormal.ReadsBlueAsOpacity(mat?.ShaderType)),
        SourceMode.Diffuse => tex.UseDiffuseNormal || tex.Type == TextureType.Normal && mat?.ShaderType == ShaderType.Character,
        SourceMode.Maps    => tex.UseMadeMask || tex.Type == TextureType.Mask && mat?.ShaderType == ShaderType.Character,
        _                  => true,
    };

    /// <summary>Switching to From diffuse starts from the material's own diffuse (its first variant), when it has one; else none is set.</summary>
    private static void StartFromDiffuse(MaterialSetup mat, TextureSlot tex)
    {
        var diffuse = mat.Textures.FirstOrDefault(t => t.Type == TextureType.Diffuse && !t.UseWhiteDummy);
        var image = diffuse == null ? null
                  : diffuse.UseVariants ? TextureSources.VariantImages(diffuse).FirstOrDefault()
                  : diffuse.SourcePath;
        if (!string.IsNullOrWhiteSpace(image) && System.IO.File.Exists(image))
            tex.SourcePath = image;
    }

    private static string ModeTooltip(TextureSlot tex, SourceMode mode)
    {
        var options = DummyTextureLibrary.OptionsFor(tex.Type);
        return mode switch
        {
            SourceMode.File     => "One image file (png / jpeg / tga / dds), converted to .tex — or a .tex, used as it is.",
            SourceMode.Variants => "Several images — each becomes one option of a switch group in the mod.",
            SourceMode.Alpha    => "A base normal map with an alpha map put into its opacity: black where there is no hair or cloth, "
                                 + "white where there is. Hair keeps it in the alpha, gear in the blue.",
            SourceMode.Diffuse  => "A normal made from a diffuse texture's shading, for ports that come without a usable normal. "
                                 + "The diffuse's alpha becomes the opacity.",
            SourceMode.Maps     => "A mask made from another game's maps, laid out like the game's own: roughness in green, "
                                 + "ambient occlusion in red and blue.",
            _ => options.Count > 1
                ? "A placeholder texture — pick which one. No source image needed."
                : $"A placeholder texture ({DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset).Label}). No source image needed.",
        };
    }

    /// <summary>
    /// The drop rule for a slot: one image sets File mode, unless the slot already takes variants.
    /// Several images, or folders (their images), are added to the slot's variants.
    /// </summary>
    public FileDropRule DropRule(TextureSlot tex) => new(FileDrop.RejectForTextureSlot, paths =>
    {
        if (paths.Files.Count == 1 && paths.Folders.Count == 0 && ModeOf(tex) != SourceMode.Variants)
        {
            tex.SourcePath = paths.Files[0];
            SetMode(tex, ModeOf(tex) is SourceMode.Alpha or SourceMode.Diffuse or SourceMode.Maps ? ModeOf(tex) : SourceMode.File);
            _session.MarkDirty();
            _session.Notify($"{TextureTypeInfo.DisplayName(tex.Type)} ← {paths.Describe()}", Severity.Info);
            return;
        }

        SetMode(tex, SourceMode.Variants);
        int added = TextureSources.AddVariantImages(tex, paths.Folders.Concat(paths.Files));
        _session.MarkDirty();
        _session.Notify($"{TextureTypeInfo.DisplayName(tex.Type)}: {added} variant image(s) added, {tex.VariantPaths.Count} in all.", Severity.Info);
    });

    /// <summary>The variant files, one row each with a remove button, then a button that adds more (and takes drops).</summary>
    private bool DrawVariantList(TextureSlot tex)
    {
        bool edited = false;
        var drop = DropRule(tex);

        // A set-up from before files could be picked still reads its folder until files are added.
        if (tex.VariantPaths.Count == 0 && !string.IsNullOrWhiteSpace(tex.SourcePath))
        {
            ImGui.TextColored(Theme.Muted, $"folder {System.IO.Path.GetFileName(tex.SourcePath.TrimEnd('\\', '/'))}");
            Gui.Tooltip($"{tex.SourcePath}\n\nIts images are the variants. Adding files turns them into a list of their own.");
        }
        else if (tex.VariantPaths.Count == 0)
        {
            Gui.Hint("(no files yet — add or drop images, or a folder of them)");
        }

        int remove = -1;
        for (int i = 0; i < tex.VariantPaths.Count; i++)
        {
            var path = tex.VariantPaths[i];
            ImGui.PushID(i);
            if (Gui.IconButton(FontAwesomeIcon.Trash, "Remove", "Remove this file", danger: true))
                remove = i;
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(System.IO.File.Exists(path) ? Theme.Muted : Theme.Bad, System.IO.Path.GetFileName(path));
            Gui.Tooltip(path);
            ImGui.PopID();
        }
        if (remove >= 0)
        {
            tex.VariantPaths.RemoveAt(remove);
            edited = true;
        }

        var startFrom = tex.VariantPaths.Count > 0 ? tex.VariantPaths[^1] : tex.SourcePath;
        Gui.PickMultipleFiles("AddTextureVariants", "Add file(s)", "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}", picked =>
        {
            TextureSources.AddVariantImages(tex, picked);
            _session.MarkDirty();
        }, startFrom);
        Gui.Tooltip("Or drop images, or a folder of them, on this button — they are added to the list.");
        if (FileDrop.Target(drop.Reject, out var dropped))
            drop.OnDrop(dropped);
        return edited;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compact (tree row)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mode combo, then the path (or the placeholder choice), then BC7, filling the width that is
    /// left minus <paramref name="reserveRight"/>. Returns true when something was edited.
    /// </summary>
    public bool DrawCompact(TextureSlot tex, float reserveRight)
    {
        if (tex.IsShared)
        {
            ImGui.AlignTextToFramePadding();
            Gui.Icon(FontAwesomeIcon.Link, Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, $"shared · {System.IO.Path.GetFileName(tex.SharedGamePath)}");
            Gui.Tooltip($"Uses {tex.SharedGamePath}, which the material it was copied from writes. No texture of its own is written.");
            ImGui.SameLine();
            Gui.AlignRight(ImGui.CalcTextSize("Separate").X + ImGui.GetStyle().FramePadding.X * 2 + reserveRight);
            if (ImGui.Button("Separate##unshare"))
            {
                tex.SharedGamePath = string.Empty;   // its own file again, from the source fields it kept
                _session.MarkDirty();
                return true;
            }
            Gui.Tooltip("Write this texture under this model instead, from the same source file(s).");
            return false;
        }

        bool edited = false;
        var mode = ModeOf(tex);

        var offered = Enum.GetValues<SourceMode>().Where(m => Offers(null, tex, m)).ToArray();
        int modeIdx = Array.IndexOf(offered, mode);
        ImGui.SetNextItemWidth(Theme.S(86));
        if (ComboWheel.Combo("##mode", ref modeIdx, offered.Select(m => SourceModeLabels[(int)m]).ToArray()))
        {
            mode = offered[modeIdx];
            SetMode(tex, mode);
            edited = true;
        }
        Gui.Tooltip(ModeTooltip(tex, mode));

        float formatW = Theme.S(78);
        float rightW = formatW + ImGui.GetStyle().ItemSpacing.X + reserveRight;

        ImGui.SameLine();
        if (mode == SourceMode.Dummy)
        {
            edited |= DrawDummyChoice(tex, compact: true, rightW);
        }
        else if (mode == SourceMode.Variants)
        {
            // One button: how many files there are; clicking adds more, and it takes drops.
            var images = tex.VariantPaths.Count;
            var label = images > 0 ? $"{images} image(s) · add more"
                      : !string.IsNullOrWhiteSpace(tex.SourcePath) ? $"folder {System.IO.Path.GetFileName(tex.SourcePath.TrimEnd('\\', '/'))} · add more"
                      : "add images, or drop them here";
            var drop = DropRule(tex);
            if (ImGui.Button($"{label}##addvariants", new Vector2(Math.Max(Theme.S(60), ImGui.GetContentRegionAvail().X - rightW), 0)))
            {
                Gui.Dialogs.OpenFileDialog("Variant images", "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}", (ok, picked) =>
                {
                    if (!ok || picked.Count == 0) return;
                    TextureSources.AddVariantImages(tex, picked);
                    _session.MarkDirty();
                }, 64, string.Empty, false);
            }
            Gui.Tooltip("Each image becomes one option of a switch group in the mod. Click to add images, or drop images or a folder "
                      + "of them here. Select the texture to remove files.");
            if (FileDrop.Target(drop.Reject, out var dropped))
                drop.OnDrop(dropped);
        }
        else if (mode == SourceMode.Alpha)
        {
            edited |= DrawAlphaPicker(tex, "src", "alpha map — or drop one here", rightW);
        }
        else if (mode == SourceMode.Maps)
        {
            var maps = GameMask.Maps(tex);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, maps.Count == 0 ? "no maps yet" : $"made from {string.Join(", ", maps.Select(m => m.Role))}");
            Gui.Tooltip(maps.Count == 0 ? "Select the texture to pick its maps." : GameMask.Describe(tex));
            ImGui.SameLine();
            float fill = ImGui.GetContentRegionAvail().X - rightW;
            if (fill > 0) ImGui.Dummy(new Vector2(fill, 0));
        }
        else
        {
            string src = tex.SourcePath;
            if (Gui.PathPicker("src", ref src, "image file — or drop one here", PathKind.File, ImageFilter,
                    picked => { tex.SourcePath = picked; _session.MarkDirty(); }, reserveRight: rightW, drop: DropRule(tex)))
            {
                tex.SourcePath = src;
                edited = true;
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(formatW);
        edited |= DrawCompression(tex);
        return edited;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full (inspector)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The inspector card for a texture. <paramref name="gamePath"/> is shown as where it is written
    /// (null leaves that to the caller); without <paramref name="variants"/> a slot takes one image or
    /// a placeholder only. Returns true when something was edited.
    /// </summary>
    public bool DrawFull(MaterialSetup mat, TextureSlot tex, bool showThumbnail, string? gamePath, bool variants = true)
    {
        bool edited = false;

        if (showThumbnail)
        {
            _thumbnails.Draw(tex, Theme.S(128));
            ImGui.Spacing();
        }

        int typeIdx = (int)tex.Type;
        if (Gui.LabeledCombo("Role", "##Type", ref typeIdx, TextureTypeInfo.Labels, "What the material uses this texture for."))
        {
            var newType = TextureTypeInfo.FromIndex(typeIdx);
            // Follow the type with the postfix while it is still the default.
            if (tex.Postfix == MaterialNaming.DefaultPostfix(tex.Type))
                tex.Postfix = MaterialNaming.DefaultPostfix(newType);
            tex.Type = newType;
            // These make a normal; any other role takes a file, From diffuse giving back the slot's own.
            if (newType != TextureType.Normal && ModeOf(tex) is SourceMode.Diffuse or SourceMode.Alpha
                || newType != TextureType.Mask && ModeOf(tex) == SourceMode.Maps)
                SetMode(tex, SourceMode.File);
            edited = true;
        }

        if (tex.IsShared)
        {
            Gui.LabeledValue("Shared with", tex.SharedGamePath, "The texture this material points at.");
            ImGui.Spacing();
            Gui.HintWrapped("This slot was pasted with \"keep texture paths\": it writes no file of its own and uses the texture " +
                           "the original material writes, so both models share one set of files (and one variant switch).");
            ImGui.Spacing();
            if (Gui.IconTextButton(FontAwesomeIcon.Unlink, "Give it its own texture",
                    "Write this texture under this model instead, from the same source file(s)."))
            {
                tex.SharedGamePath = string.Empty;   // its own file again, from the source fields it kept
                _session.MarkDirty();
                return true;
            }
            return edited;
        }

        string postfix = tex.Postfix;
        if (Gui.LabeledInput("Postfix", "##Postfix", ref postfix, 64, "postfix",
                $"Appended to the material's name to form the texture name: \"{MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix)}\"."))
        {
            tex.Postfix = postfix;
            edited = true;
        }

        Gui.Label("Source");
        var mode = ModeOf(tex);
        for (int m = 0; m < SourceModeLabels.Length; m++)
        {
            if (!variants && (SourceMode)m == SourceMode.Variants && mode != SourceMode.Variants || !Offers(mat, tex, (SourceMode)m))
                continue;
            if (m > 0) ImGui.SameLine();
            if (ImGui.RadioButton($"{SourceModeLabels[m]}##fullmode", (int)mode == m))
            {
                mode = (SourceMode)m;
                SetMode(tex, mode);
                if (mode == SourceMode.Diffuse)
                    StartFromDiffuse(mat, tex);
                edited = true;
            }
            Gui.Tooltip(ModeTooltip(tex, (SourceMode)m));
        }

        if (mode == SourceMode.Dummy)
        {
            Gui.Label("Placeholder");
            edited |= DrawDummyChoice(tex, compact: false, 0);
        }
        else if (mode == SourceMode.Variants)
        {
            Gui.Label("Files", "Each image becomes one option of a switch group in the mod, named after the file.");
            edited |= DrawVariantList(tex);
        }
        else if (mode == SourceMode.Maps)
        {
            Gui.Label("Roughness", "Goes into the mask's green. A gloss or smoothness map (by its name) is turned around.");
            edited |= DrawMapPicker(tex.SourcePath, p => tex.SourcePath = p, "mapsrough");
            Gui.Label("Metallic", "Without a roughness map, metal is made glossy. Metalness itself comes from the colour set.");
            edited |= DrawMapPicker(tex.MetallicSourcePath, p => tex.MetallicSourcePath = p, "mapsmetal");
            Gui.Label("Occlusion", "Goes into the mask's red and blue. Without one they are white.");
            edited |= DrawMapPicker(tex.OcclusionSourcePath, p => tex.OcclusionSourcePath = p, "mapsao");
        }
        else
        {
            Gui.Label(mode switch { SourceMode.Alpha => "Base normal", SourceMode.Diffuse => "Diffuse", _ => "File" },
                mode switch
                {
                    SourceMode.Alpha when mat.ShaderType != ShaderType.Hair => "The normal map the opacity is put into.",
                    SourceMode.Alpha   => "The normal map the hair starts from. New hair materials take the one set in Settings.",
                    SourceMode.Diffuse => "The diffuse whose shading becomes the bumps. Any colour variant works; they share their shading.",
                    _                  => null,
                });
            string src = tex.SourcePath;
            if (Gui.PathPicker("fullsrc", ref src, "png / jpeg / tga / dds / .tex file", PathKind.File, ImageFilter,
                    picked => { tex.SourcePath = picked; _session.MarkDirty(); }, drop: DropRule(tex)))
            {
                tex.SourcePath = src;
                edited = true;
            }

            if (mode == SourceMode.Alpha)
            {
                Gui.Label("Alpha", mat.ShaderType == ShaderType.Hair
                    ? "Black where there is no hair, white where there is. Written into the normal's alpha."
                    : "Black where it is see-through, white where it is solid. Written into the normal's blue.");
                edited |= DrawAlphaPicker(tex, "fullalpha", "png / jpeg / tga / dds / .tex file", 0);
            }
            else if (mode == SourceMode.File && tex.Type == TextureType.Normal && !TextureSources.IsTex(tex.SourcePath))   // a .tex is used as it is
            {
                edited |= DrawGreenDirection(tex);
                if (GameNormal.ReadsBlueAsOpacity(mat.ShaderType) && NormalGreen.Applies(tex) && System.IO.File.Exists(tex.SourcePath))
                {
                    Gui.Label("Blue", "The game reads a gear normal's blue as opacity. A normal from another game keeps "
                                    + "something else there, so it is written solid; one made for the game is kept.");
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Muted, GameNormal.PeekCarriesOpacity(tex.SourcePath) switch
                    {
                        true  => "kept, it holds an opacity",
                        false => "made solid",
                        null  => "checking",
                    });
                }
            }
            else if (mode == SourceMode.Diffuse)
            {
                Gui.Label("Strength", "How deep the bumps are. 1 suits most Sims clothing.");
                // Applied on release, so the normal is not remade on every step of a drag.
                float strength = _strengthDrag.Slot == tex ? _strengthDrag.Value : tex.DiffuseNormalStrength;
                ImGui.SetNextItemWidth(Theme.S(140));
                ImGui.SliderFloat("##diffusestrength", ref strength, 0.1f, 3f, "%.2f");
                _strengthDrag = ImGui.IsItemActive() ? (tex, strength) : (null, 0f);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    tex.DiffuseNormalStrength = MathF.Round(Math.Clamp(strength, 0.1f, 3f), 2);
                    edited = true;
                }
            }
        }

        Gui.Label("Format", "What the texture is compressed to in the mod.");
        ImGui.SetNextItemWidth(Theme.S(140));
        edited |= DrawCompression(tex);

        if (gamePath != null)
        {
            ImGui.Spacing();
            Gui.LabeledValue("Game path", gamePath, "Where this texture is written in the mod.");
        }

        return edited;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared pieces
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the texture is written as. Auto, the default, compresses it unless the check Optimize
    /// textures uses finds that would change how it looks. Otherwise the choice costs conversion time
    /// rather than anything else: BC7 is what the game uses for its own modern textures and holds up
    /// best, but compressing a large image with it takes the better part of a minute; BC3 is the older
    /// format the game also reads and is around twenty times quicker; None skips compression entirely
    /// and ships four times the bytes, which the game then carries in memory.
    /// </summary>
    private static bool DrawCompression(TextureSlot tex)
    {
        bool edited = false;
        int index = TextureCompressionInfo.IndexOf(tex.Compression);

        if (ComboWheel.Combo("##format", ref index, TextureCompressionInfo.Labels))
        {
            tex.Compression = TextureCompressionInfo.Order[index];
            edited = true;
        }

        Gui.Tooltip("How this texture is compressed in the mod.\n\n"
            + "Auto — compressed, unless that would visibly change it. Then it stays uncompressed.\n"
            + "BC7 — what the game uses itself, and the best quality. Slow to compress: a 4096×4096 image "
            + "takes the better part of a minute, even across every core.\n"
            + "BC3 — the older block format, around twenty times quicker to compress, a little softer on "
            + "sharp gradients.\n"
            + "BC5 — stores only red and green at full precision each; ideal for a colour set / index "
            + "texture, whose blue and alpha channels go unused. About as quick to compress as BC3.\n"
            + "None — no compression. Instant to write, four times the size in game memory.\n\n"
            + "A texture is compressed once, and again only when the image itself changes.");
        return edited;
    }

    private static readonly string[] GreenLabels = { "Auto", "OpenGL (keep)", "DirectX (flip)" };

    /// <summary>Which way a normal file's green points: what Auto found, and the choice to set it by hand.</summary>
    private static bool DrawGreenDirection(TextureSlot tex)
    {
        Gui.Label("Green", "The game's normals point green up (OpenGL). Unreal and some exporters point it down (DirectX); "
                         + "those are flipped. Auto reads it from the image, and from the file name when the image is unclear.");
        int index = (int)tex.GreenDirection;
        bool edited = false;
        ImGui.SetNextItemWidth(Theme.S(140));
        if (ComboWheel.Combo("##green", ref index, GreenLabels))
        {
            tex.GreenDirection = (NormalGreenMode)index;
            edited = true;
        }

        if (NormalGreen.Applies(tex) && System.IO.File.Exists(tex.SourcePath))
        {
            var reading = tex.GreenDirection == NormalGreenMode.Auto ? NormalGreen.Peek(tex.SourcePath) : null;
            NormalGreen.Flips(tex, reading, out var why);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, why);
        }
        return edited;
    }

    /// <summary>One map of a From maps slot; one dropped image sets it, and it can be cleared.</summary>
    private bool DrawMapPicker(string current, Action<string> set, string id)
    {
        var drop = new FileDropRule(FileDrop.RejectForTextureSlot, paths =>
        {
            if (paths.Files.Count != 1 || paths.Folders.Count > 0)
            {
                _session.Notify("Drop one image as the map.", Severity.Warning);
                return;
            }
            set(paths.Files[0]);
            _session.MarkDirty();
        });

        string path = current;
        if (Gui.PathPicker(id, ref path, "none", PathKind.File, ImageFilter,
                picked => { set(picked); _session.MarkDirty(); }, drop: drop))
        {
            set(path);
            return true;
        }
        return false;
    }

    /// <summary>The alpha map of an Add alpha slot; one dropped image sets it.</summary>
    private bool DrawAlphaPicker(TextureSlot tex, string id, string hint, float reserveRight)
    {
        var drop = new FileDropRule(FileDrop.RejectForTextureSlot, paths =>
        {
            if (paths.Files.Count != 1 || paths.Folders.Count > 0)
            {
                _session.Notify("Drop one image as the alpha map.", Severity.Warning);
                return;
            }
            tex.AlphaSourcePath = paths.Files[0];
            _session.MarkDirty();
        });

        string alpha = tex.AlphaSourcePath;
        if (Gui.PathPicker(id, ref alpha, hint, PathKind.File, ImageFilter,
                picked => { tex.AlphaSourcePath = picked; _session.MarkDirty(); }, reserveRight: reserveRight, drop: drop))
        {
            tex.AlphaSourcePath = alpha;
            return true;
        }
        return false;
    }

    /// <summary>Which placeholder fills the slot.</summary>
    private static bool DrawDummyChoice(TextureSlot tex, bool compact, float reserveRight)
    {
        bool edited = false;
        var options = DummyTextureLibrary.OptionsFor(tex.Type);
        var resolved = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);

        if (options.Count > 1)
        {
            var labels = options.Select(o => o.Label).ToArray();
            int idx = Math.Max(0, options.ToList().FindIndex(o => o.Key == resolved.Key));
            ImGui.SetNextItemWidth(Theme.S(compact ? 130 : 150));
            if (ComboWheel.Combo("##DummyPreset", ref idx, labels))
            {
                tex.DummyPreset = options[idx].Key;
                resolved = options[idx];
                edited = true;
            }
            Gui.Tooltip("Which placeholder texture fills this slot.");
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, resolved.Label);
        }

        // Keep the BC7 checkbox at the same place as on file rows.
        if (compact)
        {
            ImGui.SameLine();
            float fill = ImGui.GetContentRegionAvail().X - reserveRight;
            if (fill > 0) ImGui.Dummy(new Vector2(fill, 0));
        }
        return edited;
    }

    /// <summary>A small square preview for a tree row (the thumbnail setting permitting).</summary>
    public void DrawThumb(TextureSlot tex, bool show)
    {
        if (!show)
        {
            Gui.Icon(FontAwesomeIcon.Image, Theme.Faint);
            return;
        }
        _thumbnails.Draw(tex, ImGui.GetFrameHeight());
    }
}
