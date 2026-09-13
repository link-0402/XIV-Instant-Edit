using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

internal static class AnimationActivationFixture
{
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string root = @"G:\Penumbra\IE Animation 20260912 215419";
        const string path = "chara/human/c0801/skeleton/base/b0001/phy_c0801b0001.phyb";
        var file = new AnimationFileChange(path, root + @"\files/" + path, "created", root, "files/" + path, "", "hash", "", "staged");
        var other = file with { GamePath = "chara/animation.pap", Target = root + @"\files/chara/animation.pap" };
        check(PathRules.SamePhysicalPath(file.Target, file.Target.Replace('/', '\\')) &&
              PathRules.SamePhysicalPath(root + "/", root.ToLowerInvariant()) &&
              !PathRules.SamePhysicalPath(root, root + " other") &&
              !PathRules.SamePhysicalPath("Virtual Folder/created", root),
            "activation paths normalize Windows separators and root endings without accepting different or virtual paths");
        var events = new List<string>();
        Task Redraw() { events.Add("redraw"); return Task.CompletedTask; }
        Task<string> Resolve(string gamePath)
        {
            events.Add("resolve:" + gamePath);
            return Task.FromResult((gamePath == path ? file.Target : other.Target).Replace('/', '\\'));
        }
        void Validate(AnimationFileChange change) => events.Add("validate:" + change.GamePath);
        await AnimationCommitService.VerifyActivationAsync([file, other], Resolve, Validate, Redraw);
        check(events.SequenceEqual(["resolve:" + path, "validate:" + path, "resolve:chara/animation.pap", "validate:chara/animation.pap", "redraw"]),
            "activation verifies every effective mapping and file before issuing a single redraw");
        events.Clear();
        reject(() => AnimationCommitService.VerifyActivationAsync([file, other],
            p => p == path ? Resolve(p) : Task.FromResult(@"G:\Penumbra\Other\animation.pap"), Validate, Redraw).GetAwaiter().GetResult(),
            "an actual activation conflict is rejected");
        check(!events.Contains("redraw"), "a mapping conflict cannot trigger an unverified character rebuild");
        events.Clear();
        reject(() => AnimationCommitService.VerifyActivationAsync([file], Resolve,
            _ => throw new IOException("Output changed after activation"), Redraw).GetAwaiter().GetResult(),
            "changed animation output fails activation verification");
        check(!events.Contains("redraw"), "a changed output file cannot trigger an unverified character rebuild");
    }
}
