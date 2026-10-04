using System.Buffers.Binary;
using System.Collections.Immutable;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// New timeline entries, with the values vanilla emote timelines use (measured on the game's
/// c0101/c0801 emote PAPs): a sound plays once with its 3D range (C063 loop 1, position 1);
/// an effect lasts 30 frames from the caster to the target and shows for everyone (C012
/// visibility 3); a face is held on its pose with time control (C010 flags 1, 0 to 1); lip
/// sync plays at its own pace (C010 flags 0, 0 to its length).
/// <para>
/// A prop follows the emote_sp pattern (sp54, sp50): the sync effect every animated emote prop
/// uses (C173), the model (C198, summon slot 0), and with a weapon timeline the animation
/// (C031 on the summon, 5 frames after the spawn) behind a hide-then-fade on the summon
/// (C094 0 to 0 for 5 frames, then 0 to 1 over 7), so its first pose never shows.
/// </para>
/// </summary>
internal static class TimelineTemplates
{
    public const string SyncEffect = "vfx/common/eff/syncactiontimelineclip01t.avfx";
    public const int AnimationDelay = 5;
    private static readonly byte[] SummonFilter = [1, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The kinds an added ("n") event may have.</summary>
    public static bool CanCreate(string magic) => magic is "C009" or "C010" or "C012" or "C063" or "C031" or "C094" or "C173" or "C198";

    /// <summary>The events one Add makes, keyed "" until the editor gives them keys; a prop's share a Group.</summary>
    public static ImmutableArray<TimelineEvent> Create(TimelineAddRequest request)
    {
        if (request.Start is < 0 or > TimelineDocument.MaxFrames) throw new InvalidDataException($"A start of {request.Start} frames is out of range.");
        if (request.Duration is < 0 or > TimelineDocument.MaxFrames) throw new InvalidDataException($"A length of {request.Duration} frames is out of range.");
        int Length(int fallback) => request.Duration > 0 ? request.Duration : fallback;
        switch (request.Kind)
        {
            case TimelineAddKind.Sound:
                RequirePath(request.Path, ".scd");
                return [Event("C063", request.Start, 0, request.Path, new() { ["Loop"] = 1, ["SoundIndex"] = 0, ["PositionFlags"] = 1, ["BindId"] = 0 })];
            case TimelineAddKind.Effect:
                RequirePath(request.Path, ".avfx");
                return [Event("C012", request.Start, Length(30), request.Path, EffectDefaults())];
            case TimelineAddKind.Expression:
                if (!IsMotion(request.Path) || !request.Path.StartsWith("cfx", StringComparison.Ordinal) || request.Path.StartsWith("cfxl_", StringComparison.Ordinal))
                    throw new InvalidDataException($"'{request.Path}' is not a facial expression.");
                if (request.Pack != null && !TmbFile.IsSafePackName(request.Pack)) throw new InvalidDataException($"'{request.Pack}' is not a valid face pack name.");
                return [Event("C010", request.Start, Length(30), request.Path, new() { ["TimeControl"] = 1 })];
            case TimelineAddKind.LipSync:
                if (!IsMotion(request.Path) || !request.Path.StartsWith("cfxl_", StringComparison.Ordinal))
                    throw new InvalidDataException($"'{request.Path}' is not a lip motion.");
                return [Event("C010", request.Start, Length(40), request.Path, new() { ["TimeControl"] = 0 })];
            case TimelineAddKind.Prop:
                var prop = request.Prop ?? throw new InvalidDataException("A prop needs a model.");
                if (prop.Model is 0 or > (ushort)short.MaxValue || prop.Body > short.MaxValue)
                    throw new InvalidDataException($"Model {prop.Model} body {prop.Body} is not a valid prop.");
                var group = "prop-" + Guid.NewGuid().ToString("N")[..12];
                var events = ImmutableArray.CreateBuilder<TimelineEvent>();
                events.Add(Event("C173", request.Start, 0, SyncEffect, [], group));
                events.Add(Event("C198", request.Start, Length(60), "",
                    new() { ["Model"] = prop.Model, ["Body"] = prop.Body, ["Variant"] = prop.Variant }, group));
                if (prop.WeaponTimeline is { } timeline)
                {
                    var animated = Math.Min(request.Start + AnimationDelay, TimelineDocument.MaxFrames);
                    events.Add(Event("C031", animated, 1, "", new() { ["WeaponTimeline"] = timeline }, group));
                    events.Add(Event("C094", request.Start, AnimationDelay, "", new() { ["StartVisibility"] = 0, ["EndVisibility"] = 0 }, group));
                    events.Add(Event("C094", animated, 7, "", new() { ["StartVisibility"] = 0, ["EndVisibility"] = 1 }, group));
                }
                return events.ToImmutable();
            default:
                throw new InvalidDataException($"Cannot add {request.Kind}.");
        }
    }

    /// <summary>
    /// Adds an entry for an added event with the bytes its fields do not cover: C010's
    /// animation range, C031's summon target, C094's summon filter, C173's sync settings.
    /// The caller then writes the event's start, duration, path and values over it.
    /// </summary>
    internal static short AddTo(TmbFile file, TimelineEvent e)
    {
        if (!CanCreate(e.Magic)) throw new InvalidDataException($"Cannot add a {TmbEntrySchema.Get(e.Magic).Name} entry.");
        var bytes = new byte[TmbEntrySchema.Get(e.Magic).Size];
        var span = bytes.AsSpan();
        switch (e.Magic)
        {
            case "C010":
                // Time control plays the face's range over the duration: 0 to 1 holds a pose. Without
                // it the motion plays at its own pace, which vanilla writes as 0 to its length.
                var timed = e.Values.GetValueOrDefault("TimeControl") != 0;
                BinaryPrimitives.WriteSingleLittleEndian(span[28..], timed ? 1 : e.Duration);
                break;
            case "C031":
                BinaryPrimitives.WriteInt16LittleEndian(span[22..], 2);
                break;
            case "C173":
                BinaryPrimitives.WriteInt32LittleEndian(span[12..], 1);
                BinaryPrimitives.WriteInt16LittleEndian(span[26..], -1);
                BinaryPrimitives.WriteInt32LittleEndian(span[28..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(span[32..], 1);
                break;
        }
        var id = file.AddEntry(e.Magic, bytes);
        if (e.Magic == "C094") file.SetBlock(id, 28, SummonFilter);
        return id;
    }

    private static TimelineEvent Event(string magic, int start, int duration, string path, Dictionary<string, double> values, string? group = null)
    {
        var kind = TmbEntrySchema.Get(magic);
        var (lane, access, reason) = TmbEntrySchema.Classify(magic, path);
        return new TimelineEvent("", magic, lane, start, kind.HasDuration ? duration : 0, access == TimelineAccess.Editable && kind.HasDuration,
            path, access, reason, false, values.ToImmutableDictionary(), group);
    }

    private static Dictionary<string, double> EffectDefaults() => new()
    {
        ["Bind1.Origin"] = 1, ["Bind1.Type"] = 0, ["Bind1.Id"] = -1, ["Bind2.Origin"] = 2, ["Bind2.Type"] = 0, ["Bind2.Id"] = -1,
        ["Scale.X"] = 1, ["Scale.Y"] = 1, ["Scale.Z"] = 1, ["Rotation.X"] = 0, ["Rotation.Y"] = 0, ["Rotation.Z"] = 0,
        ["Position.X"] = 0, ["Position.Y"] = 0, ["Position.Z"] = 0, ["Color.R"] = 1, ["Color.G"] = 1, ["Color.B"] = 1, ["Color.A"] = 1,
        ["Visibility"] = 3,
    };

    private static void RequirePath(string path, string extension)
    {
        if (path.Length is 0 or >= 256 || path.Any(c => c is < '!' or > '~' or '\\') || path.StartsWith('/') ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{path}' is not a {extension} game path.");
    }

    private static bool IsMotion(string name) => name.Length is > 0 and < 64 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
