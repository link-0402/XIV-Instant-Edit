using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Main;

/// <summary> The toolbar's options popover: model import, texture editing and animation export settings. </summary>
internal sealed class OptionsPopup
{
    private const string OptionsPopupName = "##instant-edit-options";
    private readonly Configuration _config;
    private readonly Action _saveConfig;

    public OptionsPopup(Configuration config, Action saveConfig)
    {
        _config = config;
        _saveConfig = saveConfig;
    }

    public void Open() => ImGui.OpenPopup(OptionsPopupName);

    public void Draw()
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(600, 640));
        using var popup = ImRaii.Popup(OptionsPopupName);
        if (!popup.Success)
            return;
        Widgets.SectionHeader("Model import");
        DrawModelOptions();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Widgets.SectionHeader("Texture editing");
        DrawTextureOptions();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Widgets.SectionHeader("Animation export");
        DrawAnimationExportOptions();
    }

    private void DrawTextureOptions()
    {
        var recompress = _config.RecompressTextures;
        if (ImGui.Checkbox("Recompress saved textures", ref recompress))
        {
            _config.RecompressTextures = recompress;
            _saveConfig();
        }
        Widgets.HintWrapped("Texture edits keep their original compression (BC1–BC7). When off, saves are written uncompressed: larger files without compression artifacts.");
    }

    private void DrawModelOptions()
    {
        var useExistingSkeleton = _config.UseExistingSkeleton;
        if (ImGui.Checkbox("Remove imported armature and use existing skeleton", ref useExistingSkeleton))
        {
            _config.UseExistingSkeleton = useExistingSkeleton;
            SaveModelOptions();
        }

        if (_config.UseExistingSkeleton)
        {
            Widgets.HintWrapped("Imported meshes receive an Armature modifier targeting this Blender object:");
            var skeletonName = _config.SkeletonObjectName;
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(180), ImGui.GetContentRegionAvail().X * 0.45f));
            if (ImGui.InputText("Skeleton object", ref skeletonName, 128))
            {
                _config.SkeletonObjectName = skeletonName;
                SaveModelOptions();
            }
            ImGui.SameLine();
            Widgets.Hint("must be an existing Blender Armature");
        }
        else
        {
            Widgets.HintWrapped("Each import creates its own InstantEditArmature.");
        }

        var racialScaling = _config.ApplyRacialScaling;
        if (ImGui.Checkbox("Apply racial scaling for model import", ref racialScaling))
        {
            _config.ApplyRacialScaling = racialScaling;
            SaveModelOptions();
        }
        Widgets.HintWrapped("Models made for another race, like the c0201 gear most female races wear, go to Blender and Substance Painter " +
                            "shaped for the character's race, with its skeleton, as the game shows them on it. Browsed mods and game files use " +
                            "the first on-screen character. Quick Export can save scaled models only as that race's own model, and Simple Export warns first. " +
                            "Send my character to Blender always scales them, whether this is on or off.");

        var applyTexturesAndMaterials = _config.ApplyTexturesAndMaterials;
        if (ImGui.Checkbox("Apply textures and materials", ref applyTexturesAndMaterials))
        {
            _config.ApplyTexturesAndMaterials = applyTexturesAndMaterials;
            SaveModelOptions();
        }
        Widgets.HintWrapped("Creates display-only Blender materials. Quick Export still writes model data only.");

        using var indent = ImRaii.PushIndent();
        using (ImRaii.Disabled(!applyTexturesAndMaterials))
        {
            var excludeBodyAndGeneralMaterials = _config.ExcludeBodyAndGeneralMaterials;
            if (ImGui.Checkbox("Exclude body and general materials", ref excludeBodyAndGeneralMaterials))
            {
                _config.ExcludeBodyAndGeneralMaterials = excludeBodyAndGeneralMaterials;
                SaveModelOptions();
            }
        }
        Widgets.HintWrapped("Leaves body skin, body-piercing, and pube materials as colored placeholders.");
    }

    private void SaveModelOptions()
    {
        _config.SkeletonObjectName = string.IsNullOrWhiteSpace(_config.SkeletonObjectName)
            ? "Skeleton"
            : _config.SkeletonObjectName.Trim();
        if (_config.SkeletonObjectName.Length > 128)
            _config.SkeletonObjectName = _config.SkeletonObjectName[..128];
        _saveConfig();
    }

    private void DrawAnimationExportOptions()
    {
        var armature = _config.AnimationArmatureName;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(180), ImGui.GetContentRegionAvail().X * 0.45f));
        if (ImGui.InputText("Blender armature", ref armature, 128))
        {
            _config.AnimationArmatureName = armature;
            _saveConfig();
        }
        Widgets.HintWrapped("Animations become a new action on this armature. Without an object of this name, " +
                            "Blender uses the active armature, or the scene's only one.");
        var keyScale = _config.AnimationKeyScale;
        if (ImGui.Checkbox("Key bone scale", ref keyScale))
        {
            _config.AnimationKeyScale = keyScale;
            _saveConfig();
        }
        Widgets.HintWrapped("On keys bone scale too, and sets bones under an unevenly scaled bone to inherit scale " +
                            "Aligned, the way the game scales them. Recordings leave Customize+ out, since MagicFit " +
                            "adds it. Off leaves bone scaling applied in Blender in place.");
    }
}
