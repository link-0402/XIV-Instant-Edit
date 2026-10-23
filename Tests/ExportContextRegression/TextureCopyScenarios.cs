using System.Buffers.Binary;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// On Screen's "Edit as a separate copy": planning a texture copy for one material, then writing it into the
/// material's mod with its own game path, mapped wherever the material is, and the material pointed at it.
/// </summary>
internal static class TextureCopyScenarios
{
    private const string Diffuse = "chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex";
    private const string Shared = "chara/common/texture/white.tex";
    private const string MaterialPath = "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl";
    private const string MaterialFile = "top/mt_c0101e0001_top_a.mtrl";

    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "TextureCopies");
        Directory.CreateDirectory(root);
        var id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");
        var material = NeckSeamScenarios.Material([Diffuse, Shared], [], []);

        var (plan, gamePath, relative) = TextureCopies.Plan(material, MaterialPath, MaterialFile, Shared, Shared, id);
        Require(plan.CopyTexture == "chara/common/texture/ie_1a2b3c4d/white.tex" && gamePath == plan.CopyTexture &&
                plan.CopyGamePaths.SequenceEqual([plan.CopyTexture]) && relative == "top/ie_1a2b3c4d_white.tex" &&
                plan.MaterialTexture == Shared && TextureCopies.IsSafe(plan),
            "texture copy: a folder of its own keeps the file name, and the file sits beside the material");
        Require(SharedTextures.EditBlock(gamePath, false).Length == 0,
            "texture copy: a copy of white.tex isn't the shared blank texture any more");
        try
        {
            TextureCopies.Plan(material, MaterialPath, MaterialFile, "chara/other.tex", "chara/other.tex", id);
            Fail("texture copy: a texture the material doesn't name is refused");
        }
        catch (IOException) { }

        // The material asks for the DirectX 11 file, so the copy is mapped in both forms.
        var dx11 = material.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(dx11.AsSpan(16 + 4 + 2), 0x8000);
        var (dxPlan, dxPath, _) = TextureCopies.Plan(dx11, MaterialPath, MaterialFile, "chara/common/texture/--white.tex", Shared, id);
        Require(dxPath == "chara/common/texture/ie_1a2b3c4d/--white.tex" && dxPlan.CopyTexture == "chara/common/texture/ie_1a2b3c4d/white.tex" &&
                dxPlan.CopyGamePaths.SequenceEqual([dxPlan.CopyTexture, dxPath]),
            "texture copy: a DirectX 11 entry maps the copy's stored and -- paths");

        var mod = Path.Combine(root, "Top Mod");
        Directory.CreateDirectory(Path.Combine(mod, "top"));
        File.WriteAllBytes(Path.Combine(mod, "top", "mt_c0101e0001_top_a.mtrl"), material);
        var meta = ModMetadata.CreateV4ModMetadata("Top Mod", "A", "", "", new JsonObject
        {
            ["Files"] = new JsonObject { [MaterialPath] = "top\\mt_c0101e0001_top_a.mtrl" },
        });
        meta["Groups"] = new JsonArray
        {
            new JsonObject
            {
                ["Type"] = "Single", ["Name"] = "Colour",
                ["Options"] = new JsonArray
                {
                    new JsonObject { ["Name"] = "Red", ["Files"] = new JsonObject { ["chara/other.mtrl"] = MaterialFile } },
                    new JsonObject { ["Name"] = "Blue", ["Files"] = new JsonObject { ["chara/blue.mtrl"] = "top/blue.mtrl" } },
                },
            },
        };
        ModMetadata.WriteJsonAtomic(Path.Combine(mod, "meta.json"), meta);
        var backups = new ModelBackupStore(Path.Combine(root, "backups"));
        var tex = Tex();

        // Something else changed the material after the session opened, and it no longer names the texture: nothing is written.
        var metaBefore = File.ReadAllText(Path.Combine(mod, "meta.json"));
        var materialOnDisk = Path.Combine(mod, "top", "mt_c0101e0001_top_a.mtrl");
        File.WriteAllBytes(materialOnDisk, NeckSeamScenarios.Material([Diffuse], [], []));
        try
        {
            TextureCopies.Write(mod, "Top Mod", relative, plan, tex, backups, () => true);
            Fail("texture copy: a material that no longer names the texture stops the copy");
        }
        catch (IOException) { }
        Require(!File.Exists(Path.Combine(mod, "top", "ie_1a2b3c4d_white.tex")) && File.ReadAllText(Path.Combine(mod, "meta.json")) == metaBefore,
            "texture copy: a stopped copy leaves the mod as it was");
        File.WriteAllBytes(materialOnDisk, material);
        // A second copy, of the other texture, planned from the same material before the first one is saved.
        var (second, _, secondRelative) = TextureCopies.Plan(material, MaterialPath, MaterialFile, Diffuse, Diffuse,
            Guid.Parse("5e6f7a8b-0000-0000-0000-000000000000"));

        var backup = TextureCopies.Write(mod, "Top Mod", relative, plan, tex, backups, () => true);
        var written = ModMetadata.LoadV4ModMetadata(mod);
        var options = written["Groups"]![0]!["Options"]!.AsArray();
        Require(File.ReadAllBytes(Path.Combine(mod, "top", "ie_1a2b3c4d_white.tex")).SequenceEqual(tex) &&
                written["DefaultData"]!["Files"]![plan.CopyTexture]!.GetValue<string>() == relative &&
                options[0]!["Files"]![plan.CopyTexture]!.GetValue<string>() == relative &&
                options[1]!["Files"]![plan.CopyTexture] is null,
            "texture copy: the copy is mapped in every option that maps the material, and only there");
        var textures = MaterialPreviewBundleBuilder.ReadMaterialTextures(File.ReadAllBytes(Path.Combine(mod, "top", "mt_c0101e0001_top_a.mtrl")));
        Require(textures.Select(t => t.StoredPath).SequenceEqual([Diffuse, plan.CopyTexture]) &&
                File.ReadAllBytes(backup).SequenceEqual(material),
            "texture copy: the material names the copy, keeps its other textures, and was backed up first");
        Require(TextureModBackend.TextureGamePaths(mod, relative).SetEquals([plan.CopyTexture]),
            "texture copy: the session's later saves find the copy's mapping");

        TextureCopies.Write(mod, "Top Mod", secondRelative, second, tex, backups, () => true);
        Require(MaterialPreviewBundleBuilder.ReadMaterialTextures(File.ReadAllBytes(materialOnDisk)).Select(t => t.StoredPath)
                    .SequenceEqual([second.CopyTexture, plan.CopyTexture]) &&
                ModMetadata.LoadV4ModMetadata(mod)["DefaultData"]!["Files"]![plan.CopyTexture]!.GetValue<string>() == relative,
            "texture copy: a second copy from the same material saves too, keeping the first one's change and mapping");

        var session = new TextureEditSession
        {
            GamePath = gamePath, ModDirectory = "Top Mod", ModRoot = mod, RelativePath = relative,
            PixelHash = "pixels", ObjectIndex = 0, ActorAddress = 1, PendingCopy = plan,
        };
        var copyRequest = new TextureEditRequest(Shared, Shared, "", "", "", 0, 1,
            Copy: new TextureCopyMaterial(MaterialPath, "Top Mod", mod, MaterialFile));
        var editRequest = new TextureEditRequest(gamePath, session.TargetFile, "Top Mod", mod, relative, 0, 1);
        Require(TextureEditService.IsReusableFor(session, copyRequest) && !TextureEditService.IsReusableFor(session, editRequest) &&
                !TextureEditService.IsReusableFor(session, copyRequest with { Copy = copyRequest.Copy! with { RelativePath = "top/other.mtrl" } }),
            "texture copy: an unsaved copy continues only for the same texture and material");
        session.PendingCopy = null;
        Require(!TextureEditService.IsReusableFor(session, copyRequest) && TextureEditService.IsReusableFor(session, editRequest),
            "texture copy: once saved, the copy's session is the copied texture's session");
    }

    private static byte[] Tex()
    {
        var bytes = new byte[80 + 4 * 4 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = 1;
        bytes[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 80);
        bytes.AsSpan(80).Fill(200);
        return bytes;
    }
}
