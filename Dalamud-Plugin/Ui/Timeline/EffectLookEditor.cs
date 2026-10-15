using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The effect editor under an effect's properties: the whole effect's tint, size, offset and turn,
/// the colours its parts use with hue and saturation, glow, opacity and light range. An effect is
/// read on a task the first time it is shown. The changes stay on the event as its look until a
/// save or preview writes them into a copy of the effect (see <see cref="TimelineEffectLooks.Bake"/>).
/// </summary>
internal sealed class EffectLookEditor(TimelineSession session)
{
    private const string SwatchPopup = "##effect-swatch";
    private const int MaxSwatches = 24;
    private static readonly TimelineEffectEdit NoLook = new();

    // Effects read for the editor, by game path, as the collection in effectsCollection resolves them.
    private readonly Dictionary<string, EffectRead> effectReads = new(StringComparer.OrdinalIgnoreCase);
    private Guid? effectsCollection;
    // A look control's changes until no control is active make one undo step.
    private bool lookEditing;
    private bool sizePerAxis;
    // The swatch whose colour the popup changes.
    private Vector3 swatchEditing;

    private sealed class EffectRead
    {
        public Task<(byte[] Bytes, TimelineEffectLook Look)>? Task;
        public byte[]? Bytes;
        public TimelineEffectLook? Look;
        public string? Error;
    }

    /// <summary> Forgets the effects read for another clip. </summary>
    public void Forget()
    {
        effectReads.Clear();
        effectsCollection = null;
    }

    /// <summary> The effect at <paramref name="gamePath"/> for the editor: an import's own bytes, else read from the collection on a task. </summary>
    private EffectRead EffectFor(string gamePath, AnimationTimelineSource current)
    {
        if (effectsCollection != current.Capture.CollectionId)
        {
            effectReads.Clear();
            effectsCollection = current.Capture.CollectionId;
        }
        if (!effectReads.TryGetValue(gamePath, out var read))
        {
            read = new EffectRead();
            effectReads[gamePath] = read;
            if (session.Imports.TryGetValue(gamePath, out var import) && import.Files.TryGetValue(gamePath, out var bytes))
            {
                try { (read.Bytes, read.Look) = (bytes, TimelineEffectLooks.Read(bytes)); }
                catch (InvalidDataException e) { read.Error = e.Message; }
            }
            else if (session.Animations is { } service)
            {
                var collection = current.Capture.CollectionId;
                read.Task = Task.Run(async () =>
                {
                    var file = await service.ReadEffectAsync(collection, gamePath, CancellationToken.None);
                    return (file, TimelineEffectLooks.Read(file));
                });
            }
            else
                read.Error = "Animation editing is unavailable.";
        }
        if (read.Task is { IsCompleted: true } task)
        {
            read.Task = null;
            if (task.IsCompletedSuccessfully)
                (read.Bytes, read.Look) = task.Result;
            else
                read.Error = TimelineSession.ErrorText(task.Exception);
        }
        return read;
    }

    /// <summary> The bytes of an effect the editor read, or an import's; null when it wasn't read. </summary>
    public byte[]? EffectBytes(string gamePath) =>
        effectReads.TryGetValue(gamePath, out var read) && read.Bytes != null ? read.Bytes
        : session.Imports.TryGetValue(gamePath, out var import) && import.Files.TryGetValue(gamePath, out var bytes) ? bytes
        : null;

    /// <summary>
    /// <paramref name="document"/> with its effects' looks written into copies, and the imports a save
    /// or preview packages with those copies among them.
    /// </summary>
    public (TimelineDocument Document, List<TimelineImport> Imports) WithLooks(TimelineDocument document)
    {
        var (baked, copies) = TimelineEffectLooks.Bake(document, EffectBytes, session.Imports);
        return (baked, [.. session.Imports.Values, .. copies]);
    }

    /// <summary> Why the looks can't be saved yet: an effect with a look still being read, or one that couldn't be. </summary>
    public string? LookMissing(TimelineDocument document)
    {
        if (document.Events.IsDefault)
            return null;
        foreach (var ev in document.Events)
            if (ev.Look is { IsEmpty: false } && EffectBytes(ev.Path) == null)
                return effectReads.TryGetValue(ev.Path, out var read) && read.Error != null
                    ? $"{Path.GetFileName(ev.Path)} could not be read, so its look can't be used."
                    : "Reading the effects.";
        return null;
    }

    /// <summary> Ends the running look edit once no control is active any more. </summary>
    public void EndEdit(TimelineEditor current)
    {
        if (!lookEditing || ImGui.IsAnyItemActive())
            return;
        lookEditing = false;
        current.EndFieldEdit();
    }

    /// <summary> Drops the running look edit when the editor starts over with another document. </summary>
    public void ForgetEdit() => lookEditing = false;

    private void SetLook(TimelineEditor current, TimelineEvent ev, TimelineEffectEdit look)
    {
        if (!lookEditing)
        {
            current.BeginFieldEdit();
            lookEditing = true;
        }
        current.Replace(ev with { Look = look });
    }

    public void Draw(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        if (session.Source is not { } loaded || ev.Lane != TimelineLane.Effect || !ev.Path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) ||
            !AnimationDependencies.SafeGamePath(ev.Path))
            return;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Look##effect-look", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        var read = EffectFor(ev.Path, loaded);
        if (read.Look is not { } look)
        {
            if (read.Error != null)
                Widgets.HintWrapped("The effect could not be read. " + read.Error);
            else
            {
                Widgets.Spinner();
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, "Reading the effect");
            }
            return;
        }
        Widgets.HintWrapped("Changes go into a copy of the effect.");
        var edit = ev.Look ?? NoLook;
        using (ImRaii.Disabled(!editable))
        {
            using (var table = ImRaii.Table("##effect-look", 2, ImGuiTableFlags.SizingFixedFit))
            {
                if (table.Success)
                {
                    ImGui.TableSetupColumn("##look-name", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("##look-value", ImGuiTableColumnFlags.WidthStretch);
                    DrawWholeEffect(current, ev, look, edit);
                    DrawColours(current, ev, look, edit);
                    DrawStrengths(current, ev, look, edit);
                }
            }
            using (ImRaii.Disabled(ev.Look == null))
            {
                if (ImGui.SmallButton("Reset look"))
                    SetLook(current, ev, NoLook);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Put the effect back as it is.");
    }

    /// <summary> Tint, size, offset and turn: the file's own values until changed. </summary>
    private void DrawWholeEffect(TimelineEditor current, TimelineEvent ev, TimelineEffectLook look, TimelineEffectEdit edit)
    {
        if (look.Tint is { } fileTint)
        {
            TimelineUi.Fact("Tint");
            var tint = edit.Tint ?? fileTint;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.ColorEdit3("##tint", ref tint, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr))
                SetLook(current, ev, edit with { Tint = Changed(Vector3.Clamp(tint, Vector3.Zero, new Vector3(TimelineEffectEdit.MaxTint)), fileTint) });
            TimelineUi.Tip("Colours the whole effect. Above 1 makes it brighter.");
        }
        if (look.Size is { } fileSize)
        {
            TimelineUi.Fact("Size");
            var size = edit.Size ?? fileSize;
            var perAxis = sizePerAxis || size.X != size.Y || size.Y != size.Z;
            var button = ImGui.CalcTextSize("XYZ").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(60), ImGui.GetContentRegionAvail().X - button - Theme.Gap));
            bool changed;
            if (perAxis)
                changed = ImGui.DragFloat3("##size", ref size, .01f, TimelineEffectEdit.MinSize, TimelineEffectEdit.MaxSize, "%.2f", ImGuiSliderFlags.AlwaysClamp);
            else
            {
                var all = size.X;
                changed = ImGui.DragFloat("##size", ref all, .01f, TimelineEffectEdit.MinSize, TimelineEffectEdit.MaxSize, "%.2f", ImGuiSliderFlags.AlwaysClamp);
                size = new Vector3(all);
            }
            if (changed)
                SetLook(current, ev, edit with { Size = Changed(size, fileSize) });
            TimelineUi.Tip("Scales the whole effect.");
            ImGui.SameLine(0, Theme.Gap);
            // Back to one value only when the axes agree; otherwise it would flatten them.
            var differ = perAxis && !sizePerAxis;
            using (ImRaii.Disabled(differ))
            {
                if (ImGui.SmallButton(perAxis ? "All##size-axes" : "XYZ##size-axes"))
                    sizePerAxis = !perAxis;
            }
            TimelineUi.Tip(differ ? "The axes differ, so each is set on its own." : perAxis ? "Set all axes together." : "Set each axis on its own.");
        }
        if (look.Offset is { } fileOffset)
        {
            TimelineUi.Fact("Offset");
            var offset = edit.Offset ?? fileOffset;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat3("##offset", ref offset, .01f, -TimelineEffectEdit.MaxOffset, TimelineEffectEdit.MaxOffset, "%.2f", ImGuiSliderFlags.AlwaysClamp))
                SetLook(current, ev, edit with { Offset = Changed(offset, fileOffset) });
            TimelineUi.Tip("Moves the whole effect.");
        }
        if (look.Turn is { } fileTurn)
        {
            TimelineUi.Fact("Turn");
            var turn = edit.Turn ?? fileTurn;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat3("##turn", ref turn, .5f, -TimelineEffectEdit.MaxTurn, TimelineEffectEdit.MaxTurn, "%.0f°", ImGuiSliderFlags.AlwaysClamp))
                SetLook(current, ev, edit with { Turn = Changed(turn, fileTurn) });
            TimelineUi.Tip("Turns the whole effect, in degrees.");
        }
    }

    /// <summary> The colours the parts use as swatches to change one by one, then hue and saturation for all of them. </summary>
    private void DrawColours(TimelineEditor current, TimelineEvent ev, TimelineEffectLook look, TimelineEffectEdit edit)
    {
        if (look.Colors.IsEmpty)
            return;
        TimelineUi.Fact("Colours");
        var size = new Vector2(ImGui.GetFrameHeight());
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var shown = Math.Min(look.Colors.Length, MaxSwatches);
        for (var i = 0; i < shown; i++)
        {
            var colour = look.Colors[i];
            if (i > 0)
            {
                ImGui.SameLine(0, Theme.Scaled(3));
                if (ImGui.GetCursorPosX() + size.X > right)
                    ImGui.NewLine();
            }
            using var id = ImRaii.PushId(i);
            var swapped = edit.Swapped(colour.Rgb) != null;
            var final = edit.Recolor(colour.Rgb);
            if (ImGui.ColorButton("##swatch", new Vector4(Vector3.Clamp(final, Vector3.Zero, Vector3.One), 1),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, size))
            {
                swatchEditing = colour.Rgb;
                ImGui.OpenPopup(SwatchPopup);
            }
            if (swapped)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(Theme.Accent), 0, ImDrawFlags.None,
                    Theme.Scaled(2));
            TimelineUi.Tip(swapped ? "Changed. Click to pick another colour." : colour.Uses == 1 ? "Used once. Click to change it." : $"Used {colour.Uses} times. Click to change it.");
            DrawSwatchPopup(current, ev, edit, colour.Rgb);
        }
        if (look.Colors.Length > shown)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"+{look.Colors.Length - shown}");
            TimelineUi.Tip("More colours. Hue and Saturation change them too.");
        }

        TimelineUi.Fact("Hue");
        var hue = edit.Hue;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##hue", ref hue, -180, 180, "%.0f°", ImGuiSliderFlags.AlwaysClamp))
            SetLook(current, ev, edit with { Hue = MathF.Round(hue) });
        TimelineUi.Tip("Turns every colour around the colour wheel. Ctrl+click to type.");

        TimelineUi.Fact("Saturation");
        var saturation = edit.Saturation * 100;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##saturation", ref saturation, 0, TimelineEffectEdit.MaxSaturation * 100, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
            SetLook(current, ev, edit with { Saturation = MathF.Round(saturation) / 100 });
        TimelineUi.Tip("Makes every colour stronger or greyer.");
    }

    private void DrawSwatchPopup(TimelineEditor current, TimelineEvent ev, TimelineEffectEdit edit, Vector3 swatch)
    {
        if (swatch != swatchEditing)
            return;
        using var popup = ImRaii.Popup(SwatchPopup);
        if (!popup.Success)
            return;
        var colour = edit.Recolor(swatch);
        ImGui.SetNextItemWidth(Theme.Scaled(200));
        if (ImGui.ColorPicker3("##swatch-picker", ref colour, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.NoSidePreview))
            SetLook(current, ev, edit.WithSwap(swatch, Vector3.Clamp(colour, Vector3.Zero, new Vector3(TimelineEffectEdit.MaxTint))));
        using (ImRaii.Disabled(edit.Swapped(swatch) == null))
        {
            if (ImGui.SmallButton("Back to its own colour"))
                SetLook(current, ev, edit.WithSwap(swatch, null));
        }
    }

    /// <summary> Glow, opacity and light range, each where the effect has something for it to scale. </summary>
    private void DrawStrengths(TimelineEditor current, TimelineEvent ev, TimelineEffectLook look, TimelineEffectEdit edit)
    {
        if (look.Glow && Percent("Glow", "##glow", edit.Glow, TimelineEffectEdit.MaxGlow, "Makes the particles and lights brighter or dimmer.") is { } glow)
            SetLook(current, ev, edit with { Glow = glow });
        if (look.Opacity && Percent("Opacity", "##opacity", edit.Opacity, TimelineEffectEdit.MaxOpacity, "Makes the particles more or less see-through.") is { } opacity)
            SetLook(current, ev, edit with { Opacity = opacity });
        if (look.LightRange && Percent("Light range", "##light-range", edit.LightRange, TimelineEffectEdit.MaxLightRange, "How far the effect's light reaches.") is { } range)
            SetLook(current, ev, edit with { LightRange = range });
    }

    /// <summary> A row with a percent slider over a factor, in whole percents; the new factor when it changed. </summary>
    private static float? Percent(string label, string id, float factor, float max, string tooltip)
    {
        TimelineUi.Fact(label);
        var percent = factor * 100;
        ImGui.SetNextItemWidth(-1);
        var changed = ImGui.SliderFloat(id, ref percent, 0, max * 100, "%.0f%%", ImGuiSliderFlags.AlwaysClamp);
        TimelineUi.Tip(tooltip + " Ctrl+click to type.");
        return changed ? MathF.Round(percent) / 100 : null;
    }

    /// <summary> A whole-effect value as the look keeps it: null when it is the file's own again. </summary>
    private static Vector3? Changed(Vector3 value, Vector3 file) => Vector3.DistanceSquared(value, file) < 1e-10f ? null : value;
}
