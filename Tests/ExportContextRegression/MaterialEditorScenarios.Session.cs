using InstantEdit.Models;
using InstantEdit.Services.Materials;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>A material editing session: undo history, live versus structural edits, shader switches, and where the file came from.</summary>
internal static partial class MaterialEditorScenarios
{
    private const uint TextureMode = 0xB616DC5A;
    private const uint DecalKey = 0xD2777173;
    private const uint ReflectionSource = 0xF886E10E;

    public static void RunSession()
    {
        CheckHistory();
        CheckClassify();
        CheckReconcileToScroll();
        CheckSessionApply();
        CheckSourceFromRow();
        CheckSavedAsCopy();
    }

    private static void CheckSavedAsCopy()
    {
        var source = Source(ResourceSourceState.LoadedMod);
        var session = MaterialSession.CreateForRegression(source, PresetDocument(MaskPreset), (_, _) => Task.CompletedTask);
        const string pattern = "chara/equipment/e0085/texture/v01_c0201e0085_glv_fx.tex";
        session.AddNewFile(pattern, [1, 2, 3]);
        session.Apply(doc => doc.WithAddedSlot(0x0C5EC1F1, pattern), "Pattern");
        var saved = session.Current;
        var bytes = saved.Write();
        session.SavedAsCopy(saved, @"C:\Penumbra\Gloves\Style\Blue\glv_a.mtrl", @"Style\Blue\glv_a.mtrl", bytes);
        Require(session.Source.ActualPath == @"C:\Penumbra\Gloves\Style\Blue\glv_a.mtrl" && session.Source.RelativePath == "Style/Blue/glv_a.mtrl"
                && session.Source.Sha256 == InstantEdit.Services.PreviewMods.PreviewSource.Hash(bytes)
                && session.Source.GamePath == source.GamePath && session.Source.ModDirectory == source.ModDirectory,
            "after a copy into an option the session saves the copy, at the same game path in the same mod");
        Require(!session.History.Dirty && session.History.CanUndo && session.NewFiles.Count == 0,
            "the copied edits count as saved, stay undoable, and the textures that went with the copy aren't saved again");
    }

    private static void CheckHistory()
    {
        var start = PresetDocument(MaskPreset);
        var history = new MaterialHistory(start);
        var one = start.WithConstant(0x29AC0223, [0.1f]);
        var two = one.WithConstant(0x29AC0223, [0.2f]);
        var three = two.WithConstant(0x29AC0223, [0.3f]);
        history.Push(one, "Alpha cutoff");
        history.Push(two, "Alpha cutoff");
        history.Push(three, "Alpha cutoff");
        history.Undo();
        history.Undo();
        Require(ReferenceEquals(history.Current, one) && history.CanRedo && history.RedoLabel == "Alpha cutoff", "two undos go back two steps");
        history.Redo();
        Require(ReferenceEquals(history.Current, two) && history.Dirty, "a redo goes forward again and the document is unsaved");
        history.MarkSaved();
        Require(!history.Dirty, "marking saved clears the unsaved state");

        history.BeginGesture();
        var dragged = two;
        for (var i = 0; i < 5; i++)
        {
            dragged = dragged.WithConstant(0xD62BF368, [i]);
            history.Push(dragged, "Alpha sharpness");
        }
        Require(!history.CanUndo, "nothing can be undone while a drag runs");
        history.EndGesture();
        Require(history.Dirty && history.UndoLabel == "Alpha sharpness", "a finished drag is one step");
        history.Undo();
        Require(ReferenceEquals(history.Current, two) && !history.Dirty, "a drag of five changes undoes in one step");

        var capped = new MaterialHistory(start, capacity: 3);
        var doc = start;
        for (var i = 0; i < 5; i++)
            capped.Push(doc = doc.WithConstant(0x29AC0223, [i]), "step");
        var undone = 0;
        while (capped.Undo())
            undone++;
        Require(undone == 3, "the history keeps only its capacity of steps");
    }

    private static void CheckClassify()
    {
        var mask = PresetDocument(MaskPreset);
        Require(MaterialSession.Classify(mask, mask.WithConstant(0x29AC0223, [0.25f])) == EditClass.Live, "a constant change is shown live");
        Require(MaterialSession.Classify(mask, mask.WithSlot(0, mask.Slots[0] with { Path = "chara/x_n.tex" })) == EditClass.Structural,
            "a texture path change needs a redraw");
        Require(MaterialSession.Classify(mask, mask.WithSlot(0, mask.Slots[0].WithWrap(2, 2))) == EditClass.Live, "sampler settings are shown live");
        Require(MaterialSession.Classify(mask, mask.WithKey(TextureMode, 0x600EF9DF)) == EditClass.Structural,
            "switching the texture mode changes which textures are read, so it needs a redraw");
        var decalOff = mask.WithKey(DecalKey, 0x4242B842);
        // Writing a loaded material's key values showed nothing in game (2026-10-17), and Penumbra never writes them live.
        Require(MaterialSession.Classify(decalOff, decalOff.WithKey(DecalKey, 0x584265DD)) == EditClass.Structural, "any key change needs a redraw");
        Require(MaterialSession.Classify(mask, mask.WithShaderPackage("characterscroll.shpk")) == EditClass.Structural, "a shader switch needs a redraw");
        Require(MaterialSession.Classify(mask, mask.WithFlags(mask.ShaderFlags | 0x10)) == EditClass.Live, "shader flags are shown live");
        var table = mask.Table!.Clone();
        table.Rows[3].Roughness = 0.9f;
        Require(MaterialSession.Classify(mask, mask.WithTable(table)) == EditClass.Live && MaterialSession.Classify(mask, mask.WithTable(table.WithDyeTable(false))) == EditClass.Structural,
            "row edits are live; adding or dropping the dye table needs a redraw");
    }

    private static void CheckReconcileToScroll()
    {
        var mask = PresetDocument(MaskPreset);
        var (scroll, scrollNotes) = MaterialSession.Reconcile(mask.WithShaderPackage("characterscroll.shpk"), ShaderPackages.Get("characterscroll.shpk")!);
        Require(scroll.KeyValue(TextureMode) == 0x5CC605B5 && scroll.KeyValue(0x40D1481E) == 0x337C6BC4 && scroll.KeyValue(0xF52CCF05) == 0xA7D2FF60
                && scroll.KeyValue(ReflectionSource) == 0x69EB4AE0 && scroll.KeyValue(DecalKey) == 0x4242B842,
            "switching to the scroll shader keeps its keys, adds the reflection source and puts an unsupported decal value back to Off");
        Require(scroll.Slots.Select(slot => slot.SamplerId).SequenceEqual(mask.Slots.Select(slot => slot.SamplerId)) && scroll.Table != null
                && scrollNotes.Any(note => note.Contains("Face decal", StringComparison.Ordinal)),
            "the scroll shader keeps every slot and the table, and says what it changed");

        var (glass, _) = MaterialSession.Reconcile(mask.WithShaderPackage("characterglass.shpk"), ShaderPackages.Get("characterglass.shpk")!);
        Require(glass.KeyValue(TextureMode) == null && glass.KeyValue(0x40D1481E) == null && glass.Table is { Kind: ColorTableKind.Dawntrail }
                && glass.KeyValue(0xE8DA5B62) == 0x7B804D6E,
            "switching to glass drops the texture mode and flow keys, adds the depth key and keeps the table");

        var (hair, hairNotes) = MaterialSession.Reconcile(mask.WithShaderPackage("hair.shpk"), ShaderPackages.Get("hair.shpk")!);
        Require(hair.Slots.All(slot => slot.SamplerId != 0x565F8FD8) && hair.Table == null && hair.KeyValue(0xF52CCF05) == 0xDFE74BAC
                && hairNotes.Any(note => note.Contains("Index", StringComparison.Ordinal) && note.Contains("_id.tex", StringComparison.Ordinal))
                && hairNotes.Any(note => note.Contains("colour table", StringComparison.Ordinal)),
            "switching to hair drops the index slot and the colour table, and the notes name both");

        var bare = PresetDocument(HairPreset).WithShaderPackage("character.shpk");
        var (character, characterNotes) = MaterialSession.Reconcile(bare, ShaderPackages.Get("character.shpk")!);
        var index = character.Slots.FirstOrDefault(slot => slot.SamplerId == 0x565F8FD8);
        Require(index is { Path: "chara/human/c0101/obj/hair/h0001/texture/c0101h0001_hir_id.tex" } && character.Table is { Kind: ColorTableKind.Dawntrail, Rows.Length: 32 }
                && characterNotes.Count >= 2,
            "switching a hair material to character.shpk adds an index slot next to the normal map and a fresh colour table");
    }

    private static void CheckSessionApply()
    {
        var edits = new List<EditClass>();
        var session = MaterialSession.CreateForRegression(Source(ResourceSourceState.LoadedMod), PresetDocument(MaskPreset), (_, kind) =>
        {
            edits.Add(kind);
            return Task.CompletedTask;
        });
        session.Apply(doc => doc.WithConstant(0x29AC0223, [0.75f]), "Alpha cutoff");
        session.Apply(doc => doc.WithShaderPackage("characterscroll.shpk"), "Shader");
        session.Apply(doc => doc, "Nothing");
        Require(edits.SequenceEqual([EditClass.Live, EditClass.Structural]) && session.History.Dirty && session.History.UndoLabel == "Shader",
            "applying edits records them and reports live or structural; an edit that changes nothing adds no step");
    }

    private static MaterialSource Source(ResourceSourceState state)
    {
        var view = Row(state);
        var actor = new ActorView(new OnScreenObject
        {
            ObjectIndex = 0, Address = 0x10, Name = "Player", PresentationCategory = ActorPresentationCategory.Player, ResourceRoots = [],
        }, "Player", "Player", [view], 0);
        return MaterialSource.FromRow(view, actor, File.ReadAllBytes(PresetPath(MaskPreset)));
    }

    private static ResourceView Row(ResourceSourceState state) => state == ResourceSourceState.LoadedMod
        ? new ResourceView("Mtrl", "", "mt_c0201e0085_glv_a.mtrl", "chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a.mtrl",
            @"C:\Penumbra\Gloves\files\glv_a.mtrl", "Gloves", "Gloves", "Gloves", @"C:\Penumbra\Gloves", @"files\glv_a.mtrl", null,
            ResourceSourceState.LoadedMod, "Gear", "Hands", 0, "", [], [])
        : new ResourceView("Mtrl", "", "mt_c0201e0085_glv_a.mtrl", "chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a.mtrl",
            "chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a.mtrl", "Game", "", "", "", "", null,
            ResourceSourceState.GameData, "Gear", "Hands", 0, "", [], []);

    private static void CheckSourceFromRow()
    {
        var mod = Source(ResourceSourceState.LoadedMod);
        var game = Source(ResourceSourceState.GameData);
        Require(!mod.IsGameData && game.IsGameData && mod.RelativePath == "files/glv_a.mtrl" && mod.ObjectIndex == 0
                && mod.Sha256 == InstantEdit.Services.PreviewMods.PreviewSource.Hash(File.ReadAllBytes(PresetPath(MaskPreset))),
            "a material source knows whether it is game data, its mod-relative path, object and hash");
        Require(mod.ToPreviewSource().IsModFile && !game.ToPreviewSource().IsModFile && game.ToPreviewSource().GamePath == game.GamePath,
            "only a mod row's source can be written back in place");
    }
}
