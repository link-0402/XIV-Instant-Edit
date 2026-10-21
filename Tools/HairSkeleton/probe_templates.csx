// Custom hair skeletons: the vanilla hair skeleton each player race starts from (HairSkeletonIds.TemplateId),
// and what HairSkeletonWriter keeps of it: header, Havok offset, bone 0, float slots, partitions, and the
// rest of the container. Reads game data only (no Penumbra overrides) and changes nothing.
// xiv_exec file=Tools/HairSkeleton/probe_templates.csx
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using BoneTransform = InstantEdit.Models.BoneTransform;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Skeletons;

var est = EstTable.Parse(Data.GetFile(EstTable.GamePath(EstSlot.Hair))!.Data);

// Everything in the file besides the main skeleton's bones: what Write must leave alone or refuse.
static unsafe object Container(byte[] sklb)
{
    using var doc = new AnimationNative.Document(AnimationPap.SkeletonHavok(sklb));
    var c = doc.Container;
    var variants = new List<string>();
    for (var i = 0; i < doc.Root->NamedVariants.Length; i++)
        variants.Add($"{doc.Root->NamedVariants[i].Name.String}: {doc.Root->NamedVariants[i].ClassName.String}");
    return new
    {
        skeletons = c->Skeletons.Length, animations = c->Animations.Length, bindings = c->Bindings.Length,
        attachments = c->Attachments.Length, skins = c->Skins.Length,
        localFrames = c->Skeletons[0].ptr->LocalFrames.Length, variants,
    };
}

var races = new List<object>();
var problems = new List<string>();
for (var code = 1; code <= 18; code++)
{
    var genderRace = (ushort)(code * 100 + 1);
    var race = $"c{genderRace:D4}";
    if (HairSkeletonIds.TemplateId(est, genderRace, Data.FileExists) is not ushort template)
    {
        problems.Add($"{race}: no template");
        races.Add(new { race });
        continue;
    }
    var path = ModelSkeletonPaths.PartialPath(race, EstSlot.Hair, template);
    try
    {
        var sklb = Data.GetFile(path)!.Data;
        var skeleton = AnimationSkeleton.Inspect(sklb);
        var bone0 = skeleton.Bones[0];
        if (bone0.Name != "j_kao" || bone0.Parent != -1) problems.Add($"{race}: bone 0 is {bone0.Name} (parent {bone0.Parent})");

        // A dry run of the writer: j_kao plus one bone 10 cm below it, read back by Write itself.
        string write;
        try
        {
            var kao = HairSkeletonWriter.TemplateKao(sklb);
            var below = new SkeletonBone("j_ex_hair_01_a", 0, 0, new BoneTransform(new Vector3(0, -0.1f, 0), Quaternion.Identity, Vector3.One));
            var written = HairSkeletonWriter.Write(sklb, [kao, below]);
            var after = AnimationSkeleton.Inspect(written);
            write = $"ok: {written.Length} bytes, {after.Bones.Length} bones, {after.FloatNames.Length} floats, " +
                $"partitions [{string.Join(", ", after.Partitions.Select(p => $"{p.Name} {p.Start}+{p.Count}"))}]";
        }
        catch (Exception e) { write = $"{e.GetType().Name}: {e.Message}"; problems.Add($"{race}: Write failed: {e.Message}"); }

        races.Add(new
        {
            race, template = (int)template, path, size = sklb.Length,
            header4to7 = Convert.ToHexString(sklb.AsSpan(4, 4)),
            havokOffset = sklb.Length - AnimationPap.SkeletonHavok(sklb).Length,
            int32At12 = BinaryPrimitives.ReadInt32LittleEndian(sklb.AsSpan(12)),
            skeletonName = skeleton.Name,
            bone0 = new { bone0.Name, bone0.Parent },
            bones = skeleton.Bones.Length,
            floats = skeleton.FloatNames.Length,
            partitions = skeleton.Partitions.Select(p => new { p.Name, p.Start, p.Count }).ToArray(),
            container = Container(sklb),
            write,
        });
    }
    catch (Exception e)
    {
        problems.Add($"{race}: {path}: {e.GetType().Name}: {e.Message}");
        races.Add(new { race, template = (int)template, path });
    }
}

new { ok = problems.Count == 0, problems, races }
