using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// Plays, pauses, speeds up and scrubs a timeline on the GPose actor (the GPose target, else the
/// posed copy of the player at object 201), following Brio: AnimLock mode, BaseOverride and
/// ActionTimelineSequencer.PlayTimeline. Writes happen only in GPose, whose actors are client-only;
/// outside it the class only reads (<see cref="CurrentTime"/>). Scrubbing writes LocalTime on the
/// live Havok controls holding the clip, found by binding fingerprint rather than control index; it
/// moves the pose only, so TMB events (sounds, VFX, expressions) fire only during real playback.
/// No hooks or signatures: everything goes through ClientStructs members. Every call must come from
/// the framework thread (ImGui draw included), and pointers are looked up again on every call.
/// </summary>
internal sealed unsafe class AnimationPlayback : IDisposable
{
    // GPose copies the player into this slot; the overworld player is hidden while posing.
    public const int GPosePlayerIndex = 201;
    // GPose characters (the posed copies and actors posing tools add) start here.
    private const int FirstGPoseIndex = 200;
    // ActionTimeline row normal/idle, played after a stop when nothing overrode the base before (Brio does the same).
    private const ushort IdleTimeline = 3;
    // ActionTimeline row facial/pose/base (Straight Face), played after an expression preview: a kept face stays otherwise.
    private const ushort NeutralFaceTimeline = 604;
    private const int MaximumPartials = 16;
    private const int MaximumControls = 256;
    // ActionTimelineSequencer slots: base, upper body and facial.
    private static readonly uint[] SpeedSlots = [0, 1, 2];
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ITargetManager _targets;
    private readonly IPluginLog _log;
    private readonly RuntimeCaptureCache _bindings = new();
    // Control addresses found during one call; never kept across frames.
    private readonly List<nint> _matches = [];
    private Session? _session;
    private bool _disposed;

    /// <summary>
    /// The actor a playback runs on and what it looked like before. The address is an identity
    /// key only; the actor is looked up again on every call.
    /// </summary>
    private sealed class Session
    {
        public required ushort ObjectIndex { get; init; }
        public required ulong GameObjectId { get; init; }
        public required nint Address { get; init; }
        public required string Name { get; init; }
        public required CharacterModes Mode { get; init; }
        public required byte ModeParam { get; init; }
        public required ushort BaseOverride { get; init; }
        public required float[] Speeds { get; init; }
        public ushort Timeline { get; set; }
        /// <summary> A body timeline took over the base slot (AnimLock and BaseOverride), which a stop puts back. </summary>
        public bool BodyPlayed { get; set; }
        /// <summary> An expression played on the face, which a stop returns to neutral. </summary>
        public bool FacePlayed { get; set; }
        public string? Fingerprint { get; set; }
        public bool Paused { get; set; }
        public float Speed { get; set; } = 1;
        /// <summary> Slot speeds were changed and are held until the playback stops. </summary>
        public bool SpeedSet { get; set; }
        /// <summary> The time held while paused, so the game can't drift it. </summary>
        public float? Hold { get; set; }
    }

    public AnimationPlayback(IFramework framework, IObjectTable objects, IClientState clientState, ITargetManager targets, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _targets = targets;
        _log = log;
        _framework.Update += OnUpdate;
    }

    public bool Active => _session != null;
    public bool Paused => _session?.Paused == true;
    public float Speed => _session?.Speed ?? 1;
    public ushort Timeline => _session?.Timeline ?? 0;
    public string ActorName => _session?.Name ?? string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;

    /// <summary> Whether <see cref="Play"/> can run now, and why not. </summary>
    public bool Available(out string reason)
    {
        RequireFrameworkThread();
        reason = Problem(GPoseActor()) ?? string.Empty;
        return reason.Length == 0;
    }

    /// <summary>
    /// The object index <see cref="Play"/> would act on now: the GPose target when it is a
    /// character, else the posed player. Null outside GPose. A preview mod goes on its collection.
    /// </summary>
    public ushort? ActorIndex()
    {
        RequireFrameworkThread();
        return GPoseActor() is { } actor ? actor.ObjectIndex : null;
    }

    /// <summary>
    /// How the character bones are edited on is turned in the world: the GPose actor, else your own
    /// character. The Bones window's rotation ball shows its rings that way; null when neither is drawn.
    /// </summary>
    public Quaternion? ActorRotation()
    {
        RequireFrameworkThread();
        var actor = (IGameObject?)GPoseActor() ?? _objects.LocalPlayer;
        if (actor is not { Address: not 0 })
            return null;
        var drawObject = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address)->DrawObject;
        return drawObject != null ? Quaternion.Normalize(drawObject->Object.Rotation) : Quaternion.CreateFromAxisAngle(Vector3.UnitY, actor.Rotation);
    }

    /// <summary>
    /// Reads the body skeleton of the GPose character at <paramref name="index"/> as the game shows it this
    /// frame into <paramref name="pose"/>, for the bones the Bones window draws on it. False outside GPose,
    /// for characters of the world and when the character is not drawn.
    /// </summary>
    public bool ReadPose(ushort index, ActorPose pose)
    {
        RequireFrameworkThread();
        if (!_clientState.IsGPosing || index < FirstGPoseIndex || _objects[index] is not ICharacter { Address: not 0 } actor)
            return false;
        return pose.Read(actor.Address);
    }

    /// <summary>
    /// Plays <paramref name="timelineId"/> on the GPose actor: saves its mode, base override and slot
    /// speeds (once per actor), sets AnimLock, makes the timeline the base override and starts it.
    /// <paramref name="bindingFingerprint"/> names the clip's binding for pause, seek and time reads.
    /// </summary>
    /// <param name="expectedIndex">The actor the preview was shown on; another one means the GPose target changed.</param>
    /// <param name="face">An expression's timeline: it plays on the face slot and leaves the body, its mode and its base as they are.</param>
    public bool Play(ushort timelineId, string? bindingFingerprint = null, ushort? expectedIndex = null, bool face = false)
    {
        RequireFrameworkThread();
        if (timelineId == 0)
            return Fail("Pick a timeline to play.");
        var actor = GPoseActor();
        if (Problem(actor) is { } problem)
            return Fail(problem);
        if (expectedIndex is { } expected && actor!.ObjectIndex != expected)
            return Fail("The GPose target changed. Press Preview again.");
        if (_session is { } running && (running.ObjectIndex != actor!.ObjectIndex || running.Address != actor.Address ||
                                        running.GameObjectId != actor.GameObjectId))
            StopSession(restore: true);
        var chara = (Character*)actor!.Address;
        var session = _session ??= Capture(actor, chara);
        session.Timeline = timelineId;
        session.Fingerprint = bindingFingerprint;
        session.Paused = false;
        session.Hold = null;
        if (face)
            session.FacePlayed = true;
        else
        {
            session.BodyPlayed = true;
            chara->SetMode(CharacterModes.AnimLock, 0);
            chara->Timeline.BaseOverride = timelineId;
        }
        chara->Timeline.TimelineSequencer.PlayTimeline(timelineId);
        if (session.SpeedSet)
            Apply(chara, session);
        return Succeed("Playing.");
    }

    /// <summary> Holds the clip where it is: slot speeds and the matched controls' speed go to 0. </summary>
    public bool Pause()
    {
        RequireFrameworkThread();
        if (Current(out var session, out var chara) is { } problem)
            return Fail(problem);
        session.Paused = true;
        session.SpeedSet = true;
        if (session.Fingerprint is { } fingerprint && ReadTime(chara, fingerprint) is { } now)
            session.Hold = now;
        Apply(chara, session);
        return Succeed("Paused.");
    }

    public bool Resume()
    {
        RequireFrameworkThread();
        if (Current(out var session, out var chara) is { } problem)
            return Fail(problem);
        session.Paused = false;
        session.Hold = null;
        Apply(chara, session, resuming: true);
        return Succeed("Playing.");
    }

    /// <summary> Playback speed for the base, upper body and facial slots: 0 to 4, 1 is normal. </summary>
    public bool SetSpeed(float speed)
    {
        RequireFrameworkThread();
        if (Current(out var session, out var chara) is { } problem)
            return Fail(problem);
        session.Speed = AnimationPreviewRules.ClampSpeed(speed);
        session.SpeedSet = true;
        Apply(chara, session, resuming: true);
        return Succeed(session.Paused ? "Paused." : "Playing.");
    }

    /// <summary>
    /// Moves every live control whose binding fingerprint matches to <paramref name="seconds"/>,
    /// clamped to the animation's duration. While paused the time is written again on every update.
    /// </summary>
    public bool Seek(float seconds, string bindingFingerprint)
    {
        RequireFrameworkThread();
        if (string.IsNullOrEmpty(bindingFingerprint))
            return Fail("The clip is unknown.");
        if (Current(out var session, out var chara) is { } problem)
            return Fail(problem);
        session.Fingerprint = bindingFingerprint;
        if (session.Paused)
            session.Hold = seconds;
        if (WriteTime(chara, bindingFingerprint, seconds) == 0)
            return Fail("The clip is not playing on the character.");
        Error = string.Empty;
        return true;
    }

    /// <summary>
    /// The clip's current time in seconds, read only: on the playback's actor or the GPose actor
    /// while posing, else on the local player (for a playhead that follows the game). Null when no
    /// live control holds the clip.
    /// </summary>
    public float? CurrentTime(string bindingFingerprint)
    {
        RequireFrameworkThread();
        if (string.IsNullOrEmpty(bindingFingerprint))
            return null;
        Character* chara = null;
        if (_clientState.IsGPosing)
        {
            if (_session != null)
                chara = SessionActor(_session);
            if (chara == null && GPoseActor() is { } posed)
                chara = (Character*)posed.Address;
        }
        else if (_objects.LocalPlayer is { Address: not 0 } player)
            chara = (Character*)player.Address;
        return chara == null ? null : ReadTime(chara, bindingFingerprint);
    }

    /// <summary> Puts the actor's mode, base override and slot speeds back and plays its base again. </summary>
    public void Stop()
    {
        RequireFrameworkThread();
        if (_session == null)
            return;
        StopSession(restore: true);
        Succeed("Stopped.");
    }

    private void OnUpdate(IFramework framework)
    {
        var session = _session;
        if (session == null)
            return;
        try
        {
            // Leaving GPose removes its actors; nothing is written to them any more.
            if (!_clientState.IsGPosing)
            {
                End("GPose ended. Playback stopped.");
                return;
            }
            var chara = SessionActor(session);
            if (chara == null)
            {
                End("The character left. Playback stopped.");
                return;
            }
            if (session.SpeedSet)
                Apply(chara, session);
        }
        catch (Exception e)
        {
            _log.Warning(e, "The animation preview playback failed.");
            End("Playback stopped after an error.");
        }
    }

    /// <summary>
    /// Holds slot speeds and, while paused, the matched controls' speed and the held time. The game
    /// carries slot speed over to the controls itself; a pause must not wait for that, and
    /// <paramref name="resuming"/> restarts controls the pause stopped.
    /// </summary>
    private void Apply(Character* chara, Session session, bool resuming = false)
    {
        var speed = session.Paused ? 0 : session.Speed;
        ref var sequencer = ref chara->Timeline.TimelineSequencer;
        foreach (var slot in SpeedSlots)
            if (sequencer.TimelineSpeeds[(int)slot] != speed)
                sequencer.SetSlotSpeed(slot, speed);
        if (session.Fingerprint is not { } fingerprint || !session.Paused && !resuming)
            return;
        foreach (var address in Matching(chara, fingerprint))
        {
            var control = (hkaDefaultAnimationControl*)address;
            if (session.Paused || control->PlaybackSpeed == 0)
                control->PlaybackSpeed = speed;
            if (session.Paused && session.Hold is { } hold)
                control->LocalTime = AnimationPreviewRules.ClampTime(hold, control->Binding.ptr->Animation.ptr->Duration);
        }
    }

    private int WriteTime(Character* chara, string fingerprint, float seconds)
    {
        var matches = Matching(chara, fingerprint);
        foreach (var address in matches)
        {
            var control = (hkaDefaultAnimationControl*)address;
            control->LocalTime = AnimationPreviewRules.ClampTime(seconds, control->Binding.ptr->Animation.ptr->Duration);
        }
        return matches.Count;
    }

    /// <summary> The time of the matched control with the most weight. </summary>
    private float? ReadTime(Character* chara, string fingerprint)
    {
        hkaDefaultAnimationControl* best = null;
        foreach (var address in Matching(chara, fingerprint))
        {
            var control = (hkaDefaultAnimationControl*)address;
            if (best == null || control->Weight > best->Weight)
                best = control;
        }
        return best == null || !float.IsFinite(best->LocalTime) ? null : best->LocalTime;
    }

    /// <summary>
    /// Every live control, in every partial skeleton and both animated-skeleton buffers, whose
    /// binding fingerprint (<see cref="RuntimeCaptureCache.BindingFingerprint"/>) is <paramref name="fingerprint"/>.
    /// The list is reused; read it before the next call.
    /// </summary>
    private List<nint> Matching(Character* chara, string fingerprint)
    {
        _matches.Clear();
        var drawn = chara->GetCharacterBase();
        if (drawn == null || drawn->Skeleton == null)
            return _matches;
        var skeleton = drawn->Skeleton;
        if (skeleton->PartialSkeletonCount is <= 0 or > MaximumPartials || skeleton->PartialSkeletons == null)
            return _matches;
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            if (partial->SkeletonResourceHandle == null)
                continue;
            string? resource = null;
            for (var buffer = 0; buffer < 2; buffer++)
            {
                var animated = partial->GetHavokAnimatedSkeleton(buffer);
                if (animated == null || animated->Skeleton == null)
                    continue;
                try { AnimationNative.ValidateArray(animated->AnimationControls, MaximumControls, "animation controls"); }
                catch (InvalidDataException) { continue; }
                for (var i = 0; i < animated->AnimationControls.Length; i++)
                {
                    var control = animated->AnimationControls[i].Value;
                    if (control == null || control->Binding.ptr == null || control->Binding.ptr->Animation.ptr == null)
                        continue;
                    resource ??= partial->SkeletonResourceHandle->FileName.Utf8();
                    // A live motion copy counts as the clip it stands for, so pause, seek and time reads still find it.
                    if (!LiveBindings.TryGet((nint)control->Binding.ptr, out _, out var print))
                        try { print = _bindings.BindingFingerprint(control->Binding.ptr, animated->Skeleton, resource); }
                        catch (InvalidDataException) { continue; }
                    if (string.Equals(print, fingerprint, StringComparison.Ordinal) && !_matches.Contains((nint)control))
                        _matches.Add((nint)control);
                }
            }
        }
        return _matches;
    }

    private Session Capture(IGameObject actor, Character* chara)
    {
        var speeds = new float[SpeedSlots.Length];
        for (var i = 0; i < SpeedSlots.Length; i++)
            speeds[i] = chara->Timeline.TimelineSequencer.TimelineSpeeds[(int)SpeedSlots[i]];
        return new Session
        {
            ObjectIndex = actor.ObjectIndex,
            GameObjectId = actor.GameObjectId,
            Address = actor.Address,
            Name = actor.Name.TextValue,
            Mode = chara->Mode,
            ModeParam = chara->ModeParam,
            BaseOverride = chara->Timeline.BaseOverride,
            Speeds = speeds,
        };
    }

    /// <summary> Ends the session; with <paramref name="restore"/> and still in GPose, puts the actor back as Brio does. </summary>
    private void StopSession(bool restore)
    {
        var session = _session;
        if (session == null)
            return;
        _session = null;
        if (!restore || !_clientState.IsGPosing)
            return;
        var chara = SessionActor(session);
        if (chara == null)
            return;
        try
        {
            if (session.SpeedSet)
                for (var i = 0; i < SpeedSlots.Length; i++)
                    chara->Timeline.TimelineSequencer.SetSlotSpeed(SpeedSlots[i], session.Speeds[i]);
            if (session.BodyPlayed)
            {
                chara->Timeline.BaseOverride = session.BaseOverride;
                chara->Mode = session.Mode;
                chara->ModeParam = session.ModeParam;
                chara->Timeline.TimelineSequencer.PlayTimeline(session.BaseOverride != 0 ? session.BaseOverride : IdleTimeline);
            }
            if (session.FacePlayed)
                chara->Timeline.TimelineSequencer.PlayTimeline(NeutralFaceTimeline);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not restore the character after the animation preview playback.");
        }
    }

    /// <summary>
    /// The posing actor: the GPose target when it is a GPose character, else the posed copy of the
    /// player. Characters of the world (below index 200) never are: they outlive GPose, and one left
    /// locked in a preview would stay that way.
    /// </summary>
    private ICharacter? GPoseActor()
    {
        if (!_clientState.IsGPosing)
            return null;
        if (_targets.GPoseTarget is ICharacter { Address: not 0, ObjectIndex: >= FirstGPoseIndex } target)
            return target;
        return _objects[GPosePlayerIndex] is ICharacter { Address: not 0 } posed ? posed : null;
    }

    private Character* SessionActor(Session session)
    {
        var current = _objects[session.ObjectIndex];
        return current is ICharacter && current.Address == session.Address && current.GameObjectId == session.GameObjectId
            ? (Character*)current.Address
            : null;
    }

    private string? Problem(ICharacter? actor)
    {
        if (!_clientState.IsGPosing)
            return "Enter GPose to play the preview.";
        if (actor == null)
            return "No character to play on.";
        var chara = (Character*)actor.Address;
        if (chara->Mode is CharacterModes.Mounted or CharacterModes.RidingPillion)
            return "Dismount to play the preview.";
        var drawn = chara->GetCharacterBase();
        return drawn == null || drawn->Skeleton == null ? "The character is not drawn." : null;
    }

    /// <summary> The running session and its actor, or why there is none (ending a session whose actor is gone). </summary>
    private string? Current(out Session session, out Character* chara)
    {
        session = _session!;
        chara = null;
        if (_session == null)
            return "Play the preview first.";
        if (!_clientState.IsGPosing)
        {
            End("GPose ended. Playback stopped.");
            return "Enter GPose to play the preview.";
        }
        chara = SessionActor(session);
        if (chara != null)
            return null;
        End("The character left. Playback stopped.");
        return "The character is gone.";
    }

    private void End(string status)
    {
        _session = null;
        Status = status;
    }

    private bool Fail(string error)
    {
        Error = error;
        return false;
    }

    private bool Succeed(string status)
    {
        Status = status;
        Error = string.Empty;
        return true;
    }

    private void RequireFrameworkThread()
    {
        if (!_framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Animation playback must run on the framework thread.");
    }

    /// <summary> Puts the actor back when still posing; waits briefly when called off the framework thread. </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _framework.Update -= OnUpdate;
        if (_session == null)
            return;
        try
        {
            if (_framework.IsInFrameworkUpdateThread)
                StopSession(restore: true);
            else
                _framework.RunOnFrameworkThread(() => StopSession(restore: true)).Wait(DisposeWait);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not restore the character while unloading.");
        }
    }
}
