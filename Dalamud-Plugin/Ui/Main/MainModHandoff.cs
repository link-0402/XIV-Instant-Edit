using InstantEdit.Studio;

namespace InstantEdit.Ui.Main;

/// <summary>
/// The hand-offs of the files of a mod open in a mod window: the same Edit in Blender, Edit
/// texture and Send animation as the main window's rows, reported in its status feed.
/// </summary>
internal sealed class MainModHandoff : IModHandoff
{
    private readonly ModelSender _models;
    private readonly TextureSessionsTab _textureTab;
    private readonly AnimationSendDialog _animationSend;
    private readonly StatusFeed _feed;

    public MainModHandoff(ModelSender models, TextureSessionsTab textureTab, AnimationSendDialog animationSend, StatusFeed feed)
    {
        _models = models;
        _textureTab = textureTab;
        _animationSend = animationSend;
        _feed = feed;
    }

    string? IModHandoff.ModelBlock(ResourceView row)
    {
        if (!row.IsModel || !ResourceViews.IsSafeModel(row))
            return "Only .mdl files of the mod can be sent to Blender.";
        return _models.Busy.IsBusy ? "Another model is being sent." : null;
    }

    void IModHandoff.EditModel(ResourceView row, ActorView mod) => _models.TryEditNode(row, mod);

    string? IModHandoff.TextureBlock(ResourceView row)
    {
        if (!TextureSessionsTab.IsTextureRow(row))
            return "Only .tex files can be edited.";
        if (TextureSessionsTab.TextureEditBlock(row) is { Length: > 0 } blocked)
            return blocked;
        if (!TextureSessionsTab.TextureEditAvailable(row))
            return "This texture has no verified writable mod source.";
        return _textureTab.Busy.IsBusy ? "Another texture is being opened." : null;
    }

    void IModHandoff.EditTexture(ResourceView row, ActorView mod) => _textureTab.StartTextureEdit(row, mod);

    string? IModHandoff.AnimationBlock(ResourceView row) => _animationSend.AnimationSendBlock(row);

    void IModHandoff.SendAnimation(ResourceView row, string owner) => _animationSend.OpenAnimationSend(row, owner);

    void IModHandoff.DrawDialogs(string owner) => _animationSend.Draw(owner);

    (DateTimeOffset Time, string Text, FeedbackSeverity Severity)? IModHandoff.LatestMessage
    {
        get
        {
            StatusEntry? latest = null;
            foreach (var channel in new[] { StatusChannel.Models, StatusChannel.Textures, StatusChannel.Animations })
                if (_feed.Latest(channel) is { } entry && (latest is null || entry.Time > latest.Time))
                    latest = entry;
            return latest is null ? null : (latest.Time, latest.Text, latest.Severity);
        }
    }
}
