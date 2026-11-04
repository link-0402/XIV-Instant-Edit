using System.Collections.Immutable;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Copying faces into an Instant Edit library: which copies go in, which the library has already, which need a yes to
/// replace, the names copies may take, and the expression set list taking a library's new file.
/// </summary>
internal static class FaceLibraryCopyFixture
{
    private static readonly byte[] SetA = [1];
    private static readonly byte[] SetB = [2];
    private static readonly byte[] Held = [3];
    private static readonly IReadOnlySet<string> Resident = new HashSet<string>(StringComparer.Ordinal) { "cfxf_blink_r" };

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var library = new[] { new FaceCopyEntry("cfxf_grin", 0, "grin", Held), new FaceCopyEntry("cfxf_pout", 1, "pout", Held) };
        var wink = new FaceCopy(new FaceCopyEntry("cfxf_wink", 4, "wink", SetA), "cfxf_wink");

        var plan = FaceLibraryCopy.Plan(library, [wink], Resident, false);
        check(plan.Added is ["cfxf_wink"] && plan.Already.IsEmpty && plan.Asks.IsEmpty && plan.Build is { Count: 3 } build &&
              build[0] == (Held, 0, "cfxf_grin") && build[1] == (Held, 1, "cfxf_pout") && build[2] == (SetA, 4, "cfxf_wink"),
            "a new face goes in after the library's own, which stay in order");
        check(FaceLibraryCopy.Plan([], [wink], Resident, false).Build is [(var pap, 4, "cfxf_wink")] && pap == SetA,
            "a library without a file for the face gets one holding the copy");

        var sameGrin = new FaceCopy(new FaceCopyEntry("cfxf_grin", 7, "grin", SetB), "cfxf_grin");
        check(FaceLibraryCopy.Plan(library, [sameGrin], Resident, false) is { Build: null, Already: ["cfxf_grin"], Added.IsEmpty: true },
            "a face the library has with the same motion is there already, and nothing is written");
        check(FaceLibraryCopy.Plan(library, [sameGrin, wink], Resident, false) is { Build.Count: 3, Already: ["cfxf_grin"], Added: ["cfxf_wink"] },
            "faces already there are skipped while the others go in");

        var otherGrin = new FaceCopy(new FaceCopyEntry("cfxf_grin", 2, "another grin", SetB), "cfxf_grin");
        check(FaceLibraryCopy.Plan(library, [otherGrin, wink], Resident, false) is { Build: null, Asks: ["cfxf_grin"], Added: ["cfxf_wink"] },
            "a name the library has for another motion is asked about, and nothing is written until then");
        check(FaceLibraryCopy.Plan(library, [otherGrin], Resident, true) is { Replaced: ["cfxf_grin"], Build: [var kept, var copied] } &&
              kept == (Held, 1, "cfxf_pout") && copied == (SetB, 2, "cfxf_grin"),
            "with a yes the copy replaces the library's face of that name");

        var renamed = new FaceCopy(otherGrin.Source, "cfxf_grin2");
        check(FaceLibraryCopy.Plan(library, [renamed], Resident, false) is { Added: ["cfxf_grin2"], Build.Count: 3 },
            "a copy under another name goes in beside the library's face");
        check(FaceLibraryCopy.Plan(library, [wink, new FaceCopy(new FaceCopyEntry("cfxf_wink", 9, "wink", SetB), "cfxf_wink")], Resident, false)
              is { Added: ["cfxf_wink"], Build.Count: 3 },
            "the same motion copied twice goes in once");

        reject(() => FaceLibraryCopy.Plan(library, [wink, new FaceCopy(new FaceCopyEntry("cfxf_wink", 9, "other wink", SetB), "cfxf_wink")], Resident, false),
            "two copies of one name with different motions are refused");
        reject(() => FaceLibraryCopy.Plan(library, [new FaceCopy(new FaceCopyEntry("cfxf_blink_r", 0, "blink", SetA), "cfxf_blink_r")], Resident, false),
            "a face the face always has loaded is not copied");
        reject(() => FaceLibraryCopy.Plan(library, [], Resident, false), "a copy needs a face");

        check(FaceLibraryCopy.NameProblem("cfxf_grin", "cfxf_grin") == null && FaceLibraryCopy.NameProblem("cfxf_grin", "cfxf_big_grin2") == null &&
              FaceLibraryCopy.NameProblem("cfxl_lip_a", "cfxl_lip_b") == null,
            "a copy keeps its name, or takes one of its own kind");
        check(FaceLibraryCopy.NameProblem("cfxf_grin", "cfxl_grin") != null && FaceLibraryCopy.NameProblem("cfxf_grin", "cfxf_") != null &&
              FaceLibraryCopy.NameProblem("cfxf_grin", "cfxf_Big") != null && FaceLibraryCopy.NameProblem("cfxf_grin", "cfxf_" + new string('a', 27)) != null,
            "a new name of another kind, empty, with other characters or over 31 bytes is refused");
        check(FaceLibraryCopy.NameProblem("cfxf_" + new string('a', 26), "cfxf_" + new string('a', 26)) == null &&
              FaceLibraryCopy.NameProblem("cfxf_" + new string('é', 14), "cfxf_" + new string('é', 14)) != null,
            "a kept name counts bytes, not characters");

        // The expression set list takes a library's new file in place of its old one.
        FaceLibrary Lib(string mod, string path, string file) =>
            new("my_faces", "c0801", "f0002", path, file, mod, "My Faces", [new FaceLibraryMotion("cfxf_grin", false)]);
        const string path = "chara/human/c0801/animation/f0002/nonresident/my_faces.pap";
        const string otherPath = "chara/human/c0801/animation/f0001/nonresident/my_faces.pap";
        var list = ImmutableArray.Create(Lib("mine", path, "old.pap"), Lib("mine", otherPath, "f1.pap"), Lib("theirs", path, "theirs.pap"));
        var next = FaceLibraryIndex.Upserted(list, "MINE", path.ToUpperInvariant(), Lib("mine", path, "new.pap"));
        check(next.Length == 3 && next.Count(l => l.ModDirectory == "mine" && l.GamePath == path) == 1 &&
              next.Single(l => l.ModDirectory == "mine" && l.GamePath == path).File == "new.pap" && next.Any(l => l.File == "f1.pap") && next.Any(l => l.File == "theirs.pap"),
            "a library's new file replaces its old one for that face only, and other mods' stay");
        check(FaceLibraryIndex.Upserted(list, "mine", path, null) is { Length: 2 } gone && gone.All(l => l.File != "old.pap"),
            "a library that maps no faces there any more leaves the list");
    }
}
