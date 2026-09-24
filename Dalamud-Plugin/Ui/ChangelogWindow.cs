using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace InstantEdit.Ui;

public sealed class ChangelogWindow : Window
{
    private readonly Configuration _config;
    private readonly Action _saveConfiguration;
    private readonly string _currentVersion;
    private string _since;
    private bool _onlyNew;

    public ChangelogWindow(Configuration config, string currentVersion, Action saveConfiguration)
        : base("XIV Instant Edit Changelog##Changelog")
    {
        _config = config;
        _currentVersion = currentVersion;
        _saveConfiguration = saveConfiguration;
        _since = _config.LastSeenChangelogVersion;

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
            _onlyNew = HasNewReleases();
            IsOpen = true;
            MarkAsSeen();
        }
    }

    public void Open()
    {
        if (!IsOpen)
            _since = _config.LastSeenChangelogVersion;
        IsOpen = true;
        MarkAsSeen();
    }

    public void Close() => IsOpen = false;

    public override void Draw()
    {
        ImGui.TextColored(Theme.Accent, "XIV INSTANT EDIT");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, "CHANGELOG");
        ImGui.TextColored(Theme.Muted, $"Version {_currentVersion}");
        if (HasNewReleases())
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            if (Widgets.Chip($"New since {_since}", CountNewReleases(), _onlyNew))
                _onlyNew = !_onlyNew;
        }
        ImGui.Separator();

        var availableHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y);
        using var content = ImRaii.Child("##instant-edit-changelog-content", new Vector2(0, availableHeight), true);
        if (!content.Success)
            return;

        foreach (var release in ChangelogCatalog.Releases)
        {
            if (_onlyNew && !IsNewerThanSince(release.Version))
                continue;
            DrawRelease(release);
            ImGui.Spacing();
        }
    }

    private bool HasNewReleases()
        => _since.Length > 0 && Version.TryParse(_since, out _) && CountNewReleases() > 0;

    private int CountNewReleases()
        => ChangelogCatalog.Releases.Count(release => IsNewerThanSince(release.Version));

    private bool IsNewerThanSince(string version)
        => Version.TryParse(_since, out var since) && Version.TryParse(version, out var candidate) && candidate > since;

    private static void DrawRelease(ChangelogRelease release)
    {
        ImGui.TextColored(Theme.Accent, $"Version {release.Version}");
        ImGui.Separator();

        foreach (var entry in release.Entries)
        {
            using var indent = ImRaii.PushIndent(ImGui.GetStyle().IndentSpacing * entry.Indent, false, entry.Indent > 0);

            var color = entry.Kind switch
            {
                ChangelogEntryKind.Highlight => Theme.Highlight,
                ChangelogEntryKind.Important => Theme.Important,
                _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
            };

            ImGui.TextColored(color, "•");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextWrapped(entry.Text);
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
