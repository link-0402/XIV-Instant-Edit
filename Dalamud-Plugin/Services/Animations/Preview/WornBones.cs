using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;
using Lumina.Data;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// The bones the models a character draws have weight on: its gear (not what other gear hides), hair, tail
/// and ears, on the parts the game shows. The Bones window's map shows them besides the bones the clip moves.
/// The models are looked up on the framework thread twice a second; their files are read on a task, each
/// once, and kept.
/// </summary>
internal sealed class WornBones(IDataManager data, IPluginLog log)
{
    private const long CheckMilliseconds = 500;
    private const long MaximumFileBytes = 64L * 1024 * 1024;

    // Each model file's bones by submesh; null for a file that couldn't be read.
    private readonly ConcurrentDictionary<string, ModelBones?> files = new(StringComparer.Ordinal);
    private long checkedAt;
    // The models the character draws, as last looked up, and the ones the last read was started for.
    private PainterLiveModel[] models = [];
    private PainterLiveModel[]? read;
    private (PainterLiveModel[] Models, Task<IReadOnlySet<string>?> Task)? reading;
    private IReadOnlySet<string>? bones;

    /// <summary>
    /// The bones the models of the character <paramref name="playback"/> edits for have weight on; null while unknown, and when a
    /// model can't be read. After an outfit change the last ones stay until the new ones are read.
    /// </summary>
    public IReadOnlySet<string>? Current(AnimationPlayback playback)
    {
        if (reading is { Task.IsCompleted: true } done)
        {
            reading = null;
            // A read for models the character no longer draws is dropped; the read below starts for the new ones.
            if (ReferenceEquals(done.Models, models))
                bones = done.Task.IsCompletedSuccessfully ? done.Task.Result : null;
        }
        var now = Environment.TickCount64;
        if (now - checkedAt >= CheckMilliseconds)
        {
            checkedAt = now;
            IReadOnlyList<PainterLiveModel>? live;
            try { live = playback.ActorModels(); }
            catch (InvalidOperationException) { live = null; }
            // No character, or one without models for a moment while it is drawn again.
            if (live is not { Count: > 0 })
            {
                models = [];
                bones = null;
            }
            else if (!live.SequenceEqual(models))
                models = [.. live];
        }
        if (reading == null && models.Length > 0 && !ReferenceEquals(models, read))
        {
            var wanted = models;
            read = wanted;
            reading = (wanted, Task.Run(() => Read(wanted)));
        }
        return bones;
    }

    private IReadOnlySet<string>? Read(PainterLiveModel[] wanted)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in wanted)
        {
            // Weapons move bones of their own skeleton.
            if (!model.Drawn || model.Slot < 0)
                continue;
            if (files.GetOrAdd(model.Path, Load) is not { } weighted)
                return null;
            union.UnionWith(weighted.Weighted([model.EnabledAttributes]));
        }
        return union;
    }

    private ModelBones? Load(string path)
    {
        try
        {
            byte[]? bytes;
            if (Path.IsPathRooted(path))
            {
                var info = new FileInfo(path);
                bytes = info.Exists && info.Length is > 0 and <= MaximumFileBytes ? File.ReadAllBytes(info.FullName) : null;
            }
            else
                bytes = data.GetFile<FileResource>(PathRules.NormalizeGamePath(path))?.Data;
            return bytes == null ? null : ModelBones.Of(ModelMeshReader.Read(bytes, skin: true));
        }
        catch (Exception e)
        {
            log.Debug(e, "Could not read which bones {Path} is weighted to.", path);
            return null;
        }
    }
}
