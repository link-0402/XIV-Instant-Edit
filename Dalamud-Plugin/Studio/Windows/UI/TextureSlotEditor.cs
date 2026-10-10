using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// Edits one texture slot — where its image comes from (a file, a folder of variants, or a
/// placeholder), BC7, and its name. <see cref="DrawCompact"/> is the one-line version in the
/// model tree; <see cref="DrawFull"/> is the inspector card. Both edit the same slot through
/// the same rules, so they never disagree.
/// </summary>
internal sealed class TextureSlotEditor
{
    private enum SourceMode { File, Variants, Dummy }

    private static readonly string[] SourceModeLabels = { "File", "Variants", "Dummy" };
    private static readonly int[]    DummySizes       = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };
    private static readonly string[] DummySizeLabels  = DummySizes.Select(s => $"{s} × {s}").ToArray();
    private const int DefaultDummySize = 32;

    private readonly ISetUpSession _session;
    private readonly TextureThumbnailCache _thumbnails;

    public TextureSlotEditor(ISetUpSession session, TextureThumbnailCache thumbnails)
    {
        _session    = session;
        _thumbnails = thumbnails;
    }

    private static SourceMode ModeOf(TextureSlot tex)
        => tex.UseWhiteDummy ? SourceMode.Dummy : tex.UseVariants ? SourceMode.Variants : SourceMode.File;

    private static void SetMode(TextureSlot tex, SourceMode mode)
    {
        tex.UseVariants   = mode == SourceMode.Variants;
        tex.UseWhiteDummy = mode == SourceMode.Dummy;
    }

    private static string ModeTooltip(TextureSlot tex, SourceMode mode)
    {
        var options = DummyTextureLibrary.OptionsFor(tex.Type);
        return mode switch
        {
            SourceMode.File     => "One image file (png / jpeg / tga / dds), converted to .tex — or a .tex, used as it is.",
            SourceMode.Variants => "A folder of images — each becomes one option of a switch group in the mod.",
            _ => options.Count > 1
                ? "A placeholder texture — pick which one. No source image needed."
                : $"A bundled placeholder texture ({DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset).Label}). No source image needed.",
        };
    }

    /// <summary>The drop rule for a slot's path field: an image sets File mode, a folder sets Variants.</summary>
    public FileDropRule DropRule(TextureSlot tex) => new(FileDrop.RejectForTexture, paths =>
    {
        if (paths.Folders.Count == 1)
        {
            tex.SourcePath = paths.Folders[0];
            SetMode(tex, SourceMode.Variants);
        }
        else
        {
            tex.SourcePath = paths.Files[0];
            SetMode(tex, SourceMode.File);
        }
        _session.MarkDirty();
        _session.Notify($"{TextureTypeInfo.DisplayName(tex.Type)} ← {paths.Describe()}", Severity.Info);
    });

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

        int modeIdx = (int)mode;
        ImGui.SetNextItemWidth(Theme.S(86));
        if (ImGui.Combo("##mode", ref modeIdx, SourceModeLabels, SourceModeLabels.Length))
        {
            mode = (SourceMode)modeIdx;
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
        else
        {
            string src = tex.SourcePath;
            if (Gui.PathPicker("src", ref src, mode == SourceMode.Variants ? "folder of images — or drop one here" : "image file — or drop one here",
                    mode == SourceMode.Variants ? PathKind.Folder : PathKind.File, "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}",
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

    /// <summary>The inspector card for a texture. Returns true when something was edited.</summary>
    public bool DrawFull(PortSubject subject, MaterialSetup mat, TextureSlot tex, bool showThumbnail)
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
            if (m > 0) ImGui.SameLine();
            if (ImGui.RadioButton($"{SourceModeLabels[m]}##fullmode", (int)mode == m))
            {
                mode = (SourceMode)m;
                SetMode(tex, mode);
                edited = true;
            }
            Gui.Tooltip(ModeTooltip(tex, (SourceMode)m));
        }

        if (mode == SourceMode.Dummy)
        {
            Gui.Label("Placeholder");
            edited |= DrawDummyChoice(tex, compact: false, 0);
        }
        else
        {
            Gui.Label(mode == SourceMode.Variants ? "Folder" : "File");
            string src = tex.SourcePath;
            if (Gui.PathPicker("fullsrc", ref src, mode == SourceMode.Variants ? "folder of variant images" : "png / jpeg / tga / dds / .tex file",
                    mode == SourceMode.Variants ? PathKind.Folder : PathKind.File, "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}",
                    picked => { tex.SourcePath = picked; _session.MarkDirty(); }, drop: DropRule(tex)))
            {
                tex.SourcePath = src;
                edited = true;
            }
        }

        Gui.Label("Format", "What the texture is compressed to in the mod.");
        ImGui.SetNextItemWidth(Theme.S(140));
        edited |= DrawCompression(tex);

        ImGui.Spacing();
        var gamePath = subject.TextureGamePath(MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix));
        Gui.LabeledValue("Game path", gamePath, "Where this texture is written in the mod.");

        return edited;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared pieces
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] CompressionLabels = { "None", "BC3", "BC7", "BC5" };

    /// <summary>
    /// What the texture is written as. The choice costs conversion time rather than anything else: BC7 is
    /// what the game uses for its own modern textures and holds up best, but compressing a large
    /// image with it takes the better part of a minute; BC3 is the older format the game also reads
    /// and is around twenty times quicker; None skips compression entirely and ships four times the
    /// bytes, which the game then carries in memory.
    /// </summary>
    private static bool DrawCompression(TextureSlot tex)
    {
        bool bundled = tex.UseWhiteDummy && !DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset).IsGenerated;
        bool edited = false;
        int index = (int)tex.Compression;

        using (Gui.Disabled(bundled))
        {
            if (ImGui.Combo("##format", ref index, CompressionLabels, CompressionLabels.Length))
            {
                tex.Compression = (TextureCompression)index;
                edited = true;
            }
        }

        Gui.Tooltip(bundled
            ? "Not applicable — this bundled placeholder is copied in as-is."
            : "How this texture is compressed in the mod.\n\n"
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

    /// <summary>Which placeholder fills the slot, and the size of a generated one.</summary>
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
            if (ImGui.Combo("##DummyPreset", ref idx, labels, labels.Length))
            {
                tex.DummyPreset = options[idx].Key;
                resolved = options[idx];
                edited = true;
            }
            Gui.Tooltip("Which placeholder texture fills this slot.");
            ImGui.SameLine();
        }

        if (resolved.IsGenerated)
        {
            int sizeIdx = Array.IndexOf(DummySizes, tex.WhiteDummySize);
            if (sizeIdx < 0) sizeIdx = Array.IndexOf(DummySizes, DefaultDummySize);
            ImGui.SetNextItemWidth(Theme.S(110));
            if (ImGui.Combo("##DummySize", ref sizeIdx, DummySizeLabels, DummySizeLabels.Length))
            {
                tex.WhiteDummySize = DummySizes[sizeIdx];
                edited = true;
            }
            Gui.Tooltip("Size of the generated white texture.");
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, compact ? resolved.Label : $"Bundled \"{resolved.Label}\" texture — no size or format options.");
            Gui.Tooltip($"Bundled placeholder texture — {resolved.Label}. Copied in as-is.");
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
