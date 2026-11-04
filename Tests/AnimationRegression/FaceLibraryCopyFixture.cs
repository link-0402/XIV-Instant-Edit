using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

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
        check(FaceLibraryCopy.Plan(library, [new FaceCopy(new FaceCopyEntry("cfxf_blink_r", 0, "edited blink", SetA), "cfxf_sleepy", true)], Resident, false)
              is { Added: ["cfxf_sleepy"], Build.Count: 3 },
            "an edited face from one the face always has loaded goes in under its new name");
        reject(() => FaceLibraryCopy.Plan(library, [new FaceCopy(new FaceCopyEntry("cfxf_grin", 0, "edited grin", SetA), "cfxf_blink_r", true)], Resident, false),
            "an edited face can't take the name of one the face always has loaded");

        // The save row's Library choice.
        check(LibrarySaveRules.Destinations(true) is [AnimationDestination.NewMod, AnimationDestination.InPlace, AnimationDestination.Library] &&
              LibrarySaveRules.Destinations(false) is [AnimationDestination.NewMod, AnimationDestination.InPlace],
            "Save to offers Library only for a face");
        check(LibrarySaveRules.Choice(["mine", "theirs"], null) == "mine" && LibrarySaveRules.Choice(["mine", "theirs"], "theirs") == "theirs" &&
              LibrarySaveRules.Choice(["mine"], "") == "" && LibrarySaveRules.Choice([], null) == "" && LibrarySaveRules.Choice(["mine"], "gone") == "mine",
            "the library chosen stays while it is there, else the first, else New library");
        check(LibrarySaveRules.Stem("cfxf_grin") == "grin" && LibrarySaveRules.Motion("grin") == "cfxf_grin" && LibrarySaveRules.Motion("big_grin2") == "cfxf_big_grin2",
            "the face name is the motion without cfxf_");
        check(LibrarySaveRules.Motion("") == null && LibrarySaveRules.Motion("Grin") == null && LibrarySaveRules.Motion("big grin") == null &&
              LibrarySaveRules.Motion(new string('a', 27)) == null && LibrarySaveRules.Motion(new string('a', 26)) == "cfxf_" + new string('a', 26),
            "a face name is a-z, 0-9 and _ within 31 bytes with cfxf_");
        var game = new HashSet<string>(StringComparer.Ordinal) { "cfxf_smile" };
        check(LibrarySaveRules.FaceNameProblem("smile", "cfxf_smile", false, game) == null && LibrarySaveRules.FaceNameProblem("smile", "cfxf_smile", false, null) == null,
            "an unedited face copied under its own name may keep a game face's name");
        check(LibrarySaveRules.FaceNameProblem("smile", "cfxf_grin", false, game) != null && LibrarySaveRules.FaceNameProblem("smile", "cfxf_smile", true, game) != null &&
              LibrarySaveRules.FaceNameProblem("grin", "cfxf_smile", true, game) == null,
            "a renamed or edited face can't take a game face's name");
        check(LibrarySaveRules.FaceNameProblem("grin", "cfxf_smile", true, null) != null && LibrarySaveRules.FaceNameProblem("Grin", "cfxf_grin", false, game) != null,
            "an edited face waits for the game's faces, and a bad name is refused");
        check(LibrarySaveRules.Missing(true, true, false, null, null) != null && LibrarySaveRules.Missing(false, false, false, null, null) != null &&
              LibrarySaveRules.Missing(false, true, true, "bad name", null) == "bad name" && LibrarySaveRules.Missing(false, true, false, "bad name", null) == null &&
              LibrarySaveRules.Missing(false, true, false, null, "face") == "face" && LibrarySaveRules.Missing(false, true, true, null, null) == null,
            "Save into a library waits for an unchanged timeline, the libraries, a new library's name and the face's name");
        var savedTip = LibrarySaveRules.Tooltip(true, "My Faces", false, "cfxf_grin");
        check(savedTip != LibrarySaveRules.Tooltip(false, "My Faces", false, "cfxf_grin") && savedTip.Contains("My Faces") && savedTip.Contains("cfxf_grin") &&
              LibrarySaveRules.Tooltip(true, "Evening Faces", true, "cfxf_grin") != LibrarySaveRules.Tooltip(true, "Evening Faces", false, "cfxf_grin"),
            "Save's tooltip says whether the face is saved or copied, and into which library");

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
