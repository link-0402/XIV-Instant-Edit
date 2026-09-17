using System.Text;
using InstantEdit.Services.Animations;

internal static class AnimationFaceFixture
{
    /// <summary>
    /// A body PAP whose timeline names its own motion plus an external facial one,
    /// mirroring the documented pose01_loop case: the body motion cbem_pose01_2lp
    /// lives in the file, while cfxf_bad is played from the character's face pack.
    /// </summary>
    private static byte[] BodyPap(string body, string face)
    {
        static byte[] Timeline(string path)
        {
            var text = Encoding.UTF8.GetBytes(path + "\0");
            var bytes = new byte[12 + 24 + text.Length];
            "TMLB"u8.CopyTo(bytes);
            BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 4);
            BitConverter.GetBytes(1).CopyTo(bytes, 8);
            Encoding.ASCII.GetBytes("C009").CopyTo(bytes, 12);
            BitConverter.GetBytes(24).CopyTo(bytes, 16);
            BitConverter.GetBytes(16).CopyTo(bytes, 32);
            text.CopyTo(bytes, 36);
            return bytes;
        }
        var first = Timeline(body);
        var second = Timeline(face);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)2);
        writer.Write((short)0x0801); writer.Write((byte)0); writer.Write((byte)0);
        var offsets = stream.Position;
        writer.Write(0); writer.Write(0); writer.Write(0);
        var info = (int)stream.Position;
        // Only the body motion is an entry in this file; the face is played elsewhere.
        foreach (var (name, face_) in new[] { (body, 0), ("cbfc_unused", 1) })
        {
            var text = Encoding.UTF8.GetBytes(name);
            writer.Write(text); writer.Write(new byte[32 - text.Length]);
            writer.Write((short)7); writer.Write((short)(name == body ? 0 : 1)); writer.Write(face_);
        }
        var havok = (int)stream.Position;
        writer.Write(new byte[] { 0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10 });
        var timeline = (int)stream.Position;
        writer.Write(first);
        writer.Write(new byte[(-first.Length) & 3]);
        writer.Write(second);
        stream.Position = offsets;
        writer.Write(info); writer.Write(havok); writer.Write(timeline);
        return stream.ToArray();
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var pap = BodyPap("cbem_pose01_2lp", "cfxf_bad");

        // The body motion is an entry in the file; the facial one is not, which is
        // exactly what distinguishes them.
        var external = AnimationFaces.ExternalMotions(pap);
        check(external.Length == 1 && external[0] == "cfxf_bad",
            "the facial motion an animation plays is the one its timeline names but the file does not contain");

        var swapped = AnimationFaces.Retarget(pap, "cfxf_bad", "cfxf_angry");
        var references = AnimationDependencies.Read("x.pap", swapped).References.Select(r => r.Path).ToArray();
        check(references.Contains("cfxf_angry") && !references.Contains("cfxf_bad") &&
              references.Contains("cbem_pose01_2lp") &&
              new AnimationPap(swapped).Entries.SequenceEqual(new AnimationPap(pap).Entries) &&
              new AnimationPap(swapped).Havok.SequenceEqual(new AnimationPap(pap).Havok),
            "attaching an expression repoints only the facial reference, leaving the body motion, entries and motion data alone");
        check(AnimationFaces.ExternalMotions(swapped) is ["cfxf_angry"],
            "the attached expression becomes the animation's facial motion");

        var shorter = AnimationFaces.Retarget(pap, "cfxf_bad", "cfxf_x");
        check(shorter.Length == pap.Length &&
              AnimationDependencies.Read("x.pap", shorter).References.Select(r => r.Path).Contains("cfxf_x"),
            "a shorter expression name is written in place");
        var longer = AnimationFaces.Retarget(pap, "cfxf_bad", "cfxf_surprised_hard");
        check(longer.Length > pap.Length &&
              AnimationDependencies.Read("x.pap", longer).References.Select(r => r.Path).Contains("cfxf_surprised_hard") &&
              AnimationDependencies.Read("x.pap", longer).Problems.IsEmpty,
            "a longer expression name grows the timeline and still walks cleanly to the end of the PAP");

        check(AnimationFaces.Retarget(pap, "cfxf_bad", "cfxf_bad").SequenceEqual(pap),
            "attaching the expression already playing rewrites nothing");
        reject(() => AnimationFaces.Retarget(pap, "cfxf_missing", "cfxf_angry"),
            "attaching over an expression this animation does not play is refused");
        reject(() => AnimationFaces.Retarget(pap, "cfxf_bad", "cbem_pose01_2lp"),
            "an animation cannot be repointed at its own body motion as if it were a face");
        reject(() => AnimationFaces.Retarget(pap, "cfxf_bad", "cfxf/bad"),
            "an unsafe expression name is refused");

        // An animation with no external reference has no face to swap.
        var faceless = BodyPap("cbem_pose01_2lp", "cbem_pose01_2lp");
        check(AnimationFaces.ExternalMotions(faceless).IsEmpty,
            "an animation that names only its own motion reports no facial expression");
        reject(() => AnimationFaces.Retarget(faceless, "cfxf_bad", "cfxf_angry"),
            "an animation without a facial reference cannot have one attached");

        check(AnimationCatalog.FacialName("facial/pose/bad") == "Bad" &&
              AnimationCatalog.FacialName("facial/pose/angry_hard") == "Angry Hard" &&
              AnimationCatalog.FacialName("other/key") == "Other/key",
            "facial expression labels are derived from their timeline key");
    }
}
