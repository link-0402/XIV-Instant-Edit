using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class ObservationPerformanceFixture
{
    public static void Run(Action<bool, string> check)
    {
        var control = new RuntimeControlStamp(1, 0, 2, (nint)0x10, (nint)0x20, (nint)0x30, 123);
        var stamp = new RuntimeChangeStamp(42, 0x1000, 7, 3, 100, 1, 200, 1, 300);
        check(stamp.SameAs(stamp with { TimelineCount = 3, TimelineFingerprint = 100, PartialCount = 1,
            SkeletonFingerprint = 200, ActiveControlCount = 1, ControlFingerprint = 300 }),
            "unchanged runtime stamps avoid a full capture");
        check(!stamp.SameAs(stamp with { ActorId = 43 }) &&
              !stamp.SameAs(stamp with { TimelineFingerprint = 101 }) &&
              !stamp.SameAs(stamp with { ControlFingerprint = 301 }),
            "actor, timeline, and binding changes invalidate the runtime stamp");
        check(!stamp.SameAs(stamp with { ControlFingerprint = 302 }),
            "skeleton identity changes invalidate the runtime stamp");
        check(!stamp.SameAs(stamp with { SkeletonFingerprint = 201 }),
            "partial skeleton resource changes invalidate the runtime stamp");

        var resources = new AnimationResourceCache();
        for (var i = 0; i < 512; i++)
        {
            var resource = new AnimationResource($"chara/{i}.pap", $"chara/{i}.pap", $"hash-{i}");
            resources.Set(resource.GamePath, (resource, [(byte)i]));
        }
        var last = new AnimationResource("chara/last.pap", "chara/last.pap", "last");
        resources.Set(last.GamePath, (last, [255]));
        check(!resources.TryGet("chara/0.pap", out _) && resources.TryGet(last.GamePath, out _),
            "resource cache uses bounded eviction");
        resources.Clear();
        check(!resources.TryGet(last.GamePath, out _), "resource cache clears on collection changes");

        var firstMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/a.pap"] = new(StringComparer.Ordinal) { "mod/a.pap", "mod/b.pap" },
            ["chara/b.pap"] = new(StringComparer.Ordinal) { "mod/c.pap" },
        };
        var reorderedMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/b.pap"] = new(StringComparer.Ordinal) { "mod/c.pap" },
            ["chara/a.pap"] = new(StringComparer.Ordinal) { "mod/b.pap", "mod/a.pap" },
        };
        var changedMap = new Dictionary<string, HashSet<string>>(firstMap, StringComparer.OrdinalIgnoreCase)
        {
            ["chara/b.pap"] = new(StringComparer.Ordinal) { "mod/changed.pap" },
        };
        check(AnimationResources.ResourceMapKey(firstMap) == AnimationResources.ResourceMapKey(reorderedMap) &&
              AnimationResources.ResourceMapKey(firstMap) != AnimationResources.ResourceMapKey(changedMap),
            "equivalent resource maps share a fingerprint while changed mappings invalidate it");

        var write = DateTime.UtcNow;
        var cachedSkeleton = new SkeletonDescription("cached", "fingerprint",
            [new SkeletonBone("root", -1, 0, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One))],
            [], [], []);
        var cachedSource = new SkeletonSource(SkeletonSourceKind.Mod,
            new AnimationResource("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb",
                "C:/mods/cached.sklb", "skeleton-hash", "C:/mods", "C:/mods", "cached.sklb", "Cached"));
        var json = JsonSerializer.Serialize(new
        {
            Version = 3,
            BuiltUtc = write,
            MappingFingerprint = "map-a",
            Skeletons = new[]
            {
                new
                {
                    Skeleton = cachedSkeleton,
                    Sources = new[] { new { Source = cachedSource, Length = 10L, Write = write } },
                },
            },
        }, new JsonSerializerOptions { IncludeFields = true });
        Func<string, (bool Exists, long Length, DateTime Write)> state = _ => (true, 10L, write);
        check(AnimationSkeletonIndex.TryLoadSessionLibraryJson(json, state, out var library, "map-a") && library.Length == 1,
            "valid session-library files load from disk");
        check(!AnimationSkeletonIndex.TryLoadSessionLibraryJson(json, _ => (true, 11L, write), out _),
            "stale session-library source metadata is rejected");
        check(!AnimationSkeletonIndex.TryLoadSessionLibraryJson(json, state, out _, "map-b"),
            "changed mod mappings invalidate the cached session skeleton library");
        check(!AnimationSkeletonIndex.TryLoadSessionLibraryJson("{not-json", state, out _),
            "corrupt session-library files are rejected");
        var wrongVersion = json.Replace("\"Version\":3", "\"Version\":2", StringComparison.Ordinal);
        check(!AnimationSkeletonIndex.TryLoadSessionLibraryJson(wrongVersion, state, out _),
            "version-mismatched session-library files are rejected");
    }
}
