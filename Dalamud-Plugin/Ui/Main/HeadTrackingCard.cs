using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Ui.Main;

/// <summary>
/// Quick Actions' Head tracking: where the back turns from and how strongly the back, neck and head, and eyes turn
/// toward what your character looks at, previewed on your character and saved as a mod for your race.
/// </summary>
internal sealed class HeadTrackingCard
{
    private HeadTrackingService? _service;
    // The controls' values; set from what your race was saved with whenever the race changes.
    private HeadTrackingSettings _settings = HeadTrackingSettings.Game;

    public HeadTrackingCard()
    {
        Card = new QuickAction("head-tracking", FontAwesomeIcon.Eye, "Head tracking",
            "Change how your character turns toward what it looks at. Saved as a mod for your race, so it changes every animation, and others see it.",
            DrawHeadTrackingAction);
    }

    public QuickAction Card { get; }

    public void Attach(HeadTrackingService service)
    {
        _service = service;
        service.RaceChanged += () => _settings = service.Race is { } race ? service.Saved(race) : HeadTrackingSettings.Game;
    }

    private void DrawHeadTrackingAction()
    {
        if (_service is not { } service)
        {
            Widgets.Hint("Unavailable: head tracking did not start.");
            return;
        }
        service.Refresh();
        if (service.Race is null)
        {
            Widgets.Hint("Your character isn't loaded");
            return;
        }
        ImGui.TextColored(Theme.Label, "For " + service.RaceLabel);

        var busy = service.Busy;
        var applyNow = false;
        using (ImRaii.Disabled(busy))
        {
            ImGui.TextColored(Theme.Muted, "Back turns from");
            ImGui.SameLine(0, Theme.Gap);
            if (ImGui.RadioButton("Upper back##head-tracking", !_settings.LowerBack) && _settings.LowerBack)
                (_settings, applyNow) = (_settings with { LowerBack = false }, true);
            ImGui.SameLine();
            if (ImGui.RadioButton("Lower back##head-tracking", _settings.LowerBack) && !_settings.LowerBack)
                (_settings, applyNow) = (_settings with { LowerBack = true }, true);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Chest, shoulders and arms turn together, so hands resting on the body stay there.");

            applyNow |= Strength("Back and arms", "##head-tracking-back", _settings.Back, value => _settings = _settings with { Back = value });
            applyNow |= Strength("Neck and head", "##head-tracking-neck", _settings.NeckHead, value => _settings = _settings with { NeckHead = value });
            applyNow |= Strength("Eyes", "##head-tracking-eyes", _settings.Eyes, value => _settings = _settings with { Eyes = value });
            Widgets.Hint("Changes every animation for this race.");

            ImGui.Spacing();
            var previewing = service.Previewing;
            if (ImGui.Button(previewing ? "Stop preview##head-tracking" : "Preview##head-tracking"))
            {
                if (previewing)
                    _ = service.StopPreviewAsync();
                else
                    _ = service.PreviewAsync(_settings);
            }
            ImGui.SameLine();
            if (ImGui.Button("Save##head-tracking"))
                _ = service.SaveAsync(_settings);
            ImGui.SameLine();
            if (ImGui.Button("Game default##head-tracking") && !_settings.Equals(HeadTrackingSettings.Game))
                (_settings, applyNow) = (HeadTrackingSettings.Game, true);
        }
        if (busy)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
        // A slider updates the preview when you let go of it; the other controls at once.
        if (applyNow && service.Previewing && !busy)
            _ = service.PreviewAsync(_settings);

        if (service.OtherMod is { } other)
            ImGui.TextColored(Theme.Warning, $"Head tracking from {other} is replaced while this mod is on.");
        if (service.Error is { } error)
            ImGui.TextColored(Theme.Error, error);
        else if (service.Status is { } status)
            Widgets.HintWrapped(status);
    }

    /// <summary> A 0 to 100 % slider; true when it was let go of after a change. </summary>
    private static bool Strength(string label, string id, float value, Action<float> set)
    {
        ImGui.TextColored(Theme.Muted, label);
        var percent = (int)MathF.Round(Math.Clamp(value, 0, 1) * 100);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(160), ImGui.GetContentRegionAvail().X * .5f));
        if (ImGui.SliderInt(id, ref percent, 0, 100, percent == 0 ? "off" : "%d%%", ImGuiSliderFlags.AlwaysClamp))
            set(Math.Clamp(percent, 0, 100) / 100f);
        return ImGui.IsItemDeactivatedAfterEdit();
    }
}
