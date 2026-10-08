using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// LivePose adjustments as an offset layer's key: what each bone's stacks add, in the terms an offset
/// key holds (see <see cref="MotionKey"/>). A stack turns the bone in its own axes after what it had,
/// moves it in character space and adds to its scale; each stack applies after the ones before it.
/// Where a stack keeps a move or a scale from its children (LivePose leaves scale to the bone alone by
/// default), the children get the opposite change, so they stay where LivePose leaves them. A turn kept
/// from the children can't be undone that way and is counted in <see cref="Result.TurnsChildren"/>.
/// IK stacks have no offset to bake and are left out. No ImGui or Dalamud dependencies.
/// </summary>
internal static class LivePoseLayer
{
    /// <summary>
    /// The key's values by bone, how many bones had adjustments, and the bones left out: missing from
    /// the skeleton, IK stacks, face bones; and the bones whose turn now carries their children along.
    /// </summary>
    internal sealed record Result(ImmutableDictionary<string, BoneTransform> Bones, int Adjusted,
        ImmutableArray<string> Missing, ImmutableArray<string> Ik, ImmutableArray<string> TurnsChildren);

    /// <summary>
    /// The offsets of <paramref name="bones"/> on <paramref name="skeleton"/>: slot 0 only, and on
    /// <paramref name="partial"/> when it is given. Bones without a change are left out.
    /// </summary>
    public static Result Offsets(IEnumerable<PoseBone> bones, SkeletonDescription skeleton, int? partial = null)
    {
        var index = MotionEvaluator.Index(skeleton);
        var children = new List<int>[skeleton.Bones.Length];
        for (var b = 0; b < skeleton.Bones.Length; b++)
            if (skeleton.Bones[b].Parent is var parent and >= 0)
                (children[parent] ??= []).Add(b);
        var turns = new Quaternion[skeleton.Bones.Length];
        var moves = new Vector3[skeleton.Bones.Length];
        var scales = new Vector3[skeleton.Bones.Length];
        var touched = new bool[skeleton.Bones.Length];
        Array.Fill(turns, Quaternion.Identity);
        Array.Fill(scales, Vector3.One);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var ik = new SortedSet<string>(StringComparer.Ordinal);
        var carried = new SortedSet<string>(StringComparer.Ordinal);
        var adjusted = 0;
        foreach (var bone in bones)
        {
            if (bone.Id.Slot != 0 || partial is { } wanted && bone.Id.Partial != wanted || bone.Stacks.IsDefaultOrEmpty)
                continue;
            if (bone.Face || !index.TryGetValue(bone.Id.Name, out var at))
            {
                missing.Add(bone.Id.Name);
                continue;
            }
            var changed = false;
            var added = Vector3.Zero;
            foreach (var stack in bone.Stacks)
            {
                if (stack.Ik.Enabled)
                {
                    ik.Add(bone.Id.Name);
                    continue;
                }
                var turn = Quaternion.Normalize(stack.Rotation);
                var turned = !Near(turn, Quaternion.Identity);
                var moved = stack.Position != Vector3.Zero;
                var scaled = stack.Scale != Vector3.Zero;
                if (!turned && !moved && !scaled)
                    continue;
                changed = true;
                touched[at] = true;
                if (turned)
                {
                    turns[at] = Quaternion.Normalize(turns[at] * turn);
                    if (!stack.Propagate.HasFlag(PoseComponents.Rotation) && children[at] is { Count: > 0 })
                        carried.Add(bone.Id.Name);
                }
                if (moved)
                {
                    moves[at] += stack.Position;
                    // Kept from the children: they move back by as much.
                    if (!stack.Propagate.HasFlag(PoseComponents.Position))
                        foreach (var child in children[at] ?? [])
                        {
                            moves[child] -= stack.Position;
                            touched[child] = true;
                        }
                }
                if (scaled)
                    added += stack.Scale;
                if (scaled && !stack.Propagate.HasFlag(PoseComponents.Scale))
                    foreach (var child in children[at] ?? [])
                        touched[child] = true;
            }
            if (added != Vector3.Zero)
            {
                // LivePose adds to the scale the bone has; an offset multiplies it by a factor.
                var factor = Vector3.Clamp(Vector3.One + added, new Vector3(MotionEvaluator.MinScale), new Vector3(MotionEvaluator.MaxScale));
                scales[at] *= factor;
                if (bone.Stacks.Any(s => !s.Ik.Enabled && s.Scale != Vector3.Zero && !s.Propagate.HasFlag(PoseComponents.Scale)))
                    foreach (var child in children[at] ?? [])
                        scales[child] /= factor;
            }
            if (changed)
                adjusted++;
        }
        var values = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        for (var b = 0; b < skeleton.Bones.Length; b++)
        {
            if (!touched[b])
                continue;
            var value = new BoneTransform(moves[b], turns[b],
                Vector3.Clamp(scales[b], new Vector3(MotionEvaluator.MinScale), new Vector3(MotionEvaluator.MaxScale)));
            if (value.Position.LengthSquared() > 1e-12f || !Near(value.Rotation, Quaternion.Identity) ||
                Vector3.DistanceSquared(value.Scale, Vector3.One) > 1e-10f)
                values[skeleton.Bones[b].Name] = value;
        }
        return new Result(values.ToImmutable(), adjusted, [.. missing], [.. ik], [.. carried]);
    }

    /// <summary>
    /// Reads a LivePose file (.livepose, TypeName "Live Pose"): its bones' stacks. IK settings are kept
    /// only as on or off. Throws <see cref="InvalidDataException"/> for anything else.
    /// </summary>
    public static ImmutableArray<PoseBone> ReadFile(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new InvalidDataException("This is not a LivePose file: " + e.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("This is not a LivePose file.");
            var bones = new List<PoseBone>();
            foreach (var entry in data.EnumerateArray())
            {
                if (!entry.TryGetProperty("BonePoseInfoId", out var id) || !id.TryGetProperty("BoneName", out var nameValue) ||
                    nameValue.GetString() is not { Length: > 0 } name)
                    throw new InvalidDataException("A bone in the LivePose file has no name.");
                var partial = id.TryGetProperty("Partial", out var p) && Int(p) is { } pi ? pi : 0;
                var slot = id.TryGetProperty("Slot", out var s) && Int(s) is { } si ? si : 0;
                var stacks = new List<PoseStack>();
                if (entry.TryGetProperty("Stacks", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var stack in list.EnumerateArray())
                        stacks.Add(ReadStack(stack, name));
                if (stacks.Count > 0)
                    bones.Add(new PoseBone(new PoseBoneId(name, partial, slot), [.. stacks]));
            }
            return [.. bones];
        }
    }

    private static PoseStack ReadStack(JsonElement stack, string bone)
    {
        if (!stack.TryGetProperty("Transform", out var transform))
            throw new InvalidDataException($"{bone} has an adjustment without a transform.");
        var position = Vector(transform, "Position", Vector3.Zero, bone);
        var scale = Vector(transform, "Scale", Vector3.Zero, bone);
        var rotation = Quaternion.Identity;
        if (transform.TryGetProperty("Rotation", out var r))
            rotation = new Quaternion(Number(r, "X", bone), Number(r, "Y", bone), Number(r, "Z", bone), Number(r, "W", bone));
        if (rotation.LengthSquared() < 1e-8f)
            rotation = Quaternion.Identity;
        // LivePose spells it "Propogate"; position and rotation reach the children by default.
        var propagate = stack.TryGetProperty("Propogate", out var flags) && Int(flags) is { } value
            ? (PoseComponents)value & PoseComponents.All
            : PoseComponents.Position | PoseComponents.Rotation;
        var ik = stack.TryGetProperty("IK_Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        return new PoseStack(position, Quaternion.Normalize(rotation), scale, propagate,
            new PoseIk(ik, 0, false, 0, 0, 0, 0, 0, Vector3.Zero));
    }

    private static Vector3 Vector(JsonElement parent, string name, Vector3 fallback, string bone) =>
        parent.TryGetProperty(name, out var value)
            ? new Vector3(Number(value, "X", bone), Number(value, "Y", bone), Number(value, "Z", bone))
            : fallback;

    private static float Number(JsonElement parent, string name, string bone)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
            throw new InvalidDataException($"{bone} has an adjustment that can't be read.");
        return number;
    }

    /// <summary>A whole number, or null for anything else (TryGetInt32 throws on a string).</summary>
    private static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool Near(Quaternion a, Quaternion b) => 1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) < 1e-7f;
}
