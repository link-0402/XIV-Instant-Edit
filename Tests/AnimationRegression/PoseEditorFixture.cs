using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Bones window without ImGui: the bone map's pictures, sides and layout, the selection's click
/// rules, edits made to every selected bone, and mirroring to the other side.
/// </summary>
internal static class PoseEditorFixture
{
    private const string Root = "n_root", Hand = "j_te_l", Twin = "j_te_r", Neck = "j_kubi";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Map(check);
        Layout(check);
        Groups(check);
        Selection(check);
        Values(check);
        Edits(check);
        Turns(check);
        Moves(check);
        Gizmo(check);
        Following(check);
        Frames(check);
        Mirrors(check);
        Poses(check);
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-4f) => Vector3.Distance(a, b) < tolerance;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static MotionTransform M(Vector3 position, Quaternion rotation, Vector3? scale = null) => new(position, rotation, scale ?? Vector3.One);

    /// <summary>
    /// A root with a neck and two hands built as mirror images: the left hand sits to the left and is
    /// turned 30 degrees about Z, the right one to the right turned the mirrored way.
    /// </summary>
    private static SkeletonDescription Rig() => new("rig", "rig-fingerprint",
    [
        new(Root, -1, 0, T(Vector3.Zero)),
        new(Neck, 0, 0, T(Vector3.UnitY)),
        new(Hand, 0, 0, T(new Vector3(1, 1, 0), Left())),
        new(Twin, 0, 0, T(new Vector3(-1, 1, 0), PoseEdits.MirrorRotation(Left()))),
    ], [], [], []);

    private static Quaternion Left() => Quaternion.Normalize(Q(Vector3.UnitZ, 30) * Q(Vector3.UnitY, 20));

    /// <summary>31 samples over one second of the rig at rest, the root turned 10 degrees about Y.</summary>
    private static MotionClip Clip()
    {
        var rig = Rig();
        var poses = Enumerable.Range(0, 31).Select(_ => rig.Bones.Select((bone, i) => i == 0 ? T(Vector3.Zero, Q(Vector3.UnitY, 10)) : bone.Reference).ToArray()).ToArray();
        return MotionClip.FromPoses(rig, 1, poses);
    }

    private static TimelineDocument Doc(MotionLayerKind kind, params MotionKey[] keys) => new(30, 30, null, [], 1)
    {
        Motion = [new("m1", kind, "L", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [], [.. keys])],
        NextMotionKey = 2,
    };

    private static MotionKey Key(int frame, string bone, BoneTransform value, bool moved = false) =>
        new(frame, ImmutableDictionary<string, BoneTransform>.Empty.WithComparers(StringComparer.Ordinal).Add(bone, value), moved ? [bone] : []);

    private static BoneTransform? At(TimelineDocument doc, int frame, string bone) =>
        doc.Motion[0].Keys.FirstOrDefault(key => key.Frame == frame)?.Bones.GetValueOrDefault(bone);

    private static void Map(Action<bool, string> check)
    {
        check(PoseMap.Mirror(Hand) == Twin && PoseMap.Mirror(Twin) == Hand && PoseMap.Mirror("j_sk_s_a_l") == "j_sk_s_a_r" &&
              PoseMap.Mirror(Neck) == null && PoseMap.Mirror("n_hara") == null && PoseMap.Mirror("_l") == null,
            "a bone's twin swaps its _l or _r ending; bones in the middle have none");
        check(PoseMap.Side(Hand) == 'l' && PoseMap.Side(Twin) == 'r' && PoseMap.Side(Neck) == '\0', "a bone's side comes from its ending");

        var body = PoseMap.Place(PoseMap.Body, false).ToList();
        var swapped = PoseMap.Place(PoseMap.Body, true).ToList();
        PoseSpot Find(List<PoseSpot> spots, string bone) => spots.Single(spot => spot.Bone == bone);
        check(Find(body, "n_hte_l").Position == new Vector2(170, 970) && Find(body, "n_hte_r").Position == new Vector2(1100, 970) &&
              Find(swapped, "n_hte_l").Position == new Vector2(1100, 970) && Find(swapped, "n_hte_r").Position == new Vector2(170, 970),
            "the left side's bones sit on the picture's left, their twins mirrored across it, and swapping sides trades them");
        check(Find(body, Neck).Position == Find(swapped, Neck).Position && Find(body, "n_hara").Handle && !Find(body, Neck).Handle,
            "bones in the middle stay put when the sides swap, and the body's handle draws no lines");
        var tail = PoseMap.Place(PoseMap.Tail, false).ToList();
        check(tail.Count == PoseMap.Tail.Spots.Length && tail.SequenceEqual(PoseMap.Tail.Spots), "the tail's picture is not mirrored");
        check(PoseMap.Sections.All(section => section.Spots.All(spot => spot.Position.X >= 0 && spot.Position.Y >= 0 &&
                  spot.Position.X <= section.Size.X && spot.Position.Y <= section.Size.Y)) &&
              PoseMap.Sections.All(section => section.Spots.Select(spot => spot.Bone).Distinct().Count() == section.Spots.Length) &&
              PoseMap.Sections.Where(section => section.Mirrored).All(section => section.Spots.All(spot => PoseMap.Side(spot.Bone) != 'r')),
            "every dot lies on its picture, once, and mirrored pictures list only the left side and the middle");
        check(PoseMap.Placed.Contains("j_ude_a_l") && PoseMap.Placed.Contains("j_ude_a_r") && PoseMap.Placed.Contains("n_sippo_e") &&
              PoseMap.Placed.Contains("iv_asi_ko_b_r") && !PoseMap.Placed.Contains("j_f_ago"),
            "the map places both sides of the body's bones and no face bones");
        check(AnimationPresentation.BoneName("j_asi_b_l") == "Left Knee (j_asi_b_l)" && AnimationPresentation.BoneName("j_asi_d_r") == "Right Foot (j_asi_d_r)" &&
              AnimationPresentation.BoneName("iv_ko_c_r") == "Right Pinky C (iv_ko_c_r)" && AnimationPresentation.BoneName("n_hara") == "Body (n_hara)" &&
              AnimationPresentation.BoneName(Hand) == "Left Hand (j_te_l)" && AnimationPresentation.BoneName("j_sk_f_b_l") == "Left Skirt Front B (j_sk_f_b_l)",
            "the map's bones have the game's body part names, with their side in front");
        var resources = typeof(PoseMap).Assembly.GetManifestResourceNames();
        check(PoseMap.Resource(PoseMap.Body) == "InstantEdit.Pose.PoseBody.png" && PoseMap.Sections.All(section => resources.Contains(PoseMap.Resource(section))),
            "every picture is embedded in the plugin under the name the map asks for");
    }

    private static void Layout(Action<bool, string> check)
    {
        var area = new Vector2(900, 600);
        var full = PoseMap.Layout(area, 6, true, true, true);
        bool Inside(PosePlacement placement) => placement.Origin.X >= -1e-3f && placement.Origin.Y >= -1e-3f &&
            placement.Origin.X + placement.Section.Size.X * placement.Scale <= area.X + 1e-3f &&
            placement.Origin.Y + placement.Section.Size.Y * placement.Scale <= area.Y + 1e-3f;
        check(full.Select(placement => placement.Section).SequenceEqual([PoseMap.Body, PoseMap.Armor, PoseMap.Hands, PoseMap.Toes, PoseMap.Tail]) &&
              full.All(Inside),
            "the body, the armor and the hands with toes and tail fill three columns inside the area");
        static float Centre(PosePlacement placement) => placement.Origin.X + placement.Section.Size.X * placement.Scale / 2;
        check(full[2].Origin.Y < full[3].Origin.Y && full[3].Origin.Y < full[4].Origin.Y &&
              Math.Abs(Centre(full[2]) - Centre(full[3])) < 1e-3f && Math.Abs(Centre(full[3]) - Centre(full[4])) < 1e-3f,
            "the hands, the toes and the tail stack down their column, centred in it");
        var plain = PoseMap.Layout(area, 6, false, false, false);
        check(plain.Count == 2 && plain[1].Section == PoseMap.Hands && plain.All(Inside) && plain[1].Origin.X > area.X / 2 - 6,
            "without armor, toes or a tail the body and the hands share two columns");
        check(Math.Abs(plain[0].Section.Size.Y * plain[0].Scale - area.Y) < 1e-3f, "the body fills the area's height when its column is wide enough");
    }

    private static void Groups(Action<bool, string> check)
    {
        PoseGroup Group(string name) => PoseMap.Groups.Single(group => group.Name == name);
        check(Group("Left fingers").Contains("j_hito_a_l") && Group("Left fingers").Contains("iv_ko_c_l") &&
              !Group("Left fingers").Contains("j_hito_a_r") && !Group("Left fingers").Contains(Hand),
            "a hand's fingers are one side's finger bones, IVCS ones included");
        check(Group("Left arm").Contains("j_ude_b_l") && Group("Left arm").Contains("n_hte_l") && Group("Left arm").Contains(Hand) &&
              !Group("Left arm").Contains("j_hito_a_l") && Group("Right arm").Contains(Twin),
            "an arm runs from the clavicle to the hand");
        check(Group("Right leg").Contains("j_asi_c_r") && Group("Right leg").Contains("iv_asi_oya_a_r") && !Group("Right leg").Contains("j_asi_c_l") &&
              Group("Spine").Contains("j_sebo_b") && Group("Neck and head").Contains(Neck) && Group("Tail").Contains("n_sippo_c") &&
              Group("Hair").Contains("j_kami_a") && Group("Skirt").Contains("j_sk_f_a_l") &&
              Group("Armor and weapons").Contains("n_buki_l") && Group("Armor and weapons").Contains("j_buki_sebo_r"),
            "the other groups take their bones by name");
    }

    private static void Selection(Action<bool, string> check)
    {
        var selection = new PoseSelection();
        selection.Click("a", false);
        selection.Click("b", true);
        check(selection.Bones.SequenceEqual(["a", "b"]) && selection.Primary == "b", "Ctrl+click adds a bone as the primary one");
        selection.Click("b", true);
        check(selection.Bones.SequenceEqual(["a"]) && selection.Primary == "a", "Ctrl+click on a selected bone takes it out, and the primary falls back");
        selection.Click("a", true);
        check(selection.Count == 0 && selection.Primary == null, "taking the last bone out leaves nothing selected");
        selection.Set(["x", "y", "z"], false);
        check(selection.Primary == "x" && selection.Count == 3, "a group in place of the selection starts on its first bone");
        selection.Set(["a"], true);
        check(selection.Primary == "x" && selection.Bones.SequenceEqual(["x", "y", "z", "a"]), "a group added keeps the primary bone");
        selection.Set(["q", "r"], false, "r");
        check(selection.Primary == "r" && selection.Bones.SequenceEqual(["q", "r"]), "a branch can name its primary bone");
        selection.Keep(bone => bone != "r");
        check(selection.Bones.SequenceEqual(["q"]) && selection.Primary == "q", "bones another skeleton lacks drop out");
        selection.Click("z", false);
        check(selection.Bones.SequenceEqual(["z"]) && selection.Primary == "z" && selection.Contains("z") && !selection.Contains("q"),
            "a plain click selects only that bone");
    }

    private static void Values(Action<bool, string> check)
    {
        var turned = T(Vector3.Zero, Q(Vector3.UnitX, 40));
        var offset = Doc(MotionLayerKind.Offset, Key(10, Hand, turned)).Motion[0];
        var own = M(Vector3.One, Q(Vector3.UnitY, 5));
        check(PoseEdits.Value(offset, Twin, 10, null) == MotionTransform.Identity && PoseEdits.Value(offset, Hand, 20, null) is { } held &&
              Degrees(held.Rotation, turned.Rotation) < 1e-3f,
            "an offset's bone shows its keys, held past the last, and no change without keys");
        var pose = offset with { Kind = MotionLayerKind.Pose };
        check(PoseEdits.Value(pose, Twin, 10, own) == own && PoseEdits.Value(pose, Twin, 10, null) == null,
            "a pose's unkeyed bone shows the clip's own transform, unknown until it is read");
        check(PoseEdits.Keyed(offset, Hand, 10) && !PoseEdits.Keyed(offset, Hand, 11) && PoseEdits.Changes(offset, Hand) && !PoseEdits.Changes(offset, Twin) &&
              PoseEdits.Changes(offset with { Kind = MotionLayerKind.Amplitude, Bones = [Twin] }, Twin),
            "a bone is keyed only at its key's frame; a layer changes the bones it keys, or an amplitude the ones it is limited to");
    }

    /// <summary>Two bones' frames, the rotations from character space to the spaces their values turn in, pointing different ways.</summary>
    private static Quaternion Space(string bone) => bone == Hand ? Q(Vector3.UnitX, 40) * Q(Vector3.UnitZ, 15) : Q(Vector3.UnitY, -60);

    private static void Edits(Action<bool, string> check)
    {
        var doc = Doc(MotionLayerKind.Offset);
        PoseTarget hand = new(Hand, M(Vector3.Zero, Q(Vector3.UnitZ, 5)), false), twin = new(Twin, M(Vector3.Zero, Q(Vector3.UnitY, 30)), false);
        var target = M(new Vector3(0, .01f, 0), Q(Vector3.UnitZ, 25) * Q(Vector3.UnitX, 10), new Vector3(1.5f));
        var both = PoseEdits.Set(doc, "m1", 15, hand, target, [twin], Space, true);
        check(At(both, 15, Hand) == target.ToBone(), "the primary bone is keyed with exactly the value set");
        // As the character sees it, both bones turn by the same rotation, whatever their own axes.
        Quaternion Change(string bone, Quaternion from, Quaternion to) => Space(bone) * to * Quaternion.Inverse(Space(bone) * from);
        check(At(both, 15, Twin) is { } other && Near(other.Position, new Vector3(0, .01f, 0)) && Near(other.Scale, new Vector3(1.5f)) &&
              Degrees(Change(Twin, twin.Value.Rotation, other.Rotation), Change(Hand, hand.Value.Rotation, target.Rotation)) < 1e-3f,
            "every other selected bone turns the same way as the character sees it, moves the same way and scales as much");
        var pose = Doc(MotionLayerKind.Pose);
        var moved = PoseEdits.Set(pose, "m1", 15, hand, target, [twin], Space, false, true);
        check(moved.Motion[0].Keys.Single().Moved.Order().SequenceEqual([Hand, Twin]), "a move in a pose layer makes every selected key set its position");
        // A pose moves in its parent's space: the twin moves the same way in character space.
        check(At(moved, 15, Twin) is { } shifted &&
              Near(Vector3.Transform(shifted.Position - twin.Value.Position, Space(Twin)), Vector3.Transform(target.Position - hand.Value.Position, Space(Hand))),
            "a pose's move goes the same way for every selected bone, each in its parent's space");

        var keyed = PoseEdits.Key(pose, "m1", 5, [hand with { Moved = true }, twin], true);
        check(At(keyed, 5, Hand) == hand.Value.ToBone() && At(keyed, 5, Twin) == twin.Value.ToBone() &&
              keyed.Motion[0].Keys.Single().Moved.SequenceEqual([Hand]),
            "Key keys every selected bone as it is, keeping whether it sets its position");
        var own = M(Vector3.UnitY, Q(Vector3.UnitX, 7));
        var reset = PoseEdits.Reset(keyed, "m1", 5, [Hand, Twin], true, bone => bone == Hand ? own : null);
        check(At(reset, 5, Hand) == own.ToBone() && At(reset, 5, Twin) == twin.Value.ToBone() && reset.Motion[0].Keys.Single().Moved.IsEmpty,
            "Reset keys a pose's bones back to the clip's own transform, leaving positions to the clip and bones whose motion is unknown alone");
        check(At(PoseEdits.Reset(both, "m1", 15, [Twin], false, _ => null), 15, Twin) == MotionTransform.Identity.ToBone(),
            "Reset keys no change in an offset");
        var unkeyed = PoseEdits.Unkey(both, "m1", 15, [Hand, Twin]);
        check(unkeyed.Motion[0].Keys.IsEmpty, "Delete key takes every selected bone out of the key, and an empty key goes");
        var twice = PoseEdits.Set(both, "m1", 25, hand, target, [], Space, true);
        check(PoseEdits.Clear(twice, "m1", [Hand]).Motion[0].Keys is var left && left.Length == 1 && !left[0].Bones.ContainsKey(Hand),
            "Clear takes the bones out of every key of the layer");
        var marked = PoseEdits.SetMoved(pose, "m1", 3, [hand, twin], true);
        check(marked.Motion[0].Keys.Single() is { Frame: 3 } setKey && setKey.Moved.Order().SequenceEqual([Hand, Twin]) && setKey.Bones.Count == 2,
            "Set position keys the selected bones where needed and marks them");
    }

    private static void Turns(Action<bool, string> check)
    {
        var doc = Doc(MotionLayerKind.Offset);
        var value = M(Vector3.Zero, Q(Vector3.UnitZ, 20));
        var turn = Q(Vector3.UnitX, 15);
        // The ball's rings on a bone's own axes: the turn about them, made in character space, follows the value.
        var axes = Space(Hand) * value.Rotation;
        var own = PoseEdits.Turn(doc, "m1", 4, [new(Hand, value, false)], PoseEdits.Around(axes, turn), Space);
        check(At(own, 4, Hand) is { } local && Degrees(local.Rotation, value.Rotation * turn) < 1e-3f, "about the bone's own axes a turn follows the value");

        // About the character's axes every bone turns the same way, each about its own joint.
        PoseTarget[] both = [new(Hand, value, false), new(Twin, M(Vector3.Zero, Q(Vector3.UnitY, 30)), false)];
        var character = PoseEdits.Turn(doc, "m1", 4, both, turn, Space);
        check(both.All(target => At(character, 4, target.Bone) is { } turned &&
                  Degrees(Space(target.Bone) * turned.Rotation, turn * Space(target.Bone) * target.Value.Rotation) < 1e-3f),
            "about the character's axes every bone ends up turned the same way in character space");
    }

    private static void Moves(Action<bool, string> check)
    {
        var move = new Vector3(.03f, -.02f, .01f);
        PoseTarget hand = new(Hand, M(new Vector3(.1f, 0, 0), Q(Vector3.UnitZ, 5)), false), twin = new(Twin, M(new Vector3(0, .2f, 0), Q(Vector3.UnitY, 30)), false);
        var offset = PoseEdits.Move(Doc(MotionLayerKind.Offset), "m1", 6, [hand, twin], move, Space, true);
        check(At(offset, 6, Hand) is { } a && Near(a.Position, hand.Value.Position + move) && a.Rotation == hand.Value.Rotation &&
              At(offset, 6, Twin) is { } b && Near(b.Position, twin.Value.Position + move) && offset.Motion[0].Keys.Single().Moved.IsEmpty,
            "an offset's move goes the same way in character space for every bone and keeps the turn");

        // A pose moves in its parent's space; the key then sets the position, from the clip's own when it didn't before.
        var own = new Vector3(.5f, .5f, .5f);
        var pose = PoseEdits.Move(Doc(MotionLayerKind.Pose), "m1", 6, [hand with { Moved = true }, twin], move, Space, false,
            bone => bone == Twin ? own : null);
        check(At(pose, 6, Hand) is { } c && Near(Vector3.Transform(c.Position - hand.Value.Position, Space(Hand)), move) &&
              At(pose, 6, Twin) is { } d && Near(Vector3.Transform(d.Position - own, Space(Twin)), move) &&
              pose.Motion[0].Keys.Single().Moved.Order().SequenceEqual([Hand, Twin]),
            "a pose's move goes the same way in character space, starts from the clip's own position and keys it");
    }

    /// <summary>The gizmo on the character: placing it, projecting it and reading a drag back in character space.</summary>
    private static void Gizmo(Action<bool, string> check)
    {
        var place = new Vector3(10, 0, 5);
        var facing = Q(Vector3.UnitY, 90);
        var world = PoseGizmo.World(place, facing, new Vector3(2));
        var point = new Vector3(1, .5f, -.25f);
        check(Near(Vector3.Transform(point, world), place + Vector3.Transform(point * 2, facing)),
            "the character's model space is scaled, turned, then moved into the world");
        var bone = PoseGizmo.Bone(new Vector3(0, 1, 0), Q(Vector3.UnitX, 30));
        check(Near(bone.Translation, Vector3.UnitY) && Degrees(PoseGizmo.Rotation(bone), Q(Vector3.UnitX, 30)) < 1e-3f &&
              Degrees(PoseGizmo.Rotation(Matrix4x4.CreateScale(3) * bone), Q(Vector3.UnitX, 30)) < 1e-3f,
            "a bone's gizmo sits on the bone, turned like it, and a scale doesn't change its rotation");

        // A camera five units in front of the middle, looking at it.
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
        var lens = Matrix4x4.CreatePerspectiveFieldOfView(1, 16 / 9f, .1f, 100);
        var game = lens;
        game.M33 = 0;
        game.M43 = .1f;
        var projection = PoseGizmo.Projection(game, .1f, 100);
        float Depth(float distance)
        {
            var clip = Vector4.Transform(new Vector4(0, 0, 5 - distance, 1), view * projection);
            return clip.Z / clip.W;
        }
        check(Math.Abs(Depth(.1f) + 1) < 1e-3f && Math.Abs(Depth(100) - 1) < 1e-3f && projection.M11 == lens.M11 && projection.M22 == lens.M22 &&
              projection.M34 == lens.M34,
            "ImGuizmo's projection gets an ordinary depth range from the near to the far plane and keeps the lens");
        var screen = new Vector2(1920, 1080);
        var origin = new Vector2(100, 50);
        check(PoseGizmo.Project(Vector3.Zero, view * projection, origin, screen, out var middle) && Near(new Vector3(middle, 0), new Vector3(origin + screen / 2, 0), 1e-2f) &&
              PoseGizmo.Project(Vector3.UnitX, view * projection, origin, screen, out var right) && right.X > middle.X && Math.Abs(right.Y - middle.Y) < 1e-2f &&
              PoseGizmo.Project(Vector3.UnitY, view * projection, origin, screen, out var up) && up.Y < middle.Y &&
              !PoseGizmo.Project(new Vector3(0, 0, 9), view * projection, origin, screen, out _),
            "a point lands on the screen where the camera sees it, right to the right and up higher, and nothing behind the camera does");

        // A turn about the character's axes (ImGuizmo's world mode) and about the bone's own (local mode).
        var from = Q(Vector3.UnitZ, 40) * Q(Vector3.UnitX, -15);
        var turn = Q(Vector3.UnitY, 25);
        var start = PoseGizmo.Bone(new Vector3(.2f, 1, 0), from);
        var (worldTurn, still) = PoseGizmo.Delta(start, PoseGizmo.Bone(new Vector3(.2f, 1, 0), turn * from));
        check(Degrees(worldTurn, turn) < 1e-3f && Near(still, Vector3.Zero), "a gizmo turn about the character's axes is that turn in character space");
        var (localTurn, _) = PoseGizmo.Delta(start, PoseGizmo.Bone(new Vector3(.2f, 1, 0), from * turn));
        check(Degrees(localTurn, PoseEdits.Around(from, turn)) < 1e-3f,
            "a gizmo turn about the bone's own axes reads back as the ball's turn about them");
        var (none, moved) = PoseGizmo.Delta(start, PoseGizmo.Bone(new Vector3(.2f, 1.5f, -.1f), from));
        check(Degrees(none, Quaternion.Identity) < 1e-3f && Near(moved, new Vector3(0, .5f, -.1f)), "a gizmo move reads back as the move in character space");
        var (back, _) = PoseGizmo.Delta(PoseGizmo.Bone(Vector3.Zero, Q(Vector3.UnitY, 170)), PoseGizmo.Bone(Vector3.Zero, Q(Vector3.UnitY, -170)));
        check(back.W >= 0 && Degrees(back, Q(Vector3.UnitY, 20)) < 1e-3f, "a turn reads back the short way, with one sign");
    }

    /// <summary>A chain root, a, b, c with a side bone x on the root: the bones an edit changes when several are selected.</summary>
    private static void Following(Action<bool, string> check)
    {
        var chain = new SkeletonDescription("chain", "chain-print",
        [
            new(Root, -1, 0, T(Vector3.Zero)), new("j_a", 0, 0, T(Vector3.UnitY)), new("j_b", 1, 0, T(Vector3.UnitY)),
            new("j_c", 2, 0, T(Vector3.UnitY)), new("j_x", 0, 0, T(Vector3.UnitX)),
        ], [], [], []);
        PoseTarget[] all = [.. new[] { "j_a", "j_b", "j_c", "j_x" }.Select(bone => new PoseTarget(bone, MotionTransform.Identity, false))];
        string[] Turned(string primary) => [.. PoseEdits.Turned(all, primary, chain).Select(target => target.Bone)];
        check(Turned("j_a").SequenceEqual(["j_a", "j_x"]) && Turned("j_x").SequenceEqual(["j_a", "j_x"]),
            "bones below a changed selected bone follow it instead of turning twice");
        check(Turned("j_b").SequenceEqual(["j_b", "j_x"]) && Turned("j_c").SequenceEqual(["j_c", "j_x"]),
            "the primary bone always changes, and the selected bones above it stay");
        check(PoseEdits.Turned([all[0]], "j_a", chain).Count == 1, "one bone alone is the bone that changes");
    }

    /// <summary>One pose with the layers applied at a sample's frame matches the whole clip's bake at that sample.</summary>
    private static void Frames(Action<bool, string> check)
    {
        var rig = Rig();
        var clip = Clip();
        var twist = T(new Vector3(.02f, 0, 0), Q(Vector3.UnitX, 35)) with { Scale = new Vector3(1.2f) };
        ImmutableArray<MotionLayer> layers =
        [
            new("m1", MotionLayerKind.Offset, "O", 3, 27, 6, 6, .8f, PoseComponents.All, MotionInterpolation.Smooth, [],
                [Key(5, Neck, T(Vector3.Zero, Q(Vector3.UnitZ, 20))), Key(10, Hand, twist), Key(20, Neck, T(Vector3.Zero, Q(Vector3.UnitZ, -10)))]),
            new("m2", MotionLayerKind.Pose, "P", 0, 30, 0, 10, 1, PoseComponents.All, MotionInterpolation.Linear, [],
                [Key(0, Twin, T(new Vector3(-1, 1.2f, 0), Q(Vector3.UnitY, 45)), true)]),
            new("m3", MotionLayerKind.Amplitude, "A", 0, 30, 0, 0, 2, PoseComponents.Rotation, MotionInterpolation.Linear),
        ];
        var baked = clip.Clone();
        MotionEvaluator.Apply(baked, [.. layers.Take(2)]);
        var same = true;
        for (var i = 0; i < clip.Samples; i += 3)
        {
            var at = MotionEvaluator.ApplyAt(rig, clip.Pose(clip.Frame(i)), [.. layers.Take(2)], clip.Frame(i));
            for (var b = 0; b < rig.Bones.Length; b++)
            {
                var expected = baked.Get(i, b);
                same &= Degrees(at[b].Rotation, expected.Rotation) < 1e-2f && Near(at[b].Position, expected.Position, 1e-3f) && Near(at[b].Scale, expected.Scale, 1e-3f);
            }
        }
        check(same, "one pose with the offset and pose layers applied at a frame is the bake's sample there");
        var pose = clip.Pose(12);
        var amplified = MotionEvaluator.ApplyAt(rig, pose, [layers[2]], 12);
        check(amplified.SequenceEqual(pose), "a single pose leaves amplitudes out: they need their whole section");
    }

    private static void Mirrors(Action<bool, string> check)
    {
        check(Degrees(PoseEdits.MirrorRotation(Q(Vector3.UnitY, 30)), Q(Vector3.UnitY, -30)) < 1e-3f &&
              Degrees(PoseEdits.MirrorRotation(Q(Vector3.UnitZ, 30)), Q(Vector3.UnitZ, -30)) < 1e-3f &&
              Degrees(PoseEdits.MirrorRotation(Q(Vector3.UnitX, 30)), Q(Vector3.UnitX, 30)) < 1e-3f &&
              PoseEdits.MirrorPosition(new Vector3(1, 2, 3)) == new Vector3(-1, 2, 3),
            "mirrored across the middle, turns to the side and twists reverse, nods stay, and moves to the side flip");

        // The hands are mirror images, so the twin's offset must leave it the mirror image of the turned hand.
        var left = Left();
        var right = PoseEdits.MirrorRotation(left);
        var offset = M(new Vector3(.02f, .01f, 0), Q(Vector3.UnitX, 25) * Q(Vector3.UnitY, -15), new Vector3(1.2f, 1, 1));
        var twin = PoseEdits.MirrorOffset(offset, left, right, Quaternion.Identity);
        check(Degrees(right * twin.Rotation, PoseEdits.MirrorRotation(left * offset.Rotation)) < 1e-2f &&
              twin.Position == PoseEdits.MirrorPosition(offset.Position) && twin.Scale == offset.Scale,
            "a mirrored offset turns and moves the twin as the mirror image of the bone");
        var body = Q(Vector3.UnitY, 70);
        var turned = PoseEdits.MirrorOffset(offset, body * left, body * right, body);
        check(Degrees(body * right * turned.Rotation, body * PoseEdits.MirrorRotation(left * offset.Rotation)) < 1e-2f &&
              Near(Vector3.Transform(turned.Position, Quaternion.Inverse(body)), PoseEdits.MirrorPosition(Vector3.Transform(offset.Position, Quaternion.Inverse(body)))),
            "the middle the copy mirrors across turns with the body");

        var rig = Rig();
        var clip = Clip();
        var doc = Doc(MotionLayerKind.Offset, Key(0, Hand, offset.ToBone()), Key(30, Twin, T(Vector3.UnitZ)));
        doc = MotionEdits.SetKey(doc, "m1", 15, Hand, offset.ToBone());
        var mirrored = PoseEdits.Mirror(doc, "m1", [(Hand, Twin)], rig, frame => clip.Pose(frame));
        check(mirrored != null && mirrored.Motion[0].Keys.Where(key => key.Bones.ContainsKey(Twin)).Select(key => key.Frame).SequenceEqual([0, 15]),
            "Mirror copies every key of the bone to its twin and replaces the twin's own keys");
        // The clip turns the root, the bone above both hands: the copy mirrors across the turned middle.
        var model = MotionEvaluator.ModelSpace(rig, clip.Pose(0));
        var root = Quaternion.Inverse(model[0].Rotation);
        check(mirrored != null && At(mirrored, 0, Twin) is { } copied &&
              Degrees(root * model[3].Rotation * copied.Rotation, PoseEdits.MirrorRotation(root * model[2].Rotation * offset.Rotation)) < 1e-2f,
            "each copy is read against the motion at its key's frame");
        var amplitude = doc with { Motion = [doc.Motion[0] with { Kind = MotionLayerKind.Amplitude }] };
        check(PoseEdits.Mirror(doc, "m1", [(Hand, Twin)], rig, _ => null) == null &&
              ReferenceEquals(PoseEdits.Mirror(amplitude, "m1", [(Hand, Twin)], rig, _ => null), amplitude),
            "Mirror waits for the motion, and an amplitude has no keys to mirror");
        check(PoseEdits.CommonAncestor(rig, 2, 3) == 0 && PoseEdits.CommonAncestor(rig, 1, 2) == 0 && PoseEdits.CommonAncestor(rig, 0, 2) == -1,
            "the middle is the nearest bone above both sides");

        var all = new[] { Hand, Twin, Neck };
        bool Exists(string bone) => rig.Bones.Any(b => b.Name == bone);
        check(PoseEdits.MirrorPairs([Hand], Hand, Exists).SequenceEqual([(Hand, Twin)]) &&
              PoseEdits.MirrorPairs(all, Twin, Exists).SequenceEqual([(Twin, Hand)]) &&
              PoseEdits.MirrorPairs(all, Neck, Exists).SequenceEqual([(Hand, Twin)]) &&
              PoseEdits.MirrorPairs([Neck], Neck, Exists).Count == 0 &&
              PoseEdits.MirrorPairs(["j_ude_a_l"], "j_ude_a_l", Exists).Count == 0,
            "Mirror copies from the primary bone's side, from the left when it is in the middle, and only to twins the skeleton has");
    }

    private static void Poses(Action<bool, string> check)
    {
        // A pose key for the left hand, read against the clip's own pose: the twin gets the mirror image.
        var rig = Rig();
        var clip = Clip();
        var local = clip.Pose(10);
        var model = MotionEvaluator.ModelSpace(rig, local);
        var key = M(local[2].Position + new Vector3(.1f, 0, .05f), local[2].Rotation * Q(Vector3.UnitX, 30));
        var twin = PoseEdits.MirrorPose(key, local[2], model[0].Rotation, local[3], model[0].Rotation, model[0].Rotation);
        var keyed = MotionEvaluator.Compose(model[0], key);
        var copied = MotionEvaluator.Compose(model[0], twin);
        var root = model[0].Rotation;
        // In the root's frame both rest hands are mirror images, so the keyed hand and its copy must be too.
        var inverse = Quaternion.Inverse(root);
        check(Degrees(inverse * copied.Rotation, PoseEdits.MirrorRotation(inverse * keyed.Rotation)) < 1e-2f,
            "a mirrored pose key turns the twin as the mirror image of the keyed bone");
        check(Near(Vector3.Transform(copied.Position - model[0].Position, inverse), PoseEdits.MirrorPosition(Vector3.Transform(keyed.Position - model[0].Position, inverse)), 1e-3f),
            "a mirrored pose key moves the twin as the mirror image of the keyed bone");

        // A clip's pose at a frame lies between its two nearest samples.
        var slide = MotionClip.FromPoses(new SkeletonDescription("one", "one-print", [new(Root, -1, 0, T(Vector3.Zero))], [], [], []), 1,
            [[T(Vector3.Zero)], [T(Vector3.UnitX)]]);
        check(Near(slide.Pose(15)[0].Position, new Vector3(.5f, 0, 0)) && slide.Pose(-5)[0].Position == Vector3.Zero &&
              slide.Pose(1000)[0].Position == Vector3.UnitX && slide.Pose(float.NaN)[0].Position == Vector3.Zero,
            "a clip's pose at a frame is interpolated between samples and held at its ends");
    }
}
