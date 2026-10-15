using InstantEdit.Studio;

namespace InstantEdit.Ui;

/// <summary>
/// The hand-offs of the files of a mod open in a mod window: the same Edit in Blender, Edit
/// texture and Send animation as the rows here, reported in this window's status feed.
/// </summary>
public sealed partial class MainWindow : IModHandoff
{
    string? IModHandoff.ModelBlock(ResourceView row)
    {
        if (!row.IsModel || !ResourceViews.IsSafeModel(row))
            return "Only .mdl files of the mod can be sent to Blender.";
        return _models.Busy.IsBusy ? "Another model is being sent." : null;
    }

    void IModHandoff.EditModel(ResourceView row, ActorView mod) => _models.TryEditNode(row, mod);

    string? IModHandoff.TextureBlock(ResourceView row)
    {
        if (!IsTextureRow(row))
            return "Only .tex files can be edited.";
        if (TextureEditBlock(row) is { Length: > 0 } blocked)
            return blocked;
        if (!TextureEditAvailable(row))
            return "This texture has no verified writable mod source.";
        return Volatile.Read(ref _textureBusy) != 0 ? "Another texture is being opened." : null;
    }

    void IModHandoff.EditTexture(ResourceView row, ActorView mod) => StartTextureEdit(row, mod);

    string? IModHandoff.AnimationBlock(ResourceView row) => AnimationSendBlock(row);

    void IModHandoff.SendAnimation(ResourceView row, string owner) => OpenAnimationSend(row, owner);

    void IModHandoff.DrawDialogs(string owner) => DrawAnimationSendDialog(owner);

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
