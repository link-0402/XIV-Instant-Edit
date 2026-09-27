using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> A model the character has loaded, and which of its attributes are enabled. </summary>
/// <param name="Path">The loaded file as <see cref="PainterVisibility.NormalizePath"/> writes it.</param>
public sealed record PainterLiveModel(string Path, uint EnabledAttributes);

/// <summary> What an on-screen character draws right now: its loaded models and its own colors. </summary>
public sealed record PainterLiveCharacter(IReadOnlyList<PainterLiveModel> Models, PainterCharacterColors? Colors)
{
    /// <summary> The enabled-attribute masks of each loaded copy of a model; empty when it isn't loaded. </summary>
    public IReadOnlyList<uint> MasksFor(PainterModelRef model)
    {
        var source = PainterVisibility.NormalizePath(model.SourcePath);
        var game = PainterVisibility.NormalizePath(model.GamePath);
        return Models.Where(m => m.Path == source || m.Path == game).Select(m => m.EnabledAttributes).ToList();
    }
}

internal static class PainterVisibility
{
    /// <summary>
    /// The game draws a submesh only while all of its attributes are enabled: gear turns body parts
    /// off this way, and face mods offer optional parts (piercings, horns, other brows). With no
    /// masks nothing is known, and everything counts as drawn.
    /// </summary>
    public static bool Draws(IReadOnlyList<uint>? masks, ModelSubmesh submesh)
        => masks is not { Count: > 0 } || submesh.AttributeMask == 0 || masks.Any(mask => (submesh.AttributeMask & ~mask) == 0);

    /// <summary> A resource path without Penumbra's "|...|" prefix, with forward slashes, in lower case. </summary>
    public static string NormalizePath(string path)
    {
        path = path.Trim();
        if (path.StartsWith('|') && path.IndexOf('|', 1) is var end and > 0)
            path = path[(end + 1)..];
        return path.Replace('\\', '/').ToLowerInvariant();
    }
}

/// <summary> Reads an on-screen character's loaded models and colors on the framework thread. </summary>
internal sealed class PainterLiveReader(IFramework framework, IObjectTable objects)
{
    private const int MaximumModels = 64;
    private const int MaximumChildren = 16;

    public Task<PainterLiveCharacter?> ReadAsync(int objectIndex, long address)
        => framework.RunOnFrameworkThread(() => Read(objectIndex, (nint)address));

    private unsafe PainterLiveCharacter? Read(int objectIndex, nint address)
    {
        if (objectIndex is < 0 or > ushort.MaxValue || address == 0 || objects[objectIndex] is not ICharacter character ||
            character.Address != address)
            return null;
        var drawObject = ((Character*)address)->GetCharacterBase();
        if (drawObject == null)
            return null;
        var models = new List<PainterLiveModel>();
        AddModels(drawObject, models);
        // Weapons are character bases of their own, attached as children.
        var first = drawObject->DrawObject.Object.ChildObject;
        var child = first;
        for (var i = 0; child != null && i < MaximumChildren; i++)
        {
            if (child->GetObjectType() == ObjectType.CharacterBase)
                AddModels((CharacterBase*)child, models);
            child = child->NextSiblingObject;
            if (child == first)
                break;
        }

        PainterCharacterColors? colors = null;
        if (drawObject->GetModelType() == CharacterBase.ModelType.Human)
        {
            var buffer = ((Human*)drawObject)->CustomizeParameterTypedCBuffer.TryGetBuffer();
            if (buffer.Length > 0)
                colors = new PainterCharacterColors(Display(buffer[0].MainColor), Display(buffer[0].MeshColor));
        }
        return new PainterLiveCharacter(models, colors);
    }

    private static unsafe void AddModels(CharacterBase* character, List<PainterLiveModel> models)
    {
        if (character->Models == null)
            return;
        var count = Math.Min(character->SlotCount, MaximumModels);
        for (var slot = 0; slot < count; slot++)
        {
            var model = character->Models[slot];
            if (model == null || model->ModelResourceHandle == null)
                continue;
            var path = model->ModelResourceHandle->FileName.ToString();
            if (path.Length > 0)
                models.Add(new PainterLiveModel(PainterVisibility.NormalizePath(path), model->EnabledAttributeIndexMask));
        }
    }

    // The customize buffer holds squared colors, which the shaders treat as linear.
    private static Vector3 Display(Vector3 value) => Vector3.SquareRoot(Vector3.Clamp(value, Vector3.Zero, Vector3.One));
}
