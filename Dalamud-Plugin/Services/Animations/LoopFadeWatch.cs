using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Measures <see cref="LoopFades"/> on the followed character, every frame on the framework thread: when its base
/// slot turns from a heard startup to that startup's loop, the loop's Havok control shows within a frame or two at its
/// start with no weight, and the startup's, still the heaviest, has its fade left to play. Reads only.
/// </summary>
/// <param name="startsInto">Whether a timeline is a startup that leads into another, its loop.</param>
internal sealed unsafe class LoopFadeWatch(LoopFades fades, Func<ushort, ushort, bool> startsInto)
{
    private const int MaximumPartials = 16;
    private const int MaximumControls = 256;
    // How long (seconds) after the slot turns the loop's control may take to show.
    private const float Patience = .5f;
    // A control counts as just started below this local time (seconds) and weight: the loop's, seen late when a
    // frame took long (it loads as it starts). Its own time is taken off, so a late look measures the same.
    private const float StartTime = .25f, StartWeight = .5f;

    private nint character;
    private ushort slot;
    private (ushort Startup, ushort Loop)? waiting;
    private float waited;
    // The startup's Havok animation: the heaviest control's at the first look after the slot turned, the only one read after.
    private nint startup;

    /// <param name="delta">Seconds since the last frame.</param>
    public void Update(IGameObject? subject, float delta)
    {
        if (subject is not ICharacter { Address: not 0 } followed)
        {
            character = 0;
            waiting = null;
            return;
        }
        var chara = (Character*)followed.Address;
        var now = chara->Timeline.TimelineSequencer.TimelineIds[0];
        if (followed.Address != character)
        {
            (character, slot, waiting) = (followed.Address, now, null);
            return;
        }
        if (now != slot)
        {
            var from = slot;
            slot = now;
            waiting = from != 0 && now != 0 && startsInto(from, now) ? (from, now) : null;
            waited = 0;
            startup = 0;
        }
        if (waiting is not { } pair)
            return;
        if ((waited += delta) > Patience)
        {
            waiting = null;
            return;
        }
        if (FramesLeft(chara, ref startup) is { } frames)
        {
            fades.Record(pair.Startup, pair.Loop, frames);
            waiting = null;
        }
    }

    /// <summary>
    /// Once a control has just started on the main skeleton, the frames the startup's had left when it started; null
    /// before, or once the startup's control is gone. With no <paramref name="startup"/> yet, the heaviest other control
    /// is taken as it and its animation kept there. shortcut: an overlay heavier than the startup at that first look
    /// still wins; tell them apart by slot if one ever does.
    /// </summary>
    private static int? FramesLeft(Character* chara, ref nint startup)
    {
        var drawn = chara->GetCharacterBase();
        if (drawn == null || drawn->Skeleton == null)
            return null;
        var skeleton = drawn->Skeleton;
        if (skeleton->PartialSkeletonCount is <= 0 or > MaximumPartials || skeleton->PartialSkeletons == null)
            return null;
        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null)
            return null;
        try { AnimationNative.ValidateArray(animated->AnimationControls, MaximumControls, "animation controls"); }
        catch (InvalidDataException) { return null; }
        float? started = null;
        float heaviest = -1, duration = 0, time = 0;
        nint found = 0;
        for (var i = 0; i < animated->AnimationControls.Length; i++)
        {
            var control = animated->AnimationControls[i].Value;
            if (control == null || control->Binding.ptr == null || control->Binding.ptr->Animation.ptr == null)
                continue;
            var animation = (nint)control->Binding.ptr->Animation.ptr;
            if (control->LocalTime is >= 0 and < StartTime && control->Weight < StartWeight)
                started = Math.Min(started ?? StartTime, control->LocalTime);
            else if (startup != 0 ? animation == startup : control->Weight > heaviest)
                (heaviest, found, duration, time) = (control->Weight, animation, control->Binding.ptr->Animation.ptr->Duration, control->LocalTime);
        }
        if (startup == 0)
            startup = found;
        return started is { } since && heaviest > 0 ? LoopFades.FramesLeft(duration, time - since) : null;
    }
}
