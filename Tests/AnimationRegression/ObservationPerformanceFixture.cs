using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // The saved library is only rebuilt on request (Settings), so loading never
        // invalidates it: mod-mapping changes are ignored, changed files are kept and
        // counted, and only files that no longer exist are left out.
        check(AnimationSkeletonIndex.TryLoadSessionLibraryJson(json, state, out var library, out var missing, out var changed, out var built) &&
              library.Length == 1 && missing == 0 && changed == 0 && built == write,
            "a saved skeleton library loads from the cache folder with its build time, whatever its mod-mapping fingerprint");
        check(AnimationSkeletonIndex.TryLoadSessionLibraryJson(json, _ => (true, 11L, write), out var kept, out _, out var rewritten, out _) &&
              kept.Length == 1 && rewritten == 1,
            "a skeleton file changed since the build keeps its entry and is counted instead of invalidating the library");
        check(AnimationSkeletonIndex.TryLoadSessionLibraryJson(json, _ => (false, 0L, DateTime.MinValue), out var pruned, out var removed, out _, out _) &&
              pruned.IsEmpty && removed == 1,
            "a skeleton file removed since the build is left out of the loaded library and counted");
        check(!AnimationSkeletonIndex.TryLoadSessionLibraryJson("{not-json", state, out _, out _, out _, out _),
            "corrupt session-library files are rejected");
        var wrongVersion = json.Replace("\"Version\":3", "\"Version\":2", StringComparison.Ordinal);
        check(!AnimationSkeletonIndex.TryLoadSessionLibraryJson(wrongVersion, state, out _, out _, out _, out _),
            "version-mismatched session-library files are rejected");
        // Libraries saved before only main skeletons were read also list the skeletons
        // mappers embed, each source named by its variant.
        var legacy = JsonNode.Parse(json)!;
        var entry = legacy["Skeletons"]![0]!;
        var mapperSource = entry["Sources"]![0]!.DeepClone();
        mapperSource["Source"]!["Variant"] = "Mapper 0 A (entry 2)";
        var mapperOnly = entry.DeepClone();
        mapperOnly["Skeleton"]!["Fingerprint"] = "mapper-only";
        mapperOnly["Sources"] = new JsonArray(mapperSource.DeepClone());
        entry["Sources"]!.AsArray().Add(mapperSource);
        legacy["Skeletons"]!.AsArray().Add(mapperOnly);
        check(AnimationSkeletonIndex.TryLoadSessionLibraryJson(legacy.ToJsonString(), state, out var mainOnly, out var gone, out _, out _) &&
              mainOnly.Length == 1 && mainOnly[0].Sources.Length == 1 && gone == 0,
            "a saved library's mapper skeletons are left out on load, without a rebuild or counting them as removed");
    }
}
