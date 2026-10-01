using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Heels;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private HeelsOffsetService? _heels;

    internal void AttachHeelsOffset(HeelsOffsetService service)
    {
        _heels = service;
        // The automatic fix also runs with this window closed, so what it writes goes to the status line and notifications.
        service.AutomaticallyFixed += result => _feed.Report(StatusChannel.Models, FeedbackSeverity.Success,
            $"Heels offset: wrote {result.Plan.Attribute} into the {result.Model.Slot.Name()} model of {result.Source}.");
        service.AutomaticFixFailed += error => _feed.Report(StatusChannel.Models, FeedbackSeverity.Warning, $"Heels offset: {error}");
    }

    private QuickAction HeelsOffsetCard => new("heels-offset", FontAwesomeIcon.ShoePrints, "Heels offset",
        "Measures how far your shoes reach below the ground and writes the matching Simple Heels offset into their model, so that " +
        "heels stand on the ground instead of sinking into it. When your gear hides your feet, the legs or one-piece body model that " +
        "holds them gets the offset.",
        DrawHeelsOffsetAction,
        () => _config.AutoFixHeels = false);

    private void DrawHeelsOffsetAction()
    {
        if (_heels is not { } service)
        {
            Widgets.Hint("Unavailable: the heels tools did not start.");
            return;
        }
        var busy = service.Busy;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Fix offset##quick-heels"))
                service.Fix();
        }
        ImGui.SameLine();
        var automatic = _config.AutoFixHeels;
        if (ImGui.Checkbox("Fix automatically##quick-heels", ref automatic))
        {
            _config.AutoFixHeels = automatic;
            _saveConfig();
        }
        ImGui.SameLine();
        Widgets.HelpTip("Fixes each feet model you put on once it has loaded, or the legs or body model when your gear hides your feet, " +
                        "also while this window is closed. It writes into your mods' files, keeping a backup of each for a week, and " +
                        "redraws your character. It waits during combat, cutscenes and GPose, and leaves the game's own files alone.");
        if (busy)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
        if (service.LastError is { } error)
            HeelsText(Theme.Error, error);
        if (service.Last is { } result)
            DrawHeelsResult(service, result);
    }

    private void DrawHeelsResult(HeelsOffsetService service, HeelsOffsetResult result)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Label, result.ItemName);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, result.Source);
        if (result.Model.Slot == HeelsSlot.Legs)
            Widgets.HintWrapped("Your legs gear hides your feet, so the legs model holds them and gets the offset.");
        else if (result.Model.Slot == HeelsSlot.Top)
            Widgets.HintWrapped("Your body gear hides your legs and feet, so the body model holds them and gets the offset.");

        var measurement = result.Measurement;
        ImGui.TextColored(Theme.Text, "Offset:");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Accent, HeelsModelOffset.Format(measurement.Offset));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, measurement.Lowest <= 0
            ? $"(reaches {HeelsFix.Centimetres(-measurement.Lowest)} below the ground)"
            : $"(stays {HeelsFix.Centimetres(measurement.Lowest)} above the ground)");

        if (result.Written)
        {
            HeelsText(Theme.Success, $"Wrote {result.Plan.Attribute} into the model{Replacing(measurement)}. The old file is backed up for a week.");
            using (ImRaii.Disabled(service.Busy))
            {
                if (ImGui.SmallButton("Undo##quick-heels-undo"))
                    service.UndoLast();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Puts the model back as it was before this fix.");
        }
        else if (result.Undone)
            HeelsText(Theme.Hint, _config.AutoFixHeels
                ? "Undone: the model is back as it was. Fix automatically leaves it alone until you press Fix offset while wearing it."
                : "Undone: the model is back as it was.");
        else if (result.NotWritten is { } reason)
        {
            HeelsText(Theme.Warning, reason);
            if (!result.ModFile)
            {
                // Simple Heels doesn't scale its entries by height, so the entry gets the offset at your height.
                var entry = HeelsModelOffset.Format(measurement.Offset * result.HeightScale);
                Widgets.HintWrapped($"Simple Heels can still use it: add an Equipment Offsets entry for {result.ItemName} with {entry}, the offset at your height.");
                if (Widgets.IconButton("##quick-heels-copy", FontAwesomeIcon.Copy, $"Copy {entry}"))
                    ImGui.SetClipboardText(entry);
            }
        }
        else
            HeelsText(result.Plan.Action == HeelsFixAction.Refuse ? Theme.Warning : Theme.Success, result.Plan.Reason);

        if (result.Earlier is { } earlier)
            HeelsText(Theme.Warning, $"Simple Heels reads your {earlier.Slot.Name()} model's offset, {earlier.Offset.Attribute}, before this one's, " +
                                     "so that one applies. Fix offset doesn't change that model.");
        if (service.Ipc.Offset is { } now)
        {
            var expected = result.ModelOffset * result.HeightScale;
            var matches = expected is { } value && MathF.Abs(now - value) < 0.002f;
            HeelsText(matches ? Theme.Success : Theme.Hint, $"Simple Heels applies {HeelsModelOffset.Format(now)} to this outfit now.");
            if (!matches && expected is not null && result.Earlier is null)
                Widgets.MutedWrapped("If that stays different, Simple Heels applies something else first, such as an Equipment Offsets entry " +
                                     "for this outfit or a temporary or emote offset, or its Use model assigned offsets option is off.");
        }
        else
            Widgets.HintWrapped("Simple Heels isn't running, or this version of it isn't supported.");

        var scaling = result.Scaling is { } scaled ? $", reshaped from c{scaled.ModelRace:D4} for {scaled.CharacterLabel}" : "";
        var hidden = measurement.HiddenParts switch
        {
            0 => "",
            1 => ", without 1 part your outfit hides",
            var count => $", without {count} parts your outfit hides",
        };
        var checkedAt = result.Automatic ? $" Checked automatically at {result.Time.ToString("HH:mm", CultureInfo.InvariantCulture)}." : "";
        Widgets.MutedWrapped($"Measured in the standard pose{scaling}{hidden}. Simple Heels scales the offset to your height; " +
                             $"animations and Customize+ scaling aren't included.{checkedAt}");
        foreach (var warning in result.Warnings)
            HeelsText(Theme.Warning, warning);

        if (!_config.AutoFixHeels && service.LiveModel is { } live && !live.SameModel(result.Model))
            Widgets.HintWrapped("Your outfit changed since. Press Fix offset again.");
    }

    /// <summary> What writing replaced: ", replacing heels_offset=0.14", or where a new offset went. </summary>
    private static string Replacing(HeelsMeasurement measurement) => measurement.OffsetAttributes switch
    {
        0 => ", on its first part",
        1 => $", replacing {measurement.ModelOffset?.Attribute ?? "an offset Simple Heels couldn't read"}",
        var count => $", replacing its {count} offset attributes",
    };

    private static void HeelsText(Vector4 colour, string text)
    {
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(colour, text);
    }
}
