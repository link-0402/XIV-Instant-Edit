using System.Collections.Immutable;
using System.Diagnostics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>Whose skeleton a recording follows.</summary>
internal enum RecordingSubject { Self, Target }

/// <summary>Where a recording stands, for the UI.</summary>
internal sealed record RecordingProgress(bool Active, bool Waiting, double Elapsed, double Duration, int Frames, string Character);

/// <summary>
/// Records a character's live skeleton on every framework update. That pose is the one the game
/// renders: the animation with bone physics, Customize+ and LivePose applied, which is what a
/// clipping test in Blender needs to see. Recordings are resampled to <see cref="SampleRate"/>
/// before they leave the plugin.
/// </summary>
internal sealed class AnimationRecorder : IDisposable
{
    public const double SampleRate = 60;
    public const float MinimumSeconds = 0.5f;
    public const float MaximumSeconds = 30f;
    public const float MaximumDelaySeconds = 10f;
    // GPose copies the player into this slot; the overworld player is hidden while posing.
    private const int GPosePlayerIndex = 201;
    private const int MaximumPartials = 16;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly ITargetManager targets;
    private readonly object sync = new();
    private Session? session;
    private bool disposed;

    public AnimationRecorder(IFramework framework, IObjectTable objects, IClientState clientState, ITargetManager targets)
    {
        this.framework = framework; this.objects = objects; this.clientState = clientState; this.targets = targets;
    }

    public bool Active { get { lock (sync) return session != null; } }

    public RecordingProgress? Progress
    {
        get
        {
            lock (sync)
                return session is { } s ? new RecordingProgress(true, s.Waiting, s.Elapsed, s.Duration, s.Times.Count, s.Character) : null;
        }
    }

    /// <summary>
    /// Waits <paramref name="delaySeconds"/>, then records <paramref name="seconds"/> of the
    /// subject's live skeleton. <see cref="Stop"/> ends the recording early and keeps what it has.
    /// </summary>
    public async Task<AnimationTake> RecordAsync(RecordingSubject subject, float seconds, float delaySeconds, CancellationToken token)
    {
        seconds = Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);
        delaySeconds = Math.Clamp(delaySeconds, 0, MaximumDelaySeconds);
        var created = new Session(subject, seconds, delaySeconds, token);
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(AnimationRecorder));
            if (session != null) throw new InvalidOperationException("A recording is already running.");
            session = created;
        }
        framework.Update += OnUpdate;
        using var registration = token.Register(() => created.Fail(new OperationCanceledException(token)));
        Recording recording;
        try { recording = await created.Result.Task.ConfigureAwait(false); }
        finally
        {
            framework.Update -= OnUpdate;
            lock (sync) if (session == created) session = null;
        }
        // Resampling copies tens of megabytes; keep it off the framework thread.
        return await Task.Run(() => recording.Build(), token).ConfigureAwait(false);
    }

    /// <summary>Ends the running recording now, keeping the frames recorded so far.</summary>
    public void Stop()
    {
        lock (sync) session?.RequestStop();
    }

    public void Dispose()
    {
        Session? running;
        lock (sync) { disposed = true; running = session; }
        running?.Fail(new OperationCanceledException("The plugin is unloading."));
        framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework _)
    {
        Session? current;
        lock (sync) current = session;
        if (current == null || current.Result.Task.IsCompleted) return;
        try
        {
            if (current.Tick(this)) current.Finish();
        }
        catch (Exception e)
        {
            current.Fail(e);
        }
    }

    private IGameObject? Resolve(RecordingSubject subject)
    {
        if (subject == RecordingSubject.Target)
            return clientState.IsGPosing ? targets.GPoseTarget : targets.Target;
        if (clientState.IsGPosing && objects[GPosePlayerIndex] is { } posed) return posed;
        return objects.LocalPlayer;
    }

    private static unsafe Skeleton* SkeletonOf(IGameObject gameObject)
    {
        if (gameObject is not ICharacter || gameObject.Address == 0) return null;
        var characterBase = ((Character*)gameObject.Address)->GetCharacterBase();
        return characterBase == null ? null : characterBase->Skeleton;
    }

    /// <summary>
    /// Where each bone of the take is read from. Partial skeletons (body, face, hair, ...) are
    /// merged by bone name; a partial's connecting bone is the body bone it hangs from.
    /// </summary>
    private sealed record Layout(ImmutableArray<AnimationTakeBone> Bones, ImmutableArray<Part> Parts, int PartialCount);

    private sealed record Part(int Partial, nint HavokSkeleton, int BoneCount, ImmutableArray<(int Bone, int Output)> Bones);

    private static unsafe Layout Describe(Skeleton* skeleton)
    {
        if (skeleton->PartialSkeletonCount is 0 or > MaximumPartials || skeleton->PartialSkeletons == null)
            throw new InvalidDataException("The character has no readable skeleton.");
        var bones = ImmutableArray.CreateBuilder<AnimationTakeBone>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = ImmutableArray.CreateBuilder<Part>();
        int[]? body = null;
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null)
            {
                if (p == 0) throw new InvalidDataException("The character's body skeleton has no live pose.");
                continue;
            }
            var havok = pose->Skeleton;
            var count = havok->Bones.Length;
            if (count is < 1 or > AnimationTake.MaximumBones || havok->ParentIndices.Length != count || havok->ReferencePose.Length != count)
                throw new InvalidDataException("The character's skeleton is not readable.");
            AnimationNative.ValidateArray(havok->Bones, AnimationTake.MaximumBones, "bones");
            AnimationNative.ValidateArray(havok->ParentIndices, AnimationTake.MaximumBones, "parents");
            AnimationNative.ValidateArray(havok->ReferencePose, AnimationTake.MaximumBones, "reference transforms");
            var map = new int[count];
            var owned = ImmutableArray.CreateBuilder<(int, int)>();
            for (var i = 0; i < count; i++)
            {
                var name = havok->Bones[i].Name.String;
                if (string.IsNullOrEmpty(name)) throw new InvalidDataException("The character's skeleton has an unnamed bone.");
                if (p > 0 && body != null && i == partial->ConnectedBoneIndex &&
                    partial->ConnectedParentBoneIndex >= 0 && partial->ConnectedParentBoneIndex < body.Length)
                {
                    map[i] = body[partial->ConnectedParentBoneIndex];
                    continue;
                }
                if (byName.TryGetValue(name, out var existing))
                {
                    map[i] = existing;
                    continue;
                }
                var parent = havok->ParentIndices[i];
                if (parent >= i) throw new InvalidDataException("The character's skeleton hierarchy is not readable.");
                map[i] = bones.Count;
                byName[name] = bones.Count;
                owned.Add((i, bones.Count));
                bones.Add(new AnimationTakeBone(name, parent >= 0 ? map[parent] : -1, AnimationSkeleton.Transform(havok->ReferencePose[i])));
            }
            if (p == 0) body = map;
            parts.Add(new Part(p, (nint)havok, count, owned.ToImmutable()));
        }
        return new Layout(bones.ToImmutable(), parts.ToImmutable(), skeleton->PartialSkeletonCount);
    }

    /// <summary>Copies the live local pose of every bone into <paramref name="frame"/>, or reports that the skeleton changed.</summary>
    private static unsafe bool Capture(Skeleton* skeleton, Layout layout, float[] frame)
    {
        if (skeleton->PartialSkeletonCount != layout.PartialCount || skeleton->PartialSkeletons == null) return false;
        foreach (var part in layout.Parts)
        {
            var pose = skeleton->PartialSkeletons[part.Partial].GetHavokPose(0);
            if (pose == null || (nint)pose->Skeleton != part.HavokSkeleton || pose->Skeleton->Bones.Length != part.BoneCount) return false;
            // The synced local pose is what the game renders after physics and pose plugins
            // write into the model pose; reading it does not invalidate the model pose.
            var local = pose->GetSyncedPoseLocalSpace();
            if (local == null || local->Length != part.BoneCount || local->Data == null) return false;
            foreach (var (bone, output) in part.Bones)
                AnimationTake.Write(AnimationSkeleton.Transform(local->Data[bone]), frame.AsSpan(output * AnimationTake.Stride, AnimationTake.Stride));
        }
        return true;
    }

    private sealed record Recording(Layout Layout, List<double> Times, List<float[]> Frames, string Character, DateTime Started)
    {
        public AnimationTake Build()
        {
            var (times, samples) = AnimationTakeTiming.Resample(Times, Frames, Layout.Bones.Length, SampleRate);
            var source = new Dictionary<string, string>
            {
                ["character"] = Character,
                ["recordedAt"] = Started.ToString("O"),
                ["recordedFrames"] = Frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sampleRate"] = SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return new AnimationTake(AnimationTake.RecordingKind, $"Live pose {Started.ToLocalTime():HH:mm:ss}", Layout.Bones,
                times, samples, source: source);
        }
    }

    private sealed class Session(RecordingSubject subject, double seconds, double delay, CancellationToken token)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Layout? layout;
        private ulong gameObjectId;
        private nint address;
        private ushort objectIndex;
        private double started = double.NaN;
        private DateTime startedUtc;
        private volatile bool stopRequested;
        public TaskCompletionSource<Recording> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<double> Times { get; } = [];
        public List<float[]> Frames { get; } = [];
        public string Character { get; private set; } = "";
        public double Duration => seconds;
        public bool Waiting => double.IsNaN(started);
        public double Elapsed => Waiting ? clock.Elapsed.TotalSeconds - delay : clock.Elapsed.TotalSeconds - started;

        public void RequestStop() => stopRequested = true;

        /// <summary>Records one frame. Returns true once the recording is complete.</summary>
        public unsafe bool Tick(AnimationRecorder owner)
        {
            token.ThrowIfCancellationRequested();
            var now = clock.Elapsed.TotalSeconds;
            if (Waiting)
            {
                if (stopRequested) throw new OperationCanceledException("The recording was stopped before it started.");
                if (now < delay) return false;
                var subjectObject = owner.Resolve(subject) ?? throw new InvalidOperationException(subject == RecordingSubject.Target
                    ? "Target a character to record." : "Your character is not available.");
                var skeleton = SkeletonOf(subjectObject);
                if (skeleton == null) throw new InvalidOperationException($"{subjectObject.Name.TextValue} has no skeleton to record.");
                layout = Describe(skeleton);
                gameObjectId = subjectObject.GameObjectId; address = subjectObject.Address; objectIndex = subjectObject.ObjectIndex;
                Character = subjectObject.Name.TextValue;
                started = now; startedUtc = DateTime.UtcNow;
            }
            var current = owner.objects[objectIndex];
            if (current == null || current.Address != address || current.GameObjectId != gameObjectId)
                throw new InvalidOperationException($"{Character} left or was redrawn during the recording.");
            var frame = new float[layout!.Bones.Length * AnimationTake.Stride];
            var live = SkeletonOf(current);
            if (live == null || !Capture(live, layout, frame))
                throw new InvalidOperationException($"{Character}'s skeleton changed during the recording, for example after a gear or appearance change.");
            var time = now - started;
            var done = stopRequested || time >= seconds;
            // Frames closer together than half a sample add nothing to the resampled take
            // but memory, at high frame rates.
            if (Times.Count == 0 || time - Times[^1] >= 0.5 / SampleRate || done && time > Times[^1])
            {
                Times.Add(time);
                Frames.Add(frame);
            }
            return done;
        }

        public void Finish()
        {
            if (Frames.Count == 0) Fail(new InvalidOperationException("No frames were recorded."));
            else Result.TrySetResult(new Recording(layout!, Times, Frames, Character, startedUtc));
        }

        public void Fail(Exception error) => Result.TrySetException(error);
    }
}
