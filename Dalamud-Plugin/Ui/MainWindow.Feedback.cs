using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private FeedbackState GetFeedback(MainTab tab)
    {
        if (tab == MainTab.TextureEdits)
        {
            lock (_stateLock)
                return new FeedbackState(_textureStatus, _textureStatusSeverity);
        }

        if (tab == MainTab.Animations)
        {
            var service = animations;
            if (service is null)
                return new FeedbackState(string.Empty, FeedbackSeverity.Error);
            return new FeedbackState(service.Status, service.LastResult is { Success: false } && !service.Busy
                ? FeedbackSeverity.Error
                : FeedbackSeverity.Success);
        }

        lock (_stateLock)
            return new FeedbackState(_status, _statusSeverity);
    }

    private static void DrawFeedback(FeedbackState feedback)
    {
        if (feedback.Text.Length == 0)
            return;
        Widgets.Banner("##instant-edit-feedback", feedback.Severity, feedback.Text);
    }

    private static float GetFeedbackHeight(FeedbackState feedback)
        => feedback.Text.Length == 0 ? 0 : Widgets.BannerHeight(feedback.Text);

    private void SetStatus(string text, FeedbackSeverity severity) { lock (_stateLock) { _status = text; _statusSeverity = severity; } }
    private void SetTextureStatus(string text, FeedbackSeverity severity) { lock (_stateLock) { _textureStatus = text; _textureStatusSeverity = severity; } }
    private static string Sanitize(string name) { var invalid = Path.GetInvalidFileNameChars(); var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(); return value.Length == 0 ? "Object" : value; }

    private static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SafeId(string? value)
    {
        var id = Safe(value, "resource");
        return id.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private enum MainTab
    {
        OnScreen,
        ModBrowser,
        TextureEdits,
        Animations,
    }

    private readonly record struct FeedbackState(string Text, FeedbackSeverity Severity);

    private sealed record ActorView(
        OnScreenObject? Entity,
        string Category,
        string Name,
        List<ResourceView> Roots,
        int ImportObjectIndex);
    private sealed record ResourceView(
        string Type,
        string Icon,
        string Name,
        string GamePath,
        string ActualPath,
        string SourceLabel,
        string SourceModName,
        string SourceModDirectory,
        string SourceModRootPath,
        string SourceRelativePath,
        Guid? SourceModStableId,
        ResourceSourceState SourceState,
        string Section,
        string Slot,
        int Order,
        string OptionMapping,
        IReadOnlyList<string> OptionMemberships,
        List<ResourceView> Children)
    {
        // Classified once: the type filters and tree drawing query these every frame.
        // Children are complete when a view is constructed and are never mutated.
        public ResourceKinds Kinds { get; } = ResourceKindClassifier.Classify(Type, GamePath, ActualPath);
        public ResourceKinds DescendantKinds { get; } =
            Children.Aggregate(ResourceKinds.None, (kinds, child) => kinds | child.SubtreeKinds);
        public ResourceKinds SubtreeKinds => Kinds | DescendantKinds;

        private ResourceView[]? _flattened;

        /// <summary> This view followed by all of its descendants, depth first. </summary>
        public IReadOnlyList<ResourceView> Flattened => _flattened ??= Flatten(this).ToArray();
    }
}
