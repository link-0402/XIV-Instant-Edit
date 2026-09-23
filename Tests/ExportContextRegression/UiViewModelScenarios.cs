using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Ui;
using Lumina.Data.Files;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The window view models: kind filters, search, expansion keys, the status feed, session
/// grouping and the mod-view builder. None of these touch ImGui or Dalamud.
/// </summary>
internal static class UiViewModelScenarios
{
    private static ResourceNode Node(string type, string name, string gamePath, string actualPath,
        ResourceSourceState state = ResourceSourceState.LoadedMod, string? modDirectory = "ModDir",
        ResourceSection section = ResourceSection.Gear, int order = 0, ResourceNode[]? children = null)
        => new()
        {
            Type = type,
            Icon = "",
            Name = name,
            GamePath = gamePath,
            ActualPath = actualPath,
            Children = children ?? [],
            SourceState = state,
            SourceLabel = state == ResourceSourceState.GameData ? "Game data" : "Loaded from: Mod",
            SourceModName = state == ResourceSourceState.LoadedMod ? "Mod" : null,
            SourceModDirectory = state == ResourceSourceState.LoadedMod ? modDirectory : null,
            SlotLabel = "Body",
            ResourceSection = section,
            SortOrder = order,
        };

    public static void Run()
    {
        const string safeModelPath = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var texture = Node("Tex", "skin_d.tex", "chara/skin_d.tex", @"C:\mods\Mod\skin_d.tex");
        var material = Node("Mtrl", "skin.mtrl", "chara/skin.mtrl", @"C:\mods\Mod\skin.mtrl", children: [texture]);
        var model = Node("Mdl", "body.mdl", safeModelPath, @"C:\mods\Mod\body.mdl", children: [material]);
        var skeleton = Node("Sklb", "skeleton.sklb", "chara/skeleton.sklb", @"C:\mods\Mod\skeleton.sklb");
        var modelView = ResourceViews.FromNode(model);
        var materialView = modelView.Children[0];
        var textureView = materialView.Children[0];
        var skeletonView = ResourceViews.FromNode(skeleton);

        // ---- Kind selection ----
        var kinds = new ResourceKindSelection();
        Require(kinds.IsAll && !kinds.IsFlat && kinds.Admitted == ResourceKinds.Editable,
            "no selection shows every editable kind as a tree");
        Require(kinds.Admits(modelView) && kinds.Admits(materialView) && kinds.Admits(textureView) && !kinds.Admits(skeletonView),
            "the tree view admits models, materials and textures but not skeletons");
        kinds.Set(ResourceKinds.Texture);
        Require(kinds.IsFlat && kinds.Admits(textureView) && !kinds.Admits(modelView) && !kinds.Admits(materialView),
            "a selected kind flattens to that kind only");
        Require(kinds.AdmitsSubtree(modelView) && kinds.AdmitsChild(materialView) && !kinds.AdmitsChild(textureView),
            "subtree and child admission follow the nested texture");
        kinds.Toggle(ResourceKinds.Model);
        Require(kinds.Contains(ResourceKinds.Model) && kinds.Contains(ResourceKinds.Texture) && kinds.Admits(modelView),
            "toggling adds a second kind");
        kinds.Toggle(ResourceKinds.Model);
        kinds.Toggle(ResourceKinds.Texture);
        Require(kinds.IsAll, "removing the last kind returns to the tree view");

        // ---- Search ----
        var search = new ResourceSearch();
        Require(!search.Active && search.Matches(skeletonView), "an empty search matches everything");
        search.Text = "SKIN";
        Require(search.Active && search.Matches(textureView) && search.Matches(modelView) && !search.Matches(skeletonView),
            "search is case-insensitive and bubbles up from descendants");
        search.Text = "zzz";
        Require(!search.Matches(modelView), "changing the text forgets memoised results");
        search.Text = "  ";
        Require(!search.Active, "whitespace clears the search");
        var entity = new OnScreenObject
        {
            ObjectIndex = 2,
            Address = 0x1A2B,
            Name = "Player Name",
            PresentationCategory = ActorPresentationCategory.Player,
            ResourceRoots = [],
        };
        var actor = new ActorView(entity, "Player", "Player Name", [modelView, skeletonView], 2);
        search.Text = "player";
        Require(search.ActorIdentityMatches(actor), "actor identity matches the category or name");

        // ---- Counter ----
        var counter = new ResourceTypeCounter();
        var groups = new[] { ResourceKinds.Editable, ResourceKinds.Model, ResourceKinds.Texture };
        var counts = counter.Count(new List<ActorView> { actor }, groups);
        Require(counts[0] == 3 && counts[1] == 1 && counts[2] == 1, "counts cover the flattened editable rows per kind");
        Require(ReferenceEquals(counts, counter.Count(new List<ActorView> { actor }, groups)),
            "counts are memoised while the actors are unchanged");
        Require(!ReferenceEquals(counts, counter.Count(new List<ActorView> { actor, actor }, groups)),
            "counts recompute when the actors change");

        // ---- Expansion ----
        var expansion = new ExpansionState();
        Require(!expansion.IsExpanded("k", false) && expansion.IsExpanded("k", true),
            "tree rows start collapsed and flat rows start expanded");
        Require(expansion.IsExpanded("k", false, forceExpanded: true), "an active search forces tree rows open");
        expansion.Toggle("k", false, false);
        Require(expansion.IsExpanded("k", false), "toggling a collapsed tree row opens it");
        expansion.Toggle("k", true, false);
        Require(!expansion.IsExpanded("k", false), "toggling again closes it");
        expansion.Toggle("f", true, true);
        Require(!expansion.IsExpanded("f", true), "toggling an expanded flat row closes it");
        expansion.Toggle("f", false, true);
        Require(expansion.IsExpanded("f", true), "and toggling once more reopens it");
        Require(ExpansionState.ActorKey(entity) == "actor:1A2B:2", "actor keys use the hex address and object index");
        Require(ExpansionState.SectionKey("actor:1A2B:2", ResourceSection.Gear) == "actor:1A2B:2:gear"
                && ExpansionState.SectionKey("a", ResourceSection.CharacterFeatures) == "a:features"
                && ExpansionState.SectionKey("a", ResourceSection.Other) == "a:other",
            "section keys keep their historical suffixes");
        Require(ExpansionState.NodeKey("actor:1A2B:2:gear:0", modelView)
                == @"actor:1A2B:2:gear:0:Mdl:body.mdl:" + safeModelPath + @":C:\mods\Mod\body.mdl",
            "node keys are scope, type, name, game path and actual path");
        var blank = ResourceViews.FromNode(Node("", "", "", "a\0b"));
        Require(ExpansionState.NodeKey("s", blank) == "s:Resource:Unnamed resource::ab",
            "node keys fall back for blank fields and strip embedded nulls");

        // ---- Mod list filter ----
        var mods = new List<PenumbraMod> { new("dir-a", "Alpha Gear"), new("dir-b", "Beta Hair") };
        var modList = new ModListFilter();
        Require(ReferenceEquals(modList.Apply(mods, ""), mods), "an empty mod filter returns the source list");
        Require(modList.Apply(mods, "beta").Single().Directory == "dir-b" && modList.Apply(mods, "DIR-A").Single().Name == "Alpha Gear",
            "the mod filter matches name or directory case-insensitively");
        Require(ReferenceEquals(modList.Apply(mods, "beta"), modList.Apply(mods, "beta")), "mod filter results are memoised");

        // ---- Status feed ----
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var feed = new StatusFeed(3, () => now);
        var reported = 0;
        feed.Reported += _ => reported++;
        feed.Report(StatusChannel.Models, FeedbackSeverity.Success, "sent");
        feed.Report(StatusChannel.Textures, FeedbackSeverity.Warning, "paused");
        feed.Report(StatusChannel.Models, FeedbackSeverity.Error, "failed");
        Require(feed.Latest(StatusChannel.Models)!.Text == "failed"
                && feed.Latest(StatusChannel.Textures)!.Severity == FeedbackSeverity.Warning
                && feed.Latest(StatusChannel.Animations) is null,
            "the feed keeps the latest entry per channel");
        Require(feed.History().Select(e => e.Text).SequenceEqual(["failed", "paused", "sent"]), "history lists newest first");
        feed.Report(StatusChannel.Models, FeedbackSeverity.Success, "again");
        Require(feed.History().Select(e => e.Text).SequenceEqual(["again", "failed", "paused"]) && feed.History(2).Count == 2,
            "history is bounded by capacity");
        feed.Report(StatusChannel.Models, FeedbackSeverity.Success, "");
        Require(feed.Latest(StatusChannel.Models) is null && feed.History().Count == 3 && reported == 4,
            "an empty report clears the channel without adding history or notifying");
        Parallel.For(0, 1000, i => feed.Report(StatusChannel.Textures, FeedbackSeverity.Success, $"t{i}"));
        Require(feed.History().Count == 3 && feed.Latest(StatusChannel.Textures) is not null, "concurrent reports keep the feed consistent");

        // ---- Session views ----
        var bgra = (uint)TexFile.TextureFormat.B8G8R8A8;
        var bc7 = (uint)TexFile.TextureFormat.BC7;
        var watching = new TextureEditSession { GamePath = "chara/a.tex", ModDirectory = "ModA", Width = 1024, Height = 512, Format = bgra, SavedFormat = bgra, MipMaps = true };
        var paused = watching with { Paused = true, ModDirectory = "ModB" };
        var pending = new TextureEditSession { GamePath = "chara/b.tex", NeedsMod = true, NewModName = "New Tex" };
        var conflict = watching with { Conflict = true, Paused = true, NeedsMod = true };
        Require(SessionViews.StateOf(watching) == SessionState.Watching && SessionViews.StateOf(paused) == SessionState.Paused
                && SessionViews.StateOf(pending) == SessionState.NeedsMod && SessionViews.StateOf(conflict) == SessionState.Conflict,
            "session state precedence is conflict, paused, needs mod, watching");
        Require(SessionViews.Caption(watching) == "1024 × 512 · BGRA32 · Mipmaps", "captions list size, format and mipmaps");
        Require(SessionViews.Caption(watching with { SavedFormat = bc7, MipMaps = false }) == "1024 × 512 · BC7 (originally BGRA32) · No mipmaps",
            "captions mention the original format when it changed");
        Require(SessionViews.DestinationLine(pending) == "First save creates mod: New Tex", "pending sessions describe the mod they will create");
        var grouped = SessionViews.Group([watching, pending, paused, watching with { GamePath = "chara/c.tex" }]);
        Require(grouped.Select(g => g.Group).SequenceEqual(["ModA", "New Tex", "ModB"]) && grouped[0].Sessions.Count == 2,
            "sessions group by target mod in first-seen order");

        // ---- Mod view, safety gates, display names, section roots ----
        var snapshot = new PenumbraModSnapshot("ModDir", "Mod Name", @"C:\mods\Mod", [
            new PenumbraModResource("chara/z.tex", @"C:\mods\Mod\z.tex", "z.tex", "Group: Option", ["Group: Option"]),
            new PenumbraModResource("chara/A.mdl", @"C:\mods\Mod\A.mdl", "A.mdl", "", []),
        ]);
        var modView = ResourceViews.BuildModView(snapshot, 3);
        Require(modView.Entity is null && modView.Category == "Mod" && modView.ImportObjectIndex == 3,
            "mod views have no actor and keep the import index");
        Require(modView.Roots.Select(r => r.GamePath).SequenceEqual(["chara/A.mdl", "chara/z.tex"])
                && modView.Roots[1].OptionMapping == "Group: Option"
                && modView.Roots[0].SourceState == ResourceSourceState.LoadedMod
                && modView.Roots[0].Type == "Model",
            "mod rows sort by game path and keep their option mapping");
        Require(ResourceViews.IsSafeModel(modelView), "a rooted mod model with a directory is editable");
        Require(ResourceViews.IsSafeModel(ResourceViews.FromNode(Node("Mdl", "v", safeModelPath, safeModelPath, ResourceSourceState.GameData))),
            "a vanilla model with a game path is editable");
        Require(!ResourceViews.IsSafeModel(ResourceViews.FromNode(Node("Mdl", "x", safeModelPath, @"C:\other\x.mdl", ResourceSourceState.ExternalResolvedFile))),
            "externally resolved models are not editable");
        Require(!ResourceViews.IsSafeModel(textureView)
                && !ResourceViews.IsSafeModel(ResourceViews.FromNode(Node("Mdl", "m", safeModelPath, @"C:\mods\Mod\m.mdl", ResourceSourceState.LoadedMod, modDirectory: null))),
            "textures and mod models without a directory are not editable");
        Require(textureView.DisplayName == "skin_d.tex"
                && ResourceViews.FromNode(Node("Tex", "", "", @"C:\mods\Mod\named.tex")).DisplayName == "named.tex",
            "display names fall back to the file name");
        var gearLate = ResourceViews.FromNode(Node("Mdl", "g1", "chara/g1.mdl", @"C:\g1.mdl", section: ResourceSection.Gear, order: 5));
        var gearEarly = ResourceViews.FromNode(Node("Mdl", "g0", "chara/g0.mdl", @"C:\g0.mdl", section: ResourceSection.Gear, order: 1));
        var face = ResourceViews.FromNode(Node("Mdl", "f", "chara/f.mdl", @"C:\f.mdl", section: ResourceSection.CharacterFeatures));
        var sectioned = new ActorView(null, "Player", "P", [gearLate, face, gearEarly], 0);
        Require(sectioned.RootsInSection(ResourceSection.Gear).Select(r => r.Name).SequenceEqual(["g0", "g1"])
                && sectioned.RootsInSection(ResourceSection.CharacterFeatures).Count == 1
                && sectioned.RootsInSection(ResourceSection.Other).Count == 0,
            "section roots are grouped and ordered once per view");
    }
}
