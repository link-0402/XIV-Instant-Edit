using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Audio;

namespace InstantEdit.Ui.Timeline;

/// <summary>What the person chose in the <see cref="SoundLinkPopup"/> this frame.</summary>
internal enum SoundLinkChoice { None, Import, DownloadForFile, Cancel }

/// <summary>
/// The "Sound from a link" popup: a YouTube link with Start and End, the question before the sound
/// tools are downloaded, and the running import's phase with Cancel. A picked file that needs ffmpeg
/// opens it on the question alone. <see cref="TimelinePickers"/> runs the import; this only draws and asks.
/// </summary>
internal sealed class SoundLinkPopup
{
    private const string PopupId = "##timeline-sound-link";
    public const string ToolsQuestion = "This needs yt-dlp and ffmpeg (about 230 MB). Download them?";
    public const string FileToolsQuestion = "This file needs ffmpeg (about 230 MB with yt-dlp). Download them?";

    private bool openRequested;
    private bool closeRequested;
    private bool forFile;
    private bool asking;
    private string url = string.Empty;
    private string start = string.Empty;
    private string end = string.Empty;
    private LinkRequest? pending;

    /// <summary>Why the last import failed, shown until the next try.</summary>
    public string? Error { get; set; }

    /// <summary>The running import's phase ("Downloading 42%"); null when nothing runs.</summary>
    public string? Phase { get; set; }

    public bool Running => Phase != null;

    /// <summary>Whether the popup was opened for a picked file (the question alone) rather than a link.</summary>
    public bool ForFile => forFile;

    public void OpenForLink()
    {
        openRequested = true;
        closeRequested = false;
        forFile = false;
        asking = false;
        Error = null;
    }

    public void OpenForFile()
    {
        openRequested = true;
        closeRequested = false;
        forFile = true;
        asking = true;
        Error = null;
    }

    /// <summary>Closes the popup on its next draw.</summary>
    public void Close() => closeRequested = true;

    /// <summary>Draws the popup at the current ID level; <paramref name="request"/> is set with <see cref="SoundLinkChoice.Import"/>.</summary>
    public SoundLinkChoice Draw(bool toolsInstalled, out LinkRequest? request)
    {
        request = null;
        if (openRequested)
        {
            ImGui.OpenPopup(PopupId);
            openRequested = false;
        }
        if (!ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
        {
            // A close asked for while it was already shut must not shut the next one.
            closeRequested = false;
            return SoundLinkChoice.None;
        }
        try
        {
            if (closeRequested)
            {
                closeRequested = false;
                ImGui.CloseCurrentPopup();
                return SoundLinkChoice.None;
            }
            var width = Theme.Scaled(420);
            using var wrap = ImRaii.TextWrapPos(width);
            ImGui.TextColored(Theme.Label, forFile ? "Sound tools" : "Sound from a link");
            ImGui.Separator();
            if (Running)
                return DrawRunning(width);
            if (asking)
                return DrawQuestion(out request);
            return DrawForm(width, toolsInstalled, out request);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private SoundLinkChoice DrawForm(float width, bool toolsInstalled, out LinkRequest? request)
    {
        request = null;
        var parse = DrawFields(width, false);
        if (url.Trim().Length > 0)
        {
            if (parse.Error != null)
                ImGui.TextColored(Theme.Warning, parse.Error);
            else if (parse.Request is { } valid)
                ImGui.TextColored(Theme.Hint, valid.ToEnd
                    ? $"From {LinkInput.Format(valid.Start)} to the end, at most 10 minutes."
                    : $"{LinkInput.Format(valid.End - valid.Start)} of audio.");
        }
        if (Error != null)
            ImGui.TextColored(Theme.Warning, Error);
        Widgets.Hint("You need the rights to use this audio.");
        ImGui.Spacing();
        var choice = SoundLinkChoice.None;
        using (ImRaii.Disabled(!parse.Ok))
        {
            if (ImGui.Button("Import") && parse.Request is { } picked)
            {
                Error = null;
                if (toolsInstalled)
                {
                    request = picked;
                    choice = SoundLinkChoice.Import;
                }
                else
                {
                    pending = picked;
                    asking = true;
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
            choice = SoundLinkChoice.Cancel;
        }
        return choice;
    }

    private SoundLinkChoice DrawQuestion(out LinkRequest? request)
    {
        request = null;
        ImGui.TextUnformatted(forFile ? FileToolsQuestion : ToolsQuestion);
        Widgets.HintWrapped("They are kept in the plugin's folder.");
        if (Error != null)
            ImGui.TextColored(Theme.Warning, Error);
        ImGui.Spacing();
        var choice = SoundLinkChoice.None;
        if (ImGui.Button("Download"))
        {
            Error = null;
            if (forFile)
                choice = SoundLinkChoice.DownloadForFile;
            else if (pending is { } waiting)
            {
                request = waiting;
                choice = SoundLinkChoice.Import;
            }
            asking = forFile;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            if (forFile)
            {
                ImGui.CloseCurrentPopup();
                choice = SoundLinkChoice.Cancel;
            }
            asking = false;
        }
        return choice;
    }

    private SoundLinkChoice DrawRunning(float width)
    {
        if (!forFile)
            DrawFields(width, true);
        Widgets.Spinner();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, Phase ?? string.Empty);
        if (!ImGui.Button("Cancel"))
            return SoundLinkChoice.None;
        ImGui.CloseCurrentPopup();
        return SoundLinkChoice.Cancel;
    }

    /// <summary>The link, Start and End; a pasted link with t= fills an empty Start.</summary>
    private LinkParse DrawFields(float width, bool locked)
    {
        using (ImRaii.Disabled(locked))
        {
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputTextWithHint("##sound-link-url", "YouTube link", ref url, 512) && start.Trim().Length == 0 &&
                LinkInput.Parse(url, null, null).LinkStart is { } linked)
                start = LinkInput.Format(linked);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Start");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Theme.Scaled(80));
            ImGui.InputTextWithHint("##sound-link-start", "0:00", ref start, 16);
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.TextColored(Theme.Muted, "End");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Theme.Scaled(80));
            ImGui.InputTextWithHint("##sound-link-end", "to the end", ref end, 16);
        }
        return LinkInput.Parse(url, start, end);
    }
}
