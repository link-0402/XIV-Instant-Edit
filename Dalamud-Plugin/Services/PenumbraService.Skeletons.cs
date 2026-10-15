using Penumbra.Api.IpcSubscribers;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModCleanup;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    /// <summary>
    /// The encoded meta manipulations Penumbra applies to a game object (see
    /// <c>AnimationMetadata.Decode</c>), or null when Penumbra can't give them.
    /// </summary>
    internal Task<string?> MetaManipulationsAsync(int objectIndex) => _framework.RunOnFrameworkThread(() =>
    {
        try
        {
            var encoded = new GetMetaManipulations(_pi).Invoke(objectIndex);
            return string.IsNullOrEmpty(encoded) ? null : encoded;
        }
        catch (Exception e)
        {
            _log.Debug(e, "Could not read the meta manipulations of object {ObjectIndex}.", objectIndex);
            return null;
        }
    });
}
