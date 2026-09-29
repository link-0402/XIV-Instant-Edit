using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Heels;

/// <summary>
/// A Simple Heels offset stored in a model as an attribute, which Simple Heels applies to whoever wears
/// the model: <c>heels_offset=0.05</c>, or the form TexTools and Penumbra keep, <c>heels_offset_a_af</c>,
/// with the letters a to j for digits, <c>_</c> for the point and <c>n_</c> for a minus. Simple Heels
/// scales it by the character's height.
/// </summary>
internal sealed record HeelsModelOffset(string Attribute, float Value)
{
    private const string Prefix = "heels_offset";

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

    /// <summary> The TexTools-safe attribute for an offset, as Simple Heels' Copy Attribute button writes it. </summary>
    public static string AttributeFor(float value)
    {
        var text = new StringBuilder(Prefix + "_");
        foreach (var c in Round(value).ToString("0.0###", CultureInfo.InvariantCulture).Replace("-", "n_").Replace(".", "_"))
            text.Append(c is >= '0' and <= '9' ? (char)('a' + (c - '0')) : c);
        return text.ToString();
    }

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
/// A feet model measured unanimated, in the standard pose the game's models are made in: the height of
/// its lowest drawn point above the character's origin, where the character stands (in the character's
/// model space), and the offset the model stores for Simple Heels, if any.
/// </summary>
internal sealed record HeelsMeasurement(float Lowest, int DrawnParts, int HiddenParts, HeelsModelOffset? ModelOffset)
{
    /// <summary> How far to lift the character, in model units, so that the lowest point stands on the ground. </summary>
    public float Offset => -Lowest;
}

internal static class HeelsMeasure
{
    /// <summary>
    /// Measures a feet model as the character wears it: reshaped by <paramref name="deformer"/> when it
    /// is another race's model, without the parts <paramref name="enabledAttributes"/> turns off (null
    /// counts every part), and with the shapes <paramref name="enabledShapes"/> turns on. The ground is
    /// the model origin: the game's bare feet stand on it, and so do the offsets mod authors store
    /// (checked on the heels in a real mod library).
    /// </summary>
    public static HeelsMeasurement Measure(byte[] model, RacialDeformer? deformer, uint? enabledAttributes, uint enabledShapes)
    {
        if (model.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(model) != ModelInfo.V6)
            throw new NotSupportedException("The feet model isn't a Dawntrail model, which the measurement needs.");
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
            throw new InvalidDataException("No part of the feet model is drawn.");
        return new HeelsMeasurement(lowest, drawn, hidden, HeelsModelOffset.Find(mesh.Attributes));
    }

    /// <summary>
    /// The feet model among an On Screen character's resources whose file is <paramref name="drawnFile"/>
    /// (as <see cref="PainterVisibility.NormalizePath"/> writes it): the one the character draws in its feet
    /// slot. A node with the feet slot's game path wins over a copy without one. Null when the list
    /// predates the character's current shoes.
    /// </summary>
    public static ResourceNode? FeetNode(IReadOnlyList<ResourceNode> roots, string drawnFile)
    {
        ResourceNode? found = null;
        foreach (var node in Flatten(roots))
        {
            if (!node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || PainterVisibility.NormalizePath(node.ActualPath) != drawnFile)
                continue;
            if (IsFeetPath(node.GamePath))
                return node;
            found ??= node;
        }
        return found;
    }

    /// <summary> The path whose race code says which race the model was made for: the game path, else the file's name. </summary>
    public static string RacePath(ResourceNode node)
        => node.GamePath.Length > 0 ? node.GamePath : Path.GetFileName(node.ActualPath);

    private static bool IsFeetPath(string gamePath) => gamePath.EndsWith("_sho.mdl", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ResourceNode> Flatten(IEnumerable<ResourceNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }
}

/// <summary> What Simple Heels calls things and reports, so the card can point at the entry to add. </summary>
internal static class SimpleHeels
{
    /// <summary> The Item sheet's UI category of the feet items Simple Heels lists as footwear. </summary>
    public const uint FeetCategory = 38;

    /// <summary>
    /// The name Simple Heels' Equipment Offsets list gives a feet model: the names of the feet items that
    /// use it, in sheet order, joined as its ShoeModel.Name joins them.
    /// </summary>
    public static string FeetName(ushort modelId, IReadOnlyList<string> items) => modelId == 0
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
