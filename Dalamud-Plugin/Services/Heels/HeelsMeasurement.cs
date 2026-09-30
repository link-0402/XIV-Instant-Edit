using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Heels;

/// <summary>
/// A Simple Heels offset stored in a model as an attribute, which Simple Heels applies to whoever wears
/// the model: <c>heels_offset=0.05</c>, or the TexTools-safe spelling <c>heels_offset_a_af</c>, with the
/// letters a to j for digits, <c>_</c> for the point and <c>n_</c> for a minus. Simple Heels scales it by
/// the character's height.
/// </summary>
internal sealed record HeelsModelOffset(string Attribute, float Value)
{
    private const string Prefix = "heels_offset";

    /// <summary>
    /// Whether an attribute is a heels offset in any spelling, readable or not: the attributes Fix offset
    /// replaces, as Simple Heels' model editor and the Blender add-on replace them.
    /// </summary>
    public static bool IsOffset(string attribute) => attribute.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary> The first offset attribute Simple Heels can read, read the way it reads them (Plugin.CheckModelSlot). </summary>
    public static HeelsModelOffset? Find(IEnumerable<string> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.StartsWith(Prefix + "=", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(attribute[13..].Replace(',', '.'), CultureInfo.InvariantCulture, out var value))
                    return new HeelsModelOffset(attribute, value);
            }
            else if (attribute.StartsWith(Prefix + "_", StringComparison.OrdinalIgnoreCase))
            {
                var text = new StringBuilder(attribute[13..].Replace("n_", "-"));
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] is >= 'a' and <= 'j')
                        text[i] = (char)('0' + (text[i] - 'a'));
                    else if (text[i] == '_')
                        text[i] = '.';
                }
                if (float.TryParse(text.ToString(), CultureInfo.InvariantCulture, out var value))
                    return new HeelsModelOffset(attribute, value);
            }
        }
        return null;
    }

    /// <summary>
    /// The attribute Fix offset writes for an offset: <c>heels_offset=0.119</c>, with 2 to 4 decimals, as the
    /// Blender add-on writes it. Most heels mods use this spelling.
    /// </summary>
    public static string AttributeFor(float value) => $"{Prefix}={Round(value).ToString("0.00##", CultureInfo.InvariantCulture)}";

    /// <summary> An offset to type or paste into Simple Heels: four decimals, a tenth of a millimetre. </summary>
    public static string Format(float value) => Round(value).ToString("0.0000", CultureInfo.InvariantCulture);

    // Rounded to what is shown, without a minus sign on a value that rounds to zero.
    private static float Round(float value)
    {
        var rounded = MathF.Round(value, 4);
        return rounded == 0 ? 0 : rounded;
    }
}

/// <summary>
/// A model holding a character's feet, measured unanimated, in the standard pose the game's models are
/// made in: the height of its lowest drawn point above the character's origin, where the character
/// stands (in the character's model space), and the offset the model stores for Simple Heels, if any.
/// </summary>
/// <param name="OffsetAttributes">How many heels offset attributes the model has in any spelling, readable or not.</param>
internal sealed record HeelsMeasurement(float Lowest, int DrawnParts, int HiddenParts, HeelsModelOffset? ModelOffset, int OffsetAttributes = 0)
{
    /// <summary> How far to lift the character, in model units, so that the lowest point stands on the ground. </summary>
    public float Offset => -Lowest;
}

internal static class HeelsMeasure
{
    /// <summary>
    /// Measures a model as the character wears it: reshaped by <paramref name="deformer"/> when it is
    /// another race's model, without the parts <paramref name="enabledAttributes"/> turns off (null counts
    /// every part), and with the shapes <paramref name="enabledShapes"/> turns on. The ground is the model
    /// origin: the game's bare feet stand on it, and so do the offsets mod authors store (checked on the
    /// heels in a real mod library, and on dresses whose authors set the hem on the ground).
    /// </summary>
    public static HeelsMeasurement Measure(byte[] model, RacialDeformer? deformer, uint? enabledAttributes, uint enabledShapes)
    {
        if (model.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(model) != ModelInfo.V6)
            throw new NotSupportedException("The model isn't a Dawntrail model, which the measurement needs.");
        var bytes = deformer is { BoneCount: > 0 } ? RacialScalingModel.Apply(model, deformer) : model;
        var mesh = ModelMeshReader.Read(bytes, shapes: enabledShapes != 0);

        // Shapes swap some of a mesh's indices for their own vertices; later shapes win, as the game applies them in order.
        var swaps = new Dictionary<int, Dictionary<int, int>>();
        for (var s = 0; s < mesh.Shapes.Count && s < 32; s++)
        {
            if ((enabledShapes & (1u << s)) == 0)
                continue;
            foreach (var shapeMesh in mesh.Shapes[s].Meshes)
            {
                if (!swaps.TryGetValue(shapeMesh.MeshIndex, out var swap))
                    swaps[shapeMesh.MeshIndex] = swap = [];
                for (var i = 0; i < shapeMesh.Indices.Length; i++)
                    swap[shapeMesh.Indices[i]] = shapeMesh.Vertices[i];
            }
        }

        var lowest = float.PositiveInfinity;
        int drawn = 0, hidden = 0;
        foreach (var part in mesh.Meshes)
        {
            swaps.TryGetValue(part.MeshIndex, out var swap);
            foreach (var submesh in part.Submeshes)
            {
                // The game draws a part only while all of its attributes are enabled.
                if (enabledAttributes is { } enabled && (submesh.AttributeMask & ~enabled) != 0)
                {
                    hidden++;
                    continue;
                }
                drawn++;
                for (var i = 0; i < submesh.Indices.Length; i++)
                {
                    var vertex = swap is not null && swap.TryGetValue(submesh.MeshIndexStart + i, out var swapped) ? swapped : submesh.Indices[i];
                    lowest = Math.Min(lowest, part.Positions[vertex].Y);
                }
            }
        }
        if (drawn == 0 || !float.IsFinite(lowest))
            throw new InvalidDataException("No part of the model is drawn.");
        return new HeelsMeasurement(lowest, drawn, hidden, HeelsModelOffset.Find(mesh.Attributes), mesh.Attributes.Count(HeelsModelOffset.IsOffset));
    }

    /// <summary>
    /// The file behind the model a character draws in <paramref name="slot"/>, from Penumbra's resolved paths
    /// for the character (file to the game paths it serves): the file that is <paramref name="drawnFile"/> (as
    /// <see cref="PainterVisibility.NormalizePath"/> writes it), with the slot's game path, whose race code
    /// says which race the model was made for. Null when Penumbra doesn't list the file.
    /// </summary>
    public static (string ActualPath, string GamePath)? Resolve(IReadOnlyDictionary<string, HashSet<string>>? resolved, string drawnFile,
        HeelsSlot slot)
    {
        if (resolved is null)
            return null;
        (string, string)? found = null;
        foreach (var (actualPath, gamePaths) in resolved.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!actualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || PainterVisibility.NormalizePath(actualPath) != drawnFile)
                continue;
            var paths = gamePaths.Select(PathRules.NormalizeGamePath).Where(path => path.Length > 0).Order(StringComparer.Ordinal).ToList();
            if (paths.FirstOrDefault(path => path.EndsWith(slot.Suffix(), StringComparison.OrdinalIgnoreCase)) is { } slotPath)
                return (actualPath, slotPath);
            found ??= (actualPath, paths.FirstOrDefault() ?? string.Empty);
        }
        return found;
    }
}

/// <summary> The model slots that can hold a character's feet, numbered as a human's models are. </summary>
internal enum HeelsSlot
{
    Top = 1,
    Legs = 3,
    Feet = 4,
}

internal static class HeelsSlots
{
    /// <summary>
    /// Where a character's feet are: the feet model, else the legs when their gear hides the feet, else a
    /// one-piece body when its gear hides the legs too. The game draws no model in a slot gear hides.
    /// </summary>
    public static readonly IReadOnlyList<HeelsSlot> FeetOrder = [HeelsSlot.Feet, HeelsSlot.Legs, HeelsSlot.Top];

    /// <summary> The slots whose model offsets Simple Heels reads before this one's: it takes the body's, then the legs', then the feet's. </summary>
    public static IEnumerable<HeelsSlot> ReadBefore(this HeelsSlot slot) => slot switch
    {
        HeelsSlot.Feet => [HeelsSlot.Top, HeelsSlot.Legs],
        HeelsSlot.Legs => [HeelsSlot.Top],
        _ => [],
    };

    public static string Name(this HeelsSlot slot) => slot switch
    {
        HeelsSlot.Top => "body",
        HeelsSlot.Legs => "legs",
        _ => "feet",
    };

    public static string Suffix(this HeelsSlot slot) => slot switch
    {
        HeelsSlot.Top => "_top.mdl",
        HeelsSlot.Legs => "_dwn.mdl",
        _ => "_sho.mdl",
    };
}

/// <summary> What Fix offset does with a measured model. </summary>
internal enum HeelsFixAction
{
    /// <summary> The model's offset is right, or it stands on the ground and has none. </summary>
    None,
    /// <summary> The model gets <see cref="HeelsFixPlan.Attribute"/> as its only heels offset. </summary>
    Write,
    /// <summary> The model doesn't stand on the ground, so no offset can be right. </summary>
    Refuse,
}

/// <param name="Attribute">The attribute to write, for <see cref="HeelsFixAction.Write"/>.</param>
/// <param name="Reason">Why nothing is written, for the other actions.</param>
internal sealed record HeelsFixPlan(HeelsFixAction Action, string Attribute, string Reason);

internal static class HeelsFix
{
    /// <summary> Within 1 mm a model stands on the ground, and an offset it stores is right. </summary>
    public const float Tolerance = 0.001f;

    /// <summary>
    /// The highest a model's lowest point may float: the bare feet of body mods float up to 2.4 cm. Legs
    /// that end at the ankle float 14 cm, so they hold no feet.
    /// </summary>
    public const float MaxAboveGround = 0.05f;

    /// <summary> The deepest a model may reach: the deepest heels among 1,185 shoe models of a large mod library reach 21 cm. </summary>
    public const float MaxBelowGround = 0.3f;

    public static HeelsFixPlan Plan(HeelsMeasurement measurement, HeelsSlot slot)
    {
        if (measurement.Lowest > MaxAboveGround)
            return new HeelsFixPlan(HeelsFixAction.Refuse, string.Empty,
                $"The {slot.Name()} model's lowest point is {Centimetres(measurement.Lowest)} above the ground, so it doesn't stand on the ground and no offset fits it.");
        if (measurement.Lowest < -MaxBelowGround)
            return new HeelsFixPlan(HeelsFixAction.Refuse, string.Empty,
                $"The {slot.Name()} model reaches {Centimetres(-measurement.Lowest)} below the ground, deeper than any heel. Check it for stray vertices.");
        if (measurement.OffsetAttributes == 1 && measurement.ModelOffset is { } stored && MathF.Abs(stored.Value - measurement.Offset) < Tolerance)
            return new HeelsFixPlan(HeelsFixAction.None, string.Empty, $"The model's offset, {stored.Attribute}, is right already.");
        if (measurement.OffsetAttributes == 0 && MathF.Abs(measurement.Offset) < Tolerance)
            return new HeelsFixPlan(HeelsFixAction.None, string.Empty, $"The {slot.Name()} model stands on the ground already and needs no offset.");
        return new HeelsFixPlan(HeelsFixAction.Write, HeelsModelOffset.AttributeFor(measurement.Offset), string.Empty);
    }

    public static string Centimetres(float metres) => (metres * 100).ToString("0.0", CultureInfo.InvariantCulture) + " cm";
}

/// <summary> What Simple Heels calls things and reports. </summary>
internal static class SimpleHeels
{
    /// <summary>
    /// The name Simple Heels' Equipment Offsets list gives a model: the names of the items that use it, in
    /// sheet order, joined as its ShoeModel.Name joins them.
    /// </summary>
    public static string ItemName(HeelsSlot slot, ushort modelId, IReadOnlyList<string> items) => modelId == 0 && slot == HeelsSlot.Feet
        ? "Smallclothes (Barefoot)"
        : items.Count switch
        {
            0 => $"Unknown#{modelId}",
            1 => items[0],
            2 => string.Join(" and ", items),
            > 3 => $"{items[0]} & {items.Count - 1} others.",
            _ => string.Join(", ", items),
        };

    /// <summary>
    /// The offset Simple Heels gives the local player's outfit, from a SimpleHeels.GetLocalPlayer or
    /// LocalChanged message: its DefaultOffset, which it leaves out when it is zero. Null for an empty or
    /// unreadable message.
    /// </summary>
    public static float? CurrentOffset(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return document.RootElement.TryGetProperty("DefaultOffset", out var offset) && offset.ValueKind == JsonValueKind.Number
                ? offset.GetSingle()
                : 0;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
