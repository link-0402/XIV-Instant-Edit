using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// Shows bone layers on the GPose actor while it plays the clip: an uncompressed copy of the clip's
/// motion with the layers applied, the same samples a bake compresses, takes the place of the clip's
/// own binding in the actor's live Havok controls. No hooks: the game samples the edited motion
/// itself, so physics follows it as it will follow the saved file, and pause, seek and speed work
/// as for any clip (<see cref="LiveBindings"/>). Only GPose actors (index 200 and up)
/// are touched. Every swapped control is put back on Stop, when the actor leaves or GPose ends,
/// and on unload. A built copy is freed only after no control of the actor has held it for a while;
/// one that might still be held is left allocated rather than freed under the game.
/// Every call must come from the framework thread.
/// </summary>
internal sealed unsafe class MotionLiveDisplay : IDisposable
{
    private const int FirstGPoseIndex = 200;
    private const int MaximumPartials = 16;
    private const int MaximumControls = 256;
    // Framework updates a built copy stays allocated once no control holds it: the game may still
    // read a skeleton being taken down for a frame or two.
    private const int ReleaseUpdates = 300;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly AnimationNative native;
    private readonly IPluginLog log;
    private readonly RuntimeCaptureCache bindings = new();
    // Every built copy still allocated; the session's current one among them.
    private readonly List<Built> built = [];
    private Session? session;
    private Task<Built?>? evaluation;
    private CancellationTokenSource? evaluationCancel;
    private Edits evaluating;
    private long updates;
    private bool disposed;

    /// <summary>
    /// What the display shows over the clip: its bone layers and the bones left out. Two are the same
    /// when they hold the same arrays, as an unchanged document's are.
    /// </summary>
    private readonly record struct Edits(ImmutableArray<MotionLayer> Layers, ImmutableArray<string> Excluded)
    {
        public bool Changes => MotionEvaluator.Changes(Layers) || !Excluded.IsDefaultOrEmpty;
    }

    /// <summary>What a swapped control held before: the clip's own binding and track weights.</summary>
    private struct Original
    {
        public nint Binding;
        public hkArray<byte> Weights;
    }

    private sealed class Session
    {
        public required ushort ObjectIndex { get; init; }
        public required nint Address { get; init; }
        public required ulong GameObjectId { get; init; }
        /// <summary>The fingerprint the clip goes by: swapped controls read as playing it.</summary>
        public required string Fingerprint { get; init; }
        /// <summary>
        /// Every fingerprint a control playing the clip may hold: the clip as captured, and after a save
        /// the saved file's or the file before it, whichever the game has loaded.
        /// </summary>
        public required IReadOnlySet<string> Accepted { get; init; }
        /// <summary>The clip's motion as decoded, which layers apply to.</summary>
        public required MotionClip Base { get; init; }
        /// <summary>The edits to show; a new set is built as soon as the last build ends.</summary>
        public Edits Requested { get; set; } = new([], []);
        public Edits Shown { get; set; } = new([], []);
        /// <summary>The copy to show, or null to show the clip's own motion.</summary>
        public Built? Current { get; set; }
        /// <summary>Swapped controls by address, with what they held.</summary>
        public Dictionary<nint, Original> Swapped { get; } = [];
    }

    /// <summary>One built copy of the clip: its samples, and an animation and binding per source binding.</summary>
    private sealed class Built
    {
        /// <summary>Owns the samples, the shells and the weights handed to controls.</summary>
        public required AnimationNative.Arena Arena { get; init; }
        public required short[] Tracks { get; init; }
        /// <summary>For each of <see cref="Tracks"/>, its place among the clip's own tracks, or -1 for one the layers added.</summary>
        public required int[] Sources { get; init; }
        public required int Frames { get; init; }
        public required int FloatTracks { get; init; }
        public required nint Transforms { get; init; }
        public required nint Floats { get; init; }
        /// <summary>Our binding for each source binding it was swapped in for.</summary>
        public readonly Dictionary<nint, nint> Shells = [];
        /// <summary>The update when no control held it any more; -1 while one may.</summary>
        public long ReleasedAt = -1;
    }

    public MotionLiveDisplay(IFramework framework, IObjectTable objects, IClientState clientState, AnimationNative native, IPluginLog log)
    {
        this.framework = framework;
        this.objects = objects;
        this.clientState = clientState;
        this.native = native;
        this.log = log;
        this.framework.Update += OnUpdate;
    }

    /// <summary>Whether a clip is being shown with its layers, or waits to be.</summary>
    public bool Active => session != null;

    /// <summary>Whether layers are being applied for the display.</summary>
    public bool Building => evaluation != null;

    /// <summary>How many of the actor's controls show the edited motion.</summary>
    public int Showing => session?.Swapped.Count ?? 0;

    /// <summary>Whether the controls show <paramref name="layers"/> now, with nothing left to apply.</summary>
    public bool Shows(ImmutableArray<MotionLayer> layers) => session is { } current && evaluation == null && current.Shown.Layers == layers;

    public string Error { get; private set; } = string.Empty;

    /// <summary>
    /// Starts showing <paramref name="layers"/> over <paramref name="decoded"/> on the GPose actor at
    /// <paramref name="actorIndex"/>, wherever it plays the clip: a binding with <paramref name="fingerprint"/>
    /// or one of <paramref name="alsoAccepted"/>, whose tracks begin with the decoded ones. The
    /// <paramref name="excluded"/> bones lose their tracks, as a save leaves them out.
    /// </summary>
    public bool Start(ushort actorIndex, string fingerprint, IEnumerable<string> alsoAccepted, MotionClip decoded, ImmutableArray<MotionLayer> layers,
        ImmutableArray<string> excluded = default)
    {
        RequireFrameworkThread();
        Stop();
        if (actorIndex < FirstGPoseIndex || !clientState.IsGPosing)
            return Fail("Enter GPose to show the edits live.");
        if (string.IsNullOrEmpty(fingerprint) || decoded.SourceTracks.IsEmpty)
            return Fail("The clip is unknown.");
        if (objects[actorIndex] is not ICharacter { Address: not 0 } actor)
            return Fail("No character to show the edits on.");
        session = new Session
        {
            ObjectIndex = actor.ObjectIndex, Address = actor.Address, GameObjectId = actor.GameObjectId,
            Fingerprint = fingerprint, Base = decoded,
            Accepted = alsoAccepted.Append(fingerprint).Where(print => print.Length > 0).ToHashSet(StringComparer.Ordinal),
        };
        Error = string.Empty;
        Show(layers, excluded);
        return true;
    }

    /// <summary>
    /// The layers to show, and the bones left out. A changed set is applied off the framework thread,
    /// then swapped in.
    /// </summary>
    public void Show(ImmutableArray<MotionLayer> layers, ImmutableArray<string> excluded = default)
    {
        RequireFrameworkThread();
        if (session == null || layers.IsDefault)
            return;
        session.Requested = new Edits(layers, excluded.IsDefault ? [] : excluded);
        Evaluate();
    }

    /// <summary>Puts every swapped control back; the clip plays its own motion again.</summary>
    public void Stop()
    {
        RequireFrameworkThread();
        evaluationCancel?.Cancel();
        if (session is not { } ending)
            return;
        var chara = Actor(ending);
        if (chara != null)
            Restore(ending, chara);
        else
            ending.Swapped.Clear();
        session = null;
        foreach (var copy in built)
            if (copy.ReleasedAt < 0)
                copy.ReleasedAt = updates;
    }

    private void Evaluate()
    {
        if (session is not { } current || evaluation != null || current.Requested == current.Shown)
            return;
        var edits = current.Requested;
        if (!edits.Changes)
        {
            // Nothing to change: the clip shows its own motion.
            current.Shown = edits;
            Swap(current, null);
            return;
        }
        evaluationCancel?.Dispose();
        evaluationCancel = new CancellationTokenSource();
        var token = evaluationCancel.Token;
        var decoded = current.Base;
        evaluating = edits;
        evaluation = Task.Run(() => (Built?)Build(decoded, edits.Layers, edits.Excluded, token), token);
    }

    private void OnUpdate(IFramework _)
    {
        updates++;
        try
        {
            PollEvaluation();
            if (session is { } current)
            {
                var chara = Actor(current);
                if (chara == null)
                {
                    // The actor is gone with its controls, or GPose ended under it.
                    current.Swapped.Clear();
                    End(clientState.IsGPosing ? "The character left. The live edits stopped." : "GPose ended. The live edits stopped.");
                }
                else if (!clientState.IsGPosing)
                {
                    Restore(current, chara);
                    End("GPose ended. The live edits stopped.");
                }
                else
                    Attach(current, chara);
            }
            Release();
        }
        catch (Exception e)
        {
            log.Warning(e, "The live motion display failed.");
            try { Stop(); }
            catch (Exception stop) { log.Warning(stop, "Could not put the clip's own motion back."); }
            Error = "The live edits stopped after an error.";
        }
    }

    private void PollEvaluation()
    {
        if (evaluation is not { IsCompleted: true } done)
            return;
        evaluation = null;
        var edits = evaluating;
        if (done.IsCanceled)
        {
            Evaluate();
            return;
        }
        if (done.IsFaulted)
        {
            var error = done.Exception?.InnerException ?? done.Exception;
            Error = error?.Message ?? "The edits could not be applied.";
            if (session is { } failed)
                failed.Shown = edits;
            Evaluate();
            return;
        }
        var copy = done.Result!;
        if (session is not { } current)
        {
            copy.ReleasedAt = updates;
            built.Add(copy);
            return;
        }
        built.Add(copy);
        current.Shown = edits;
        Error = string.Empty;
        Swap(current, copy);
        Evaluate();
    }

    /// <summary>Makes <paramref name="copy"/> the one shown (null: the clip's own motion) and moves every swapped control to it.</summary>
    private void Swap(Session current, Built? copy)
    {
        var previous = current.Current;
        current.Current = copy;
        if (previous != null && previous != copy && previous.ReleasedAt < 0)
            previous.ReleasedAt = updates;
        var chara = Actor(current);
        if (chara != null)
        {
            if (copy == null)
                Restore(current, chara);
            else
                Attach(current, chara);
        }
    }

    /// <summary>Swaps the current copy into every control of the actor that plays the clip and doesn't show it yet.</summary>
    private void Attach(Session current, Character* chara)
    {
        if (current.Current is not { } copy)
            return;
        ForEachControl(chara, (control, skeleton, resource) =>
        {
            var binding = control->Binding.ptr;
            if (Holder((nint)binding) is { } holder)
            {
                // An older copy moves to the current one; a control nothing recorded keeps what it has.
                if (holder != copy && current.Swapped.TryGetValue((nint)control, out var before))
                    Put(control, before, copy, current.Fingerprint);
                return;
            }
            string print;
            try { print = bindings.BindingFingerprint(binding, skeleton, resource); }
            catch (InvalidDataException) { return; }
            if (!current.Accepted.Contains(print) || !Fits(binding, current.Base))
                return;
            var original = new Original { Binding = (nint)binding, Weights = control->TransformTrackWeights };
            current.Swapped[(nint)control] = original;
            Put(control, original, copy, current.Fingerprint);
        });
    }

    /// <summary>Puts back what every swapped control of the actor held.</summary>
    private void Restore(Session current, Character* chara)
    {
        ForEachControl(chara, (control, _, _) =>
        {
            if (Holder((nint)control->Binding.ptr) == null)
                return;
            if (current.Swapped.TryGetValue((nint)control, out var original))
            {
                control->Binding.ptr = (hkaAnimationBinding*)original.Binding;
                control->TransformTrackWeights = original.Weights;
            }
            else
                log.Warning("A control showed the edited motion without a record of what it held; its copy stays allocated.");
        });
        current.Swapped.Clear();
    }

    private void Put(hkaDefaultAnimationControl* control, Original original, Built copy, string fingerprint)
    {
        var shell = Shell(copy, (hkaAnimationBinding*)original.Binding, fingerprint);
        control->TransformTrackWeights = Weights(copy, original);
        control->Binding.ptr = shell;
        copy.ReleasedAt = -1;
    }

    /// <summary>
    /// The copy's animation and binding for one source binding: the source's metadata (blend hint,
    /// float tracks, partitions, root motion, annotations) borrowed, with the copy's tracks and samples.
    /// </summary>
    private hkaAnimationBinding* Shell(Built copy, hkaAnimationBinding* original, string fingerprint)
    {
        if (copy.Shells.TryGetValue((nint)original, out var made))
            return (hkaAnimationBinding*)made;
        var source = original->Animation.ptr;
        var animation = copy.Arena.Alloc<AnimationNative.Interleaved>();
        native.Initialize(animation, source);
        animation->Animation.NumberOfTransformTracks = copy.Tracks.Length;
        var transforms = checked(copy.Frames * copy.Tracks.Length);
        var floats = checked(copy.Frames * copy.FloatTracks);
        // Havok must never free or grow these: the arena owns them.
        animation->Transforms = new hkArray<hkQsTransformf>
            { Data = (hkQsTransformf*)copy.Transforms, Length = transforms, CapacityAndFlags = unchecked((int)0x80000000) | transforms };
        animation->Floats = new hkArray<float>
            { Data = (float*)copy.Floats, Length = floats, CapacityAndFlags = unchecked((int)0x80000000) | floats };
        var binding = copy.Arena.BorrowBinding(original);
        binding->TransformTrackToBoneIndices = copy.Arena.Copy<short>(copy.Tracks);
        binding->Animation = new hkRefPtr<hkaAnimation> { ptr = &animation->Animation };
        copy.Shells[(nint)original] = (nint)binding;
        // Playback, seeking and the animation listener read this copy as the clip it stands for.
        LiveBindings.Set((nint)binding, (nint)original, fingerprint);
        return binding;
    }

    /// <summary>Track weights for the copy: the control's own for the clip's tracks it kept, full for the tracks the layers added.</summary>
    private static hkArray<byte> Weights(Built copy, Original original)
    {
        if (original.Weights.Length == 0 || original.Weights.Data == null)
            return original.Weights;
        var weights = copy.Arena.Array<byte>(copy.Tracks.Length);
        for (var i = 0; i < copy.Tracks.Length; i++)
            weights.Data[i] = copy.Sources[i] is var from and >= 0 && from < original.Weights.Length ? original.Weights.Data[from] : (byte)255;
        return weights;
    }

    /// <summary>
    /// Whether a playing binding is the clip the decode read, or a save of it: the same blend hint,
    /// float tracks and length, and tracks that begin with the decoded ones (a save keeps a clip's
    /// tracks in order and adds the bones its layers moved after them).
    /// </summary>
    private static bool Fits(hkaAnimationBinding* binding, MotionClip decoded)
    {
        var animation = binding->Animation.ptr;
        if (animation == null || binding->BlendHint.Storage != decoded.BlendHint ||
            animation->NumberOfFloatTracks != decoded.FloatTracks || Math.Abs(animation->Duration - decoded.Duration) > 1e-4f ||
            binding->TransformTrackToBoneIndices.Length < decoded.SourceTracks.Length)
            return false;
        for (var i = 0; i < decoded.SourceTracks.Length; i++)
            if (binding->TransformTrackToBoneIndices[i] != decoded.SourceTracks[i])
                return false;
        return true;
    }

    /// <summary>The built copy whose binding <paramref name="binding"/> is, if any.</summary>
    private Built? Holder(nint binding)
    {
        foreach (var copy in built)
            foreach (var shell in copy.Shells.Values)
                if (shell == binding)
                    return copy;
        return null;
    }

    /// <summary>
    /// Frees built copies no control has held for <see cref="ReleaseUpdates"/> updates. While the
    /// actor is there, a copy any of its controls still holds is kept.
    /// </summary>
    private void Release()
    {
        if (built.Count == 0)
            return;
        HashSet<nint>? held = null;
        var chara = session is { } current ? Actor(current) : null;
        if (chara != null)
        {
            var holding = new HashSet<nint>();
            ForEachControl(chara, (control, _, _) => holding.Add((nint)control->Binding.ptr));
            held = holding;
        }
        for (var i = built.Count - 1; i >= 0; i--)
        {
            var copy = built[i];
            if (copy == session?.Current)
                continue;
            if (held != null && copy.Shells.Values.Any(held.Contains))
            {
                copy.ReleasedAt = -1;
                continue;
            }
            if (copy.ReleasedAt < 0)
                copy.ReleasedAt = updates;
            // A copy no control ever held goes at once.
            if (copy.Shells.Count > 0 && updates - copy.ReleasedAt < ReleaseUpdates)
                continue;
            foreach (var shell in copy.Shells.Values)
                LiveBindings.Remove(shell);
            copy.Arena.Dispose();
            built.RemoveAt(i);
        }
    }

    private void End(string status)
    {
        session = null;
        evaluationCancel?.Cancel();
        Error = status;
        foreach (var copy in built)
            if (copy.ReleasedAt < 0)
                copy.ReleasedAt = updates;
    }

    private bool Fail(string error)
    {
        Error = error;
        return false;
    }

    /// <summary>The session's actor while it is the same object, else null.</summary>
    private Character* Actor(Session current)
    {
        var actor = objects[current.ObjectIndex];
        return actor is ICharacter && actor.Address == current.Address && actor.GameObjectId == current.GameObjectId
            ? (Character*)actor.Address
            : null;
    }

    /// <summary>Every live control of every partial skeleton and both animated-skeleton buffers, once each.</summary>
    private static void ForEachControl(Character* chara, ControlVisitor visit)
    {
        var drawn = chara->GetCharacterBase();
        if (drawn == null || drawn->Skeleton == null)
            return;
        var skeleton = drawn->Skeleton;
        if (skeleton->PartialSkeletonCount is <= 0 or > MaximumPartials || skeleton->PartialSkeletons == null)
            return;
        var seen = new HashSet<nint>();
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
                    if (control == null || control->Binding.ptr == null || control->Binding.ptr->Animation.ptr == null || !seen.Add((nint)control))
                        continue;
                    resource ??= partial->SkeletonResourceHandle->FileName.Utf8();
                    visit(control, animated->Skeleton, resource);
                }
            }
        }
    }

    private delegate void ControlVisitor(hkaDefaultAnimationControl* control, hkaSkeleton* skeleton, string resource);

    /// <summary>
    /// Applies <paramref name="layers"/> to a copy of <paramref name="decoded"/> and lays the result out as
    /// an interleaved animation's samples: the clip's own tracks, then a track for every bone the layers
    /// moved that had none, encoded with the clip's blend hint. The <paramref name="excluded"/> bones get
    /// no track, as in a saved file that leaves them out. Runs off the framework thread.
    /// </summary>
    private static Built Build(MotionClip decoded, ImmutableArray<MotionLayer> layers, ImmutableArray<string> excluded, CancellationToken token)
    {
        var edited = decoded.Clone();
        MotionEvaluator.Apply(edited, layers, token);
        var left = new HashSet<int>();
        if (!excluded.IsDefaultOrEmpty)
        {
            var index = MotionEvaluator.Index(decoded.Skeleton);
            foreach (var name in excluded)
                if (index.TryGetValue(name, out var bone))
                    left.Add(bone);
        }
        var tracks = new List<short>();
        var sources = new List<int>();
        for (var t = 0; t < decoded.SourceTracks.Length; t++)
            if (!left.Contains(decoded.SourceTracks[t]))
            {
                tracks.Add(decoded.SourceTracks[t]);
                sources.Add(t);
            }
        var tracked = decoded.SourceTracks.ToHashSet();
        for (var b = 0; b < edited.Bones; b++)
        {
            if (tracked.Contains((short)b) || left.Contains(b))
                continue;
            for (var i = 0; i < edited.Samples; i++)
                if (!Near(edited.Get(i, b), decoded.Get(i, b)))
                {
                    tracks.Add(checked((short)b));
                    sources.Add(-1);
                    break;
                }
        }
        token.ThrowIfCancellationRequested();
        var references = decoded.Skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray();
        var arena = new AnimationNative.Arena();
        try
        {
            var transforms = arena.Alloc<hkQsTransformf>(checked(edited.Samples * tracks.Count));
            var floats = arena.Alloc<float>(checked(edited.Samples * Math.Max(1, decoded.FloatTracks)));
            for (var f = 0; f < edited.Samples; f++)
            {
                if ((f & 63) == 0)
                    token.ThrowIfCancellationRequested();
                for (var t = 0; t < tracks.Count; t++)
                    transforms[f * tracks.Count + t] = Havok(AnimationRetarget.Encode(edited.Get(f, tracks[t]), references[tracks[t]], decoded.BlendHint));
            }
            if (decoded.TrackFloats is { } values)
                values.AsSpan().CopyTo(new Span<float>(floats, values.Length));
            return new Built
            {
                Arena = arena, Tracks = [.. tracks], Sources = [.. sources], Frames = edited.Samples, FloatTracks = decoded.FloatTracks,
                Transforms = (nint)transforms, Floats = (nint)floats,
            };
        }
        catch
        {
            arena.Dispose();
            throw;
        }
    }

    private static bool Near(MotionTransform a, MotionTransform b) =>
        Vector3.DistanceSquared(a.Position, b.Position) <= 1e-12f && Vector3.DistanceSquared(a.Scale, b.Scale) <= 1e-12f &&
        1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) <= 1e-6f;

    private static hkQsTransformf Havok(MotionTransform t)
    {
        hkQsTransformf value = default;
        value.Translation.X = t.Position.X; value.Translation.Y = t.Position.Y; value.Translation.Z = t.Position.Z;
        value.Rotation.X = t.Rotation.X; value.Rotation.Y = t.Rotation.Y; value.Rotation.Z = t.Rotation.Z; value.Rotation.W = t.Rotation.W;
        value.Scale.X = t.Scale.X; value.Scale.Y = t.Scale.Y; value.Scale.Z = t.Scale.Z;
        return value;
    }

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("The live motion display must run on the framework thread.");
    }

    /// <summary>
    /// Puts the clip's own motion back while the actor is there, then frees every copy no control can
    /// hold any more. A copy that might still be held is left allocated: a leak, never a crash.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnUpdate;
        evaluationCancel?.Cancel();
        try
        {
            if (framework.IsInFrameworkUpdateThread)
                Teardown();
            else
                framework.RunOnFrameworkThread(Teardown).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not put the clip's own motion back while unloading.");
        }
    }

    private void Teardown()
    {
        if (session is { } current)
        {
            var chara = Actor(current);
            if (chara != null)
                Restore(current, chara);
            session = null;
        }
        foreach (var copy in built.ToArray())
        {
            // Only a copy no control has held for a while is freed. One shown recently stays
            // allocated: the game reads it without any of this plugin's code, so a leak is safe.
            if (copy.Shells.Count > 0 && (copy.ReleasedAt < 0 || updates - copy.ReleasedAt < ReleaseUpdates))
            {
                log.Warning("A live motion copy may still be read by the game; it stays allocated.");
                continue;
            }
            foreach (var shell in copy.Shells.Values)
                LiveBindings.Remove(shell);
            copy.Arena.Dispose();
            built.Remove(copy);
        }
    }
}
