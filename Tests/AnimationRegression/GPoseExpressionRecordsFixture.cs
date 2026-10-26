using System.Buffers.Binary;
using InstantEdit.Services.Animations.Preview;

/// <summary>
/// GPose re-plays the facial emote it keeps for the posed player about once a second, which holds
/// the face slot and keeps a previewed timeline's own faces from playing. The preview finds those
/// records by the character they name; these fixtures cover the matching on a synthetic controller
/// laid out as measured in game on 2026-10-27 (records at 0x230 and 0x3170, emote 0x20 in).
/// </summary>
internal static class GPoseExpressionRecordsFixture
{
    private const ulong Player = 0x2AF9FE40050;
    private const uint Entity = 0x1002372D;

    public static void Run(Action<bool, string> check)
    {
        var controller = new byte[0x3590];
        Record(controller, 0x230, Player, Entity, 72);
        Record(controller, 0x3170, Player, Entity, 68);
        // The posed copy's address with another id, and the player's address alone, are not records.
        Record(controller, 0x1E0 - 0x40, 0x2AFB9C7B860, Entity, 5);
        Record(controller, 0x600, Player, 0xE0000000, 9);
        BinaryPrimitives.WriteUInt64LittleEndian(controller.AsSpan(0x3508), Player);
        // Misaligned: the controller's records sit on 8-byte boundaries.
        Record(controller, 0x804, Player, Entity, 11);
        // A match too close to the end to hold an emote field.
        BinaryPrimitives.WriteUInt64LittleEndian(controller.AsSpan(controller.Length - 0x10), Player);
        BinaryPrimitives.WriteUInt32LittleEndian(controller.AsSpan(controller.Length - 0x8), Entity);

        var found = GPoseExpressionRecords.Find(controller, Player, Entity);
        check(found.SequenceEqual([0x250, 0x3190]),
            $"the emote fields of every record naming the player are found, and nothing else ({string.Join(", ", found.Select(f => $"0x{f:X}"))})");
        check(GPoseExpressionRecords.Find(controller, Player, 0).Count == 0 && GPoseExpressionRecords.Find(controller, 0, Entity).Count == 0,
            "no records without the player's address and entity id");
        check(GPoseExpressionRecords.Find([], Player, Entity).Count == 0 && GPoseExpressionRecords.Find(new byte[0x27], Player, Entity).Count == 0,
            "an empty or short controller has no records");
    }

    private static void Record(byte[] controller, int at, ulong address, uint entity, ushort emote)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(controller.AsSpan(at), address);
        BinaryPrimitives.WriteUInt32LittleEndian(controller.AsSpan(at + 8), entity);
        BinaryPrimitives.WriteUInt16LittleEndian(controller.AsSpan(at + GPoseExpressionRecords.EmoteOffset), emote);
    }
}
