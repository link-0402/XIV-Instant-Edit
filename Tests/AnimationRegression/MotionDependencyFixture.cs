using System.Text;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class MotionDependencyFixture
{
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string root = "chara/human/c0801/animation/a0001/bt_common/emote/pose01_loop.pap";
        const string face = "chara/human/c0801/animation/f0002/nonresident/bad.pap";
        const string skeleton = "chara/human/c0801/skeleton/face/f0002/skl_c0801f0002.sklb";
        var timeline = new AnimationCatalog.Timeline(622, "facial/pose/bad", "", 0, false, [], []);
        var timelineReads = 0;
        var catalog = new AnimationCatalog([timeline], _ => { timelineReads++; return Tmb("cfxf_bad"); });
        string[] loaded = [root, skeleton];
        var files = new Dictionary<string, byte[]>
        {
            [root] = Pap("cbem_pose01_2lp", "cbem_pose01_2lp", "cfxf_bad"),
            [face] = Pap("cfxf_bad", "cfxf_bad"),
            [skeleton] = [1, 2, 3],
        };
        Task<byte[]> Read(string path) => Task.FromResult(files.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path));
        Task<IReadOnlyList<string>> Resolve(string motion, string parent = root) =>
            AnimationResources.ResolveMotionAsync(motion, parent, root, loaded, catalog, Read, CancellationToken.None);
        check((await Resolve("cbem_pose01_2lp")).Count == 0 && timelineReads == 0,
            "PAP self references resolve without searching external timelines");
        check((await Resolve("cfxf_bad")).SequenceEqual([face, skeleton]),
            "modded idle facial motion resolves through the TMB name and captured face skeleton");
        check((await Resolve("cfxf_bad")).SequenceEqual([face, skeleton]) && timelineReads == 1,
            "motion timeline index is reused without rereading the game timeline");
        var manifest = await AnimationDependencies.BuildAsync([root], async path =>
        {
            var bytes = await Read(path);
            return (new AnimationResource(path, path, AnimationPap.Hash(bytes)), bytes);
        }, (parent, reference) => Resolve(reference.Path, parent), CancellationToken.None);
        check(manifest.Files.Count == 3 && manifest.Files.Keys.ToHashSet().SetEquals([root, face, skeleton]) &&
              manifest.Files[face].SequenceEqual(files[face]) && manifest.Files[root].SequenceEqual(files[root]),
            "the idle dependency graph retains the external face PAP and skeleton without changing their bytes");
        files[face] = Pap("different_motion", "different_motion");
        reject(() => Resolve("cfxf_bad").GetAwaiter().GetResult(),
            "a collection override lacking the requested motion cannot pass on its vanilla filename");
        files.Remove(face);
        reject(() => Resolve("cfxf_bad").GetAwaiter().GetResult(),
            "a missing inferred face PAP cannot silently omit a required dependency");
        files[face] = Pap("cfxf_bad", "cfxf_bad");
        reject(() => Resolve("unknown_motion").GetAwaiter().GetResult(),
            "unknown external motions still block incomplete packaging");
        var otherTimeline = timeline with { Id = 623, Key = "facial/pose/other" };
        var ambiguous = new AnimationCatalog([timeline, otherTimeline], _ => Tmb("cfxf_bad"));
        files[face.Replace("bad.pap", "other.pap")] = files[face];
        reject(() => AnimationResources.ResolveMotionAsync("cfxf_bad", root, root, loaded, ambiguous, Read, CancellationToken.None)
                .GetAwaiter().GetResult(),
            "multiple inferred PAPs containing the motion remain ambiguous");
        check(AnimationCatalog.PapPath(timeline, root, loaded) == face &&
              AnimationCatalog.PapPath(timeline, root, [root]) == null &&
              AnimationCatalog.PapPath(timeline, root, [root, skeleton.Replace("c0801", "c0101")]) == null,
            "face variants come from the captured player skeleton instead of a guessed race or face");
        check(AnimationCatalog.PapPath(timeline, root, [root, skeleton, skeleton.Replace("f0002", "f0003")]) == null,
            "conflicting captured face skeletons cannot select an arbitrary facial PAP");
        check(AnimationCatalog.PapPath(timeline, root, [root, face]) == face &&
              AnimationCatalog.PapPath(timeline, root, [root, face, face.Replace("f0002", "f0003"), skeleton]) == face,
            "loaded face PAPs remain a fallback while captured skeletons determine the face variant");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await AnimationResources.ResolveMotionAsync("cfxf_bad", root, root, loaded, catalog,
                _ => throw new Exception("Cancelled resolution must not read files"), cancelled.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { check(true, "cancelled motion resolution performs no resource reads"); }
    }

    private static byte[] Tmb(params string[] motions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("TMLB"u8); writer.Write(0); writer.Write(motions.Length);
        var stringStart = 12 + motions.Length * 40;
        for (var i = 0; i < motions.Length; i++)
        {
            writer.Write("C010"u8); writer.Write(40); writer.Write(new byte[24]);
            writer.Write(stringStart - (12 + i * 40 + 8)); writer.Write(0);
            stringStart += Encoding.UTF8.GetByteCount(motions[i]) + 1;
        }
        foreach (var motion in motions) { writer.Write(Encoding.UTF8.GetBytes(motion)); writer.Write((byte)0); }
        stream.Position = 4; writer.Write((int)stream.Length);
        return stream.ToArray();
    }

    private static byte[] Pap(string name, params string[] motions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)1);
        writer.Write((ushort)801); writer.Write((ushort)0);
        writer.Write(26); writer.Write(66); writer.Write(74);
        var text = Encoding.UTF8.GetBytes(name);
        writer.Write(text); writer.Write(new byte[32 - text.Length]);
        writer.Write((short)0); writer.Write((short)0); writer.Write(0);
        writer.Write(new byte[8]); writer.Write(Tmb(motions));
        return stream.ToArray();
    }
}
