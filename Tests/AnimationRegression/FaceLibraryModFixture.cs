using System.Text;
using InstantEdit.Services.Animations.Timeline;

/// <summary>Instant Edit expression libraries: pack and face names, clashes, free names and the marker file.</summary>
internal static class FaceLibraryModFixture
{
    public static void Run(Action<bool, string> check)
    {
        check(FaceLibraryMod.PackName("My Faces!! v2") == "my_faces_v2" && FaceLibraryMod.PackName("  __Sample--Library__  ") == "sample_library" &&
              FaceLibraryMod.PackName("Café Faces") == "caf_faces" && FaceLibraryMod.PackName("ABC_123") == "abc_123",
            "library names become lowercase packs with single underscores and none at the ends");
        check(FaceLibraryMod.PackName("") == null && FaceLibraryMod.PackName("  ") == null && FaceLibraryMod.PackName("!!! ---") == null &&
              FaceLibraryMod.PackName("___") == null && FaceLibraryMod.PackName("ÉÈ") == null && FaceLibraryMod.PackName(new string('a', 300)) == null,
            "names with nothing usable, or too long for a pack, have no pack");

        string[] installed = ["Other_Set"];
        Func<string, bool> game = pack => pack is "emot" or "pose";
        check(FaceLibraryMod.PackProblem("sample", game, installed) == null, "a pack nobody uses is free");
        check(FaceLibraryMod.PackProblem("emot", game, installed) != null && FaceLibraryMod.PackProblem("other_set", game, installed) != null,
            "a pack the game or an installed library uses is refused, whatever its case there");
        check(FaceLibraryMod.PackProblem("Emot", game, installed) != null && FaceLibraryMod.PackProblem("a-b", _ => false, []) != null,
            "a pack that isn't lowercase a-z 0-9 _ is refused");

        var stem26 = new string('s', 26);
        check(FaceLibraryMod.FaceName("Big Smile") == "cfxf_big_smile" && FaceLibraryMod.FaceName(stem26) is { } longest &&
              Encoding.UTF8.GetByteCount(longest) == 31,
            "face names are cfxf_ and the cleaned stem, up to 31 bytes");
        check(FaceLibraryMod.FaceName(stem26 + "s") == null && FaceLibraryMod.FaceName("??") == null && FaceLibraryMod.FaceName("") == null,
            "a stem over 26 characters or with nothing usable has no face name");

        Func<string, bool> folder = motion => motion == "cfxf_smile";
        check(FaceLibraryMod.FaceNameProblem("cfxf_grin", folder, false) == null && FaceLibraryMod.FaceNameProblem("cfxf_smile", folder, false) != null,
            "a face name the game has for the folder is refused");
        check(FaceLibraryMod.FaceNameProblem("cfxf_smile", folder, true) == null, "copies keep a name the game has");
        check(FaceLibraryMod.FaceNameProblem("cfxf_Grin", folder, false) != null && FaceLibraryMod.FaceNameProblem("cfxf_" + stem26 + "s", folder, false) != null &&
              FaceLibraryMod.FaceNameProblem("smile", folder, false) != null,
            "a face name that breaks the rules is refused");

        var temp = Path.Combine(Path.GetTempPath(), "ie-face-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, FaceLibraryMod.MarkerFile);
            check(!FaceLibraryMod.TryRead(temp, out _), "a mod without a marker is no library");
            var marker = new FaceLibraryMarker(FaceLibraryMod.Version, "Sample Library", "sample_library");
            File.WriteAllText(path, FaceLibraryMod.Serialize(marker));
            check(FaceLibraryMod.TryRead(temp, out var read) && read == marker, "a marker reads back as written");
            foreach (var (json, what) in new[]
                     {
                         ("{", "bad JSON"), ("null", "null"), ("[]", "an array"),
                         ("""{"Name":"Sample","Pack":"sample"}""", "no version"),
                         ("""{"Version":2,"Name":"Sample","Pack":"sample"}""", "another version"),
                         ("""{"Version":"1","Name":"Sample","Pack":"sample"}""", "a text version"),
                         ("""{"Version":1,"Pack":"sample"}""", "no name"),
                         ("""{"Version":1,"Name":" ","Pack":"sample"}""", "a blank name"),
                         ("""{"Version":1,"Name":"Sample"}""", "no pack"),
                         ("""{"Version":1,"Name":"Sample","Pack":""}""", "an empty pack"),
                         ("""{"Version":1,"Name":"Sample","Pack":"Sample"}""", "an uppercase pack"),
                         ("""{"Version":1,"Name":"Sample","Pack":"a-b"}""", "a pack with a dash"),
                         ("""{"Version":1,"Name":"Sample","Pack":"a/b"}""", "a pack with a folder"),
                         ("""{"Version":1,"Name":"Sample","Pack":"_a"}""", "a pack with an outer underscore"),
                     })
            {
                File.WriteAllText(path, json);
                check(!FaceLibraryMod.TryRead(temp, out _), $"a marker with {what} is no library");
            }
            File.WriteAllText(path, FaceLibraryMod.Serialize(marker) + new string(' ', 70 * 1024));
            check(!FaceLibraryMod.TryRead(temp, out _), "an oversized marker is not read");
            File.Delete(path);
            Directory.CreateDirectory(path);
            check(!FaceLibraryMod.TryRead(temp, out _), "a folder named like the marker is no library");
        }
        finally { Directory.Delete(temp, true); }
    }
}
