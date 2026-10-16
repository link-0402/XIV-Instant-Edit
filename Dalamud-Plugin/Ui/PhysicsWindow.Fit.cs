using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

internal sealed partial class PhysicsWindow
{
    private const string FitPopup = "##physics-fit";

    /// <summary> The fit scales breasts by the game's own racial ranges, read from its data, which a mod changing them doesn't reach. </summary>
    private const string BustScalingNote = "The fit uses the game's own racial bust scaling, so remove any mod that changes it.";

    private Task<(PhysicsFitResult Fit, IReadOnlyList<string> Warnings, string Note, PhysicsBodyMeasure Measure, PhybFile File)>? fitting;
    private Task<PhysicsFitResult>? refitting;
    // The file the running refit measures: a result for another fit's file is dropped.
    private PhybFile? refitFile;
    private PhysicsFitResult? fit;
    // The body and file the fit measured, kept so a new tip place is measured on the same ones.
    private PhysicsBodyMeasure? fitMeasure;
    private PhybFile? fitFile;
    private IReadOnlyList<string> fitWarnings = [];
    private string fitNote = string.Empty;
    private readonly HashSet<int> fitChosen = [];
    private PhysicsDocument? fitDocument;
    private int fitRevision;

    private const string FitLabel = "Fit to my body";

    private static float FitButtonWidth => ImGui.CalcTextSize(FitLabel).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary> "Fit to my body": measures the body and fits a copy of the file as it is now. </summary>
    private void DrawFitButton(PhysicsDocument current)
    {
        using (ImRaii.Disabled(Busy || target == null))
        {
            if (ImGui.Button($"{FitLabel}##fit"))
                StartFit(current);
        }
        Tip("Measures your body standing still, with your body mod, Customize+ and bust size. Sizes the collision and keeps " +
            "swings from sinking into your body. Take your gear off first. You see every change first.\n\n" +
            BustScalingNote);
    }

    /// <summary> A fitted value in its own units. </summary>
    private static string FitValue(PhysicsFitItem item, float value) => item.Target switch
    {
        PhysicsFitTarget.Swing => $"{value:0}°",
        _ => $"{value * 100:0.0} cm",
    };

    private void StartFit(PhysicsDocument current)
    {
        if (Busy)
            return;
        var file = current.File.Clone();
        var tipAt = TipAt;
        fitDocument = current;
        fitRevision = current.Revision;
        Say(FeedbackSeverity.Info, "Measuring your body.");
        fitting = Task.Run(async () =>
        {
            var measured = await service.MeasureBodyAsync(life.Token).ConfigureAwait(false);
            return (PhysicsFit.Measure(file, measured.Body, measured.Skeleton, tipAt), measured.Warnings, measured.Note, measured, file);
        });
    }

    private PhysicsTipAt TipAt => config.PhysicsFitTipMiddle ? PhysicsTipAt.Middle : PhysicsTipAt.Nipple;

    private void PollFit()
    {
        if (refitting is { IsCompleted: true } refitted)
        {
            refitting = null;
            if (!refitted.IsCompletedSuccessfully)
                Say(FeedbackSeverity.Error, Unwrap(refitted.Exception));
            else if (fit is { } before && ReferenceEquals(refitFile, fitFile))
            {
                // Settings you unticked stay unticked; the new tip places start ticked.
                var unticked = Enumerable.Range(0, before.Items.Count).Where(i => before.Items[i].Changes && !fitChosen.Contains(i))
                    .Select(i => FitKey(before.Items[i])).ToHashSet();
                fit = refitted.Result;
                fitChosen.Clear();
                for (var i = 0; i < fit.Items.Count; i++)
                    if (fit.Items[i].Changes && !unticked.Contains(FitKey(fit.Items[i])))
                        fitChosen.Add(i);
            }
        }
        if (fitting is not { IsCompleted: true } done)
            return;
        fitting = null;
        if (!done.IsCompletedSuccessfully)
        {
            Say(FeedbackSeverity.Error, Unwrap(done.Exception));
            return;
        }
        (fit, fitWarnings, fitNote, fitMeasure, fitFile) = done.Result;
        fitChosen.Clear();
        for (var i = 0; i < fit.Items.Count; i++)
            if (fit.Items[i].Changes)
                fitChosen.Add(i);
        // With breast tips, the review also offers where they go, even when everything fits already.
        if (fitChosen.Count == 0 && fit.Notes.Count == 0 && fitWarnings.Count == 0 && !fit.BreastTips)
        {
            Say(FeedbackSeverity.Success, fit.Items.Count == 0
                ? "Nothing in this file fits to a body, so there is nothing to change."
                : "Your physics already fits your body.");
            fit = null;
            return;
        }
        Say(FeedbackSeverity.Info, "Measured your body.");
        ImGui.OpenPopup(FitPopup);
    }

    private void DrawFitPopup(PhysicsDocument current)
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(420, 0), Theme.Scaled(640, 560));
        using var popup = ImRaii.Popup(FitPopup);
        if (!popup.Success)
            return;
        if (fit is not { } result)
        {
            ImGui.CloseCurrentPopup();
            return;
        }
        ImGui.TextColored(Theme.Label, "Fit to your body");
        Widgets.MutedWrapped($"{fitNote} Your animation, jiggle and posing don't count.");
        Widgets.MutedWrapped(BustScalingNote);
        foreach (var warning in fitWarnings)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Theme.Warning);
            ImGui.TextWrapped(warning);
        }
        ImGui.Spacing();
        if (result.BreastTips)
            DrawTipChoice();

        var changing = Enumerable.Range(0, result.Items.Count).Where(i => result.Items[i].Changes).ToList();
        if (changing.Count > 0)
        {
            using var table = ImRaii.Table("##physics-fit-table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
            if (table.Success)
            {
                ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Now");
                ImGui.TableSetupColumn("Fits");
                ImGui.TableHeadersRow();
                foreach (var i in changing)
                {
                    var item = result.Items[i];
                    using var id = ImRaii.PushId(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var on = fitChosen.Contains(i);
                    if (ImGui.Checkbox(item.Label, ref on))
                    {
                        if (on)
                            fitChosen.Add(i);
                        else
                            fitChosen.Remove(i);
                    }
                    Tip(item.Detail);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Muted, FitValue(item, item.Current));
                    ImGui.TableNextColumn();
                    // A tip's size follows its tip place while that's ticked.
                    var fitted = item.FittedWith(PhysicsFit.MovesPlace(fitChosen.Select(chosen => result.Items[chosen]), item));
                    ImGui.TextColored(fitted > item.Current ? Theme.Accent : Theme.Info, FitValue(item, fitted));
                }
            }
        }
        var same = result.Items.Count - changing.Count;
        if (same > 0)
            Widgets.Hint($"{same} setting{(same == 1 ? " already fits" : "s already fit")}.");
        if (result.Notes.Count > 0)
        {
            using var node = ImRaii.TreeNode($"Not measured ({result.Notes.Count})##fit-notes");
            if (node.Success)
                foreach (var note in result.Notes)
                    Widgets.MutedWrapped(note);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(fitChosen.Count == 0 || refitting != null))
        {
            if (ImGui.Button($"Apply {fitChosen.Count} change{(fitChosen.Count == 1 ? "" : "s")}##physics-fit-apply"))
            {
                ApplyFit(current, result);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##physics-fit-cancel"))
        {
            fit = null;
            fitMeasure = null;
            fitFile = null;
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary> Where breast tips go: on the nipple or in the middle of the breast. Changing it measures the tips again. </summary>
    private void DrawTipChoice()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Breast tip");
        ImGui.SameLine(0, Theme.Gap * 2);
        using (ImRaii.Disabled(refitting != null))
        {
            if (ImGui.RadioButton("On the nipple##fit-tip-nipple", !config.PhysicsFitTipMiddle))
                SetTipAt(false);
            Tip("Tip accuracy. The tip moves exactly as your nipple does, and settings work like a preset's. " +
                "Its size covers the front, so arms coming from the side can sink into the breast.");
            ImGui.SameLine();
            if (ImGui.RadioButton("Middle of the breast##fit-tip-middle", config.PhysicsFitTipMiddle))
                SetTipAt(true);
            Tip("Collision accuracy. Its size fills the whole breast, so arms bump into it from any side. " +
                "The tip gets shorter, so the same settings swing the breast more.");
        }
        if (refitting != null)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
        ImGui.Spacing();
    }

    private void SetTipAt(bool middle)
    {
        if (config.PhysicsFitTipMiddle == middle || fitMeasure is not { } measured || fitFile is not { } file || refitting != null)
            return;
        config.PhysicsFitTipMiddle = middle;
        saveConfig();
        var tipAt = TipAt;
        refitFile = file;
        refitting = Task.Run(() => PhysicsFit.Measure(file, measured.Body, measured.Skeleton, tipAt));
    }

    private static (PhysicsFitTarget, int, int, string) FitKey(PhysicsFitItem item) => (item.Target, item.Simulator, item.Chain, item.Name);

    private void ApplyFit(PhysicsDocument current, PhysicsFitResult result)
    {
        fit = null;
        fitMeasure = null;
        fitFile = null;
        // The sizes name shapes by name and chains by place, measured on the file as it was.
        if (!ReferenceEquals(current, fitDocument) || current.Revision != fitRevision)
        {
            Say(FeedbackSeverity.Warning, "Your physics changed while your body was measured. Fit again.");
            return;
        }
        var chosen = fitChosen.Order().Select(i => result.Items[i]).ToList();
        Change(current, () => PhysicsFit.Apply(current.File, chosen), FitLabel);
        Say(FeedbackSeverity.Success, $"Fitted {chosen.Count} setting{(chosen.Count == 1 ? "" : "s")} to your body. Preview to see them.");
    }
}
