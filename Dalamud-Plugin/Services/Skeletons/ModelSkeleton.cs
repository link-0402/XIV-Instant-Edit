using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services.Skeletons;

/// <summary>The extra skeletons the game picks per race and set through its EST tables.</summary>
internal enum EstSlot { Face, Hair, Head, Body }

/// <summary>
/// The skeleton family of a model, read from its game file name: a race (<c>c0101</c>), demihuman,
/// monster or weapon ID, the kind of model (<c>e</c>quipment, <c>a</c>ccessory, <c>f</c>ace,
/// <c>h</c>air, <c>b</c>ody, <c>t</c>ail, <c>z</c>ear), its set and its slot.
/// </summary>
internal sealed record ModelSkeletonKey(string Id, char Kind, int Set, string Slot)
{
    public bool Human => Id[0] == 'c';
    public ushort GenderRace => ushort.Parse(Id.AsSpan(1));
}

/// <summary>An extra skeleton picked through an EST table, looked up by <paramref name="Set"/>.</summary>
internal sealed record EstRequest(EstSlot Slot, int Set, string? Note = null);

internal static partial class ModelSkeletonPaths
{
    [GeneratedRegex(@"^(?<id>[cdmw]\d{4})(?<kind>[abefhtz])(?<set>\d{4})(?:_(?<slot>[a-z]{3}))?\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelFileName();

    /// <summary>The skeleton family of a model game path or file name, or null for other models.</summary>
    public static ModelSkeletonKey? Parse(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || modelPath.Length > 512) return null;
        var name = modelPath.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var match = ModelFileName().Match(name);
        if (!match.Success) return null;
        var id = match.Groups["id"].Value.ToLowerInvariant();
        var kind = char.ToLowerInvariant(match.Groups["kind"].Value[0]);
        var valid = id[0] switch
        {
            'c' => true,
            'd' => kind == 'e',
            'm' or 'w' => kind == 'b',
            _ => false,
        };
        return valid
            ? new ModelSkeletonKey(id, kind, int.Parse(match.Groups["set"].Value), match.Groups["slot"].Value.ToLowerInvariant())
            : null;
    }

    private static string Folder(ModelSkeletonKey key) => key.Id[0] switch
    {
        'c' => "human",
        'd' => "demihuman",
        'm' => "monster",
        _ => "weapon",
    };

    /// <summary>The model's base skeleton: a race's body, or a demihuman's, monster's or weapon's own.</summary>
    public static string BasePath(ModelSkeletonKey key)
        => $"chara/{Folder(key)}/{key.Id}/skeleton/base/b0001/skl_{key.Id}b0001.sklb";

    /// <summary>The EST table and set that pick the model's extra skeleton, if its kind has one.</summary>
    public static EstRequest? Extra(ModelSkeletonKey key)
    {
        if (!key.Human) return null;
        return key.Kind switch
        {
            'f' => new(EstSlot.Face, key.Set),
            'h' => new(EstSlot.Hair, key.Set),
            'e' when key.Slot == "met" => new(EstSlot.Head, key.Set),
            'e' when key.Slot == "top" => new(EstSlot.Body, key.Set),
            // Viera ear bones belong to the face skeleton; the model doesn't say which face.
            'z' => new(EstSlot.Face, 1, "The ears use face 1's skeleton."),
            _ => null,
        };
    }

    public static string PartialPath(string race, EstSlot slot, int skeleton)
    {
        var (folder, letter) = slot switch
        {
            EstSlot.Face => ("face", 'f'),
            EstSlot.Hair => ("hair", 'h'),
            EstSlot.Head => ("met", 'm'),
            _ => ("top", 't'),
        };
        return $"chara/human/{race}/skeleton/{folder}/{letter}{skeleton:D4}/skl_{race}{letter}{skeleton:D4}.sklb";
    }
}

/// <summary>
/// An EST table (<c>chara/xls/charadb/*.est</c>): a count, then that many (set, gender-race) pairs of
/// 16-bit values, then one 16-bit skeleton ID per pair. A set without an entry has no extra skeleton.
/// </summary>
internal sealed class EstTable
{
    private readonly Dictionary<(ushort GenderRace, ushort Set), ushort> entries;

    private EstTable(Dictionary<(ushort, ushort), ushort> entries) => this.entries = entries;

    public int Count => entries.Count;

    public static string GamePath(EstSlot slot) => slot switch
    {
        EstSlot.Face => "chara/xls/charadb/faceskeletontemplate.est",
        EstSlot.Hair => "chara/xls/charadb/hairskeletontemplate.est",
        EstSlot.Head => "chara/xls/charadb/extra_met.est",
        _ => "chara/xls/charadb/extra_top.est",
    };

    public static EstTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new InvalidDataException("The EST table is truncated.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || 4L + count * 6L > bytes.Length) throw new InvalidDataException("The EST table is truncated.");
        var entries = new Dictionary<(ushort, ushort), ushort>(count);
        for (var i = 0; i < count; i++)
        {
            var set = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + i * 4)..]);
            var genderRace = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(6 + i * 4)..]);
            entries[(genderRace, set)] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + count * 4 + i * 2)..]);
        }
        return new EstTable(entries);
    }

    /// <summary>The skeleton ID for a gender-race code (101 for <c>c0101</c>) and set, 0 for none.</summary>
    public ushort this[ushort genderRace, int set]
        => set is >= 0 and <= ushort.MaxValue && entries.TryGetValue((genderRace, (ushort)set), out var value) ? value : (ushort)0;

    /// <summary>
    /// The entry a collection's EST manipulations (Penumbra's metadata, decoded by
    /// <see cref="AnimationMetadata.Decode"/>) set for a slot, gender-race and set, if any.
    /// </summary>
    public static ushort? Override(JsonArray manipulations, EstSlot slot, ushort genderRace, int set)
    {
        (string Gender, string Race) names;
        try { names = AnimationMetadata.GenderRaceNames(genderRace); }
        catch (InvalidDataException) { return null; }
        var slotName = slot.ToString();
        ushort? result = null;
        foreach (var node in manipulations)
        {
            try
            {
                if (node is not JsonObject item || item["Type"]?.GetValue<string>() != "Est" ||
                    item["Manipulation"] is not JsonObject manipulation)
                    continue;
                if (manipulation["Slot"]?.GetValue<string>() != slotName || manipulation["SetId"]?.GetValue<int>() != set ||
                    manipulation["Gender"]?.GetValue<string>() != names.Gender || manipulation["Race"]?.GetValue<string>() != names.Race)
                    continue;
                if (manipulation["Entry"]?.GetValue<int>() is int entry and >= 0 and <= ushort.MaxValue)
                    result = (ushort)entry;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                // An entry of another shape is not an EST override this lookup understands.
            }
        }
        return result;
    }
}

/// <summary>The header of a skeleton file (<c>.sklb</c>) that precedes its Havok data.</summary>
internal static class SkeletonFileHeader
{
    /// <summary>
    /// The body bone index a partial skeleton file names as the bone it hangs from, or null. Version
    /// "0031" files keep one index at 0x10 (0xFFFF for none: body skeletons), version "1031" files a
    /// list of four at 0x28. Hair skeletons name <c>j_kao</c>, the vanilla top skeletons <c>j_sebo_c</c>.
    /// </summary>
    public static int? ConnectBone(ReadOnlySpan<byte> file)
    {
        if (file.Length < 0x30 || !file.StartsWith("blks"u8) || file[6] != (byte)'3' || file[7] != (byte)'1')
            return null;
        if (file[4] == (byte)'1')
        {
            for (var i = 0; i < 4; i++)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(file[(0x28 + 2 * i)..]);
                if (value != ushort.MaxValue) return value;
            }
            return null;
        }
        var index = BinaryPrimitives.ReadUInt16LittleEndian(file[0x10..]);
        return index == ushort.MaxValue ? null : index;
    }
}

/// <summary>
/// One of a character's Havok skeletons: the body first, then partial skeletons (face, hair,
/// headgear, top). <paramref name="ConnectedParent"/> is the body bone a partial skeleton hangs
/// from, -1 when unknown.
/// </summary>
internal sealed record SkeletonPart(string Path, SkeletonDescription Skeleton, int ConnectedParent = -1);

/// <summary>A model's game skeleton, as Blender builds its armature from it.</summary>
internal sealed record ModelSkeleton(string Source, string Race, ImmutableArray<string> Skeletons,
    ImmutableArray<AnimationTakeBone> Bones, ImmutableArray<string> Warnings)
{
    public const string Schema = "instant-edit.skeleton";
    public const int Version = 1;
    public const string CharacterSource = "character";
    public const string FilesSource = "files";

    public ModelSkeletonPayload ToPayload() => new(Schema, Version, Source, Race, Skeletons, Warnings,
        Bones.Select(bone =>
        {
            var reference = new float[AnimationTake.Stride];
            AnimationTake.Write(bone.Reference, reference);
            if (reference.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException($"Bone {bone.Name} has a reference pose that is not finite.");
            return new ModelSkeletonPayloadBone(bone.Name, bone.Parent, reference);
        }).ToArray());
}

/// <summary>
/// The <c>skeleton</c> field of an import request: bones in hierarchy order with their parent index
/// (-1 for roots) and reference transform relative to it (translation, rotation x/y/z/w, scale),
/// in the game's Y-up model space, as in an animation take. See Blender-Addon/instant_edit/skeleton.py.
/// </summary>
public sealed record ModelSkeletonPayload(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("race")] string Race,
    [property: JsonPropertyName("skeletons")] IReadOnlyList<string> Skeletons,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("bones")] IReadOnlyList<ModelSkeletonPayloadBone> Bones);

public sealed record ModelSkeletonPayloadBone(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parent")] int Parent,
    [property: JsonPropertyName("reference")] float[] Reference);

/// <summary>
/// Merges a character's skeletons into one hierarchy. A partial skeleton's bone that has the name of
/// a bone already merged is that bone (a face skeleton's <c>j_kao</c> is the body's). Other roots hang
/// from the body bone the partial skeleton connects to. Every new bone keeps its place in its own
/// skeleton's model space, so a bone hanging from another skeleton's bone gets its transform relative
/// to that bone; within a skeleton, reference transforms are kept as they are.
/// </summary>
internal static class ModelSkeletonMerge
{
    private readonly record struct Rigid(Quaternion Rotation, Vector3 Position);

    // Model space from rotations and positions alone, as Blender's rest bones, which hold no scale.
    private static Rigid[] ModelSpace(ImmutableArray<SkeletonBone> bones)
    {
        var result = new Rigid[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var reference = bones[i].Reference;
            var rotation = Quaternion.Normalize(reference.Rotation);
            var parent = bones[i].Parent;
            result[i] = parent < 0
                ? new Rigid(rotation, reference.Position)
                : new Rigid(Quaternion.Normalize(result[parent].Rotation * rotation),
                    result[parent].Position + Vector3.Transform(reference.Position, result[parent].Rotation));
        }
        return result;
    }

    private static BoneTransform Relative(Rigid parent, Rigid child, Vector3 scale)
    {
        var inverse = Quaternion.Conjugate(parent.Rotation);
        return new BoneTransform(Vector3.Transform(child.Position - parent.Position, inverse),
            Quaternion.Normalize(inverse * child.Rotation), scale);
    }

    public static ImmutableArray<AnimationTakeBone> Merge(IReadOnlyList<SkeletonPart> parts, ICollection<string> warnings)
    {
        if (parts.Count == 0) throw new InvalidDataException("A skeleton needs a body.");
        var bones = ImmutableArray.CreateBuilder<AnimationTakeBone>();
        var models = new List<Rigid>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var bodyBones = parts[0].Skeleton.Bones.Length;
        for (var p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var source = part.Skeleton.Bones;
            var own = ModelSpace(source);
            var map = new int[source.Length];
            var added = new bool[source.Length];
            var loose = 0;
            for (var i = 0; i < source.Length; i++)
            {
                var bone = source[i];
                if (byName.TryGetValue(bone.Name, out var existing))
                {
                    map[i] = existing;
                    continue;
                }
                int parent;
                BoneTransform reference;
                if (bone.Parent >= 0 && added[bone.Parent])
                {
                    parent = map[bone.Parent];
                    reference = bone.Reference;
                }
                else
                {
                    parent = bone.Parent >= 0 ? map[bone.Parent]
                        : p > 0 && part.ConnectedParent >= 0 && part.ConnectedParent < bodyBones ? part.ConnectedParent : -1;
                    // A root's reference transform is its place in model space.
                    reference = parent < 0 ? bone.Reference : Relative(models[parent], own[i], bone.Reference.Scale);
                    if (p > 0 && parent < 0) loose++;
                }
                map[i] = bones.Count;
                added[i] = true;
                byName[bone.Name] = bones.Count;
                bones.Add(new AnimationTakeBone(bone.Name, parent, reference));
                models.Add(own[i]);
            }
            if (loose > 0)
                warnings.Add($"{Path.GetFileName(part.Path)} has {loose} root bone{(loose == 1 ? "" : "s")} that no body bone connects.");
        }
        if (bones.Count > AnimationTake.MaximumBones)
            throw new InvalidDataException($"The merged skeleton has {bones.Count} bones, more than {AnimationTake.MaximumBones}.");
        return bones.ToImmutable();
    }
}
