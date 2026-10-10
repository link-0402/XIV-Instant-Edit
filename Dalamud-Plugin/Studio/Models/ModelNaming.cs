namespace InstantEdit.Studio.Models;

/// <summary>Model (.mdl) file naming and game paths, per race.</summary>
public static class ModelNaming
{
    /// <summary>
    /// Full game path of a race's model, e.g.
    /// "chara/equipment/e0164/model/c0101e0164_top.mdl".
    /// </summary>
    public static string ModelGamePath(EquipSlot slot, ushort modelId, string raceCode)
        => $"{MaterialNaming.ItemFolder(slot, modelId)}/model/c{raceCode}{SlotInfo.ItemPrefix(slot)}{modelId:D4}_{SlotInfo.KeyMap[slot]}.mdl";
}
