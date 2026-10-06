using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

internal sealed partial class PhysicsWindow
{
    private const string FitPopup = "##physics-fit";
    private Task<(PhysicsFitResult Fit, IReadOnlyList<string> Warnings)>? fitting;
    private PhysicsFitResult? fit;
    private IReadOnlyList<string> fitWarnings = [];
    private readonly HashSet<int> fitChosen = [];
    private PhysicsDocument? fitDocument;
    private int fitRevision;

    /// <summary> "Fit collision to my body": measures the body and sizes a copy of the file as it is now. </summary>
    private void DrawFitButton(PhysicsDocument current, bool small)
    {
        using (ImRaii.Disabled(Busy || target == null))
        {
            var clicked = small ? ImGui.SmallButton("Fit to my body##fit") : ImGui.Selectable("Fit collision to my body##fit");
            if (clicked)
                StartFit(current);
        }
        Tip("Measures your body as the game draws it now, Customize+ included, and sizes the collision to it. " +
            "Take your gear off first. You see every size before it changes.");
    }

    private void StartFit(PhysicsDocument current)
    {
        if (Busy)
            return;
        var file = current.File.Clone();
        fitDocument = current;
        fitRevision = current.Revision;
        Say(FeedbackSeverity.Info, "Measuring your body.");
        fitting = Task.Run(async () =>
        {
            var measured = await service.MeasureBodyAsync(life.Token).ConfigureAwait(false);
            return (PhysicsFit.Measure(file, measured.Body, measured.Skeleton), measured.Warnings);
        });
    }

    private void PollFit()
    {
        if (fitting is not { IsCompleted: true } done)
            return;
        fitting = null;
        if (!done.IsCompletedSuccessfully)
        {
            Say(FeedbackSeverity.Error, Unwrap(done.Exception));
            return;
        }
        (fit, fitWarnings) = done.Result;
        fitChosen.Clear();
        for (var i = 0; i < fit.Items.Count; i++)
            if (fit.Items[i].Changes)
                fitChosen.Add(i);
        if (fitChosen.Count == 0 && fit.Notes.Count == 0 && fitWarnings.Count == 0)
        {
            Say(FeedbackSeverity.Success, fit.Items.Count == 0
                ? "Nothing in this file bumps into your body, so there is nothing to fit."
                : "Your collision already fits your body.");
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
        ImGui.TextColored(Theme.Label, "Fit collision to your body");
        Widgets.MutedWrapped("Measured on your body as the game draws it now: your body mod, Customize+ and the bust slider.");
        foreach (var warning in fitWarnings)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Theme.Warning);
            ImGui.TextWrapped(warning);
        }
        ImGui.Spacing();

        var changing = Enumerable.Range(0, result.Items.Count).Where(i => result.Items[i].Changes).ToList();
        if (changing.Count > 0)
        {
            using var table = ImRaii.Table("##physics-fit-table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
            if (table.Success)
            {
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch);
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
                    ImGui.TextColored(Theme.Muted, $"{item.Current * 100:0.0} cm");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(item.Fitted > item.Current ? Theme.Accent : Theme.Info, $"{item.Fitted * 100:0.0} cm");
                }
            }
        }
        var same = result.Items.Count - changing.Count;
        if (same > 0)
            Widgets.Hint($"{same} size{(same == 1 ? " already fits" : "s already fit")}.");
        if (result.Notes.Count > 0)
        {
            using var node = ImRaii.TreeNode($"Not measured ({result.Notes.Count})##fit-notes");
            if (node.Success)
                foreach (var note in result.Notes)
                    Widgets.MutedWrapped(note);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(fitChosen.Count == 0))
        {
            if (ImGui.Button($"Apply {fitChosen.Count} size{(fitChosen.Count == 1 ? "" : "s")}##physics-fit-apply"))
            {
                ApplyFit(current, result);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##physics-fit-cancel"))
        {
            fit = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void ApplyFit(PhysicsDocument current, PhysicsFitResult result)
    {
        fit = null;
        // The sizes name shapes by name and chains by place, measured on the file as it was.
        if (!ReferenceEquals(current, fitDocument) || current.Revision != fitRevision)
        {
            Say(FeedbackSeverity.Warning, "Your physics changed while your body was measured. Fit again.");
            return;
        }
        var chosen = fitChosen.Order().Select(i => result.Items[i]).ToList();
        Change(current, () => PhysicsFit.Apply(current.File, chosen));
        Say(FeedbackSeverity.Success, $"Fitted {chosen.Count} size{(chosen.Count == 1 ? "" : "s")} to your body. Preview to see them.");
    }
}
