using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The save row's Library choice for a face session, without ImGui: Save to's choices, the library chosen, the face's name
/// and why Save into a library waits.
/// </summary>
internal static class LibrarySaveRules
{
    private const string FacePrefix = "cfxf_";
    private static readonly AnimationDestination[] BodyDestinations = [AnimationDestination.NewMod, AnimationDestination.InPlace];
    private static readonly AnimationDestination[] FaceDestinations = [AnimationDestination.NewMod, AnimationDestination.InPlace, AnimationDestination.Library];

    /// <summary>The choices of Save to, in order: a face session also has Library.</summary>
    public static AnimationDestination[] Destinations(bool face) => face ? FaceDestinations : BodyDestinations;

    /// <summary>A choice's name under Save to.</summary>
    public static string Name(AnimationDestination place) => place switch
    {
        AnimationDestination.NewMod => "New mod",
        AnimationDestination.Library => "Library",
        _ => "In place",
    };

    /// <summary>
    /// The library chosen, by mod folder: <paramref name="chosen"/> while it is one of <paramref name="directories"/>, or ""
    /// for New library; otherwise the first library, or New library when there are none.
    /// </summary>
    public static string Choice(IReadOnlyList<string> directories, string? chosen) =>
        chosen == "" || chosen != null && directories.Contains(chosen) ? chosen : directories.Count > 0 ? directories[0] : "";

    /// <summary>The Face name field's text for a face's motion: the motion without cfxf_.</summary>
    public static string Stem(string motion) => motion.StartsWith(FacePrefix, StringComparison.Ordinal) ? motion[FacePrefix.Length..] : motion;

    /// <summary>The motion a face name saves as, cfxf_ and the name; null unless it is a-z, 0-9 and _ within 31 bytes.</summary>
    public static string? Motion(string stem) => FaceLibraryMod.FaceName(stem) is { } motion && motion == FacePrefix + stem ? motion : null;

    /// <summary>
    /// Why the face can't be saved under <paramref name="stem"/>, or null. An edited face, or one under a new name, can't take
    /// the name of a face the game has (<paramref name="gameNames"/>, null while unknown): the pickers would leave it out.
    /// A face copied as it is keeps its own name (<paramref name="ownMotion"/>).
    /// </summary>
    public static string? FaceNameProblem(string stem, string ownMotion, bool edited, IReadOnlySet<string>? gameNames)
    {
        if (Motion(stem) is not { } motion) return "Use letters, numbers or _ in the face name.";
        if (!edited && motion == ownMotion) return null;
        if (gameNames == null) return "Reading the game's faces.";
        return gameNames.Contains(motion) ? $"The game has a face named {stem}. Choose another name." : null;
    }

    /// <summary>
    /// Why Save into a library waits, before what every save waits for: the session changed the expression's timeline too,
    /// which a library doesn't take; your libraries are being read; a new library's name (<paramref name="newLibraryProblem"/>);
    /// the face's name (<paramref name="faceProblem"/>). Null when it can save.
    /// </summary>
    public static string? Missing(bool timelineChanged, bool librariesRead, bool newLibrary, string? newLibraryProblem, string? faceProblem)
    {
        if (timelineChanged) return "A library takes the face only. Save the timeline's changes first.";
        if (!librariesRead) return "Reading your libraries.";
        if (newLibrary && newLibraryProblem != null) return newLibraryProblem;
        return faceProblem;
    }

    /// <summary>Save's tooltip for a library: an edited face is saved, an unedited one copied, into <paramref name="library"/> as <paramref name="motion"/>.</summary>
    public static string Tooltip(bool edited, string library, bool created, string motion)
    {
        var into = created ? $"a new library, {library}," : library;
        return edited ? $"Save the face into {into} as {motion}." : $"Copy the face into {into} as {motion}.";
    }
}
