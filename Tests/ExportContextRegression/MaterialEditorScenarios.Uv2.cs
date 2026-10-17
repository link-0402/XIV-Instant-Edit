using System.Buffers.Binary;
using InstantEdit.Models;
using InstantEdit.Services.Materials;
using InstantEdit.Services.Previews;
using InstantEdit.Services.PreviewMods;
using static InstantEdit.TestSupport.Assertions;

internal static partial class MaterialEditorScenarios
{
    /// <summary>
    /// The scroll shader reads its pattern at the mesh's second UV map (TEXCOORD0.zw); a mesh without one
    /// flashed as a whole in game (2026-10-17). The synthetic model's mesh 0 (mt_b) has a two-value half
    /// UV after a half4 normal in stream 1; mesh 1 (mt_a) has a four-value float UV already.
    /// </summary>
    private static void CheckModelUv2()
    {
        var model = PainterScenarios.SyntheticModel();
        var without = ModelUv2.Check(model, "mt_b.mtrl");
        var with = ModelUv2.Check(model, "mt_a.mtrl");
        Require(without is { Meshes: 1, WithoutUv2: 1, CanAdd: true } && with is { Meshes: 1, WithoutUv2: 0 } && ModelUv2.Check(model, "mt_c.mtrl").Meshes == 0,
            "the check finds the material's meshes and which lack a second UV map");

        var added = ModelUv2.Add(model, "mt_b.mtrl");
        Require(ModelUv2.Check(added, "mt_b.mtrl") is { Meshes: 1, WithoutUv2: 0 } && added.Length == model.Length + 4 * 4,
            "adding gives the mesh a second UV map and grows the file by one half pair per vertex");
        var before = ModelMeshReader.Read(model);
        var after = ModelMeshReader.Read(added);
        Require(after.Meshes.Count == before.Meshes.Count && after.Meshes.Zip(before.Meshes).All(pair =>
                    pair.First.Positions.SequenceEqual(pair.Second.Positions) && pair.First.Normals.SequenceEqual(pair.Second.Normals)
                    && pair.First.Uvs.SequenceEqual(pair.Second.Uvs)
                    && pair.First.Submeshes.SelectMany(s => s.Indices).SequenceEqual(pair.Second.Submeshes.SelectMany(s => s.Indices))),
            "positions, normals, the first UV map and the triangles read as before");

        // Mesh 0's stream 1 is now normal (8) + uv (8) per vertex: the second map holds the first's halves.
        int meshTable = MeshTableOffset(added);
        var vertexStart = BinaryPrimitives.ReadUInt32LittleEndian(added.AsSpan(16));
        var stream1 = vertexStart + BinaryPrimitives.ReadUInt32LittleEndian(added.AsSpan(meshTable + 24));
        Require(added[meshTable + 33] == 16, "the UV stream's stride grew from 12 to 16");
        var copied = true;
        for (var v = 0; v < 4; v++)
        {
            var at = (int)stream1 + v * 16 + 8;
            copied &= added.AsSpan(at, 4).SequenceEqual(added.AsSpan(at + 4, 4));
        }
        Require(copied, "each vertex's second UV map is a copy of its first");
        Require(Throws<InvalidOperationException>(() => ModelUv2.Add(added, "mt_b.mtrl")), "a model that has the map already isn't changed again");
        Require(ModelUv2.Check(model[..200], "mt_b.mtrl").Blocked != null, "a cut-short model is reported, not thrown");
    }

    /// <summary>The model change goes with the material while it is on the scroll shader, and saves over its own mod's file.</summary>
    private static void CheckModelChangeSaves()
    {
        var mod = Source(ResourceSourceState.LoadedMod);
        var game = Source(ResourceSourceState.GameData);
        var modModel = new PreviewSource
        {
            GamePath = "chara/equipment/e0085/model/c0201e0085_glv.mdl", ActualPath = @"C:\Penumbra\Gloves\files\glv.mdl", State = ResourceSourceState.LoadedMod,
            ModName = "Gloves", ModDirectory = "Gloves", ModRootPath = @"C:\Penumbra\Gloves", RelativePath = "files/glv.mdl", Sha256 = "AA",
        };
        var gameModel = modModel with { ActualPath = modModel.GamePath, State = ResourceSourceState.GameData, ModName = "", ModDirectory = "", ModRootPath = "", RelativePath = "" };
        byte[] material = [1, 2, 3], changed = [7, 7];

        var intoMod = MaterialSession.SaveWrites(mod, material, [], [(modModel, changed)]);
        Require(intoMod.ModWrites.Count == 2 && intoMod.ModWrites[1].Source == modModel && intoMod.ModWrites[1].Bytes == changed && intoMod.GameFiles.Count == 0,
            "a mod model saves over its own file next to the material, backed up the same way");
        Require(Throws<InvalidOperationException>(() => MaterialSession.SaveWrites(mod, material, [], [(gameModel, changed)])),
            "a game model can't change with a mod material, which makes no new mod");
        var intoNewMod = MaterialSession.SaveWrites(game, material, [], [(gameModel, changed)]);
        Require(intoNewMod.GameFiles.Count == 2 && intoNewMod.GameFiles[1].RelativePath == "Files/" + gameModel.GamePath,
            "a game model goes into the new mod with a game material");

        var session = MaterialSession.CreateForRegression(mod, PresetDocument(MaskPreset), (_, _) => Task.CompletedTask);
        session.AddModelChange(modModel, changed);
        var scroll = session.Current.WithShaderPackage(MaterialSession.ScrollShader);
        Require(session.ModelChangesFor(session.Current).Count == 0 && session.ModelChangesFor(scroll).Single().Bytes == changed
                && session.HasModelChange(modModel.GamePath),
            "the model change counts only while the material is on the scroll shader, so undoing the preset drops it");
    }

    private static int MeshTableOffset(byte[] model)
    {
        int declarations = BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(12));
        var stringHeader = 68 + declarations * 17 * 8;
        var meshHeader = stringHeader + 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(stringHeader + 4));
        int elementIds = BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(meshHeader + 24));
        return meshHeader + 56 + elementIds * 32 + 3 * 60;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
}
