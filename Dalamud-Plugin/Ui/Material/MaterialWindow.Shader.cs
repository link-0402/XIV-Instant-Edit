using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Materials;
using InstantEdit.Studio.Models;

namespace InstantEdit.Ui.Material;

/// <summary>The Shader section: the shader package, the shader flags and the material keys, all named.</summary>
internal sealed partial class MaterialWindow
{
    private const uint HideBackfaces = 0x01;
    private const uint Translucent = 0x10;

    private void DrawShader(MaterialSession current, MaterialDocument doc)
    {
        Widgets.SectionHeader("Shader");
        var facts = ShaderPackages.Get(doc.ShaderPackage);
        using var disabled = ImRaii.Disabled(!doc.Editable || working != null);

        var known = ShaderInfo.All.FirstOrDefault(def => def.ShaderPackName.Equals(doc.ShaderPackage, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##material-shader", known?.DisplayName ?? doc.ShaderPackage))
        {
            if (combo.Success)
                foreach (var def in ShaderInfo.All)
                    if (ImGui.Selectable($"{def.DisplayName}##{def.ShaderPackName}", ReferenceEquals(def, known)) && !ReferenceEquals(def, known))
                        SwitchShader(current, def);
        }
        Tip("Which shader the game draws it with. Switching keeps what the new shader can use.");

        var flags = doc.ShaderFlags;
        var backfaces = (flags & HideBackfaces) != 0;
        if (ImGui.Checkbox("Hide backfaces##material-backfaces", ref backfaces))
            _ = current.Apply(d => d.WithFlags(backfaces ? d.ShaderFlags | HideBackfaces : d.ShaderFlags & ~HideBackfaces), "Hide backfaces");
        Tip(ShaderLabels.Flags[0].Help);
        ImGui.SameLine(0, Theme.Gap * 3);
        var crashes = Services.Materials.Presets.PresetChecks.TranslucentCrashes(doc.ShaderPackage);
        var translucent = (flags & Translucent) != 0;
        using (ImRaii.Disabled(crashes && !translucent))
        {
            if (ImGui.Checkbox("Translucent##material-translucent", ref translucent))
                _ = current.Apply(d => d.WithFlags(translucent ? d.ShaderFlags | Translucent : d.ShaderFlags & ~Translucent), "Translucent");
        }
        Tip(crashes ? Services.Materials.Presets.PresetChecks.TranslucentCrashNote : ShaderLabels.Flags[4].Help);
        var other = flags & ~(HideBackfaces | Translucent);
        if (other != 0)
        {
            ImGui.SameLine(0, Theme.Gap * 3);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, $"Other flags 0x{other:X}");
            Tip("Flags the editor does not know. They are kept.");
        }

        if (facts == null)
        {
            foreach (var (id, value) in doc.Keys)
                ImGui.TextColored(Theme.Muted, $"{KeyName(id)} = 0x{value:X8}");
            return;
        }
        foreach (var key in facts.Keys)
            DrawKey(current, doc, key);
        var stray = doc.Keys.Where(key => facts.Key(key.Id) == null).ToList();
        if (stray.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(Theme.Hint, "Ignored by this shader");
        foreach (var (id, value) in stray)
        {
            using var keyId = ImRaii.PushId($"stray-{id:X8}");
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"{KeyName(id)}: {ShaderLabels.KeyValue(id, value)}");
            ImGui.SameLine();
            if (Widgets.GhostIconButton("##remove-key", FontAwesomeIcon.Trash, "Remove it. The game ignores it on this shader."))
                _ = current.Apply(d => d.WithoutKey(id), "Remove key");
        }
    }

    private void DrawKey(MaterialSession current, MaterialDocument doc, PackageKey key)
    {
        var label = ShaderLabels.Key(key.Id);
        var value = doc.KeyValue(key.Id) ?? key.Default;
        using var id = ImRaii.PushId($"key-{key.Id:X8}");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, KeyName(key.Id));
        Tip(label.Help);
        ImGui.SameLine(Theme.Scaled(150));
        ImGui.SetNextItemWidth(-1);
        var preview = ShaderLabels.KeyValue(key.Id, value) + (key.Values.Contains(value) ? string.Empty : " (not in this shader)");
        using var combo = ImRaii.Combo("##value", preview);
        if (!combo.Success)
            return;
        foreach (var option in key.Values)
        {
            var text = ShaderLabels.KeyValue(key.Id, option) + (option == key.Default ? " (default)" : string.Empty);
            if (ImGui.Selectable($"{text}##{option:X8}", option == value) && option != value)
                _ = current.Apply(d => d.WithKey(key.Id, option), label.Label);
        }
    }

    /// <summary>A key's label; with the id beside it when a mod replaces the shader, as the labels may not fit then.</summary>
    private string KeyName(uint id)
    {
        var label = ShaderLabels.Key(id).Label;
        return customShader && !label.StartsWith("Unknown", StringComparison.Ordinal) ? $"{label} (0x{id:X8})" : label;
    }

    private void SwitchShader(MaterialSession current, ShaderDef def)
    {
        if (ShaderPackages.Get(def.ShaderPackName) is not { } facts)
            return;
        IReadOnlyList<string> notes = [];
        _ = current.Apply(doc =>
        {
            var (switched, changed) = MaterialSession.Reconcile(doc.WithShaderPackage(def.ShaderPackName), facts);
            notes = changed;
            return switched;
        }, "Shader");
        Say(notes.Count == 0 ? FeedbackSeverity.Info : FeedbackSeverity.Warning,
            $"Shader is now {def.DisplayName}." + (notes.Count == 0 ? string.Empty : " " + string.Join(" ", notes)));
    }
}
