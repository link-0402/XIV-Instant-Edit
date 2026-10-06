using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InstantEdit.Services.Painter;
using RenderModel = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;

namespace InstantEdit.Services;

/// <summary> The models of a person's gear slots that other gear hides: the models themselves, and their files. </summary>
/// <param name="Files">Normalized files (<see cref="PainterVisibility.NormalizePath"/>) of the hidden models that no drawn model of the person uses.</param>
internal sealed record HiddenGearModels(IReadOnlySet<nint> Models, IReadOnlySet<string> Files)
{
    public static HiddenGearModels None { get; } = new(new HashSet<nint>(), new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Whether the game draws a person's gear models. Gear hides other slots through its EQP flags: a body
/// item without "show legs" hides the legs, and the feet then follow that body item's own legs flags.
/// The game keeps a hidden slot's model loaded, so Penumbra still lists it, and only clears its drawn bit.
/// </summary>
internal static unsafe class ModelVisibility
{
    /// <summary> The gear slots, head to left ring, which the game shows or hides by the EQP flags. </summary>
    public const int GearSlots = 10;

    // Render::Model's flags, which ClientStructs doesn't name. Human's visibility update sets bit 0 on each
    // gear model, then clears it where the EQP flags hide the slot (ffxiv_dx11.exe 2026.09.15). Meddle reads the same bit.
    private const int FlagsOffset = 0x28;

    /// <summary> Whether the game draws a loaded gear model of a person; false when other gear hides its slot. </summary>
    public static bool IsDrawn(RenderModel* model) => (*(uint*)((byte*)model + FlagsOffset) & 1) != 0;

    /// <summary> Whether a slot of a person's draw object holds a gear model that other gear hides. </summary>
    public static bool IsHiddenGear(CharacterBase* drawObject, int slot)
    {
        if (slot is < 0 or >= GearSlots || drawObject == null || drawObject->GetModelType() != CharacterBase.ModelType.Human ||
            drawObject->Models == null || slot >= drawObject->SlotCount)
            return false;
        var model = drawObject->Models[slot];
        return model != null && model->ModelResourceHandle != null && !IsDrawn(model);
    }

    /// <summary> The gear models other gear hides on a person's draw object; none for other draw objects. Call on the framework thread. </summary>
    public static HiddenGearModels HiddenGear(CharacterBase* drawObject)
    {
        if (drawObject == null || drawObject->GetModelType() != CharacterBase.ModelType.Human || drawObject->Models == null)
            return HiddenGearModels.None;
        var models = new HashSet<nint>();
        var hiddenFiles = new HashSet<string>(StringComparer.Ordinal);
        var drawnFiles = new HashSet<string>(StringComparer.Ordinal);
        for (var slot = 0; slot < drawObject->SlotCount; slot++)
        {
            var model = drawObject->Models[slot];
            if (model == null || model->ModelResourceHandle == null)
                continue;
            var file = PainterVisibility.NormalizePath(model->ModelResourceHandle->FileName.Utf8());
            if (IsHiddenGear(drawObject, slot))
            {
                models.Add((nint)model);
                hiddenFiles.Add(file);
            }
            else
                drawnFiles.Add(file);
        }
        if (models.Count == 0)
            return HiddenGearModels.None;
        hiddenFiles.ExceptWith(drawnFiles);
        return new HiddenGearModels(models, hiddenFiles);
    }
}
