using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>Link layers: a bone that moves with a partner's bone. Their validation, activity, partner tracks, journals and solves.</summary>
internal static class LinkLayerFixture
{
    private static readonly Guid Partner = new("6f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    private const string Hug = "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Tracks(check, reject);
        Journals(check);
        Solving(check);
        Hips(check);
        Partners(check, reject);
        Planner(check);
        Service(check);
        AddLinks(check);
        Wiring(check);
        Views(check);
        Picker(check);
    }

    /// <summary>The link picker's bones and the link back it asks for.</summary>
    private static void Picker(Action<bool, string> check)
    {
        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        bool Linkable(string bone) => LinkPicker.Linkable(body, index, index[bone]);
        check(!Linkable("n_root") && !Linkable(PinChains.Body) && !Linkable("j_sebo_a"),
            "picker: the root, the body and a bone right under it other than the hips can't be linked");
        check(Linkable(PinChains.Hips) && Linkable("j_te_r") && Linkable("j_sebo_b") && Linkable("j_asi_a_l") && Linkable(PinChains.FootL),
            "picker: the hips, a hand, the back and the legs can be linked");
        var bones = LinkPicker.Linkable(body);
        check(bones.Length == body.Bones.Length && Enumerable.Range(0, body.Bones.Length).All(b => bones[b] == LinkPicker.Linkable(body, index, b)) &&
              ReferenceEquals(LinkPicker.Linkable(body), bones), "picker: each bone's answer, kept for the skeleton");
        // Every bone the picker offers passes the save's checks.
        check(Enumerable.Range(0, body.Bones.Length).Where(b => bones[b]).All(b =>
            {
                try
                {
                    MotionEvaluator.CheckBones([Layer(Hand() with { Bone = body.Bones[b].Name, Top = null })], body);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }), "picker: every linkable bone passes the link checks");
        var back = LinkPicker.Back(Hand(), null, "chara/self.pap", 2);
        check(back.Bone == "j_te_l" && back.PartnerBone == "j_te_r" && back.Partner == null && back.PartnerClip == "chara/self.pap" && back.PartnerBinding == 2 &&
              back.HoldTurn && back.Top == null && back.Holds.IsEmpty, "picker: a link back swaps the bones and points at this clip");
        check(MotionLinks.LinkBack([Layer(back)], Hand(), null) != null, "picker: the partner finds the link back it asked for");

        // No duplicates: the same bones to the same partner are one link, whatever its settings or whether it is on.
        ImmutableArray<MotionLayer> linked = [Offset("m1", "j_te_l", Vector3.Zero), Layer(Hand(), key: "m2") with { Enabled = false }];
        check(MotionLinks.HasLink(linked, Hand()) && MotionLinks.HasLink(linked, Hand() with { Bend = 10, Top = null, PartnerClip = "chara/other.pap", PartnerBinding = 3 }),
            "picker: HasLink finds a link of the same bones and partner, on or off, whatever its settings");
        check(!MotionLinks.HasLink(linked, Hand() with { PartnerBone = "j_te_r" }) && !MotionLinks.HasLink(linked, Hand() with { Bone = "j_te_l" }) &&
              !MotionLinks.HasLink(linked, Hand() with { Partner = null }) && !MotionLinks.HasLink([], Hand()),
            "picker: HasLink tells another bone, partner bone or partner apart");
        var linkedDoc = new TimelineDocument(30, 30, null, [], 1) { Motion = linked, NextMotionKey = 3 };
        check(ReferenceEquals(MotionEdits.AddLink(linkedDoc, Hand() with { Bend = 10 }, "Link: Aria Rose", out var twiceKey), linkedDoc) && twiceKey == "" &&
              MotionEdits.AddLink(linkedDoc, Hand() with { PartnerBone = "j_te_r" }, "Link: Aria Rose", out var otherKey).Motion.Length == 3 && otherKey == "m3",
            "picker: AddLink refuses a link the document already has, and adds another one");
        check(TimelineWindowViews.LinkAlreadyThere == "This link is already there.", "picker: the Link button says the link is already there");

        // Link back: refused while the partner's clip is read-only, it already links back so, or nothing can bend its bone.
        var aria = new WorkspaceKey(Partner);
        var played = Reaching();
        var theirIndex = MotionEvaluator.Index(played.Skeleton);
        var plain = View(aria, new TimelineDocument(30, 30, null, [], 1), played, 1);
        string? Refused(LinkView view, string? bone = "j_te_r", string? partnerBone = "j_te_l") =>
            LinkPicker.BackRefused(view, bone, partnerBone, theirIndex, null, "chara/self.pap", 2);
        var already = plain with { Document = plain.Document with { Motion = [Layer(LinkPicker.Back(new MotionLink("j_te_r", Partner, "j_te_l", Hug, 1), null, "chara/old.pap", 0))] } };
        check(Refused(plain) == null && Refused(plain, bone: null) == null && Refused(plain, partnerBone: null) == null,
            "picker: a partner that can link back may, and nothing is refused before both bones are picked");
        check(Refused(already) == "Aria Rose already links back." && Refused(already, partnerBone: "j_te_r") == null &&
              Refused(View(WorkspaceKey.Self, already.Document, played, 1)) == "You already link back.",
            "picker: a partner already linking back the same bones can't link back again");
        check(Refused(plain with { ReadOnly = true }) == "Aria Rose's clip is read-only." && Refused(already with { ReadOnly = true }) == "Aria Rose's clip is read-only.",
            "picker: a read-only partner can't link back, first");
        check(Refused(plain, partnerBone: PinChains.Body) == LinkPicker.Refused(PinChains.Body) && Refused(plain, partnerBone: "j_sebo_a") == LinkPicker.Refused("j_sebo_a") &&
              Refused(plain, partnerBone: PinChains.Hips) == null,
            "picker: a partner bone nothing can bend can't link back, though one may still link to it");
    }

    /// <summary>A link layer's name, look and texts, and how the edits, the Bones window and the views treat the kind.</summary>
    private static void Views(Action<bool, string> check)
    {
        static string? Names(Guid? id) => id == null ? "You" : id == Partner ? "Aria Rose" : null;
        var hand = Hand();
        var mine = hand with { Partner = null };
        var gone = hand with { Partner = Guid.NewGuid() };

        // The kind: its name, hint, icon, colour and place in the Add menu.
        check(MotionEdits.KindName(MotionLayerKind.Link) == "Link" && TimelineWindowViews.LayerKindHint(MotionLayerKind.Link) == "Moves a bone with a partner's bone.",
            "views: a link layer is named Link and says what it does");
        check(TimelineUi.LayerIcon(MotionLayerKind.Link).ToString() == "Handshake" && TimelineWindowViews.LayerColour(MotionLayerKind.Link) == Theme.OtherTexture &&
              Enum.GetValues<MotionLayerKind>().Where(kind => kind != MotionLayerKind.Link).All(kind =>
                  TimelineWindowViews.LayerColour(kind) != Theme.OtherTexture && TimelineUi.LayerIcon(kind) != TimelineUi.LayerIcon(MotionLayerKind.Link)),
            "views: a link layer has the handshake icon and a colour no other kind uses");
        check(TimelineWindowViews.LayerKinds[^2] == MotionLayerKind.Link && TimelineWindowViews.LayerKinds[^1] == MotionLayerKind.Pin &&
              !TimelineWindowViews.FaceLayerKinds.Contains(MotionLayerKind.Link), "views: the Add menu offers Link right before Pin, and a face's none");

        // The texts, for a partner and for You.
        check(TimelineWindowViews.LinkName("Aria Rose") == "Link: Aria Rose" && TimelineWindowViews.LinkName("You") == "Link: You",
            "views: a link layer is named after its partner");
        check(TimelineWindowViews.LinkNoPartner == "Open a clip on a partner first." &&
              TimelineWindowViews.LinkReadOnly("Aria Rose") == "Aria Rose's clip is read-only." && TimelineWindowViews.LinkReadOnly("You") == "Your clip is read-only.",
            "views: the picker says why it can't link or link back");
        check(TimelineWindowViews.LinkSummary(hand, Names) == "j_te_r with Aria Rose's j_te_l" && TimelineWindowViews.LinkSummary(mine, Names) == "j_te_r with your j_te_l",
            "views: a link's summary names both bones and the partner");
        check(TimelineWindowViews.LayerContent(Layer(hand), Names) == "Follows Aria Rose's j_te_l" && TimelineWindowViews.LayerContent(Layer(mine), Names) == "Follows your j_te_l",
            "views: a link's tooltip says whose bone it follows");
        string? State(LinkState? state, MotionLink link, string own = "You") => TimelineWindowViews.LinkStateText(state, link, Names, own);
        check(State(LinkState.On, hand) == "Moves with Aria Rose's j_te_l." && State(LinkState.On, mine) == "Moves with your j_te_l." &&
              State(LinkState.NotOpen, hand) == "Aria Rose's clip isn't open." && State(LinkState.NotOpen, mine) == "Your clip isn't open." &&
              State(LinkState.OtherClip, hand) == "Aria Rose has another clip open." && State(LinkState.OtherClip, mine) == "You have another clip open." &&
              State(LinkState.Despawned, hand) == "Aria Rose isn't spawned." && State(LinkState.Despawned, mine) == "You aren't spawned." &&
              State(LinkState.OutOfGPose, hand) == "Links work in GPose." && State(LinkState.OutOfGPose, mine) == "Links work in GPose." &&
              State(LinkState.PartnerGone, gone) == "That partner no longer exists." &&
              State(LinkState.NoBone, hand) == "Aria Rose's skeleton has no j_te_l." && State(LinkState.NoBone, mine) == "Your skeleton has no j_te_l." &&
              State(LinkState.Unreadable, hand) == "Aria Rose's clip can't be read." && State(LinkState.Unreadable, mine) == "Your clip can't be read.",
            "views: each link state says why, for a partner and for you");
        check(State(null, hand) == null && State(LinkState.NotOpen, gone) == "That partner no longer exists.",
            "views: a link that is off itself has no state line, and one to a deleted preset says it is gone");
        check(State(LinkState.Reading, hand) == "Reading this clip." && State(LinkState.Reading, mine, "Aria Rose") == "Reading this clip." &&
              State(LinkState.SelfDespawned, hand) == "You aren't spawned." && State(LinkState.SelfDespawned, mine, "Aria Rose") == "Aria Rose isn't spawned." &&
              State(LinkState.ItsOwnClip, hand) == "This link points at its own clip." && State(LinkState.ItsOwnClip, mine, "Aria Rose") == "This link points at its own clip." &&
              State(LinkState.SelfDespawned, gone) == "You aren't spawned." &&
              State(LinkState.SelfUnreadable, hand) == "This clip can't be read." && State(LinkState.SelfUnreadable, mine, "Aria Rose") == "This clip can't be read.",
            "views: this side's states name this clip's actor, for you and for a partner, whatever the partner");
        check(TimelineWindowViews.LinkAlreadyBack("Aria Rose") == "Aria Rose already links back." && TimelineWindowViews.LinkAlreadyBack("You") == "You already link back." &&
              TimelineWindowViews.LinkUnreadable("Aria Rose") == "Aria Rose's clip can't be read." && TimelineWindowViews.LinkUnreadable("You") == "Your clip can't be read.",
            "views: the picker says who already links back and whose clip can't be read");
        check(TimelineWindowViews.LinkBackHover("Aria Rose") == "Aria Rose doesn't follow your edits yet." &&
              TimelineWindowViews.LinkBackHover("You") == "You don't follow these edits yet.", "views: Link back says who doesn't follow yet");
        check(TimelineWindowViews.LinkPullLabel == "Pull" && TimelineWindowViews.LinkPullTip == "How far the bone follows the partner. Ctrl+click to type.",
            "views: a link's amount is its pull");
        check(TimelineWindowViews.LinkGizmo == "Links follow the partner. Edit a layer above." &&
              BonePosing.StateText(BoneState.Changed, Layer(hand)) == "Bent to follow a link." && BonePosing.StateText(BoneState.Pinned, Layer(hand)) == "Bent to follow a link.",
            "views: the gizmo and the Bones window say a link follows its partner");

        // The save footer names a partner whose link is off.
        var doc = new TimelineDocument(30, 30, null, [], 1) { Motion = [Layer(hand)] };
        IReadOnlyDictionary<string, LinkState> States(LinkState state) => ImmutableDictionary<string, LinkState>.Empty.Add("m1", state);
        check(TimelineWindowViews.LinkOffWarning(doc, States(LinkState.NotOpen), Names) == "The link to Aria Rose is off. Aria Rose's edits aren't in this save." &&
              TimelineWindowViews.LinkOffWarning(doc with { Motion = [Layer(mine)] }, States(LinkState.Despawned), Names) ==
                  "The link to you is off. Your edits aren't in this save.",
            "views: the footer says a save leaves out the edits of a partner whose link is off");
        check(TimelineWindowViews.LinkOffWarning(doc, States(LinkState.On), Names) == null &&
              TimelineWindowViews.LinkOffWarning(doc, ImmutableDictionary<string, LinkState>.Empty, Names) == null &&
              TimelineWindowViews.LinkOffWarning(doc with { Motion = [Layer(hand) with { Enabled = false }] }, States(LinkState.NotOpen), Names) == null,
            "views: the footer says nothing while the link is on or off itself");

        // The save footer warns when a save that bakes the layers in ends a link.
        check(TimelineWindowViews.LinkBakeWarning(true, ["Aria Rose"]) == "Baking the layers in ends the link to Aria Rose." &&
              TimelineWindowViews.LinkBakeWarning(true, ["You"]) == "Baking the layers in ends the link to you.",
            "views: the footer says baking the layers in ends the link, for a partner and for you");
        check(TimelineWindowViews.LinkBakeWarning(false, ["Aria Rose"]) == null && TimelineWindowViews.LinkBakeWarning(true, []) == null &&
              TimelineWindowViews.LinkBakeWarning(false, []) == null,
            "views: no bake-in line when the save keeps the layers or no link is there");
        check(TimelineWindowViews.LinkBakeWarning(true, ["You", "Aria Rose"]) == "Baking the layers in ends the link to you." &&
              TimelineWindowViews.LinkBakeWarning(true, ["Aria Rose", "You"]) == "Baking the layers in ends the link to Aria Rose.",
            "views: the bake-in line names the first partner");
        var stretched = doc with { Length = 60 };
        check(stretched.Stretched && SaveFooter.DropsLayers(stretched, null) && SaveFooter.DropsLayers(doc, SkeletonStandard.Vanilla) &&
              SaveFooter.DropsLayers(doc with { ExcludedBones = ["j_kosi"] }, null) && !SaveFooter.DropsLayers(doc, null),
            "views: a stretch, a repair or bones left out drop the layers on save; a plain save keeps them");
        check(TimelineWindowViews.LinkBakeNames(doc, Names, []).SequenceEqual(["Aria Rose"]) &&
              TimelineWindowViews.LinkBakeNames(doc with { Motion = [Layer(mine), Layer(hand, key: "m2"), Layer(hand with { Bone = "j_te_l" }, key: "m3")] }, Names, ["Bea", "Aria Rose"])
                  .SequenceEqual(["You", "Aria Rose", "Bea"]),
            "views: the bake-in names are this clip's own links first, then the others linking to it, each once");
        check(TimelineWindowViews.LinkBakeNames(doc with { Motion = [Layer(hand) with { Enabled = false }, Layer(gone, key: "m2"), Offset("m3", "j_te_l", Vector3.Zero)] }, Names, []).Count == 0 &&
              TimelineWindowViews.LinkBakeNames(new TimelineDocument(30, 30, null, [], 1), Names, []).Count == 0,
            "views: a link off itself, one to a deleted preset and other layers name nobody");
        var toYou = new TimelineDocument(30, 30, null, [], 1) { Motion = [Layer(new MotionLink("j_te_l", null, "j_te_r", Hug, 1))] };
        var toAria = new TimelineDocument(30, 30, null, [], 1) { Motion = [Layer(new MotionLink("j_te_l", Partner, "j_te_r", Hug, 1))] };
        check(TimelineWindowViews.LinkedBy([("Aria Rose", toYou), ("Bea", toAria), ("Cid", toYou), ("Aria Rose", toYou)], null, Hug.ToUpperInvariant(), 1).SequenceEqual(["Aria Rose", "Cid"]) &&
              TimelineWindowViews.LinkedBy([("You", toAria), ("Bea", toYou)], Partner, Hug, 1).SequenceEqual(["You"]),
            "views: LinkedBy names the other workspaces whose links follow this workspace, each once");
        check(TimelineWindowViews.LinkedBy([("Aria Rose", toYou)], null, "chara/other.pap", 1).Count == 0 && TimelineWindowViews.LinkedBy([("Aria Rose", toYou)], null, Hug, 2).Count == 0 &&
              TimelineWindowViews.LinkedBy([("Aria Rose", toYou with { Motion = [toYou.Motion[0] with { Amount = 0 }] })], null, Hug, 1).Count == 0 &&
              TimelineWindowViews.LinkedBy([], null, Hug, 1).Count == 0,
            "views: LinkedBy skips links to another clip or binding, and links off themselves");

        // Edits: a link is added from its picker only, takes no keys, and is kept valid.
        var face = TimelineFace.Open(new TimelineDocument(150, 150, null, [], 1), 0, 1, 150);
        check(ReferenceEquals(MotionEdits.Add(doc, MotionLayerKind.Link, 0, 30, out var addedKey, out var refused), doc) && addedKey == "" &&
              refused == "Add a link from the Link layer menu." &&
              ReferenceEquals(MotionEdits.Add(face, MotionLayerKind.Link, 0, 30, out _, out var faceRefused), face) && faceRefused == "Add a link from the Link layer menu.",
            "views: Add refuses a link layer, which needs its partner");
        check(ReferenceEquals(MotionEdits.SetKey(doc, "m1", 5, "j_te_r", T(Vector3.UnitX)), doc), "views: a link layer takes no keys");
        var added = MotionEdits.AddLink(new TimelineDocument(30, 30, null, [], 1), Hips() with { Holds = [] }, TimelineWindowViews.LinkName("Aria Rose"), out var hipsKey);
        check(MotionEdits.Find(added, hipsKey)?.Link?.Holds.SequenceEqual([PinChains.FootL, PinChains.FootR]) == true, "views: AddLink gives a hips link both feet");
        var body = HipDragFixture.Body();
        MotionLink? Linked(MotionLink link, SkeletonDescription? skeleton = null) => MotionEdits.Find(MotionEdits.SetLink(doc, "m1", link, skeleton), "m1")?.Link;
        check(Linked(hand with { Bend = 400 })?.Bend == 180 && Linked(hand with { Bend = -400 })?.Bend == -180 && Linked(hand with { Bend = float.NaN })?.Bend == 0,
            "views: SetLink keeps the bend within a half turn");
        check(Linked(hand with { Holds = [PinChains.FootL] })?.Holds.IsEmpty == true && Linked(Hips() with { Top = "j_sebo_a" }) is { Top: null } hipsSet &&
              hipsSet.Holds.SequenceEqual([PinChains.FootL, PinChains.FootR]), "views: SetLink holds bones only on the hips, which bend up to nothing");
        check(Linked(hand with { Bone = "j_te_l" }, body) is { Top: null } && Linked(hand, body)?.Top == "j_sako_r" && Linked(hand with { Bone = "j_te_l" })?.Top == "j_sako_r",
            "views: SetLink drops a top that isn't above the bone on the skeleton");
        check(ReferenceEquals(MotionEdits.SetLink(doc, "m1", hand with { Holds = [] }), doc) && ReferenceEquals(MotionEdits.SetLink(doc, "m9", Hips()), doc),
            "views: SetLink leaves an unchanged link, or a missing layer, as it was");
        var hipsLayer = Layer(Hips());
        check(MotionEdits.Same(hipsLayer, Layer(Hips() with { Holds = [PinChains.FootL, PinChains.FootR] })) &&
              !MotionEdits.Same(hipsLayer, Layer(Hips() with { Holds = [PinChains.FootL] })) && !MotionEdits.Same(Layer(hand), Layer(hand with { Bend = 10 })) &&
              !MotionEdits.Same(Layer(hand), Layer(null)),
            "views: Same compares links by their fields and holds");

        // The Bones window: the linked bone square, the chain it bends changed, never keyed.
        var bones = BonePosing.LayerBones(Layer(hand), body);
        ImmutableArray<string> moved = [.. MotionLinks.Moved(body, Layer(hand)).Select(b => body.Bones[b].Name)];
        check(bones.Pinned.SetEquals(["j_te_r"]) && bones.Changed.SetEquals(moved.Where(name => name != "j_te_r")) && bones.Changed.Contains("j_ude_b_r") &&
              bones.Changed.Contains("j_sako_r"), "views: a link marks its bone as pinned and the chain it bends as changed");
        var keyed = Layer(hand) with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_ude_b_r", T(Vector3.Zero)))] };
        check(PoseEdits.Changes(Layer(hand), "j_ude_b_r", bones.Changed) && !PoseEdits.Changes(Layer(hand), "j_te_l", bones.Changed) &&
              !PoseEdits.Changes(Layer(hand), "j_ude_b_r") &&
              BonePosing.StateOf("j_ude_b_r", keyed, 0, null, bones) == BoneState.Changed && BonePosing.StateOf("j_te_r", keyed, 0, null, bones) == BoneState.Pinned,
            "views: a link's chain bones show as changed by it, never keyed");
        check(BonePosing.LayerBones(Layer(Hips()), body).Pinned.SetEquals([PinChains.Hips]), "views: a hips link marks the hips");
        check(!TimelineLayout.Unfolds(keyed), "views: a link layer has no bone rows");

        // Live on a rebuilt clip: a limb link moves no lengths, a hips link moves the hips.
        check(!TimelineWindowViews.LiveRebuiltPositions(doc) && TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [Layer(Hips())] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [Layer(Hips()) with { Enabled = false }] }),
            "views: LiveRebuiltPositions is false for a limb link and true for a hips link");

        // A link that bends bones outside the standard skeletons warns as a pin does.
        var kosi = MotionEvaluator.Index(body)[PinChains.Hips];
        var tail = body with
        {
            Bones = [.. body.Bones, new("j_custom_a", (short)kosi, 0, T(new Vector3(0, 0, -.1f))), new("j_custom_b", (short)body.Bones.Length, 0, T(new Vector3(0, 0, -.1f))),
                new("j_custom_c", (short)(body.Bones.Length + 1), 0, T(new Vector3(0, 0, -.1f)))],
        };
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, new AnimationResource("chara/a.pap", "a.pap", "hash")), tail, 0, "");
        var clip = new AnimationClip("chara/a.pap", "loop", 0, 0, 1, "s.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [.. Enumerable.Range(0, body.Bones.Length).Select(b => (short)b)],
                Standards: [new StandardSkeleton(SkeletonStandard.Vanilla, body, "game")]));
        var tailLink = doc with { Motion = [Layer(hand with { Bone = "j_custom_c", Top = null })] };
        check(TimelineWindowViews.LayerBonesWarning(tailLink, clip, tail) is { } warning && warning.Contains("j_custom_b", StringComparison.Ordinal) &&
              TimelineWindowViews.LayerBonesWarning(tailLink with { Motion = [tailLink.Motion[0] with { Enabled = false }] }, clip, tail) == null,
            "views: a link bending bones outside the standard skeletons warns as a pin does");
        check(TimelineWindowViews.BonesProblem(tailLink with { ExcludedBones = ["j_custom_b"] },
                  clip with { Resolution = clip.Resolution! with { TrackBones = [.. Enumerable.Range(0, tail.Bones.Length).Select(b => (short)b)] } }, tail, null) is { } problem &&
              problem.Contains("j_custom_b", StringComparison.Ordinal), "views: a link bending a bone left out of the save refuses to save");
    }

    /// <summary>Where the partner tracks go: saves, Live, the Bones window and the unsaved state.</summary>
    private static void Wiring(Action<bool, string> check)
    {
        var layer = Layer(Hand());
        var doc = new TimelineDocument(30, 30, null, [], 1) { Motion = [layer] };
        // MotionChanged_OnlyLink: a link that may change the motion counts without its track.
        check(doc.MotionChanged && !(doc with { Motion = [layer with { Enabled = false }] }).MotionChanged &&
              !(doc with { Motion = [layer with { Amount = 0 }] }).MotionChanged && !(doc with { Motion = [layer with { Link = null }] }).MotionChanged,
            "wiring: MotionChanged_OnlyLink: a document whose only layer is a link that is on with a pull changes the motion");

        // The save rules read it so: a link-only document bakes, keeps its layers in the request and its base for Edit again.
        var clip = new AnimationClip(Hug, "hug", 1, 0, 42, "chara/test.sklb");
        var source = TimelineToolsFixture.Source(clip, doc);
        check(AnimationBakeRules.ChangesMotion(doc) && AnimationBakeRules.NewMotion(doc) && AnimationEditService.ChangesMotion(doc),
            "wiring: a link-only document bakes its motion when saved");
        check(AnimationBakeRules.BakeInNote(doc, SkeletonStandard.Vanilla) != null && AnimationBakeRules.BakeInNote(doc, null) == null,
            "wiring: a repair says it bakes a link-only document's layers in");
        byte[] file = [1, 2, 3];
        check(AnimationEditService.MotionBase(source, doc, file) == file, "wiring: a link-only save keeps the motion from before it for Edit again");
        var set = Set(("m1", Still()));
        var request = AnimationEditService.TimelineRequest(source, doc, AnimationDestination.InPlace, "Mod", links: set);
        check(request.Timeline is { Motion: [{ Kind: MotionLayerKind.Link }] } edit && ReferenceEquals(edit.Links, set) &&
              AnimationEditService.TimelineRequest(source, doc, AnimationDestination.InPlace, "Mod").Timeline!.Links == null,
            "wiring: a save's request carries the link layers and the partner tracks it was given");
        check(AnimationBakeRules.RebuildKeepsEdits(request with { RepairModel = "c0801", RepairTarget = SkeletonStandard.Vanilla }, doc),
            "wiring: a rebuild keeps a link-only document's edits open");

        // Live copies the clip for a link only with its partner's track.
        check(MotionLiveDisplay.Copies([layer], [], null, 0, false, set) && !MotionLiveDisplay.Copies([layer], [], null, 0, false) &&
              !MotionLiveDisplay.Copies([layer], [], null, 0, false, MotionLinkSet.Empty), "wiring: Live shows a link only with its partner's track");
        check(TimelinePreview.LiveLinks(doc, set) == set &&
              TimelinePreview.LiveLinks(TimelineFace.Open(new TimelineDocument(150, 150, null, [], 1), 0, 1, 150) with { Motion = [layer] }, set) == null,
            "wiring: a face's Live and preview get no partner tracks");
        check(TimelinePreview.LiveLinks(doc, MotionLinkSet.Empty) == null && TimelinePreview.LiveLinks(doc, new MotionLinkSet(MotionLinkSet.Empty.Tracks)) == null,
            "wiring: a clip without partner tracks shows as one without links, so a caller asking Live without links still matches");

        // The Bones window's onion skins and paths pose a frame as Apply bakes it, with the same tracks.
        var fine = Reaching(61);
        var hand = MotionEvaluator.Index(fine.Skeleton)["j_te_r"];
        var fineSet = Set(("m1", Track(fine, hand, (_, before) => before with { Position = before.Position + new Vector3(.1f, 0, 0) })));
        var faded = doc with { Motion = [layer with { FadeIn = 10 }] };
        var applied = Applied(fine, faded.Motion, fineSet);
        // Samples 0, 20 and 30 of a clip with two samples a frame: frames 0, 10 and 15.
        check(new[] { 0, 20, 30 }.All(s => BonePosing.Layered(fine, faded, (int)fine.Frame(s), 0, 30, fineSet).Zip(Row(applied, s)).All(pair => Near(pair.First, pair.Second))) &&
              BonePosing.Layered(fine, faded, 15, 0, 30, null).SequenceEqual(fine.Pose(15)),
            "wiring: the Bones window's pose at a frame is Apply's with the same tracks, and the clip's own without");

        // A track's print is its content: the same samples in a new instance print the same, another After prints otherwise.
        var moving = Track(fine, hand, (_, before) => before with { Position = before.Position + new Vector3(.1f, 0, 0) });
        var rebuiltSame = new LinkTrack(moving.Before, [.. moving.After], moving.Duration);
        var movedMore = new LinkTrack(moving.Before, [.. moving.After.Select(t => t with { Position = t.Position + new Vector3(0, 1e-4f, 0) })], moving.Duration);
        check(moving.Print.Length == 16 && rebuiltSame.Print == moving.Print && movedMore.Print != moving.Print &&
              new LinkTrack(moving.Before, moving.After, moving.Duration + 1 / 30f).Print != moving.Print,
            "wiring: a track's print is its content: equal for a same-content rebuild, other for another After or length");

        // LinksUnsaved: an active link follows a track whose content its save didn't bake.
        var saved = Set(("m1", moving));
        var savedPrints = MotionLinks.Prints(doc.Motion, saved);
        check(savedPrints.Count == 1 && savedPrints["m1"] == moving.Print && MotionLinks.Prints(doc.Motion, null).IsEmpty &&
              MotionLinks.Prints([layer with { Enabled = false }], saved).IsEmpty, "wiring: a save remembers the prints of its active links only");
        check(!MotionLinks.Unsaved(doc.Motion, saved, savedPrints) && !MotionLinks.Unsaved(doc.Motion, Set(("m1", rebuiltSame)), savedPrints),
            "wiring: LinksUnsaved: a same-content rebuild of the track stays saved");
        check(MotionLinks.Unsaved(doc.Motion, Set(("m1", movedMore)), savedPrints), "wiring: LinksUnsaved: a track of other content is unsaved");
        check(MotionLinks.Unsaved(doc.Motion, saved, MotionLinks.NoPrints), "wiring: LinksUnsaved: a link that was off when saved and is on now is unsaved");
        var oneMove = new LinkTrack(moving.Before, [.. moving.Before.Select((t, i) => i == 3 ? moving.After[i] : t)], moving.Duration);
        check(moving.Carries && oneMove.Carries && !Still().Carries && !new LinkTrack(moving.Before, moving.Before, moving.Duration).Carries,
            "wiring: a track carries something when the partner's edits move the bone at some sample");
        check(!MotionLinks.Unsaved(doc.Motion, Set(("m1", Still())), MotionLinks.NoPrints) && MotionLinks.Unsaved(doc.Motion, Set(("m1", oneMove)), MotionLinks.NoPrints) &&
              MotionLinks.Unsaved(doc.Motion, Set(("m1", Still())), savedPrints),
            "wiring: LinksUnsaved: a link off when saved that carries nothing now stays saved; a saved print still compares");
        check(!MotionLinks.Unsaved([layer with { Enabled = false }], Set(("m1", movedMore)), savedPrints) &&
              !MotionLinks.Unsaved(doc.Motion, MotionLinkSet.Empty, savedPrints), "wiring: LinksUnsaved: a link that is off now is never unsaved");
        var session = new TimelineSession(null, null!, null!, null!, null, null!, null!, null!) { Links = Set(("m1", movedMore)), SavedPrints = savedPrints };
        check(!session.LinksUnsaved, "wiring: a session without an open clip has nothing unsaved");
        session.Editor = new TimelineEditor(doc);
        check(session.LinksUnsaved, "wiring: a session's open document is unsaved when its link follows other content");
        session.Links = Set(("m1", rebuiltSame));
        check(!session.LinksUnsaved, "wiring: a session's open document stays saved across a same-content rebuild");

        // The prints go into the request and its journal, and Edit again takes them back as the saved state.
        var printed = AnimationEditService.TimelineRequest(source, doc, AnimationDestination.InPlace, "Mod", links: saved);
        check(printed.Timeline!.LinkPrints is { } inRequest && MotionLinks.SamePrints(inRequest, savedPrints) &&
              AnimationEditService.TimelineRequest(source, doc, AnimationDestination.InPlace, "Mod").Timeline!.LinkPrints == null,
            "wiring: a save's request journals the prints of the links it bakes");
        var temp = Path.Combine(Path.GetTempPath(), "ie-link-prints-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var journaled = printed with { Timeline = printed.Timeline with { Links = null } };
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = journaled.Id, Request = AnimationCommitService.RecoveryRequest(journaled) });
            var loaded = new AnimationJournalStore(temp).Load().Single();
            check(loaded.Request.Timeline is { Links: null, LinkPrints: { } back } && MotionLinks.SamePrints(back, savedPrints) &&
                  MotionLinks.SamePrints(TimelineSession.SavedPrintsOf(loaded), savedPrints),
                "wiring: a journal keeps the link prints and Edit again seeds the saved state from them");
            var older = new AnimationEditJournal { Id = Guid.NewGuid(), Request = journaled with { Timeline = journaled.Timeline! with { LinkPrints = null } } };
            check(TimelineSession.SavedPrintsOf(older).IsEmpty, "wiring: an edit saved without prints opens again with none saved");
        }
        finally { Directory.Delete(temp, true); }

        // A built preview is stale once its links' content changes, or a link turns on or off.
        check(!TimelinePreview.LinksStale(savedPrints, doc, Set(("m1", rebuiltSame))) && TimelinePreview.LinksStale(savedPrints, doc, Set(("m1", movedMore))) &&
              TimelinePreview.LinksStale(savedPrints, doc, MotionLinkSet.Empty) && TimelinePreview.LinksStale(MotionLinks.NoPrints, doc, saved) &&
              !TimelinePreview.LinksStale(MotionLinks.NoPrints, doc, MotionLinkSet.Empty),
            "wiring: a preview is stale when its links' content or which are on changed");

        // The base stays the base: read back after an in-place save, the clip's layers apply to the motion from before it again,
        // which is read again as a new decode (so a partner's track rebuilds on the same motion).
        var opened = source with { FileFingerprint = "before" };
        var motion = SaveFooter.KeptMotion(opened);
        var reread = opened with { Pap = [9, 9], Resource = opened.Resource with { Hash = "saved" } };
        var based = SaveFooter.WithKeptMotion(reread, motion);
        check(ReferenceEquals(based.MotionSource, opened.Pap) && based.BaseHash == "file" && ReferenceEquals(SaveFooter.WithKeptMotion(opened, motion).MotionSource, opened.Pap) &&
              SaveFooter.WithKeptMotion(opened, motion).BaseMotion == null && MotionDecode.KeyOf(based) != MotionDecode.KeyOf(opened),
            "wiring: after an in-place save the clip's motion is still the one from before, decoded again");
    }

    /// <summary>How a link stands with its partner's view, and the inputs its track is built from.</summary>
    private static void Planner(Action<bool, string> check)
    {
        var you = WorkspaceKey.Self;
        var aria = new WorkspaceKey(Partner);
        var played = Reaching();
        var theirPlayed = Reaching();
        var layer = Layer(Hand());
        var mine = new TimelineDocument(30, 30, null, [], 1) { Motion = [layer] };
        var theirs = new TimelineDocument(30, 30, null, [], 1) { Motion = [Offset("m1", "j_te_l", new Vector3(.05f, 0, 0))] };
        var me = View(you, mine, played, 1);
        var partner = View(aria, theirs, theirPlayed, 1.1f);
        LinkState State(LinkView? from, LinkView? to, bool gposing = true, bool exists = true) =>
            PartnerLinks.Plan(you, layer, from, to, gposing, exists).State;

        check(State(me, null) == LinkState.NotOpen, "planner: a partner without a ready clip leaves the link off as not open");
        check(State(null, partner) == LinkState.Reading && State(null, null) == LinkState.Reading && State(null, partner with { Size = null }) == LinkState.Reading,
            "planner: this clip's motion still read says so, before the partner's states");
        check(State(me with { Size = null }, null) == LinkState.SelfDespawned && State(me with { Size = null }, partner with { GamePath = "chara/other.pap" }) == LinkState.SelfDespawned &&
              State(me with { Size = null }, partner with { Size = null }) == LinkState.SelfDespawned,
            "planner: this actor not drawn says so, before the partner's states");
        check(State(null, null, gposing: false) == LinkState.OutOfGPose && State(me with { Size = null }, partner, exists: false) == LinkState.PartnerGone,
            "planner: out of GPose and a deleted preset still come before this side's states");
        LinkState Failed(LinkView? from, LinkView? to, bool self, bool other) => PartnerLinks.Plan(you, layer, from, to, true, true, self, other).State;
        check(Failed(null, partner, true, false) == LinkState.SelfUnreadable && Failed(null, null, true, true) == LinkState.SelfUnreadable &&
              Failed(me, partner, true, false) == LinkState.On,
            "planner: this clip's failed read says it can't be read, only while its view is missing");
        check(Failed(me, null, false, true) == LinkState.Unreadable && Failed(me, null, false, false) == LinkState.NotOpen &&
              Failed(me with { Size = null }, null, false, true) == LinkState.SelfDespawned && Failed(me, partner, false, true) == LinkState.On,
            "planner: the partner's failed read says its clip can't be read, after this side's states and only while its view is missing");
        check(State(me, partner with { GamePath = "chara/human/c0101/animation/a0001/bt_common/emote/dance.pap" }) == LinkState.OtherClip &&
              State(me, partner with { Binding = 2 }) == LinkState.OtherClip, "planner: a partner on another clip or binding leaves it off");
        check(State(me, partner with { GamePath = Hug.ToUpperInvariant() }) == LinkState.On, "planner: game paths compare without case");
        check(State(me, partner with { Size = null }) == LinkState.Despawned && State(me with { Size = null }, partner) == LinkState.SelfDespawned,
            "planner: a partner that isn't drawn leaves it off as despawned, this actor as itself despawned");
        check(State(me, partner, gposing: false) == LinkState.OutOfGPose && State(me, partner with { Size = null }, gposing: false) == LinkState.OutOfGPose,
            "planner: out of GPose the link is off, before the sizes are asked");
        check(State(me, null, exists: false) == LinkState.PartnerGone && State(me, partner, gposing: false, exists: false) == LinkState.PartnerGone,
            "planner: a deleted preset leaves it off as gone, before anything else");
        check(PartnerLinks.Plan(you, layer, me, partner with { Key = new WorkspaceKey(Guid.NewGuid()) }, true, true).State == LinkState.NotOpen,
            "planner: another workspace's view isn't the partner's");

        var (state, inputs) = PartnerLinks.Plan(you, layer, me, partner, true, true);
        check(state == LinkState.On && inputs is { Self: null } ready && ReferenceEquals(ready.PartnerDocument, theirs) &&
              ReferenceEquals(ready.Played, theirPlayed) && ReferenceEquals(ready.Link, layer.Link) && ready.Ratio == 1.1f,
            "planner: a ready partner turns the link on, with its document, played clip and the size ratio 1.1");
        check(PartnerLinks.Plan(you, layer, me, partner, true, true).Inputs is { } again && !ReferenceEquals(again, inputs) &&
              again.Equals(inputs) && again.GetHashCode() == inputs!.GetHashCode(), "planner: SameInputs_KeepsSet: the same references give equal inputs");
        check(!inputs!.Equals(PartnerLinks.Plan(you, layer, me, partner with { Document = theirs with { } }, true, true).Inputs) &&
              !inputs.Equals(PartnerLinks.Plan(you, layer, me, partner with { Played = theirPlayed.Clone() }, true, true).Inputs),
            "planner: a new document or played clip reference gives other inputs");
        check(inputs.Equals(inputs with { Link = inputs.Link with { Holds = [.. inputs.Link.Holds] } }) &&
              !inputs.Equals(inputs with { Link = inputs.Link with { Bend = 10 } }) && !inputs.Equals(inputs with { Ratio = 1.2f }),
            "planner: the link and the ratio compare by value");

        // The partner's side: its link to You names no preset, so a deleted preset doesn't matter, and its inputs name its own.
        var back = Layer(new MotionLink("j_te_l", null, "j_te_r", Hug, 1));
        var (backState, backInputs) = PartnerLinks.Plan(aria, back, partner, me, true, false);
        check(backState == LinkState.On && backInputs is { Self: { } self } && self == Partner && ReferenceEquals(backInputs.PartnerDocument, mine) &&
              backInputs.Ratio == 1 / 1.1f, "planner: a partner's link to you is on, built for that partner");

        // A link to its own workspace (pasted from another one) is never on.
        check(PartnerLinks.Plan(you, Layer(Hand() with { Partner = null }), me, me, true, true).State == LinkState.ItsOwnClip &&
              PartnerLinks.Plan(aria, layer, partner, partner, true, true).State == LinkState.ItsOwnClip &&
              PartnerLinks.Plan(aria, layer, null, null, false, true).State == LinkState.ItsOwnClip,
            "planner: a link to its own workspace stays off as pointing at its own clip, first");
    }

    /// <summary>The service: tracks built on a worker and handed out, the same set while nothing changed, and off links dropped.</summary>
    private static void Service(Action<bool, string> check)
    {
        var you = WorkspaceKey.Self;
        var aria = new WorkspaceKey(Partner);
        var warned = new List<string>();
        long later = 0;
        using var links = new PartnerLinks((_, message) => warned.Add(message), () => Environment.TickCount64 + later);
        var mySession = new TimelineSession(null, null!, null!, null!, null, null!, null!, null!);
        var theirSession = new TimelineSession(null, null!, null!, null!, null, null!, null!, null!);
        var played = Reaching();
        var theirPlayed = Reaching();
        var nudge = Offset("m1", "j_te_l", new Vector3(.05f, 0, 0));
        var mine = new TimelineDocument(30, 30, null, [], 1) { Motion = [Layer(Hand())] };
        var theirs = new TimelineDocument(30, 30, null, [], 1) { Motion = [nudge] };
        // How often each workspace's full view was asked for, which reads its motion.
        var asked = new Dictionary<WorkspaceKey, int>();
        LinkTarget Target(WorkspaceKey key, TimelineDocument? doc, MotionClip clip, float? size, TimelineSession session) =>
            new(key, doc == null ? null : new LinkCandidate(key, key.IsSelf ? "You" : "Aria Rose", Hug, 1, doc, false, null), () =>
            {
                asked[key] = asked.GetValueOrDefault(key) + 1;
                return doc == null ? null : View(key, doc, clip, size);
            }, session);
        LinkTarget[] Targets(TimelineDocument my, TimelineDocument their, float? mySize = 1, float? theirSize = 1) =>
            [Target(you, my, played, mySize, mySession), Target(aria, their, theirPlayed, theirSize, theirSession)];

        // Nobody links and no picker asks: no motion is read. Alone, a link reads none either and says why it's off.
        var plain = new TimelineDocument(30, 30, null, [], 1) { Motion = [nudge] };
        links.UpdateViews(Targets(plain, theirs), true, _ => true);
        check(asked.Count == 0 && links.Views.Count == 0 && links.HasPartner(you) && links.HasPartner(aria),
            "service: without links or a picker no workspace's motion is read, and the partner is still offered");
        links.UpdateViews([Target(you, mine, played, 1, mySession)], true, _ => true);
        check(asked.Count == 0 && mySession.LinkStates.GetValueOrDefault("m1") == LinkState.NotOpen && !links.HasPartner(you),
            "service: a workspace alone reads no motion, its link off as not open");
        links.UpdateViews([Target(you, mine, played, 1, mySession)], true, _ => false);
        check(asked.Count == 0 && mySession.LinkStates.GetValueOrDefault("m1") == LinkState.PartnerGone,
            "service: alone, a link to a deleted preset says so");

        // A picker asks: the other workspaces are read for a while, then no more.
        links.Want(you);
        links.UpdateViews(Targets(plain, theirs), true, _ => true);
        check(asked.GetValueOrDefault(aria) == 1 && !asked.ContainsKey(you) && links.Views.Single().Key == aria,
            "service: an open picker reads the other workspaces");
        later += PartnerLinks.WantFor;
        links.UpdateViews(Targets(plain, theirs), true, _ => true);
        check(asked.GetValueOrDefault(aria) == 1 && links.Views.Count == 0, "service: a picker that stopped asking reads nothing more");
        later = 0;
        asked.Clear();
        void Settle(LinkTarget[] targets)
        {
            links.UpdateViews(targets, true, _ => true);
            for (var until = Environment.TickCount64 + 10_000; mySession.LinksBuilding && Environment.TickCount64 < until;)
            {
                Thread.Sleep(5);
                links.UpdateViews(targets, true, _ => true);
            }
        }

        check(mySession.Links == MotionLinkSet.Empty && mySession.LinkStates.IsEmpty && !mySession.LinksBuilding, "service: a session starts without links");
        var first = Targets(mine, theirs);
        links.UpdateViews(first, true, _ => true);
        check(mySession.LinksBuilding && mySession.LinkStates.GetValueOrDefault("m1") == LinkState.On, "service: a new link builds its track on a worker");
        check(asked.GetValueOrDefault(aria) == 1 && asked.GetValueOrDefault(you) == 1, "service: a link reads the partner it names and its own workspace");
        Settle(first);
        var expected = MotionLinks.Track(theirPlayed, [nudge], "j_te_l", 1, default);
        var built = mySession.Links.For("m1");
        check(!mySession.LinksBuilding && built != null && built.Before.SequenceEqual(expected.Before) && built.After.SequenceEqual(expected.After),
            "service: the track is built from the partner's own edits");
        check(ReferenceEquals(theirSession.Links, MotionLinkSet.Empty) && ReferenceEquals(theirSession.LinkStates, PartnerLinks.NoStates),
            "service: a workspace without links keeps the empty set and states");
        check(links.Views.Count == 2, "service: every view read is offered to the picker");

        var set = mySession.Links;
        var states = mySession.LinkStates;
        links.UpdateViews(Targets(mine, theirs), true, _ => true);
        check(ReferenceEquals(mySession.Links, set) && ReferenceEquals(mySession.LinkStates, states) && !mySession.LinksBuilding,
            "service: SameInputs_KeepsSet: new views of the same document and clip keep the set and the states");

        // A partner revision that doesn't move the bone: rebuilt, but the same content keeps the track and the set.
        var revised = Targets(mine, theirs with { });
        links.UpdateViews(revised, true, _ => true);
        check(mySession.LinksBuilding && ReferenceEquals(mySession.Links, set), "service: the last track stands while a newer one builds");
        Settle(revised);
        check(ReferenceEquals(mySession.Links, set) && ReferenceEquals(mySession.Links.For("m1"), built) && ReferenceEquals(mySession.LinkStates, states),
            "service: a rebuild to the same content keeps the track and the set");

        // The partner edits: the last track stands while the new one builds, then a new set comes.
        var edited = Targets(mine, theirs with { Motion = [Offset("m1", "j_te_l", new Vector3(.08f, 0, 0))] });
        links.UpdateViews(edited, true, _ => true);
        check(mySession.LinksBuilding && ReferenceEquals(mySession.Links, set), "service: the last track stands while a newer one of other content builds");
        Settle(edited);
        var rebuilt = mySession.Links.For("m1");
        check(!ReferenceEquals(mySession.Links, set) && rebuilt != null && !ReferenceEquals(rebuilt, built) && rebuilt.Print != built!.Print &&
              ReferenceEquals(mySession.LinkStates, states), "service: a partner edit that moves the bone gives a new set, the states unchanged");

        // The link's own settings change: the last track stands while it rebuilds. Another bone or partner bone: it goes at once.
        var bent = Targets(mine with { Motion = [Layer(Hand() with { Bend = 10 })] }, edited[1].Candidate!.Document);
        links.UpdateViews(bent, true, _ => true);
        check(mySession.LinksBuilding && ReferenceEquals(mySession.Links.For("m1"), rebuilt), "service: a new bend keeps the last track while it rebuilds");
        Settle(bent);
        var retargeted = Targets(mine with { Motion = [Layer(Hand() with { PartnerBone = "j_te_r" })] }, edited[1].Candidate!.Document);
        links.UpdateViews(retargeted, true, _ => true);
        check(mySession.Links.For("m1") == null && mySession.LinkStates.GetValueOrDefault("m1") == LinkState.On && mySession.LinksBuilding,
            "service: another partner bone drops the stale track at once and builds its own");
        Settle(retargeted);
        var expectedRight = MotionLinks.Track(theirPlayed, edited[1].Candidate!.Document.Motion, "j_te_r", 1, default);
        check(mySession.Links.For("m1") is { } right && right.After.SequenceEqual(expectedRight.After), "service: the new partner bone's track comes once built");
        Settle(Targets(mine, theirs));

        // Off: the track goes at once.
        links.UpdateViews(Targets(mine, theirs, theirSize: null), true, _ => true);
        check(ReferenceEquals(mySession.Links, MotionLinkSet.Empty) && mySession.LinkStates.GetValueOrDefault("m1") == LinkState.Despawned &&
              !mySession.LinksBuilding, "service: a despawned partner drops the track");
        links.UpdateViews(Targets(mine with { Motion = [Layer(Hand()) with { Enabled = false }] }, theirs), true, _ => true);
        check(ReferenceEquals(mySession.Links, MotionLinkSet.Empty) && ReferenceEquals(mySession.LinkStates, PartnerLinks.NoStates),
            "service: a link layer that is off isn't planned");
        links.UpdateViews([Target(you, null, played, 1, mySession), Target(aria, theirs, theirPlayed, 1, theirSession)], true, _ => true);
        check(ReferenceEquals(mySession.Links, MotionLinkSet.Empty) && ReferenceEquals(mySession.LinkStates, PartnerLinks.NoStates) &&
              links.Views.Count == 0 && !links.HasPartner(aria), "service: a workspace without an editable clip has no links and isn't offered");
        links.UpdateViews([new LinkTarget(you, new LinkCandidate(you, "You", Hug, 1, mine, false, null), () => null, mySession),
            Target(aria, theirs, theirPlayed, 1, theirSession)], true, _ => true);
        check(mySession.LinkStates.GetValueOrDefault("m1") == LinkState.Reading && mySession.Links.For("m1") == null,
            "service: with its partner open, a link whose own clip is still read says so");
        links.UpdateViews([new LinkTarget(you, new LinkCandidate(you, "You", Hug, 1, mine, false, null, true), () => null, mySession),
            Target(aria, theirs, theirPlayed, 1, theirSession)], true, _ => true);
        check(mySession.LinkStates.GetValueOrDefault("m1") == LinkState.SelfUnreadable, "service: a link whose own clip couldn't be read says so");
        links.UpdateViews([Target(you, mine, played, 1, mySession),
            new LinkTarget(aria, new LinkCandidate(aria, "Aria Rose", Hug, 1, theirs, false, null, true), () => null, theirSession)], true, _ => true);
        check(mySession.LinkStates.GetValueOrDefault("m1") == LinkState.Unreadable, "service: a link whose partner's clip couldn't be read says so");

        // A partner bone the skeleton lacks, and a build that fails otherwise, logged once.
        var noBone = Targets(mine with { Motion = [Layer(Hand() with { PartnerBone = "j_nothing" })] }, theirs);
        Settle(noBone);
        check(mySession.LinkStates.GetValueOrDefault("m1") == LinkState.NoBone && mySession.Links.For("m1") == null && warned.Count == 0,
            "service: a partner skeleton without the bone leaves the link off, unlogged");
        var unreadable = Targets(mine, theirs, mySize: float.PositiveInfinity);
        Settle(unreadable);
        links.UpdateViews(unreadable, true, _ => true);
        check(mySession.LinkStates.GetValueOrDefault("m1") == LinkState.Unreadable && mySession.Links.For("m1") == null && !mySession.LinksBuilding &&
              warned.Count == 1, "service: a track that can't be built leaves the link off, logged once");
    }

    /// <summary>A link layer added to a document, as a link back is: the document's own next key, and both feet for the hips.</summary>
    private static void AddLinks(Action<bool, string> check)
    {
        var doc = new TimelineDocument(40, 40, null, [], 1)
        {
            Motion = [Offset("m1", "j_te_l", Vector3.Zero), Offset("m5", "j_te_r", Vector3.Zero)], NextMotionKey = 5,
        };
        var added = MotionEdits.AddLink(doc, Hips() with { Holds = [] }, "Link: Aria Rose", out var key);
        check(key == "m6" && added.NextMotionKey == 7 && added.Motion.Length == 3 &&
              added.Motion[^1] is { Kind: MotionLayerKind.Link, Key: "m6", Name: "Link: Aria Rose", Start: 0, End: 40, FadeIn: 0, FadeOut: 0, Amount: 1,
                  Components: PoseComponents.None, Link: { } hips } &&
              hips.Holds.SequenceEqual([PinChains.FootL, PinChains.FootR]),
            "add link: a link layer over the whole clip, keyed from the document, the hips holding both feet");
        MotionEvaluator.Validate(added.Motion[^1]);
        var hand = MotionEdits.AddLink(added, Hand(), "Link: You", out var handKey);
        check(handKey == "m7" && hand.Motion[^1].Link is { Bone: "j_te_r", PartnerBone: "j_te_l", Holds.IsEmpty: true },
            "add link: a hand link holds nothing");
        check(MotionEdits.AddLink(doc, Hips() with { Holds = [PinChains.FootL] }, "Link", out _).Motion[^1].Link!.Holds.SequenceEqual([PinChains.FootL]),
            "add link: a hips link keeps the holds it was given");
    }

    private static LinkView View(WorkspaceKey key, TimelineDocument doc, MotionClip played, float? size) =>
        new(key, key.IsSelf ? "You" : "Aria Rose", Hug, 1, played, doc, false, size);

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = HipDragFixture.Body();
        var hand = Hand();
        var hips = Hips();
        MotionEvaluator.Validate(Layer(hand));
        MotionEvaluator.Validate(Layer(hips));
        MotionEvaluator.CheckBones([Layer(hand), Layer(hips, key: "m2")], body);
        MotionEvaluator.CheckBones([Layer(hand with { Top = null })], body);

        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Layer(null), "a link layer without its link is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, Link: hand),
            "a link on an offset layer is refused");
        Refused(Layer(hand with { Bone = "" }), "a link without its bone is refused");
        Refused(Layer(hand with { PartnerBone = "" }), "a link without the partner's bone is refused");
        Refused(Layer(hand) with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_r", T(Vector3.Zero)))] },
            "a link layer with keys is refused");
        Refused(Layer(hand with { Holds = [PinChains.FootL] }), "holds on a link that isn't on the hips are refused");
        Refused(Layer(hips with { Holds = [PinChains.FootL, PinChains.FootL] }), "a link holding a bone twice is refused");
        Refused(Layer(hips with { Holds = [""] }), "a link holding a bone without a name is refused");
        Refused(Layer(hand, amount: 1.5f), "a link's pull stops at 100%");
        Refused(Layer(hand with { Bend = float.NaN }), "a link bend that isn't a number is refused");
        Refused(Layer(hand with { Bend = 200 }), "a link bend past a half turn is refused");
        Refused(Layer(hand) with { Pins = [new MotionPin("j_te_r")] }, "a link layer with pins is refused");
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Link) && MotionEvaluator.Weight(Layer(hand), 15, 30) == 1,
            "a link's pull blends at most fully");

        void Missing(MotionLink link, string what) => reject(() => MotionEvaluator.CheckBones([Layer(link)], body), what);
        Missing(hand with { Bone = "j_nothing" }, "a linked bone the skeleton lacks is refused");
        Missing(hand with { Top = "j_nothing" }, "a link top the skeleton lacks is refused");
        Missing(hand with { Top = "j_asi_a_l" }, "a link top that isn't above the bone is refused");
        Missing(hand with { Top = PinChains.Body }, "the body as a link's top is refused");
        Missing(hand with { Bone = PinChains.Body }, "a link on the body is refused");
        Missing(hand with { Bone = "n_root" }, "a link on the root is refused");
        Missing(hips with { Holds = ["j_nothing"] }, "a held bone the skeleton lacks is refused");
        Missing(hips with { Holds = [PinChains.Hips] }, "a link holding the hips themselves is refused");
        Missing(hips with { Holds = [PinChains.Body] }, "a link holding a bone above the hips is refused");

        var layer = Layer(hand);
        var set = Set(("m1", Still()));
        check(!MotionEvaluator.Active(layer), "a link layer without its partner's track does nothing");
        check(MotionEvaluator.Active(layer, set), "a link layer with its partner's track is active");
        check(!MotionEvaluator.Active(layer, Set(("m9", Still()))) && !MotionEvaluator.Active(layer, MotionLinkSet.Empty),
            "another layer's track doesn't make a link active");
        check(!MotionEvaluator.Active(layer with { Amount = 0 }, set) && !MotionEvaluator.Active(layer with { Enabled = false }, set) &&
              !MotionEvaluator.Active(layer with { Weights = [new MotionWeight(0, 0), new MotionWeight(30, 0)] }, set) &&
              !MotionEvaluator.Active(layer with { Link = null }, set),
            "a link at 0%, off, weighted to 0 all along or without its link does nothing");
        check(MotionLinks.MayChange(layer) && !MotionLinks.MayChange(layer with { Amount = 0 }) && !MotionLinks.MayChange(layer with { Enabled = false }) &&
              !MotionLinks.MayChange(layer with { Weights = [new MotionWeight(0, 0), new MotionWeight(30, 0)] }) &&
              !MotionLinks.MayChange(layer with { Link = null }) &&
              !MotionLinks.MayChange(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear)),
            "a link may change the motion when it is on with a pull, without asking for its track");
        var doc = new TimelineDocument(30, 30, null, [], 1) { Motion = [layer] };
        check(!MotionEvaluator.Changes(doc.Motion) && MotionEvaluator.Changes(doc.Motion, set) && !MotionEvaluator.Changes(doc.Motion, null),
            "a document with only a link changes the motion only with its partner's track");
    }

    private static void Tracks(Action<bool, string> check, Action<Action, string> reject)
    {
        MotionTransform[] before = [M(new Vector3(0, 1, 0), 0), M(new Vector3(.3f, 1, 0), 30), M(new Vector3(.3f, 1.6f, 0), 90)];
        MotionTransform[] after = [M(new Vector3(0, 1, .1f), 10), M(new Vector3(.2f, 1, .1f), 40), M(new Vector3(.2f, 1.4f, .1f), 60)];
        var track = new LinkTrack([.. before], [.. after], 2 / 30f);
        check(track.Samples == 3 && Math.Abs(track.Duration - 2 / 30f) < 1e-7f, "a track keeps its samples and duration");
        check(Near(track.At(1), before[1], after[1]), "a track gives its middle sample at frame 1");
        check(Near(track.At(.5f), MotionEvaluator.Lerp(before[0], before[1], .5f), MotionEvaluator.Lerp(after[0], after[1], .5f)),
            "a track goes halfway between its samples at frame 0.5");
        check(Near(track.At(2), before[2], after[2]) && Near(track.At(40), before[2], after[2]), "a track holds its last sample from its end on");
        check(Near(track.At(-1), before[0], after[0]) && Near(track.At(float.NaN), before[0], after[0]), "a track holds its first sample before its start");
        var single = new LinkTrack([before[0]], [after[0]], 0);
        check(Near(single.At(12), before[0], after[0]), "a one-sample track holds it");
        reject(() => _ = new LinkTrack([.. before], [after[0]], 2 / 30f), "a track whose sides differ in length is refused");
        reject(() => _ = new LinkTrack([], [], 0), "a track without samples is refused");

        var set = Set(("m1", track));
        check(MotionLinkSet.Empty.Tracks.IsEmpty && MotionLinkSet.Empty.For("m1") == null && ReferenceEquals(MotionLinkSet.Empty, MotionLinkSet.Empty),
            "the empty set has no tracks");
        check(ReferenceEquals(set.For("m1"), track) && set.For("m2") == null && set.Tracks.Count == 1, "a set finds its tracks by layer key");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-link-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_loop.pap", "pose06_loop.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var hips = Layer(Hips() with { HoldTurn = false, Bend = 15 }) with { Weights = [new MotionWeight(10, .5f)], FadeIn = 3 };
            var hand = Layer(Hand() with { Partner = null }, key: "m2") with { Name = "Link 2" };
            var offset = new MotionLayer("m3", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(false, 0, null, [hips, hand, offset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 3 && loaded[0].Kind == MotionLayerKind.Link && SameLink(loaded[0].Link, hips.Link) && MotionEdits.Same(loaded[0], hips) &&
                  loaded[1].Kind == MotionLayerKind.Link && SameLink(loaded[1].Link, hand.Link) && loaded[1].Link!.Partner == null &&
                  loaded[0].Link!.Holds.SequenceEqual([PinChains.FootL, PinChains.FootR]) && loaded[2].Link == null,
                "a journal keeps a link layer, its link and its holds");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var saved = File.ReadAllText(path);
            JsonNode Layers(JsonObject json) => json["Request"]!["Timeline"]!["Motion"]!;
            MotionLayer LoadEdited(Action<JsonObject> edit)
            {
                var json = JsonNode.Parse(saved)!.AsObject();
                edit(json);
                File.WriteAllText(path, json.ToJsonString());
                return new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0];
            }

            check(Layers(JsonNode.Parse(saved)!.AsObject())[0]!["Kind"]!.GetValue<int>() == 11, "a journal stores a link layer's kind as 11");
            // Journals written before link layers have no Link, and none of their layers is a link.
            var removed = false;
            var older = LoadEdited(json =>
            {
                var layer = Layers(json)[2]!.AsObject();
                removed = layer.Remove("Link");
                Layers(json).AsArray().RemoveAt(1);
                Layers(json).AsArray().RemoveAt(0);
            });
            check(removed && older.Kind == MotionLayerKind.Offset && older.Link == null, "an older journal's offset loads with no link");
            var unlinked = LoadEdited(json => Layers(json)[0]!.AsObject().Remove("Link"));
            var refused = false;
            try { MotionEvaluator.Validate(unlinked); }
            catch (InvalidDataException) { refused = true; }
            check(unlinked.Kind == MotionLayerKind.Link && unlinked.Link == null && refused && !MotionEvaluator.Active(unlinked, Set(("m1", Still()))),
                "a link layer without Link loads with none and is refused");
            // A link written without holds, as a limb link's may be, holds nothing.
            var held = false;
            var unheld = LoadEdited(json => held = Layers(json)[0]!["Link"]!.AsObject().Remove("Holds"));
            check(held && unheld.Link is { Holds: { IsDefault: false, IsEmpty: true } } link && SameLink(link, hips.Link! with { Holds = [] }),
                "a journal's link without Holds loads holding nothing");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void Solving(Action<bool, string> check)
    {
        // The carry: the partner's move and turn of its bone, applied to this bone as if it were attached to the partner's.
        var spot = new MotionTransform(new Vector3(.2f, 1.1f, -.1f), Q(Vector3.UnitX, 25), Vector3.One);
        var current = new MotionTransform(new Vector3(.4f, 1.1f, -.1f), Q(Vector3.UnitZ, 40), new Vector3(1.1f, 1, 1));
        check(MotionLinks.Aim(current, spot, spot, true) == current && MotionLinks.Aim(current, spot, spot, false) == current,
            "a partner that doesn't move leaves the aim where the bone is");
        var moved = MotionLinks.Aim(current, spot, spot with { Position = spot.Position + new Vector3(.1f, 0, 0) }, true);
        check(Vector3.Distance(moved.Position, current.Position + new Vector3(.1f, 0, 0)) < 1e-6f && Degrees(moved.Rotation, current.Rotation) < .01f &&
              moved.Scale == current.Scale, "a partner moved 10 cm moves the aim 10 cm");
        var turn = Q(Vector3.UnitY, 30);
        var turned = spot with { Rotation = Quaternion.Normalize(turn * spot.Rotation) };
        var held = MotionLinks.Aim(current, spot, turned, true);
        var loose = MotionLinks.Aim(current, spot, turned, false);
        var around = spot.Position + Vector3.Transform(current.Position - spot.Position, turn);
        check(Vector3.Distance(held.Position, around) < 1e-6f && Vector3.Distance(loose.Position, around) < 1e-6f,
            "a partner turned 30° carries the bone round its spot");
        check(Degrees(held.Rotation, Quaternion.Normalize(turn * current.Rotation)) < .01f && Math.Abs(Degrees(held.Rotation, current.Rotation) - 30) < .05f &&
              loose.Rotation == current.Rotation, "holding the turn adds the partner's turn, position only leaves the bone's own");
        check(MotionLinks.AsPin(Hand()) is { Bone: "j_te_r", HoldTurn: true, RelativeTo: null, Top: "j_sako_r", Planted: false, Bend: 30 } pin &&
              pin.Sensitivity == PinSensitivity.Normal, "a link bends its chain as a pin on its bone would");

        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        int hand = index["j_te_r"], forearm = index["j_ude_b_r"], upper = index["j_ude_a_r"];
        var link = new MotionLink("j_te_r", Partner, "j_te_l", "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap", 1);
        var layer = Layer(link);
        var clip = Reaching();
        var chain = PinChains.Chain(body, MotionLinks.AsPin(link));

        var still = Set(("m1", Track(clip, hand, (_, before) => before)));
        check(SameRows(Applied(clip, [layer], still), Applied(clip, [])), "a link whose partner doesn't move leaves every sample exactly as it was");

        // The right arm hangs out along -X, so a move along +X brings the hand in, within the arm's reach.
        var push = new Vector3(.1f, 0, 0);
        var pushed = Set(("m1", Track(clip, hand, (_, before) => before with { Position = before.Position + push })));
        var reached = Applied(clip, [layer], pushed);
        check(All(clip, i => Vector3.Distance(Model(reached, i)[hand].Position, Model(clip, i)[hand].Position + push) < 1e-4f),
            "a partner's hand moved 10 cm moves the hand 10 cm");
        check(All(clip, i => Vector3.Dot(BendNormal(Model(reached, i), chain), BendNormal(Model(clip, i), chain)) > 0), "the elbow keeps its side");
        check(All(clip, i => Math.Abs(Length(Model(reached, i), upper, forearm) - Length(Model(clip, i), upper, forearm)) < 1e-5f &&
                             Math.Abs(Length(Model(reached, i), forearm, hand) - Length(Model(clip, i), forearm, hand)) < 1e-5f),
            "the upper arm and forearm keep their lengths");
        check(All(clip, i => Enumerable.Range(0, clip.Bones).Where(b => b != hand && !chain.Bones.Contains(b)).All(b => reached.Get(i, b) == clip.Get(i, b))),
            "only the chain and the hand are written");
        check(SameRows(Applied(clip, [layer with { Amount = 0 }], pushed), Applied(clip, [])), "a pull of 0 changes nothing");
        var half = Applied(clip, [layer with { Amount = .5f }], pushed);
        check(All(clip, i => Vector3.Distance(Model(half, i)[hand].Position, Model(clip, i)[hand].Position + push * .5f) < 1e-4f),
            "a pull of 50% lands the hand halfway along the straight line");
        var fading = layer with { FadeIn = 10 };
        var faded = Applied(clip, [fading], pushed);
        check(Model(faded, 0)[hand].Position == Model(clip, 0)[hand].Position &&
              All(clip, i => Vector3.Distance(Model(faded, i)[hand].Position, Model(clip, i)[hand].Position + push * MotionEvaluator.Weight(fading, clip.Frame(i), 30)) < 1e-4f) &&
              MotionEvaluator.Weight(fading, 3, 30) is > 0 and < .5f, "a fade-in eases the hand in");

        // The partner turns its hand 20° about Y round a spot 5 cm in front of this hand.
        var swing = Q(Vector3.UnitY, 20);
        var offset = new Vector3(0, 0, .05f);
        var swung = Applied(clip, [layer], Set(("m1", Track(clip, hand,
            (_, before) => before with { Position = before.Position + offset, Rotation = Quaternion.Normalize(swing * before.Rotation) },
            (_, before) => before with { Position = before.Position + offset }))));
        check(All(clip, i =>
        {
            var was = Model(clip, i)[hand];
            var now = Model(swung, i)[hand];
            return Vector3.Distance(now.Position, was.Position + offset + Vector3.Transform(-offset, swing)) < 1e-4f &&
                   Degrees(now.Rotation, Quaternion.Normalize(swing * was.Rotation)) < .01f && Math.Abs(Degrees(now.Rotation, was.Rotation) - 20) < .05f;
        }), "a partner's 20° turn carries the hand round and turns it 20°");

        // Out of reach: on a still arm, the partner's hand goes out along -X up to a metre, over the samples.
        var stand = Reaching(still: true);
        var far = Applied(stand, [layer], Set(("m1", Track(stand, hand, (i, before) => before with { Position = before.Position - new Vector3(i / 30f, 0, 0) }))));
        var reach = Length(Model(stand, 0), upper, forearm) + Length(Model(stand, 0), forearm, hand);
        var shoulder = Model(stand, 0)[upper].Position;
        var smooth = true;
        float? previous = null;
        for (var i = 0; i < stand.Samples; i++)
        {
            var aim = Model(stand, i)[hand].Position - new Vector3(i / 30f, 0, 0);
            var got = Model(far, i)[hand].Position;
            var distance = Vector3.Distance(got, shoulder);
            smooth &= distance <= reach + 1e-5f && (previous == null || distance >= previous - 1e-5f && distance - previous < 1 / 30f + 1e-4f) &&
                      Vector3.Cross(got - shoulder, Vector3.Normalize(aim - shoulder)).Length() < 1e-4f;
            previous = distance;
        }
        check(smooth && previous > .97f * reach, "an aim out of reach straightens the arm and falls short along the line, without a jump");

        // ApplyAt gives Apply's samples: frames 7.5 and 15 are samples 15 and 30 of a clip with two samples a frame.
        var fine = Reaching(61);
        var chest = new MotionLayer("m0", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_sebo_c", new BoneTransform(Vector3.Zero, Q(Vector3.UnitY, 10), Vector3.One)))]);
        ImmutableArray<MotionLayer> layers = [chest, fading];
        var fineSet = Set(("m1", Track(fine, hand, (_, before) => before with { Position = before.Position + push })));
        var applied = Applied(fine, layers, fineSet);
        check(new[] { 0, 15, 30 }.All(s => MotionEvaluator.ApplyAt(body, Row(fine, s), layers, fine.Frame(s), 0, 30, links: fineSet)
                .Zip(Row(applied, s)).All(pair => Near(pair.First, pair.Second))),
            "ApplyAt gives Apply's samples with the same partner tracks");
        check(Model(applied, 30)[hand].Position != Model(Applied(fine, [chest]), 30)[hand].Position, "the link moves the hand the offset left");

        // No track: the link is left out, the layers round it still apply.
        var after = new MotionLayer("m2", MotionLayerKind.Offset, "Offset 2", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_r", new BoneTransform(Vector3.Zero, Q(Vector3.UnitX, 15), Vector3.One)))]);
        ImmutableArray<MotionLayer> around3 = [chest, layer, after];
        var without = Applied(fine, [chest, after]);
        check(SameRows(Applied(fine, around3, null), without) && SameRows(Applied(fine, around3, MotionLinkSet.Empty), without) &&
              !SameRows(without, Applied(fine, [])), "a link without its track is left out and the layers round it apply");
        check(new[] { 0f, 7.5f, 15f }.All(frame => MotionEvaluator.ApplyAt(body, fine.Pose(frame), around3, frame, 0, 30)
                  .SequenceEqual(MotionEvaluator.ApplyAt(body, fine.Pose(frame), [chest, after], frame, 0, 30))),
            "ApplyAt without tracks leaves the link out too");

        // A bone right under the body has nothing to bend, as for a pin; the hips follow in their own way (Hips).
        var back = Layer(link with { Bone = "j_sebo_a", PartnerBone = "j_sebo_a" });
        var backSet = Set(("m1", Track(clip, index["j_sebo_a"], (_, before) => before with { Position = before.Position + push })));
        check(SameRows(Applied(clip, [back], backSet), Applied(clip, [])), "a link on a bone right under the body does nothing");
        var hips = Layer(link with { Bone = PinChains.Hips, PartnerBone = PinChains.Hips });

        // Symmetry counts what a link may move, with or without its track; paths mark no keys on a link.
        ImmutableArray<short> tracked = [0, 1, 2];
        var animated = MotionSymmetry.Animated(body, tracked, [layer], 1);
        check(animated != null && chain.Bones.All(animated.Contains) && animated.Contains(hand) && animated.Contains(upper),
            "a link's chain and hand count as animated for a later symmetry");
        check(MotionSymmetry.Animated(body, tracked, [layer with { Enabled = false }], 1) is { } off && !off.Contains(hand),
            "a link that is off doesn't count");
        check(MotionLinks.Moved(body, layer).SequenceEqual([upper, forearm, hand]), "a hand link may move the arm and the hand");
        var hipsMoved = MotionLinks.Moved(body, hips with { Link = hips.Link! with { Holds = [PinChains.FootL, PinChains.FootR] } });
        check(hipsMoved.Contains(index[PinChains.Hips]) && hipsMoved.Contains(index[HipSolver.BackBase]) && hipsMoved.Contains(index[PinChains.FootL]) &&
              hipsMoved.Contains(index["j_asi_b_r"]) && !hipsMoved.Contains(index[PinChains.Body]), "a hips link may move the hips, the back and the held legs");
        check(MotionLinks.Moved(body, back).IsEmpty && MotionLinks.Moved(body, chest).IsEmpty, "nothing to bend, or another kind, moves nothing");
        var keyed = layer with { Keys = [new MotionKey(5, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_r", T(Vector3.Zero)))] };
        check(MotionPaths.KeyFrames(keyed, "j_te_r", 0).Count == 0, "a link marks no keys on a motion path");
    }

    private static void Hips(Action<bool, string> check)
    {
        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        int kosi = index[PinChains.Hips], hara = index[PinChains.Body], spine = index[HipSolver.BackBase];
        int[] feet = [index[PinChains.FootL], index[PinChains.FootR]];
        int[] hands = [index["j_te_l"], index["j_te_r"]];
        var layer = Layer(Hips());
        var loose = Layer(Hips() with { Holds = [] });
        var clip = Crouching();
        bool Held(MotionClip got, int bone) => All(clip, i =>
            Vector3.Distance(Model(got, i)[bone].Position, Model(clip, i)[bone].Position) < 1e-4f && Degrees(Model(got, i)[bone].Rotation, Model(clip, i)[bone].Rotation) < .1f);
        bool Carried(MotionClip got, int bone, Vector3 by) => All(clip, i => Vector3.Distance(Model(got, i)[bone].Position, Model(clip, i)[bone].Position + by) < 1e-4f);

        var still = Set(("m1", Track(clip, kosi, (_, before) => before)));
        check(SameRows(Applied(clip, [layer], still), Applied(clip, [])), "a hips link whose partner doesn't move leaves every sample exactly as it was");

        // The partner's hips go 8 cm forward: these hips go as far, the feet stay planted and the unheld hands go with the back.
        var forward = new Vector3(0, 0, .08f);
        var pushed = Set(("m1", Track(clip, kosi, (_, before) => before with { Position = before.Position + forward })));
        var moved = Applied(clip, [layer], pushed);
        check(Carried(moved, kosi, forward), "a partner's hips moved 8 cm move the hips 8 cm");
        check(feet.All(foot => Held(moved, foot)), "the held feet stay where they were");
        check(Carried(moved, spine, forward) && hands.All(hand => Carried(moved, hand, forward)), "the unheld hands go with the upper body");
        var free = Applied(clip, [loose], pushed);
        check(Carried(free, kosi, forward) && feet.All(foot => Carried(free, foot, forward)), "with nothing held the feet go with the hips");
        var half = Applied(clip, [layer with { Amount = .5f }], pushed);
        check(Carried(half, kosi, forward * .5f) && feet.All(foot => Held(half, foot)), "a pull of 50% moves the hips halfway, the feet still held");

        // The partner's hips turn 10° about Y on their spot: these hips turn as far and the feet still hold.
        var turn = Q(Vector3.UnitY, 10);
        var turnSet = Set(("m1", Track(clip, kosi, (_, before) => before with { Rotation = Quaternion.Normalize(turn * before.Rotation) })));
        var turned = Applied(clip, [layer], turnSet);
        check(All(clip, i => Degrees(Model(turned, i)[kosi].Rotation, Quaternion.Normalize(turn * Model(clip, i)[kosi].Rotation)) < .01f &&
                             Math.Abs(Degrees(Model(turned, i)[kosi].Rotation, Model(clip, i)[kosi].Rotation) - 10) < .05f &&
                             Vector3.Distance(Model(turned, i)[kosi].Position, Model(clip, i)[kosi].Position) < 1e-5f),
            "a partner's 10° turn turns the hips 10° on their spot");
        check(feet.All(foot => Held(turned, foot)), "the feet hold through the turn");
        var unturned = Applied(clip, [Layer(Hips() with { HoldTurn = false })], turnSet);
        check(SameRows(unturned, Applied(clip, [])), "a hips link on position only leaves a turn on the spot out");

        // On a clip whose hips' move goes onto n_hara the hips keep their own spot, n_hara carries the move, and the body lands as on any clip.
        var onHara = OnHara(clip);
        var both = Set(("m1", Track(clip, kosi, (_, before) => before with
        {
            Position = before.Position + forward, Rotation = Quaternion.Normalize(turn * before.Rotation),
        })));
        var plain = Applied(clip, [layer], both);
        var carried = Applied(onHara, [layer], both);
        check(All(clip, i => carried.Get(i, kosi).Position == clip.Get(i, kosi).Position &&
                             Vector3.Distance(carried.Get(i, hara).Position, clip.Get(i, hara).Position + forward) < 1e-5f),
            "on an n_hara clip the hips keep their own spot and n_hara carries the move");
        check(All(clip, i => Degrees(carried.Get(i, kosi).Rotation, plain.Get(i, kosi).Rotation) < .01f), "on an n_hara clip the hips still turn");
        check(All(clip, i => new[] { kosi, spine, feet[0], feet[1], hands[0], hands[1] }.All(b =>
                Vector3.Distance(Model(carried, i)[b].Position, Model(plain, i)[b].Position) < 1e-4f)),
            "on an n_hara clip the hips, the back, the feet and the hands land where they do on any clip");

        // ApplyAt gives Apply's samples for a hips link, with the hips' move on n_hara or not.
        var fine = Crouching(61);
        var chest = new MotionLayer("m0", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_sebo_c", new BoneTransform(Vector3.Zero, Q(Vector3.UnitY, 10), Vector3.One)))]);
        ImmutableArray<MotionLayer> layers = [chest, layer with { FadeIn = 10 }];
        var fineSet = Set(("m1", Track(fine, kosi, (_, before) => before with
        {
            Position = before.Position + forward, Rotation = Quaternion.Normalize(turn * before.Rotation),
        })));
        foreach (var hipsOnHara in new[] { false, true })
        {
            var source = hipsOnHara ? OnHara(fine) : fine;
            var applied = Applied(source, layers, fineSet);
            check(new[] { 0, 15, 30 }.All(s => MotionEvaluator.ApplyAt(body, Row(source, s), layers, source.Frame(s), 0, 30, hipsOnHara: hipsOnHara, links: fineSet)
                    .Zip(Row(applied, s)).All(pair => Near(pair.First, pair.Second))),
                hipsOnHara ? "ApplyAt gives Apply's samples for a hips link on an n_hara clip" : "ApplyAt gives Apply's samples for a hips link");
        }
    }

    private static void Partners(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        int right = index["j_te_r"], left = index["j_te_l"];

        // A link back is the partner's link from this workspace (null for You), its bones the other way round; the first counts.
        var hand = Hand();
        var back = Layer(new MotionLink("j_te_l", null, "j_te_r", Hug, 0), key: "m2");
        var wrongWay = Layer(new MotionLink("j_te_r", null, "j_te_l", Hug, 0), key: "m3");
        var other = Layer(new MotionLink("j_te_l", Guid.NewGuid(), "j_te_r", Hug, 0), key: "m4");
        var second = Layer(new MotionLink("j_te_l", null, "j_te_r", Hug, 0, Top: "j_sako_l"), key: "m5");
        var nudge = Offset("m6", "j_te_l", new Vector3(.05f, .02f, 0));
        check(ReferenceEquals(MotionLinks.LinkBack([nudge, wrongWay, other, back, second], hand, null), back),
            "a link back links the partner's bone to this one");
        check(MotionLinks.LinkBack([nudge, wrongWay, other], hand, null) == null && MotionLinks.LinkBack([back], hand, Partner) == null,
            "the same bones the wrong way round or another workspace's link isn't a link back");
        check(ReferenceEquals(MotionLinks.LinkBack([second, back], hand, null), second), "of two link backs the first counts");
        var fromPartner = Layer(new MotionLink("j_te_l", Partner, "j_te_r", Hug, 0), key: "m7");
        check(ReferenceEquals(MotionLinks.LinkBack([back, fromPartner], hand with { Partner = null }, Partner), fromPartner),
            "a link back to a partner names that partner");

        // The partner's own edits: its layers above its link back, without links.
        var above = Offset("m8", "j_sebo_c", new Vector3(0, .01f, 0));
        var below = Offset("m9", "j_te_l", new Vector3(0, 0, .03f));
        ImmutableArray<MotionLayer> partner = [above, other, nudge, back, below, second];
        var own = MotionLinks.Own(partner, back);
        check(own.Length == 2 && ReferenceEquals(own[0], above) && ReferenceEquals(own[1], nudge), "links and layers below the link back aren't the partner's own");
        check(MotionLinks.Own(partner, null).SequenceEqual([above, nudge, below]) && MotionLinks.Own([], null).IsEmpty,
            "without a link back every layer but the links is the partner's own");

        // Tracks from the partner's clip as played: its bone's model transforms, without and with its own edits.
        var clip = Reaching();
        var plain = MotionLinks.Track(clip, [], "j_te_l", 1, default);
        check(plain.Samples == clip.Samples && plain.Duration == clip.Duration &&
              All(clip, i => plain.Before[i] == Model(clip, i)[left] && plain.After[i] == plain.Before[i]),
            "a partner without its own edits gives a track that doesn't move");
        var scaled = MotionLinks.Track(clip, [nudge], "j_te_l", 1.2f, default);
        var edited = Applied(clip, [nudge]);
        check(All(clip, i => Vector3.Distance(scaled.Before[i].Position, Model(clip, i)[left].Position * 1.2f) < 1e-6f &&
                             Vector3.Distance(scaled.After[i].Position, Model(edited, i)[left].Position * 1.2f) < 1e-6f &&
                             Vector3.Distance(scaled.After[i].Position - scaled.Before[i].Position, new Vector3(.05f, .02f, 0) * 1.2f) < 1e-5f &&
                             scaled.Before[i].Rotation == Model(clip, i)[left].Rotation && scaled.After[i].Rotation == Model(edited, i)[left].Rotation &&
                             scaled.After[i].Scale == Model(edited, i)[left].Scale),
            "a partner's hand moved by an offset gives that move, scaled by the sizes, and keeps its turn");
        check(SameRows(clip, Reaching()), "building a track leaves the played clip as it was");
        var missing = false;
        try { _ = MotionLinks.Track(clip, [], "j_nothing", 1, default); }
        catch (InvalidDataException e) { missing = e.Message == MotionEvaluator.Missing("j_nothing").Message; }
        check(missing, "a partner skeleton without the bone is refused as missing");
        reject(() => _ = MotionLinks.Track(clip, [], "j_te_l", float.NaN, default), "a size ratio that isn't a number is refused");

        // No echo: A (You) and B (the partner) link their hands and hips both ways. A's hips offset sits above its links; B has no edits.
        var a = Reaching();
        var b = Reaching();
        var lean = Offset("m1", PinChains.Hips, new Vector3(0, 0, .04f));
        var aHand = Layer(new MotionLink("j_te_r", Partner, "j_te_l", Hug, 1), key: "m2");
        var aHips = Layer(new MotionLink(PinChains.Hips, Partner, PinChains.Hips, Hug, 1), key: "m3");
        var bHand = Layer(new MotionLink("j_te_l", null, "j_te_r", Hug, 1), key: "m1");
        var bHips = Layer(new MotionLink(PinChains.Hips, null, PinChains.Hips, Hug, 1), key: "m2");
        ImmutableArray<MotionLayer> aLayers = [lean, aHand, aHips], bLayers = [bHand, bHips];
        // Each side's tracks, as the host builds them: from the partner's own edits above its link back.
        LinkTrack From(MotionClip played, ImmutableArray<MotionLayer> layers, MotionLayer link, Guid? self) =>
            MotionLinks.Track(played, MotionLinks.Own(layers, MotionLinks.LinkBack(layers, link.Link!, self)), link.Link!.PartnerBone, 1, default);
        check(ReferenceEquals(MotionLinks.LinkBack(aLayers, bHand.Link!, Partner), aHand) && ReferenceEquals(MotionLinks.LinkBack(aLayers, bHips.Link!, Partner), aHips) &&
              ReferenceEquals(MotionLinks.LinkBack(bLayers, aHand.Link!, null), bHand) && ReferenceEquals(MotionLinks.LinkBack(bLayers, aHips.Link!, null), bHips),
            "hands and hips linked both ways find each other's link backs");
        check(MotionLinks.Own(aLayers, aHips).SequenceEqual([lean]) && MotionLinks.Own(bLayers, bHand).IsEmpty && MotionLinks.Own(bLayers, null).IsEmpty,
            "links never carry links");
        var leaned = Applied(a, [lean]);
        var bHandTrack = From(a, aLayers, bHand, Partner);
        var bHipsTrack = From(a, aLayers, bHips, Partner);
        // The hips (j_kosi) and the back with its arms hang from n_hara side by side, so A's hips move doesn't reach A's hand.
        check(All(a, i => bHandTrack.Before[i] == Model(a, i)[right] && bHandTrack.After[i] == Model(leaned, i)[right] &&
                          bHipsTrack.After[i] == Model(leaned, i)[index[PinChains.Hips]] &&
                          Vector3.Distance(bHipsTrack.After[i].Position - bHipsTrack.Before[i].Position, new Vector3(0, 0, .04f)) < 1e-5f),
            "B's tracks carry A's hips move only through A's own body");
        var aHandTrack = From(b, bLayers, aHand, null);
        var aHipsTrack = From(b, bLayers, aHips, null);
        check(aHandTrack.Before.SequenceEqual(aHandTrack.After) && aHipsTrack.Before.SequenceEqual(aHipsTrack.After),
            "A's tracks from B's own edits don't move: nothing echoes back");
        check(SameRows(Applied(a, aLayers, Set(("m2", aHandTrack), ("m3", aHipsTrack))), leaned), "A's links then leave A as its own edits have it");

        // End to end: B moves its hand by an offset above its link back, and A's linked hand follows it.
        var bNudged = Applied(b, [nudge]);
        var follow = From(b, [nudge, bHand, bHips], aHand, null);
        var followed = Applied(a, [aHand], Set(("m2", follow)));
        check(All(a, i => Vector3.Distance(Model(followed, i)[right].Position - Model(a, i)[right].Position,
                              Model(bNudged, i)[left].Position - Model(b, i)[left].Position) < 1e-4f),
            "A's hand follows B's edited hand");

        // Different lengths: a 60-frame A with a 30-frame B, whose hand goes 6 cm along X over its 30 frames: past them A holds B's last sample.
        var longer = Reaching(61, 2);
        var ramp = new MotionLayer("m6", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_l", T(Vector3.Zero))),
                   new MotionKey(30, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_l", T(new Vector3(.06f, 0, 0))))]);
        var shorter = MotionLinks.Track(b, [ramp], "j_te_l", 1, default);
        check(Near(shorter.At(45), shorter.Before[^1], shorter.After[^1]) && Near(shorter.At(60), shorter.Before[^1], shorter.After[^1]),
            "a 30-frame partner's track holds its last sample past frame 30");
        var held = Applied(longer, [aHand with { End = 60 }], Set(("m2", shorter)));
        check(longer.Frame(longer.Samples - 1) == 60 && All(longer, i => Vector3.Distance(Model(held, i)[right].Position - Model(longer, i)[right].Position,
                  new Vector3(.06f * Math.Min(longer.Frame(i), 30) / 30, 0, 0)) < 1e-4f),
            "a 60-frame clip follows a 30-frame partner, holding its last sample past frame 30");
    }

    /// <summary>An offset layer moving <paramref name="bone"/> by <paramref name="move"/> in character space over frames 0 to 30.</summary>
    private static MotionLayer Offset(string key, string bone, Vector3 move) =>
        new(key, MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone, T(move)))]);

    /// <summary>
    /// <see cref="Reaching"/> with the hips 15 cm lower and the feet where they were, so the knees bend and the legs can reach the feet
    /// from hips moved a few centimetres.
    /// </summary>
    private static MotionClip Crouching(int samples = 31)
    {
        var clip = Reaching(samples);
        var body = clip.Skeleton;
        var hips = MotionEvaluator.Index(body)[PinChains.Hips];
        var anchors = HipSolver.Anchors(body, [PinChains.FootL, PinChains.FootR]);
        var crouched = clip.Clone();
        for (var i = 0; i < clip.Samples; i++)
        {
            var local = Row(clip, i);
            var start = MotionEvaluator.ModelSpace(body, local);
            var model = start.ToArray();
            HipSolver.Solve(body, local, model, hips, start[hips].Position - new Vector3(0, .15f, 0), anchors.AsSpan(), start);
            for (var b = 0; b < local.Length; b++)
                crouched.Set(i, b, local[b]);
        }
        return crouched;
    }

    /// <summary><paramref name="clip"/> as a clip whose layers' moves of the hips go onto <c>n_hara</c> (<see cref="MotionClip.HipsOnHara"/>).</summary>
    private static MotionClip OnHara(MotionClip clip)
    {
        var copy = new MotionClip(clip.Skeleton, clip.Duration, clip.Samples) { HipsOnHara = true };
        copy.CopyFrom(clip);
        return copy;
    }

    /// <summary>
    /// <see cref="HipDragFixture.Body"/> over <paramref name="duration"/> seconds in <paramref name="samples"/> samples, the right elbow
    /// bent 50° (and with <paramref name="still"/> off, swinging 10° either way while the chest sways 5°).
    /// </summary>
    private static MotionClip Reaching(int samples = 31, float duration = 1, bool still = false)
    {
        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        var poses = Enumerable.Range(0, samples).Select(i =>
        {
            var phase = still ? 0 : 2 * MathF.PI * i / (samples - 1);
            var pose = body.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray();
            pose[index["j_sebo_c"]] = pose[index["j_sebo_c"]] with { Rotation = Q(Vector3.UnitY, 5 * MathF.Sin(phase)) };
            pose[index["j_ude_b_r"]] = pose[index["j_ude_b_r"]] with { Rotation = Q(Vector3.UnitY, -50 + 10 * MathF.Sin(phase)) };
            return pose.Select(value => value.ToBone()).ToArray();
        }).ToArray();
        return MotionClip.FromPoses(body, duration, poses);
    }

    /// <summary>
    /// A partner track built from <paramref name="clip"/>'s own <paramref name="bone"/> in model space: Before as the clip has it (or as
    /// <paramref name="from"/> moves it), After as <paramref name="to"/> moves it.
    /// </summary>
    private static LinkTrack Track(MotionClip clip, int bone, Func<int, MotionTransform, MotionTransform> to, Func<int, MotionTransform, MotionTransform>? from = null)
    {
        ImmutableArray<MotionTransform> own = [.. Enumerable.Range(0, clip.Samples).Select(i => Model(clip, i)[bone])];
        return new LinkTrack([.. own.Select((value, i) => from?.Invoke(i, value) ?? value)], [.. own.Select((value, i) => to(i, value))], clip.Duration);
    }

    private static MotionClip Applied(MotionClip clip, ImmutableArray<MotionLayer> layers, MotionLinkSet? links = null)
    {
        var copy = clip.Clone();
        MotionEvaluator.Apply(copy, layers, links: links);
        return copy;
    }

    private static MotionTransform[] Row(MotionClip clip, int sample) => [.. Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b))];

    private static MotionTransform[] Model(MotionClip clip, int sample) => MotionEvaluator.ModelSpace(clip.Skeleton, Row(clip, sample));

    private static bool All(MotionClip clip, Func<int, bool> sample) => Enumerable.Range(0, clip.Samples).All(sample);

    private static bool SameRows(MotionClip a, MotionClip b) => a.Samples == b.Samples && All(a, i => Row(a, i).SequenceEqual(Row(b, i)));

    private static float Length(MotionTransform[] model, int from, int to) => Vector3.Distance(model[from].Position, model[to].Position);

    private static Vector3 BendNormal(MotionTransform[] model, PinChain chain) => Vector3.Normalize(Vector3.Cross(
        model[chain.Mid].Position - model[chain.Root].Position, model[chain.End].Position - model[chain.Root].Position));

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180 / MathF.PI;

    private static MotionLink Hand() => new("j_te_r", Partner, "j_te_l", "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap", 1, Top: "j_sako_r", Bend: 30);

    private static MotionLink Hips() =>
        new(PinChains.Hips, Partner, PinChains.Hips, "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap", 0, Holds: [PinChains.FootL, PinChains.FootR]);

    private static MotionLayer Layer(MotionLink? link, float amount = 1, string key = "m1") =>
        new(key, MotionLayerKind.Link, "Link 1", 0, 30, 0, 0, amount, PoseComponents.None, MotionInterpolation.Linear, Link: link);

    private static MotionLinkSet Set(params (string Key, LinkTrack Track)[] tracks) =>
        new(ImmutableDictionary.CreateRange(StringComparer.Ordinal, tracks.Select(t => KeyValuePair.Create(t.Key, t.Track))));

    /// <summary>A partner that doesn't move: Before and After the same on every sample.</summary>
    private static LinkTrack Still()
    {
        ImmutableArray<MotionTransform> samples = [M(Vector3.UnitY, 0), M(Vector3.UnitY, 0)];
        return new LinkTrack(samples, samples, 1 / 30f);
    }

    /// <summary>The links field by field, holds by sequence, as <see cref="MotionEdits.Same"/> compares them.</summary>
    private static bool SameLink(MotionLink? a, MotionLink? b) => a == null || b == null
        ? a == b
        : a.Bone == b.Bone && a.Partner == b.Partner && a.PartnerBone == b.PartnerBone && a.PartnerClip == b.PartnerClip &&
          a.PartnerBinding == b.PartnerBinding && a.HoldTurn == b.HoldTurn && a.Top == b.Top && a.Bend.Equals(b.Bend) && a.Holds.SequenceEqual(b.Holds);

    private static bool Near((MotionTransform Before, MotionTransform After) got, MotionTransform before, MotionTransform after) =>
        Near(got.Before, before) && Near(got.After, after);

    private static bool Near(MotionTransform a, MotionTransform b) =>
        Vector3.Distance(a.Position, b.Position) < 1e-5f && Math.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) > 1 - 1e-6f &&
        Vector3.Distance(a.Scale, b.Scale) < 1e-5f;

    private static MotionTransform M(Vector3 position, float degrees) => new(position, Q(Vector3.UnitY, degrees), Vector3.One);

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static BoneTransform T(Vector3 position) => new(position, Quaternion.Identity, Vector3.One);
}
