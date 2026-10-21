using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Skeletons;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// <see cref="HairSkeletonFiles"/> with the game's files, the framework thread for Havok and the
/// plugin's records. Everything after the model write turns a failure into a warning.
/// </summary>
internal sealed class HairSkeletonService(
    IFramework framework,
    IPluginLog log,
    IDataManager data,
    PenumbraIpc ipc,
    EstEntryStore estStore,
    HairSkeletonStore store)
{
    /// <summary>
    /// Build the model's skeleton files for every race it is used as hair for. Call it off the framework
    /// thread: Havok is marshalled onto it and waited for.
    /// </summary>
    public async Task<(HairSkeletonPlan? Plan, string? Error)> PrepareAsync(
        HairSkeletonRequest rig, string modFolder, string modDirectory, string modelRelativePath)
    {
        try
        {
            var modRoot = await framework.RunOnFrameworkThread(ipc.GetModDirectory).ConfigureAwait(false);
            await LeaveFrameworkThread();
            if (string.IsNullOrWhiteSpace(modRoot))
                throw new InvalidDataException("The Penumbra mod root is unavailable.");
            var sources = new HairSkeletonSources(
                path => data.GetFile(path)?.Data,
                data.FileExists,
                (template, bones) => framework.RunOnFrameworkThread(() => HairSkeletonWriter.Write(template, bones))
                    .GetAwaiter().GetResult(),
                template => framework.RunOnFrameworkThread(() => HairSkeletonWriter.TemplateKao(template))
                    .GetAwaiter().GetResult(),
                () => HairSkeletonIds.Library(modRoot),
                Random.Shared);
            var plan = await Task.Run(() => HairSkeletonFiles.Prepare(
                rig, modFolder, modDirectory, modelRelativePath, sources, store)).ConfigureAwait(false);
            return (plan, null);
        }
        catch (InvalidDataException e)
        {
            // A refusal of the user's rig or mod, not a fault: no stack trace.
            log.Warning("Could not build the hair skeleton: {Message}", e.Message);
            return (null, $"The hair skeleton could not be built: {e.Message}");
        }
        catch (Exception e)
        {
            log.Error(e, "Could not build the hair skeleton.");
            return (null, $"The hair skeleton could not be built: {e.Message}");
        }
    }

    /// <summary>Write and map a prepared skeleton; returns its notes and warnings.</summary>
    public List<string> TryApply(HairSkeletonPlan plan, string modFolder, string modDirectory, string modelRelativePath)
    {
        try
        {
            return HairSkeletonFiles.Apply(plan, modFolder, modDirectory, modelRelativePath, store, estStore);
        }
        catch (Exception e)
        {
            log.Error(e, "Could not write the hair skeleton.");
            return [$"The hair skeleton could not be written: {e.Message}"];
        }
    }

    /// <summary>Take back the skeleton an earlier export made for the model; nothing without a record.</summary>
    public List<string> TryRemove(string modFolder, string modDirectory, string modelRelativePath)
    {
        try
        {
            return HairSkeletonFiles.Remove(modFolder, modDirectory, modelRelativePath, store, estStore);
        }
        catch (Exception e)
        {
            log.Error(e, "Could not remove the hair skeleton.");
            return [$"The hair skeleton could not be removed: {e.Message}"];
        }
    }

    /// <summary>The model's hair skeleton as a backup carries it; null when it can't be read.</summary>
    public JsonObject? TryCaptureState(string modFolder, string modDirectory, string modelRelativePath)
    {
        try
        {
            return HairSkeletonFiles.CaptureState(modFolder, modDirectory, modelRelativePath, store);
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not read the hair skeleton to keep with the backup.");
            return null;
        }
    }

    /// <summary>Put back the hair skeleton a backup carries; returns the warnings.</summary>
    public List<string> TryRestoreState(string modFolder, string modDirectory, string modelRelativePath, JsonObject state)
    {
        try
        {
            return HairSkeletonFiles.RestoreState(modFolder, modDirectory, modelRelativePath, state, store);
        }
        catch (Exception e)
        {
            log.Error(e, "Could not restore the backup's hair skeleton.");
            return [$"The backup's hair skeleton could not be restored: {e.Message}"];
        }
    }
}
