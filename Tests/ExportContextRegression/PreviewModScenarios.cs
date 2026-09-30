using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.PreviewMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The shared preview-mod records and store the skin seam fix builds on: sources, previews saved
/// before the store was shared, round trips with every game path a file replaces, and the texts
/// applying reports.
/// </summary>
internal static class PreviewModScenarios
{
    private const string PreviewFolder = "Skin Seam Preview - A";

    public static void Run(string testRoot)
    {
        PreviewSource source = new NeckSeamSource
        {
            GamePath = "chara/x.mtrl", ActualPath = @"C:\Mods\Skin\x.mtrl", State = ResourceSourceState.LoadedMod, ModName = "Skin",
            ModDirectory = "Skin", RelativePath = "x.mtrl", Sha256 = "AB",
        };
        Require(source.IsModFile && source.Label == "Skin: x.mtrl",
            "preview mods: the neck seam's sources are the shared sources, with the same meaning");

        // A NeckSeamPreviews.json as the neck seam wrote it before the preview-mod service was shared.
        var folder = Path.Combine(testRoot, "PreviewStores");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "NeckSeamPreviews.json"),
            """
            [
              {
                "Id": "0f8fad5b-d9cb-469f-a165-70867728950e",
                "ModDirectory": "Neck Seam Preview - A",
                "ModIdentifier": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
                "CollectionId": "550e8400-e29b-41d4-a716-446655440000",
                "CollectionName": "Default",
                "ActorName": "A",
                "ObjectIndex": 0,
                "Created": "2026-09-28T12:00:00+00:00",
                "Files": [
                  {
                    "Kind": "face material",
                    "GamePath": "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                    "PreviewGamePath": "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                    "PreviewRelativePath": "Files/chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                    "PreviewSha256": "00",
                    "Source": {
                      "GamePath": "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                      "ActualPath": "C:\\Mods\\Skin\\x.mtrl",
                      "State": "LoadedMod",
                      "ModName": "Skin",
                      "ModDirectory": "Skin",
                      "ModRootPath": "",
                      "RelativePath": "x.mtrl",
                      "ModStableId": null,
                      "Sha256": "AB"
                    },
                    "TextureRewrites": { "a_ns1.tex": "a.tex" }
                  }
                ],
                "Changes": [ "Face material: tile size" ]
              }
            ]
            """);
        var neckSeam = new NeckSeamPreviewStore(folder);
        neckSeam.Load();
        var old = neckSeam.Previews.Single();
        Require(neckSeam.LoadError.Length == 0 && old.Files.Single().Source.IsModFile && old.Files.Single().TextureRewrites["a_ns1.tex"] == "a.tex" &&
                neckSeam.HoldsMod("neck seam preview - a") && !neckSeam.HoldsMod("Skin") && neckSeam.PreviewFor("A") == old,
            "preview mods: neck seam previews saved before the shared store still load, and the store knows their mod folders");

        var store = new PreviewModStore<PreviewMod>(folder, "SharedPreviews.json", "shared previews");
        var preview = new PreviewMod
        {
            Id = Guid.NewGuid(), ModDirectory = PreviewFolder, ModIdentifier = Guid.NewGuid(), CollectionId = Guid.NewGuid(), CollectionName = "Default",
            ActorName = "A", ObjectIndex = 0, Created = DateTimeOffset.UtcNow, Changes = ["x.tex: welded"],
            Files =
            [
                new PreviewModFile
                {
                    Kind = "texture", GamePath = "chara/a/x.tex", GamePaths = ["chara/a/x.tex", "chara/b/x.tex"], PreviewRelativePath = "Files/chara/a/x.tex",
                    PreviewSha256 = "01", Note = "used by Default", Source = source,
                },
                new PreviewModFile
                {
                    Kind = "texture", GamePath = "chara/c/y.tex", PreviewRelativePath = "Files/chara/c/y.tex", PreviewSha256 = "02",
                    Source = new PreviewSource { GamePath = "chara/c/y.tex", ActualPath = "chara/c/y.tex", State = ResourceSourceState.GameData, Sha256 = "03" },
                },
            ],
        };
        store.Add(preview);
        var reloaded = new PreviewModStore<PreviewMod>(folder, "SharedPreviews.json", "shared previews");
        reloaded.Load();
        var files = reloaded.Previews.Single().Files;
        Require(reloaded.LoadError.Length == 0 && files[0].FixGamePaths.SequenceEqual(["chara/a/x.tex", "chara/b/x.tex"]) &&
                files[1].FixGamePaths.SequenceEqual(["chara/c/y.tex"]) && files[0].Note == "used by Default" && !files[1].Source.IsModFile &&
                reloaded.HoldsMod(PreviewFolder),
            "preview mods: previews survive a reload with every game path their files replace");
        reloaded.Remove(preview.Id);
        Require(reloaded.Previews.Count == 0 && !reloaded.HoldsMod(PreviewFolder), "preview mods: an applied or discarded preview is forgotten");

        var entry = PreviewModEntry.At("/chara\\x\\--y.tex", [1]);
        Require(entry.RelativePath == "Files/chara/x/--y.tex" && entry.GamePaths.SequenceEqual(["chara/x/--y.tex"]),
            "preview mods: a file mapped from one game path sits at Files/<game path>");
        Require(new PreviewApplyResult(["Skin", "Face"], 2, "Neck Seam Fix - A", 1, []).Describe("Neck seam fix applied") ==
                "Neck seam fix applied: wrote 2 files into Skin, Face (backups kept for 7 days); put 1 game file in the mod Neck Seam Fix - A." &&
                new PreviewApplyResult([], 0, null, 0, []).Describe("Neck seam fix applied") == "Nothing needed writing; the preview is removed.",
            "preview mods: applying reports what it wrote the way the neck seam always has");
        Require(PenumbraService.UniqueModName("", _ => false, "Skin Seam Fix") == "Skin Seam Fix" &&
                PenumbraService.UniqueModName("Skin Seam Preview - A", name => name == "Skin Seam Preview - A") == "Skin Seam Preview - A (2)",
            "preview mods: new mod names fall back to the feature's name and are made unique");
    }
}
