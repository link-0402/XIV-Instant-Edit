using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Answers "which template will this material be built from, and what will building do?" the
/// same way the build does — a bundled preset for the shader, else the item's vanilla material —
/// so the validator and inspector can say before a build which textures get added, which shader
/// keys change, and which textures cannot be wired in at all. Render thread only.
/// </summary>
internal sealed class MaterialTemplates
{
    private readonly GameDataService _gameData;
    private readonly string _pluginDirectory;
    private readonly Dictionary<string, MtrlInfo?> _presets = new();

    public MaterialTemplates(GameDataService gameData, string pluginDirectory)
    {
        _gameData        = gameData;
        _pluginDirectory = pluginDirectory;
    }

    public sealed record Resolved(MtrlInfo Template, string Source, bool IsPreset);

    /// <summary>The template <paramref name="mat"/> would be built from, or null when there is none.</summary>
    public Resolved? Resolve(PortItem item, MaterialSetup mat)
    {
        var presetPath = MaterialPresetLibrary.FindPresetPath(mat.ShaderType, mat.Textures.Select(t => t.Type).ToHashSet());
        if (presetPath != null)
        {
            var preset = LoadPreset(presetPath);
            return preset != null ? new Resolved(preset, $"bundled preset · {Path.GetFileNameWithoutExtension(presetPath)}", true) : null;
        }

        var race = item.Subject.MaterialRaces(item.Models).FirstOrDefault();
        var vanilla = _gameData.FindVanillaMaterial(item.Subject, mat.Name, race);
        return vanilla != null ? new Resolved(vanilla.Value.Material, $"cloned from {vanilla.Value.GamePath}", false) : null;
    }

    /// <summary>What building <paramref name="mat"/> would do, or null when it has no template.</summary>
    public MaterialPlan? Plan(PortItem item, MaterialSetup mat)
    {
        var resolved = Resolve(item, mat);
        return resolved == null ? null : MaterialPatcher.Plan(resolved.Template, mat.Textures.Select(t => t.Type), resolved.IsPreset);
    }

    private MtrlInfo? LoadPreset(string relativePath)
    {
        if (_presets.TryGetValue(relativePath, out var cached))
            return cached;

        MtrlInfo? info = null;
        try
        {
            var full = Path.Combine(_pluginDirectory, relativePath);
            if (File.Exists(full))
                info = MtrlReader.Read(File.ReadAllBytes(full));
        }
        catch (IOException ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not read bundled preset {0}", relativePath);
        }
        _presets[relativePath] = info;
        return info;
    }
}
