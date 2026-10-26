using System.Buffers.Binary;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// GPose keeps the facial emote the posed player holds (the one played before posing began, or the one picked in its
/// emote list) in records inside EventGPoseController, and plays it into the posed copy's face slot about once a second.
/// A timeline in that slot keeps a previewed body timeline's own faces (C010) from playing. ClientStructs names none of
/// the controller's fields, so a record is found by the character it names: the player's address, then its entity id,
/// with the emote row 0x20 in (measured 2026-10-27). One record per pick, so there can be several.
/// </summary>
internal static class GPoseExpressionRecords
{
    public const int EmoteOffset = 0x20;
    private const int EntityOffset = 8;

    /// <summary>The offsets of the emote fields of every record in <paramref name="controller"/> naming the character.</summary>
    public static List<int> Find(ReadOnlySpan<byte> controller, ulong address, uint entityId)
    {
        var found = new List<int>();
        if (address == 0 || entityId == 0)
            return found;
        for (var at = 0; at + EmoteOffset + sizeof(ushort) <= controller.Length; at += 8)
            if (BinaryPrimitives.ReadUInt64LittleEndian(controller[at..]) == address &&
                BinaryPrimitives.ReadUInt32LittleEndian(controller[(at + EntityOffset)..]) == entityId)
                found.Add(at + EmoteOffset);
        return found;
    }
}
