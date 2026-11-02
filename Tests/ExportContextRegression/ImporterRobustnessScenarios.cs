using System.Diagnostics;
using InstantEdit.Studio.Services.Library;
using InstantEdit.Studio.Services.Unity;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Files and game paths that come from other people's mods and packages: nothing in them may make a
/// scan slow or throw.
/// </summary>
internal static class ImporterRobustnessScenarios
{
    private const string ShaderGuid = "0123456789abcdef0123456789abcdef";
    private const string TextureGuid = "fedcba9876543210fedcba9876543210";

    public static void Run()
    {
        YamlReadsBothLineEndings();
        BlankLinesDoNotMakeYamlReadsSlow();
        FullWidthDigitsInAGamePathAreNotAnId();
    }

    private static string Material(string newline) => string.Join(newline,
        "Material:", "  m_Name: Skin", $"  m_Shader: {{fileID: 46, guid: {ShaderGuid}, type: 0}}",
        "  m_SavedProperties:", "    m_TexEnvs:", "    - _MainTex:",
        $"        m_Texture: {{fileID: 2800000, guid: {TextureGuid}, type: 3}}", "");

    private static void YamlReadsBothLineEndings()
    {
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var material = UnityYaml.ReadMaterial("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Material(newline));
            Require(material is { Name: "Skin" } && material.Shader == ShaderGuid &&
                    material.Textures.TryGetValue("_MainTex", out var texture) && texture == TextureGuid,
                $"a material is read with {(newline == "\n" ? "LF" : "CRLF")} line endings");
        }
        var (guid, type) = UnityYaml.ReadMeta("fileFormatVersion: 2\r\nguid: " + TextureGuid + "\r\nTextureImporter:\r\n  textureType: 1\r\n");
        Require(guid == TextureGuid && type == 1, "a meta file with CRLF line endings keeps its GUID and texture type");
    }

    private static void BlankLinesDoNotMakeYamlReadsSlow()
    {
        // A run of N blank lines used to cost N squared in each of the line patterns: three seconds at 80,000.
        var blanks = new string('\n', 100_000);
        var watch = Stopwatch.StartNew();
        UnityYaml.ReadMaterial("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Material:\n" + blanks);
        UnityYaml.ReadMeta("TextureImporter:\n" + blanks);
        Require(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"a material and a meta file made of blank lines are read at once ({watch.ElapsedMilliseconds} ms)");
    }

    private static void FullWidthDigitsInAGamePathAreNotAnId()
    {
        // \d matches any Unicode digit, int.Parse only ASCII ones; the library scan stopped on the exception.
        var tags = ChangesIndex.Classify(
            ["chara/human/c0101/obj/face/f\uFF10\uFF10\uFF10\uFF11/model/c0101f0001_fac.mdl",
             "chara/human/c0101/obj/hair/h0007/model/c0101h0007_hir.mdl"],
            []);
        Require(tags.Count == 1, $"a game path with full-width digits is skipped and the next one still counted ({tags.Count})");
    }
}
