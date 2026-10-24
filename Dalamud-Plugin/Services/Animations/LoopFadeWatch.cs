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
    // How many frames after the slot turns the loop's control may take to show.
    private const int Patience = 30;
    // A control counts as just started below this local time (seconds) and weight.
    private const float StartTime = .1f, StartWeight = .05f;

    private nint character;
    private ushort slot;
    private (ushort Startup, ushort Loop)? waiting;
    private int waited;

    public void Update(IGameObject? subject)
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
        }
        if (waiting is not { } pair)
            return;
        if (++waited > Patience)
        {
            waiting = null;
            return;
        }
        if (FramesLeft(chara) is { } frames)
        {
            fades.Record(pair.Startup, pair.Loop, frames);
            waiting = null;
        }
    }

    /// <summary>
    /// Once a control has just started on the main skeleton beside a heavier one, the frames that heavier one (the
    /// startup) has left; null before.
    /// </summary>
    private static int? FramesLeft(Character* chara)
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
        var started = false;
        float heaviest = -1, duration = 0, time = 0;
        for (var i = 0; i < animated->AnimationControls.Length; i++)
        {
            var control = animated->AnimationControls[i].Value;
            if (control == null || control->Binding.ptr == null || control->Binding.ptr->Animation.ptr == null)
                continue;
            if (control->LocalTime < StartTime && control->Weight < StartWeight)
                started = true;
            else if (control->Weight > heaviest)
                (heaviest, duration, time) = (control->Weight, control->Binding.ptr->Animation.ptr->Duration, control->LocalTime);
        }
        return started && heaviest > 0 ? LoopFades.FramesLeft(duration, time) : null;
    }
}
