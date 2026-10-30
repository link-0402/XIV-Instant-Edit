using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// Plays, pauses, speeds up and scrubs a timeline on the GPose actor (the GPose target, else the
/// posed copy of the player at object 201), following Brio: AnimLock mode, BaseOverride and
/// ActionTimelineSequencer.PlayTimeline. Writes happen only in GPose, whose actors are client-only;
/// outside it the class only reads (<see cref="CurrentTime"/>). Scrubbing writes LocalTime on the
/// live Havok controls holding the clip, found by binding fingerprint rather than control index; it
/// moves the pose only, so TMB events (sounds, VFX, expressions) fire only during real playback.
/// No hooks or signatures: everything goes through ClientStructs members, apart from GPose's held
/// expression records (<see cref="GPoseExpressionRecords"/>), which are paused while a preview plays
/// on the posed player and put back when it stops. Every call must come from the framework thread
/// (ImGui draw included), and pointers are looked up again on every call.
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
    private const uint FaceSlot = 2;
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);
    // How close to its duration a clip's time counts as its end (well under one 30 fps frame).
    private const float EndTolerance = .001f;
    // How far the draw offset may drift from the held one before it is set again (a tenth of a millimetre).
    private const float OffsetTolerance = .0001f;

    private readonly IFramework _framework;
    private readonly IDataManager _data;
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
        /// <summary> The draw offset the actor had, heels included. </summary>
        public required Vector3 DrawOffset { get; init; }
        /// <summary> The part of <see cref="DrawOffset"/> the game set (<see cref="GameOffset(Character*)"/>), without Simple Heels'. </summary>
        public required Vector3 GameOffset { get; init; }
        /// <summary> How the actor sat or lay then, as <see cref="AnimationPreviewRules.SeatedMode"/> numbers it; 0 standing. </summary>
        public required byte SeatedMode { get; init; }
        /// <summary> The draw offset held while a sitting or lying clip plays (<see cref="HoldOffset"/>); null for other clips. </summary>
        public Vector3? HeldOffset { get; set; }
        /// <summary> The base draw offset this playback set to hold it, which a stop takes off again; null while none is set. </summary>
        public Vector3? OffsetBase { get; set; }
        public ushort Timeline { get; set; }
        /// <summary> A body timeline took over the base slot (AnimLock and BaseOverride), which a stop puts back. </summary>
        public bool BodyPlayed { get; set; }
        /// <summary> An expression played on the face, which a stop returns to neutral. </summary>
        public bool FacePlayed { get; set; }
        /// <summary> The last play was an expression's, on the face slot, rather than a body timeline. </summary>
        public bool PlayingFace { get; set; }
        /// <summary> GPose's held expressions this playback paused, by emote field offset, which a stop puts back. </summary>
        public Dictionary<int, ushort> HeldExpressions { get; } = [];
        public string? Fingerprint { get; set; }
        public bool Paused { get; set; }
        public float Speed { get; set; } = 1;
        /// <summary> Slot speeds were changed and are held until the playback stops. </summary>
        public bool SpeedSet { get; set; }
        /// <summary> The time held while paused, so the game can't drift it. </summary>
        public float? Hold { get; set; }
    }

    public AnimationPlayback(IFramework framework, IDataManager data, IObjectTable objects, IClientState clientState, ITargetManager targets, IPluginLog log)
    {
        _framework = framework;
        _data = data;
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

    /// <summary>Whether the GPose character at <paramref name="index"/> is drawn with its skeleton, so a clip can play on it.</summary>
    public bool Drawn(ushort index)
    {
        RequireFrameworkThread();
        return GPoseCharacter(index) is { } actor && Problem(actor) == null;
    }

    /// <summary>
    /// Whether the GPose character at <paramref name="index"/> is drawn and visible. A redraw gives it its skeleton a few frames
    /// before it shows, and a timeline started before that plays without any of its sounds (seen in game 2026-10-10: a play on
    /// Penumbra's redraw event asked for none, not even an effect's music; one 0.25 s later asked for all).
    /// </summary>
    public bool Shown(ushort index)
    {
        RequireFrameworkThread();
        if (GPoseCharacter(index) is not { } actor || Problem(actor) != null)
            return false;
        var drawObject = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address)->DrawObject;
        return drawObject != null && drawObject->IsVisible;
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
    /// The body skeleton's bone names by index on the character bones are edited for: the GPose actor, else your own
    /// character. An animation's tracks name bones by index, so a bone at another index there plays on another bone.
    /// Read again only when the skeleton changes; null when neither is drawn.
    /// </summary>
    public string[]? ActorBoneNames()
    {
        RequireFrameworkThread();
        var actor = (IGameObject?)GPoseActor() ?? _objects.LocalPlayer;
        if (actor is not { Address: not 0 } || actor is not ICharacter)
            return null;
        var drawn = ((Character*)actor.Address)->GetCharacterBase();
        if (drawn == null || drawn->Skeleton == null || drawn->Skeleton->PartialSkeletonCount < 1 || drawn->Skeleton->PartialSkeletons == null)
            return null;
        var pose = drawn->Skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Data == null)
            return null;
        var havok = pose->Skeleton;
        if (_boneNames is { } known && known.Skeleton == (nint)havok && known.Bones == (nint)havok->Bones.Data && known.Names.Length == havok->Bones.Length)
            return known.Names;
        var names = new string[havok->Bones.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = havok->Bones[i].Name.String ?? string.Empty;
        _boneNames = ((nint)havok, (nint)havok->Bones.Data, names);
        return names;
    }

    // The names ActorBoneNames read last, with the Havok skeleton and bone array they came from: identities only, never followed.
    private (nint Skeleton, nint Bones, string[] Names)? _boneNames;

    /// <summary>
    /// The body skeleton of the GPose character at <paramref name="index"/>, with its rest pose: Live hands a repaired clip's copy
    /// back to it by bone name. Read again only when the skeleton changes; null outside GPose, when it isn't drawn or can't be read.
    /// </summary>
    public SkeletonDescription? ActorSkeleton(ushort index)
    {
        RequireFrameworkThread();
        if (!_clientState.IsGPosing || index < FirstGPoseIndex || _objects[index] is not ICharacter { Address: not 0 } actor)
            return null;
        var drawn = ((Character*)actor.Address)->GetCharacterBase();
        if (drawn == null || drawn->Skeleton == null || drawn->Skeleton->PartialSkeletonCount < 1 || drawn->Skeleton->PartialSkeletons == null)
            return null;
        var pose = drawn->Skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Data == null)
            return null;
        var havok = pose->Skeleton;
        if (_actorSkeleton is { } known && known.Skeleton == (nint)havok && known.Bones == (nint)havok->Bones.Data)
            return known.Description;
        SkeletonDescription described;
        try { described = AnimationSkeleton.Describe(havok); }
        catch (InvalidDataException) { return null; }
        _actorSkeleton = ((nint)havok, (nint)havok->Bones.Data, described);
        return described;
    }

    // The skeleton ActorSkeleton described last, with the Havok skeleton and bone array it came from: identities only.
    private (nint Skeleton, nint Bones, SkeletonDescription Description)? _actorSkeleton;

    /// <summary>The race code (801 for c0801) of the GPose character at <paramref name="index"/>; null when it isn't a drawn human.</summary>
    public ushort? ActorRace(ushort index)
    {
        RequireFrameworkThread();
        return AnimationRuntime.RaceCode(GPoseCharacter(index)) is ['c', .. var digits] && ushort.TryParse(digits, out var race) ? race : null;
    }

    // A prop watch LearnProps started: the actor (an identity key only), the models still unknown and when it ends.
    private sealed record PropWatch(ushort ObjectIndex, nint Address, HashSet<ushort> Models, long UntilTicks, Action<ushort, string> Learn);
    private PropWatch? _propWatch;
    // The first weapon slot a summon lands in (summon 0 is DrawData's third); the two before it are the character's own weapons.
    private const int FirstSummonSlot = 2;

    /// <summary>
    /// Watches the GPose character at <paramref name="index"/> for <paramref name="duration"/>: every frame, a summon
    /// slot (DrawData's third weapon slot and the fourth after it) whose model is one of <paramref name="models"/> hands
    /// <paramref name="learn"/> the attach point its draw object names (see <see cref="PropAttachTypes.CodeInSlot"/>). Ends
    /// sooner once every model is learned, or the character leaves; a new watch replaces the last, and no models ends it.
    /// </summary>
    public void LearnProps(ushort index, IReadOnlyCollection<ushort> models, TimeSpan duration, Action<ushort, string> learn)
    {
        RequireFrameworkThread();
        _propWatch = models.Count > 0 && GPoseCharacter(index) is { } actor
            ? new PropWatch(index, actor.Address, [.. models], Environment.TickCount64 + (long)duration.TotalMilliseconds, learn)
            : null;
    }

    private void WatchProps()
    {
        if (_propWatch is not { } watch)
            return;
        if (Environment.TickCount64 > watch.UntilTicks || GPoseCharacter(watch.ObjectIndex) is not { } actor || actor.Address != watch.Address)
        {
            _propWatch = null;
            return;
        }
        var drawData = &((Character*)actor.Address)->DrawData;
        for (var i = FirstSummonSlot; i < drawData->WeaponData.Length + drawData->UnkWeaponData.Length; i++)
        {
            ref var slot = ref i < drawData->WeaponData.Length ? ref drawData->WeaponData[i] : ref drawData->UnkWeaponData[i - drawData->WeaponData.Length];
            if (!watch.Models.Contains(slot.ModelId.Id) ||
                PropAttachTypes.CodeInSlot(new ReadOnlySpan<byte>((byte*)Unsafe.AsPointer(ref slot), sizeof(DrawObjectData))) is not { } code)
                continue;
            var model = slot.ModelId.Id;
            watch.Models.Remove(model);
            _log.Debug("Prop {Model} attaches with {Code}.", model, code);
            // Learning saves a file: off the framework thread.
            _ = Task.Run(() => watch.Learn(model, code));
        }
        if (watch.Models.Count == 0)
            _propWatch = null;
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
    /// <param name="actorIndex">A GPose character to play on instead of the GPose target, such as the posed copy of your character.</param>
    public bool Play(ushort timelineId, string? bindingFingerprint = null, ushort? expectedIndex = null, bool face = false, ushort? actorIndex = null)
    {
        RequireFrameworkThread();
        if (timelineId == 0)
            return Fail("Pick a timeline to play.");
        var actor = actorIndex is { } index ? GPoseCharacter(index) : GPoseActor();
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
        session.PlayingFace = face;
        PauseHeldExpressions(session);
        if (face)
            session.FacePlayed = true;
        else
        {
            session.BodyPlayed = true;
            chara->SetMode(CharacterModes.AnimLock, 0);
            chara->Timeline.BaseOverride = timelineId;
            // A held expression in the face slot would keep the timeline's own faces from playing.
            ClearFaceSlot(chara);
            // A sitting or lying clip stays where the actor sat the same way, and goes without heels or the game's seat
            // offset (a chair's moves the body onto the seat) when it stood or sat another way.
            var seated = AnimationPreviewRules.SeatedMode(TimelineKey(timelineId));
            session.HeldOffset = seated == 0 ? null : seated == session.SeatedMode ? session.DrawOffset : Vector3.Zero;
            HoldOffset(chara, session);
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

    /// <summary>
    /// Whether the clip has played to its end on the playback's actor: its control used up its cycles (Havok then holds
    /// LocalTime at the end) or sits at its duration. Null when no live control holds the clip, as after its timeline ended.
    /// </summary>
    public bool? AtEnd(string bindingFingerprint)
    {
        RequireFrameworkThread();
        if (string.IsNullOrEmpty(bindingFingerprint) || _session == null || !_clientState.IsGPosing)
            return null;
        var chara = SessionActor(_session);
        if (chara == null)
            return null;
        hkaDefaultAnimationControl* best = null;
        foreach (var address in Matching(chara, bindingFingerprint))
        {
            var control = (hkaDefaultAnimationControl*)address;
            if (best == null || control->Weight > best->Weight)
                best = control;
        }
        if (best == null)
            return null;
        var duration = best->Binding.ptr->Animation.ptr->Duration;
        return best->MaxCycles >= 0 && best->OverflowCount >= best->MaxCycles ||
               float.IsFinite(best->LocalTime) && best->LocalTime >= duration - EndTolerance;
    }

    /// <summary> The timeline the playback's character runs in its base slot now; null without a playback. </summary>
    public ushort? BaseTimeline()
    {
        RequireFrameworkThread();
        if (_session is not { } session || !_clientState.IsGPosing)
            return null;
        var chara = SessionActor(session);
        return chara == null ? null : chara->Timeline.TimelineSequencer.TimelineIds[0];
    }

    /// <summary> Puts the actor's mode, base override and slot speeds back and plays its base again. </summary>
    public void Stop()
    {
        RequireFrameworkThread();
        _propWatch = null;
        if (_session == null)
            return;
        StopSession(restore: true);
        Succeed("Stopped.");
    }

    private void OnUpdate(IFramework framework)
    {
        try { WatchProps(); }
        catch (Exception e)
        {
            _log.Warning(e, "Could not read the preview's props.");
            _propWatch = null;
        }
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
            // An expression picked in GPose's emote list while the preview plays is paused too, and played
            // after the stop instead of the one before; a body preview takes it off the face again.
            if (PauseHeldExpressions(session) && !session.PlayingFace)
                ClearFaceSlot(chara);
            if (session.SpeedSet)
                Apply(chara, session);
            HoldOffset(chara, session);
        }
        catch (Exception e)
        {
            _log.Warning(e, "The animation preview playback failed.");
            // The character gets its speeds, draw offset and timeline back, as a stop gives them.
            try { StopSession(restore: true); }
            catch (Exception restore) { _log.Warning(restore, "Could not restore the character after the failed playback."); }
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
            DrawOffset = chara->GameObject.DrawOffset,
            GameOffset = GameOffset(chara),
            SeatedMode = chara->Mode is CharacterModes.InPositionLoop or CharacterModes.EmoteLoop && chara->ModeParam is >= 1 and <= 3
                ? chara->ModeParam
                : (byte)0,
        };
    }

    private string? TimelineKey(ushort timelineId)
        => _data.GetExcelSheet<ActionTimeline>().GetRowOrDefault(timelineId)?.Key.ExtractText();

    /// <summary>
    /// The draw offset the game gives the actor itself, under any Simple Heels offset: a chair sit on furniture moves
    /// it onto the seat this way (0, -0.5, 0.42 on an inn bed's edge, seen 2026-10-27). Simple Heels adds its offset
    /// inside SetDrawOffset, so setting 0 shows what it adds; the game's offset is put back at once.
    /// </summary>
    private static Vector3 GameOffset(Character* chara)
    {
        Vector3 drawn = chara->GameObject.DrawOffset;
        chara->GameObject.SetDrawOffset(0, 0, 0);
        var game = drawn - (Vector3)chara->GameObject.DrawOffset;
        chara->GameObject.SetDrawOffset(game.X, game.Y, game.Z);
        return game;
    }

    /// <summary>
    /// Keeps the actor's draw offset at <see cref="Session.HeldOffset"/>, or puts the game's own offset back when there
    /// is none. Simple Heels drops its heel offset only in the sitting modes, so a sitting clip in AnimLock floats by the
    /// heels; and the sitting modes play their own loop instead of the clip (seen in game 2026-10-27). Simple Heels takes
    /// a SetDrawOffset as the base it adds the heels to, so the base is set to what cancels them. Without Simple Heels
    /// the base is the offset itself.
    /// </summary>
    private static void HoldOffset(Character* chara, Session session)
    {
        if (session.HeldOffset is not { } held)
        {
            if (session.OffsetBase != null)
            {
                session.OffsetBase = null;
                chara->GameObject.SetDrawOffset(session.GameOffset.X, session.GameOffset.Y, session.GameOffset.Z);
            }
            return;
        }
        Vector3 current = chara->GameObject.DrawOffset;
        if (Vector3.Distance(current, held) <= OffsetTolerance)
            return;
        var offsetBase = (session.OffsetBase ?? session.GameOffset) + held - current;
        session.OffsetBase = offsetBase;
        chara->GameObject.SetDrawOffset(offsetBase.X, offsetBase.Y, offsetBase.Z);
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
        RestoreHeldExpressions(session);
        var chara = SessionActor(session);
        if (chara == null)
            return;
        try
        {
            if (session.SpeedSet)
                for (var i = 0; i < SpeedSlots.Length; i++)
                    chara->Timeline.TimelineSequencer.SetSlotSpeed(SpeedSlots[i], session.Speeds[i]);
            session.HeldOffset = null;
            HoldOffset(chara, session);
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

    /// <summary> The GPose character at <paramref name="index"/>; never a character of the world, for the same reason. </summary>
    private ICharacter? GPoseCharacter(ushort index)
        => _clientState.IsGPosing && index >= FirstGPoseIndex && _objects[index] is ICharacter { Address: not 0 } actor ? actor : null;

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
        // The posed player may be gone while GPose goes on; its held expression still plays again.
        if (_session is { } session && _clientState.IsGPosing)
            RestoreHeldExpressions(session);
        _session = null;
        Status = status;
    }

    /// <summary>
    /// Pauses GPose's held expressions for the posed player (<see cref="GPoseExpressionRecords"/>), keeping each one's
    /// emote for the stop; a newer pick in the same record replaces it. Other actors have none. True when one was paused.
    /// </summary>
    private bool PauseHeldExpressions(Session session)
    {
        if (session.ObjectIndex != GPosePlayerIndex || !HeldExpressionRecords(out var start, out var found))
            return false;
        var controller = new Span<byte>(start, EventGPoseController.StructSize);
        var paused = false;
        foreach (var at in found)
        {
            var emote = BinaryPrimitives.ReadUInt16LittleEndian(controller[at..]);
            if (emote == 0)
                continue;
            session.HeldExpressions[at] = emote;
            BinaryPrimitives.WriteUInt16LittleEndian(controller[at..], 0);
            paused = true;
        }
        return paused;
    }

    /// <summary> Puts paused expressions back where their record still names the player and nothing was picked since. </summary>
    private void RestoreHeldExpressions(Session session)
    {
        if (session.HeldExpressions.Count == 0)
            return;
        try
        {
            if (!HeldExpressionRecords(out var start, out var found))
                return;
            var controller = new Span<byte>(start, EventGPoseController.StructSize);
            foreach (var (at, emote) in session.HeldExpressions)
                if (found.Contains(at) && BinaryPrimitives.ReadUInt16LittleEndian(controller[at..]) == 0)
                    BinaryPrimitives.WriteUInt16LittleEndian(controller[at..], emote);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not put GPose's held expression back after the animation preview playback.");
        }
        finally
        {
            session.HeldExpressions.Clear();
        }
    }

    /// <summary> GPose's controller and the emote fields of its records naming the local player; false outside GPose. </summary>
    private bool HeldExpressionRecords(out byte* controller, out List<int> found)
    {
        controller = null;
        found = [];
        if (!_clientState.IsGPosing || _objects.LocalPlayer is not { Address: not 0 } player)
            return false;
        var events = EventFramework.Instance();
        if (events == null)
            return false;
        controller = (byte*)&events->EventSceneModule.EventGPoseController;
        found = GPoseExpressionRecords.Find(new ReadOnlySpan<byte>(controller, EventGPoseController.StructSize), (ulong)player.Address, player.EntityId);
        return true;
    }

    /// <summary> Empties the face slot, and the timeline it falls back to, so nothing holds the face. </summary>
    private static void ClearFaceSlot(Character* chara)
    {
        ref var sequencer = ref chara->Timeline.TimelineSequencer;
        if (sequencer.GetSchedulerTimeline(FaceSlot) == null && sequencer.TimelineIds[(int)FaceSlot] == 0)
            return;
        sequencer.TimelineIds2[(int)FaceSlot] = 0;
        sequencer.TimelineIds3[(int)FaceSlot] = 0;
        sequencer.TimelineIds4[(int)FaceSlot] = 0;
        sequencer.SetSlotTimeline(FaceSlot, 0);
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
