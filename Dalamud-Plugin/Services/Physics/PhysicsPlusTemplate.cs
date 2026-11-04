using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace InstantEdit.Services.Physics;

/// <summary>
/// The fit's waistband moves as a Customize+ template to import from the clipboard: the skirt bones only, each
/// with the profile's values and the move added to its position. Customize+ takes a bone from the last template
/// in a profile that has it, so added last, the template replaces those bones and leaves the rest as they are.
/// </summary>
internal static class PhysicsPlusTemplate
{
    /// <summary>
    /// The template version written: base64 of gzip holding this byte, then the template's JSON. 4 is the first
    /// template version, which every Customize+ 2 imports.
    /// </summary>
    public const byte Version = 4;

    public const string Name = "Skirt waistband";

    public static string Encode(IReadOnlyList<PhysicsWaistMove> moves, IReadOnlyDictionary<string, PhysicsPlusBone>? profile)
    {
        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", Version);
            writer.WriteString("Name", Name);
            writer.WriteStartObject("Bones");
            foreach (var move in moves)
            {
                var bone = profile?.GetValueOrDefault(move.Bone);
                writer.WriteStartObject(move.Bone);
                Vector(writer, "Translation", (bone?.Translation ?? Vector3.Zero) + move.Translation);
                Vector(writer, "Rotation", bone?.Rotation ?? Vector3.Zero);
                Vector(writer, "Scaling", bone?.Scaling ?? Vector3.One);
                Vector(writer, "ChildScaling", bone?.ChildScaling ?? Vector3.One);
                writer.WriteBoolean("PropagateTranslation", bone?.PropagateTranslation ?? false);
                writer.WriteBoolean("PropagateRotation", bone?.PropagateRotation ?? false);
                writer.WriteBoolean("PropagateScale", bone?.PropagateScale ?? false);
                writer.WriteBoolean("ChildScalingIndependent", bone?.ChildScalingIndependent ?? false);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var packed = new MemoryStream();
        using (var zip = new GZipStream(packed, CompressionLevel.Optimal))
        {
            zip.WriteByte(Version);
            zip.Write(json.GetBuffer(), 0, (int)json.Length);
        }
        return Convert.ToBase64String(packed.ToArray());
    }

    private static void Vector(Utf8JsonWriter writer, string name, Vector3 value)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteEndObject();
    }
}
