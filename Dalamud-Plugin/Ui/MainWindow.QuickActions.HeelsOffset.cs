using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Heels;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private HeelsOffsetService? _heels;
    private HeelsOffsetResult? _heelsResult;
    private string _heelsError = string.Empty;
    private int _heelsBusy;
    private IReadOnlyList<OnScreenObject>? _heelsSnapshot;
    private OnScreenObject? _heelsCharacter;
    // Whether the result was measured on other shoes than the list shows now, worked out once per snapshot and result.
    private (IReadOnlyList<OnScreenObject>? Snapshot, HeelsOffsetResult? Result, bool Stale) _heelsStale;

    internal void AttachHeelsOffset(HeelsOffsetService service) => _heels = service;

    private void DrawHeelsOffsetCard()
    {
        ImGui.Spacing();
        QuickActionCard("##quick-heels", FontAwesomeIcon.ShoePrints, "Heels offset",
            "Measures how far your shoes reach below the ground and gives the offset to enter in Simple Heels, " +
            "so that heels stand on the ground instead of sinking into it.",
            DrawHeelsOffsetAction);
    }

    private void DrawHeelsOffsetAction()
    {
        if (_heels is not { } service)
        {
            Widgets.Hint("Unavailable: the heels tools did not start.");
            return;
        }
        var character = HeelsCharacter();
        var busy = Volatile.Read(ref _heelsBusy) != 0;
        using (ImRaii.Disabled(busy || character is null))
        {
            if (ImGui.Button(_heelsResult is null ? "Measure##quick-heels" : "Measure again##quick-heels") && character is not null)
                MeasureHeels(service, character);
        }
        if (busy)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
        if (character is null)
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character isn't in the On Screen list. Refresh the list once your character is drawn.");
        if (_heelsError.Length > 0)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Error, _heelsError);
        }
        if (_heelsResult is { } result)
            DrawHeelsResult(service, result, character);
    }

    private void DrawHeelsResult(HeelsOffsetService service, HeelsOffsetResult result, OnScreenObject? character)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Label, result.ItemName);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, result.Source);

        var value = HeelsModelOffset.Format(result.Offset);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, "Offset:");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Accent, value);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, $"({result.Offset * 100:0.0} cm)");
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.IconButton("##quick-heels-copy", FontAwesomeIcon.Copy, "Copy the offset"))
            ImGui.SetClipboardText(value);

        Widgets.HintWrapped(MathF.Abs(result.Offset) < 0.001f
            ? "They stand on the ground already and need no offset."
            : $"In Simple Heels, open Equipment Offsets, add an entry for {result.ItemName} and paste this offset" +
              (result.Offset < 0 ? ". They stand above the ground, so it lowers you onto it." : "."));
        if (result.StoredOffset is { } stored)
            Widgets.HintWrapped($"The model sets its own Simple Heels offset, {HeelsModelOffset.Format(stored)} at your height. " +
                                "Simple Heels applies it without an entry while its Use model assigned offsets option is on.");
        if (service.Ipc.Offset is { } now)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(MathF.Abs(now - result.Offset) < 0.002f ? Theme.Success : Theme.Hint,
                $"Simple Heels applies {HeelsModelOffset.Format(now)} to this outfit now.");
        }
        else
            Widgets.HintWrapped("Simple Heels isn't running, or this version of it isn't supported.");

        var scaling = result.Scaling is { } scaled ? $", reshaped from c{scaled.ModelRace:D4} for {scaled.CharacterLabel}" : "";
        var hidden = result.Measurement.HiddenParts switch
        {
            0 => "",
            1 => ", without 1 part your outfit hides",
            var count => $", without {count} parts your outfit hides",
        };
        Widgets.MutedWrapped($"Measured in the standard pose{scaling}, at your height scale of {result.HeightScale:0.000}{hidden}. " +
                             "Animations and Customize+ scaling aren't included.");
        foreach (var warning in result.Warnings)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, warning);
        }

        var attribute = HeelsModelOffset.AttributeFor(result.Measurement.Offset);
        if (ImGui.SmallButton("Copy as model attribute##quick-heels-attribute"))
            ImGui.SetClipboardText(attribute);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{attribute}\n" +
                             "For mod files: Simple Heels applies this attribute to whoever wears the model,\n" +
                             "scaled to their height. It leaves your height out, so it can differ from the offset above.");

        if (HeelsResultStale(result, character))
            Widgets.HintWrapped("Your feet model changed since this measurement. Measure again.");
    }

    private void MeasureHeels(HeelsOffsetService service, OnScreenObject character)
    {
        if (Interlocked.CompareExchange(ref _heelsBusy, 1, 0) != 0)
            return;
        _heelsError = string.Empty;
        _heelsResult = null;
        _ = Task.Run(async () =>
        {
            try
            {
                _heelsResult = await service.MeasureAsync(character, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (HeelsListOutdatedException error)
            {
                _heelsError = error.Message;
                _onScreen.RequestRefresh();
            }
            catch (Exception error)
            {
                _log.Warning(error, "Could not measure the heels offset.");
                _heelsError = error.Message;
            }
            finally { Interlocked.Exchange(ref _heelsBusy, 0); }
        });
    }

    /// <summary> Your character in the On Screen list, looked up again only when the snapshot changes. </summary>
    private OnScreenObject? HeelsCharacter()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _heelsSnapshot))
        {
            _heelsSnapshot = items;
            _heelsCharacter = items.FirstOrDefault(item => item.PresentationCategory == ActorPresentationCategory.Player);
        }
        return _heelsCharacter;
    }

    /// <summary> Whether the list no longer shows the measured model on the measured character. </summary>
    private bool HeelsResultStale(HeelsOffsetResult result, OnScreenObject? character)
    {
        if (!ReferenceEquals(_heelsStale.Snapshot, _heelsSnapshot) || !ReferenceEquals(_heelsStale.Result, result))
            _heelsStale = (_heelsSnapshot, result, character is null || character.Name != result.Character ||
                                                   HeelsMeasure.FeetNode(character.ResourceRoots, result.ModelFile) is null);
        return _heelsStale.Stale;
    }
}
