using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Physics;
using FitOriginals = System.Collections.Generic.IReadOnlyDictionary<(InstantEdit.Services.Physics.PhysicsFitTarget Target, string Name),
    (System.Numerics.Vector3 Start, System.Numerics.Vector3 End)>;

namespace InstantEdit.Ui;

internal sealed partial class PhysicsWindow
{
    private const string FitPopup = "##physics-fit";

    /// <summary> The fit scales breasts by the game's own racial ranges, read from its data, which a mod changing them doesn't reach. </summary>
    private const string BustScalingNote = "The fit uses the game's own racial bust scaling, so remove any mod that changes it.";

    private Task<(PhysicsFitResult Fit, IReadOnlyList<string> Warnings, string Note, PhysicsBodyMeasure Measure, PhybFile File, FitOriginals Originals)>? fitting;
    private Task<PhysicsFitResult>? refitting;
    // The file the running refit measures: a result for another fit's file is dropped.
    private PhybFile? refitFile;
    private PhysicsFitResult? fit;
    // The body and file the fit measured, kept so a new tip place is measured on the same ones.
    private PhysicsBodyMeasure? fitMeasure;
    private PhybFile? fitFile;
    // What earlier fits moved the file's skirt shapes from, which the fit measured from and applying records again.
    private FitOriginals? fitOriginals;
    private IReadOnlyList<string> fitWarnings = [];
    private string fitNote = string.Empty;
    private readonly HashSet<int> fitChosen = [];
    // The fit's rows with the ticked sizes held 1 cm clear at rest (PhysicsFitClearance), and the fit and ticks they were held for.
    private IReadOnlyList<PhysicsFitItem>? fitHeld;
    private PhysicsFitResult? fitHeldOf;
    private string fitHeldFor = string.Empty;
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
            "swings from sinking into your body. Fits the skirt to your Customize+ shape. Take your gear off first. You see every change first.\n\n" +
            BustScalingNote);
    }

    /// <summary> A fitted value in its own units, whatever the system's language. Point sizes show the tip's; their tooltip lists every point. </summary>
    private static string FitValue(PhysicsFitItem item, float value) => item.Target switch
    {
        PhysicsFitTarget.Swing => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0}°"),
        PhysicsFitTarget.Points => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tip {value * 100:0.0} cm"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value * 100:0.0} cm"),
    };

    private void StartFit(PhysicsDocument current)
    {
        if (Busy)
            return;
        var file = current.File.Clone();
        var race = current.Race;
        var tipAt = TipAt;
        fitDocument = current;
        fitRevision = current.Revision;
        Say(FeedbackSeverity.Info, "Measuring your body.");
        fitting = Task.Run(async () =>
        {
            var measured = await service.MeasureBodyAsync(life.Token).ConfigureAwait(false);
            var originals = service.FitRecords.Originals(file, race);
            return (PhysicsFit.Measure(file, measured.Body, measured.Skeleton, tipAt, measured.Unshaped, originals), measured.Warnings, measured.Note, measured,
                file, (FitOriginals)originals);
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
                // Settings keep their ticks; new ones, like the new tip places, start as a first fit's do.
                // A modded file can name two shapes the same; either one ticked keeps the name ticked.
                var ticked = Enumerable.Range(0, before.Items.Count).Where(i => before.Items[i].Changes)
                    .GroupBy(i => FitKey(before.Items[i])).ToDictionary(group => group.Key, group => group.Any(fitChosen.Contains));
                fit = refitted.Result;
                fitChosen.Clear();
                for (var i = 0; i < fit.Items.Count; i++)
                    if (fit.Items[i].Changes && ticked.GetValueOrDefault(FitKey(fit.Items[i]), !fit.Items[i].Optional))
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
        (fit, fitWarnings, fitNote, fitMeasure, fitFile, fitOriginals) = done.Result;
        fitChosen.Clear();
        for (var i = 0; i < fit.Items.Count; i++)
            if (fit.Items[i].Changes && !fit.Items[i].Optional)
                fitChosen.Add(i);
        // With breast tips, the review also offers where they go, even when everything fits already.
        if (fit.Items.All(item => !item.Changes) && fit.Notes.Count == 0 && fitWarnings.Count == 0 && !fit.BreastTips && fit.Waist.Count == 0)
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

        // The numbers follow the ticks: geometry only, so it runs again whenever they change.
        var shown = HeldFit(result);
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
                    var item = shown[i];
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
                    ImGui.TextColored(Theme.Muted, item.Shape is { NewOn: not null } && item.Current <= 0 ? "none" : FitValue(item, item.Current));
                    ImGui.TableNextColumn();
                    // A tip's size follows its tip place while that's ticked.
                    var fitted = item.FittedWith(PhysicsFit.MovesPlace(fitChosen.Select(chosen => shown[chosen]), item));
                    var moved = item.Shape is { Distance: >= 0.005f } shape
                        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $", moved {shape.Distance * 100:0.0} cm")
                        : "";
                    ImGui.TextColored(fitted > item.Current ? Theme.Accent : Theme.Info, FitValue(item, fitted) + moved);
                }
            }
        }
        if (result.Waist.Count > 0)
            DrawWaist(result.Waist);
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
            fitOriginals = null;
            fitHeld = null;
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary> The skirt bones to move in Customize+, which physics can't move, copied as a template to import there. </summary>
    private void DrawWaist(IReadOnlyList<PhysicsWaistMove> waist)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Label, "Waistband: set in Customize+");
        Widgets.MutedWrapped("Physics can't move where a skirt starts. Import this template in Customize+ and add it last to your profile.");
        if (ImGui.Button("Copy Customize+ template##physics-fit-waist"))
        {
            ImGui.SetClipboardText(PhysicsPlusTemplate.Encode(waist, fitMeasure?.Plus));
            Say(FeedbackSeverity.Success, "Copied. Import it from the clipboard in Customize+'s Templates.");
        }
        Tip(string.Join("\n", waist.Select(move => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{move.Bone}: X {move.Translation.X:+0.000;-0.000}, Y {move.Translation.Y:+0.000;-0.000}, Z {move.Translation.Z:+0.000;-0.000} " +
            $"({move.Distance * 100:0.0} cm out)"))));
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
        var originals = fitOriginals;
        refitFile = file;
        refitting = Task.Run(() => PhysicsFit.Measure(file, measured.Body, measured.Skeleton, tipAt, measured.Unshaped, originals));
    }

    private static (PhysicsFitTarget, int, int, string) FitKey(PhysicsFitItem item) => (item.Target, item.Simulator, item.Chain, item.Name);

    /// <summary>
    /// The fit's rows with the ticked sizes kept 1 cm clear where physics runs, measured again when the ticks or the fit change
    /// (a refit, or a test ticking rows in the builder's state). The rows as measured when the body is gone.
    /// </summary>
    private IReadOnlyList<PhysicsFitItem> HeldFit(PhysicsFitResult result)
    {
        if (fitFile is not { } file || fitMeasure is not { } measured)
            return result.Items;
        var ticks = string.Join(",", fitChosen.Order());
        if (fitHeld is null || !ReferenceEquals(fitHeldOf, result) || fitHeldFor != ticks)
        {
            fitHeld = PhysicsFitClearance.Hold(file, result.Items, fitChosen, measured.Skeleton);
            (fitHeldOf, fitHeldFor) = (result, ticks);
        }
        return fitHeld;
    }

    private void ApplyFit(PhysicsDocument current, PhysicsFitResult result)
    {
        var (measuredFile, originals, measured) = (fitFile, fitOriginals, fitMeasure);
        fit = null;
        fitMeasure = null;
        fitFile = null;
        fitOriginals = null;
        fitHeld = null;
        // The sizes name shapes by name and chains by place, measured on the file as it was.
        if (!ReferenceEquals(current, fitDocument) || current.Revision != fitRevision || measuredFile is null)
        {
            Say(FeedbackSeverity.Warning, "Your physics changed while your body was measured. Fit again.");
            return;
        }
        // Held once more on the rows as chosen, so the file written always keeps the 1 cm.
        var held = measured is null ? result.Items : PhysicsFitClearance.Hold(measuredFile, result.Items, fitChosen, measured.Skeleton);
        var chosen = fitChosen.Order().Select(i => held[i]).ToList();
        Change(current, () => PhysicsFit.Apply(current.File, chosen), FitLabel);
        // Remembered as soon as it's in the file: a later fit uses a record only while the shape is still exactly where this one put it.
        try { service.FitRecords.Record(measuredFile, current.Race, chosen, originals); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning(e, "Could not remember the skirt shapes the fit moved; fitting this file again may move them twice.");
        }
        Say(FeedbackSeverity.Success, $"Fitted {chosen.Count} setting{(chosen.Count == 1 ? "" : "s")} to your body. Preview to see them.");
    }
}
