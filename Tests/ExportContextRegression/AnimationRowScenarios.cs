using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Animation rows in the resource browsers: which .pap files count as character animations that
/// can be sent to Blender, and how the Mod Browser lists them. None of this touches ImGui or Dalamud.
/// </summary>
internal static class AnimationRowScenarios
{
    public static void Run()
    {
        const string emote = "chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap";
        Require(ResourceKindClassifier.IsCharacterAnimation(emote) &&
                ResourceKindClassifier.IsCharacterAnimation("chara/human/c0101/animation/f0003/nonresident/smile.pap") &&
                ResourceKindClassifier.IsCharacterAnimation(@"Chara\Human\C1801\Animation\a0001\bt_common\resident\idle.PAP"),
            "packs under chara/human/cNNNN/animation are character animations, whatever the case or separator");
        Require(!ResourceKindClassifier.IsCharacterAnimation("chara/equipment/e6001/animation/a0001/material/mt_e6001.pap") &&
                !ResourceKindClassifier.IsCharacterAnimation("chara/monster/m0001/animation/a0001/bt_common/resident/monster.pap") &&
                !ResourceKindClassifier.IsCharacterAnimation("chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb") &&
                !ResourceKindClassifier.IsCharacterAnimation("chara/human/c0801/animation/a0001/bt_common/emote/dance01.tmb") &&
                !ResourceKindClassifier.IsCharacterAnimation("chara/human/cX801/animation/a0001/bt_common/emote/dance01.pap") &&
                !ResourceKindClassifier.IsCharacterAnimation(""),
            "material animations, monster packs, skeletons and timelines are not character animations");
        Require(ResourceKindClassifier.Classify("Pap", emote, @"C:\mods\Dance\dance01.pap") == ResourceKinds.Animation &&
                ResourceKindClassifier.Classify("Pap", "chara/equipment/e6001/animation/a0001/material/mt_e6001.pap", "") == ResourceKinds.None,
            "only character animations are classified as the Animation kind");
        Require(ResourceKindClassifier.ForFilter("Animations") == ResourceKinds.Animation &&
                (ResourceKinds.Editable & ResourceKinds.Animation) != 0 && new ResourceKindSelection().Admitted.HasFlag(ResourceKinds.Animation),
            "the Animations filter and the tree view admit animation rows");

        var snapshot = new PenumbraModSnapshot("DanceMod", "Dance Mod", @"C:\mods\Dance", [
            new PenumbraModResource(emote, @"C:\mods\Dance\dance01.pap", "dance01.pap", "Dance: On", ["Dance: On"]),
            new PenumbraModResource("chara/equipment/e0001/model/c0101e0001_top.mdl", @"C:\mods\Dance\top.mdl", "top.mdl", "", []),
        ]);
        var view = ResourceViews.BuildModView(snapshot, 0);
        var row = view.Roots.Single(r => r.GamePath == emote);
        Require(row.Type == "Animation" && row.Kinds == ResourceKinds.Animation && ResourceViews.IsAnimation(row) &&
                row.SourceState == ResourceSourceState.LoadedMod && row.ActualPath == @"C:\mods\Dance\dance01.pap" &&
                row.OptionMapping == "Dance: On",
            "the Mod Browser lists a mod's animation packs as sendable animation rows with their option");
        Require(view.Summary == "1 model · 1 animation", "a mod's summary counts its animations");
        Require(!ResourceViews.IsAnimation(view.Roots.Single(r => r.Type == "Model")), "models are not animation rows");

        // Animations your character plays have their own tab; On Screen lists no animation rows.
        var onScreen = ResourceKindChipSet.OnScreen;
        var modBrowser = ResourceKindChipSet.ModBrowser;
        Require(onScreen.Chips.All(chip => chip.Kind != ResourceKinds.Animation) && (onScreen.Listed & ResourceKinds.Animation) == 0 &&
                modBrowser.Chips.Any(chip => chip.Kind == ResourceKinds.Animation) && modBrowser.Listed == ResourceKinds.Editable,
            "On Screen offers no Animations filter, and the Mod Browser keeps it");
        Require(onScreen.Counts.SequenceEqual([onScreen.Listed, ResourceKinds.Model, ResourceKinds.Texture, ResourceKinds.Material]) &&
                modBrowser.Counts.Count == modBrowser.Chips.Count + 1 && modBrowser.Counts[0] == ResourceKinds.Editable,
            "the All chip counts every listed row, followed by one count per chip");
        var screenKinds = new ResourceKindSelection(onScreen.Listed);
        Require(!screenKinds.Admits(row) && !screenKinds.AdmitsSubtree(row) && screenKinds.AdmitsSubtree(view.Roots.Single(r => r.Type == "Model")),
            "On Screen never shows an animation row, even if Penumbra reported one");
        screenKinds.Toggle(ResourceKinds.Model);
        Require(screenKinds.Admitted == ResourceKinds.Model && new ResourceKindSelection(modBrowser.Listed).Admits(row),
            "a kind filter narrows what the browser lists, and the Mod Browser lists animation rows");
    }
}
