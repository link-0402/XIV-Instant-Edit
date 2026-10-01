using InstantEdit.Services.Animations;

/// <summary>Face-variant placement of facial family timelines, used to find an emote's startup.</summary>
internal static class FacePapPathFixture
{
    public static void Run(Action<bool, string> check)
    {
        const string root = "chara/human/c0801/animation/a0001/bt_common/emote/pose01_loop.pap";
        const string face = "chara/human/c0801/animation/f0002/nonresident/bad.pap";
        const string skeleton = "chara/human/c0801/skeleton/face/f0002/skl_c0801f0002.sklb";
        var timeline = new AnimationCatalog.Timeline(622, "facial/pose/bad", "", 0, false, [], []);
        string[] loaded = [root, skeleton];
        check(AnimationCatalog.PapPath(timeline, root, loaded) == face &&
              AnimationCatalog.PapPath(timeline, root, [root]) == null &&
              AnimationCatalog.PapPath(timeline, root, [root, skeleton.Replace("c0801", "c0101")]) == null,
            "face variants come from the captured player skeleton instead of a guessed race or face");
        check(AnimationCatalog.PapPath(timeline, root, [root, skeleton, skeleton.Replace("f0002", "f0003")]) == null,
            "conflicting captured face skeletons cannot select an arbitrary facial PAP");
        check(AnimationCatalog.PapPath(timeline, root, [root, face]) == face &&
              AnimationCatalog.PapPath(timeline, root, [root, face, face.Replace("f0002", "f0003"), skeleton]) == face,
            "loaded face PAPs remain a fallback while captured skeletons determine the face variant");
    }
}
