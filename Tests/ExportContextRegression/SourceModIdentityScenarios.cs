using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The source-mod identity rules, matched against a mod list snapshot so the meta.json reads can run off
/// the framework thread: the requested folder settles alone, every other mod only when it can't.
/// </summary>
internal static class SourceModIdentityScenarios
{
    private static readonly Guid Saved = Guid.Parse("5a5a5a5a-1111-4222-8333-944444444444");
    private static readonly Guid Other = Guid.Parse("6b6b6b6b-1111-4222-8333-944444444444");

    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "SourceModIdentity");
        var configured = Path.Combine(root, "Penumbra");
        string Mod(string name, Guid? id)
        {
            var folder = Path.Combine(configured, name);
            Directory.CreateDirectory(folder);
            if (id is { } identifier)
                File.WriteAllText(Path.Combine(folder, "meta.json"),
                    new JsonObject { ["FileVersion"] = 4, ["Identifier"] = identifier.ToString() }.ToJsonString());
            return folder;
        }
        var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Register(string name, Guid? id) { mods[name] = name; paths[name] = Mod(name, id); }
        SourceMods.ModListSnapshot Requested(string requested)
            => new(mods, paths.Where(pair => string.Equals(pair.Key, requested, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), configured);
        SourceMods.ModListSnapshot All() => new(mods, paths, configured);
        (bool Ok, string Directory, ResultCode Code, bool NeedsAll) Resolve(SourceMods.ModListSnapshot snapshot, string requested, Guid? id, bool requestedOnly)
        {
            var ok = SourceMods.TryResolveRegisteredModIdentity(snapshot, requested, id, null, requestedOnly,
                out var directory, out var error, out var needsAll);
            return (ok, directory, error.Code, needsAll);
        }

        Register("Kept", Saved);
        Register("Unrelated", Other);
        Register("NoMeta", null);

        var kept = Resolve(Requested("kept"), "kept", Saved, true);
        Require(kept is { Ok: true, Directory: "Kept", NeedsAll: false },
            "the requested folder holding the saved identity resolves from its own path alone");
        Require(Resolve(Requested("Unrelated"), "Unrelated", null, true) is { Ok: true, Directory: "Unrelated", NeedsAll: false } &&
                Resolve(Requested("Gone"), "Gone", null, true) is { Ok: false, Code: ResultCode.SourceModMissing, NeedsAll: false },
            "without a saved identity only the directory key counts");
        Require(Resolve(Requested("Kept"), "Kept", Guid.Empty, true) is { Ok: false, Code: ResultCode.SourceModIdentityUnavailable, NeedsAll: false },
            "an empty saved identity is rejected before any mod is checked");
        Require(Resolve(Requested("Kept"), "../Kept", Saved, true) is { Ok: false, Code: ResultCode.DestinationUnsafe },
            "an unsafe directory key is rejected");

        var renamed = Resolve(Requested("Gone"), "Gone", Saved, true);
        Require(renamed is { Ok: false, NeedsAll: true },
            "a requested folder that is gone asks for every mod's path instead of reading them");
        Require(Resolve(All(), "Gone", Saved, false) is { Ok: true, Directory: "Kept" },
            "a renamed mod is found by its identity among every mod");

        Require(Resolve(Requested("Unrelated"), "Unrelated", Saved, true).NeedsAll &&
                Resolve(All(), "Unrelated", Saved, false) is { Ok: false, Code: ResultCode.SourceModChanged },
            "a requested folder holding another identity reports the mod changed");
        Require(Resolve(All(), "NoMeta", Saved, false) is { Ok: true, Directory: "Kept" },
            "a requested folder without an identity defers to the single mod that has it");
        Require(Resolve(All(), "Missing", Guid.Parse("7c7c7c7c-1111-4222-8333-944444444444"), false) is { Ok: false, Code: ResultCode.SourceModMissing },
            "an identity no mod holds, with the folder gone, reports the mod missing");
        Require(Resolve(All(), "NoMeta", Guid.Parse("7c7c7c7c-1111-4222-8333-944444444444"), false) is { Ok: false, Code: ResultCode.SourceModIdentityUnavailable },
            "an identity no mod holds, with the folder present, reports it unverifiable");

        Register("Copy", Saved);
        Require(Resolve(All(), "Gone", Saved, false) is { Ok: false, Code: ResultCode.SourceModAmbiguous },
            "two mods holding the saved identity are ambiguous");
        mods.Remove("Copy");
        paths.Remove("Copy");

        // The configured root is a fallback only while the registered path doesn't exist.
        mods["Moved"] = "Moved";
        Mod("Moved", Saved);
        var stale = Path.Combine(root, "Elsewhere", "Moved");
        paths["Moved"] = stale;
        Require(Resolve(Requested("Moved"), "Moved", Saved, true) is { Ok: true, Directory: "Moved" },
            "a registered path that no longer exists falls back to the configured root");
        Directory.CreateDirectory(stale);
        Require(Resolve(Requested("Moved"), "Moved", Saved, true) is { Ok: false, NeedsAll: true },
            "an existing registered path stays authoritative over the configured root");
    }
}
