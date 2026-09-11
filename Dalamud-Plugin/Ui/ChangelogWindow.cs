using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace InstantEdit.Ui;

public sealed class ChangelogWindow : Window
{
    private static readonly Vector4 HeaderColor = new(.95f, .78f, .35f, 1f);
    private static readonly Vector4 MutedColor = new(.58f, .60f, .67f, 1f);
    private static readonly Vector4 HighlightColor = new(1f, .82f, .35f, 1f);
    private static readonly Vector4 ImportantColor = new(1f, .52f, .38f, 1f);

    private readonly Configuration _config;
    private readonly Action _saveConfiguration;
    private readonly string _currentVersion;

    public ChangelogWindow(Configuration config, string currentVersion, Action saveConfiguration)
        : base("XIV Instant Edit Changelog##Changelog")
    {
        _config = config;
        _currentVersion = currentVersion;
        _saveConfiguration = saveConfiguration;

        Size = new Vector2(640, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 320),
            MaximumSize = new Vector2(1000, 900),
        };
        AllowPinning = true;
        AllowBackgroundBlur = true;

        if (ChangelogCatalog.ShouldAutoOpen(_config.LastSeenChangelogVersion, _currentVersion))
        {
            IsOpen = true;
            MarkAsSeen();
        }
    }

    public void Open()
    {
        IsOpen = true;
        MarkAsSeen();
    }

    public void Close() => IsOpen = false;

    public override void Draw()
    {
        ImGui.TextColored(HeaderColor, "XIV INSTANT EDIT");
        ImGui.SameLine();
        ImGui.TextColored(MutedColor, "CHANGELOG");
        ImGui.TextColored(MutedColor, $"Version {_currentVersion}");
        ImGui.Separator();

        var availableHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginChild("##instant-edit-changelog-content", new Vector2(0, availableHeight), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var release in ChangelogCatalog.Releases)
        {
            DrawRelease(release);
            ImGui.Spacing();
        }

        ImGui.EndChild();
    }

    private static void DrawRelease(ChangelogRelease release)
    {
        ImGui.TextColored(HeaderColor, $"Version {release.Version}");
        ImGui.Separator();

        foreach (var entry in release.Entries)
        {
            if (entry.Indent > 0)
                ImGui.Indent(ImGui.GetStyle().IndentSpacing * entry.Indent);

            var color = entry.Kind switch
            {
                ChangelogEntryKind.Highlight => HighlightColor,
                ChangelogEntryKind.Important => ImportantColor,
                _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
            };

            ImGui.TextColored(color, "•");
            ImGui.SameLine(0, 6);
            ImGui.TextWrapped(entry.Text);

            if (entry.Indent > 0)
                ImGui.Unindent(ImGui.GetStyle().IndentSpacing * entry.Indent);
        }
    }

    private void MarkAsSeen()
    {
        if (string.IsNullOrWhiteSpace(_currentVersion) ||
            string.Equals(_config.LastSeenChangelogVersion, _currentVersion, StringComparison.OrdinalIgnoreCase))
            return;

        _config.LastSeenChangelogVersion = _currentVersion;
        _saveConfiguration();
    }
}
