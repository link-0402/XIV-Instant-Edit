using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationBakeService(AnimationNative native, IFramework framework) : IDisposable
{
    private readonly HashSet<IDisposable> active = [];
    private bool disposed;
    private void Check(CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (disposed) throw new OperationCanceledException(); }
    private static unsafe hkQsTransformf Encode(hkQsTransformf pose, hkQsTransformf reference, sbyte hint)
        => AnimationSkeleton.Transform(AnimationRetarget.Encode(AnimationSkeleton.Transform(pose),
            AnimationSkeleton.Transform(reference), hint));
    public void Dispose() => framework.RunOnFrameworkThread(() =>
    {
        disposed = true;
        foreach (var session in active) session.Dispose();
        active.Clear();
    }).GetAwaiter().GetResult();
    public async Task<byte[]> BakeAsync(byte[] source, byte[] skeleton, AnimationClip clip,
        AnimationBakeRequest request, string outputDirectory, Action<string> status, CancellationToken token, Action validateRuntime)
    {
        native.EnsureAvailable();
        Session? session = null;
        var path = Path.Combine(outputDirectory, Guid.NewGuid().ToString("N") + ".hkx");
        try
        {
            session = await framework.RunOnFrameworkThread(() =>
            {
                Check(token);
                validateRuntime();
                var created = new Session(native, source, skeleton, clip, request, path + ".motion");
                active.Add(created); return created;
            });
            status("Checking the original animation round trip");
            await framework.RunOnFrameworkThread(() => { Check(token); session.Save(path); });
            var unchanged = await File.ReadAllBytesAsync(path, token);
            await framework.RunOnFrameworkThread(() => { Check(token); session.ValidateUnchanged(unchanged); });
            var completed = false;
            while (!completed)
            {
                token.ThrowIfCancellationRequested();
                completed = await framework.RunOnTick(() => { Check(token); validateRuntime(); return session.SampleBatch(token); }, delayTicks: 1);
                status($"Baking {clip.Name}: {session.Progress:P0}");
            }
            token.ThrowIfCancellationRequested();
            if (session.EditsMotion)
            {
                // Managed work over every sample: off the framework thread, so the game keeps its frame rate.
                status($"Applying bone layers to {clip.Name}");
                await Task.Run(() => session.ApplyMotion(token), token);
            }
            await framework.RunOnFrameworkThread(() => { Check(token); validateRuntime(); session.Build(); session.Save(path); });
            var baked = await File.ReadAllBytesAsync(path, token);
            await framework.RunOnFrameworkThread(() => { Check(token); session.BeginValidation(baked); });
            completed = false;
            while (!completed)
            {
                token.ThrowIfCancellationRequested();
                completed = await framework.RunOnTick(() => { Check(token); validateRuntime(); return session.ValidateBatch(token); }, delayTicks: 1);
                status($"Verifying {clip.Name}: {session.ValidationProgress:P0}");
            }
            return new AnimationPap(source).ReplaceHavok(baked);
        }
        finally
        {
            if (session != null && !disposed) await framework.RunOnFrameworkThread(() => { session.Dispose(); active.Remove(session); });
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Samples the clip's motion on its source skeleton into owned memory, on the grid a bake of it
    /// samples, with its float tracks: the unedited motion the bone panels and the live GPose display
    /// apply layers to. Nothing is written.
    /// </summary>
    public async Task<MotionClip> DecodeAsync(byte[] source, byte[] skeleton, AnimationClip clip, AnimationCapture capture,
        string workDirectory, Action<string> status, CancellationToken token, Action validateRuntime)
    {
        native.EnsureAvailable();
        Session? session = null;
        var motionPath = Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + ".motion");
        // A timeline edit with nothing to change: the source's own length on the source's own skeleton.
        var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "", false,
            ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline);
        try
        {
            session = await framework.RunOnFrameworkThread(() =>
            {
                Check(token);
                validateRuntime();
                var created = new Session(native, source, skeleton, clip, request, motionPath);
                active.Add(created); return created;
            });
            var completed = false;
            while (!completed)
            {
                token.ThrowIfCancellationRequested();
                completed = await framework.RunOnTick(() => { Check(token); validateRuntime(); return session.SampleBatch(token); }, delayTicks: 1);
                status($"Reading {clip.Name}: {session.Progress:P0}");
            }
            token.ThrowIfCancellationRequested();
            return await Task.Run(session.Decode, token);
        }
        finally
        {
            if (session != null && !disposed) await framework.RunOnFrameworkThread(() => { session.Dispose(); active.Remove(session); });
            if (File.Exists(motionPath)) File.Delete(motionPath);
        }
    }

    private unsafe sealed class Session : IDisposable
    {
        private readonly AnimationNative native;
        private readonly AnimationNative.Arena arena = new();
        private AnimationNative.Document? doc, skeletonDoc, verification;
        private AnimationNative.Sampler? sampler, rawSampler, verifySampler;
        private AnimationNative.Sampler? targetSampler;
        private hkaAnimationBinding* outputMetadata;
        private readonly AnimationRetarget? retarget;
        private readonly SkeletonDescription sourceDescription = null!, targetDescription = null!;
        private readonly AnimationClip clip;
        private readonly AnimationBakeRequest request;
        private hkaAnimationBinding* originalBinding;
        private hkaAnimation* originalAnimation;
        private hkaAnimation* compressed;
        private hkaSkeleton* skeleton;
        private readonly List<hkQsTransformf[]> expected = [];
        private readonly List<float[]> expectedFloats = [];
        private readonly List<float[]> rawFloats = [];
        private readonly HashSet<int> affected = [];
        private readonly HashSet<int> excluded = [];
        private readonly string[] originalPrints;
        private readonly string[] originalMotionPrints;
        private readonly string motionPath;
        // The edit's bone layers (a timeline edit only), and the source skeleton float slot each
        // float track drives, kept managed so a decode can be read off the framework thread.
        private readonly ImmutableArray<MotionLayer> motion = [];
        private readonly short[] floatSlots = [];
        private readonly short[] sourceTracks = [];
        private readonly sbyte blendHint;
        private readonly int frames;
        // The output's length, and the source's: they differ only when a timeline edit
        // changes the clip's length, which samples the source at the scaled time.
        private readonly float duration, sourceDuration;
        private int frame, validationFrame;
        private bool replaced;
        public float Progress => (float)frame / frames;
        public float ValidationProgress => (float)validationFrame / frames;

        public Session(AnimationNative native, byte[] papBytes, byte[] sklb, AnimationClip clip, AnimationBakeRequest request, string motionPath)
        {
            this.native = native; this.clip = clip; this.request = request;
            originalPrints = []; originalMotionPrints = []; this.motionPath = motionPath;
            try
            {
                var pap = new AnimationPap(papBytes);
                if (!pap.Entries.Any(e => e.Name == clip.Name && e.Binding == clip.BindingIndex))
                    throw new InvalidDataException("The captured PAP clip changed.");
                doc = new AnimationNative.Document(pap.Havok);
                skeletonDoc = new AnimationNative.Document(AnimationPap.SkeletonHavok(sklb));
                if (clip.BindingIndex >= doc.Container->Bindings.Length ||
                    clip.BindingIndex >= doc.Container->Animations.Length)
                    throw new InvalidDataException("The animation or skeleton selection is ambiguous.");
                var expectedSource = clip.Resolution?.Selected?.Skeleton.Fingerprint ?? clip.SkeletonFingerprint;
                sourceDescription = AnimationSkeleton.SelectSource(skeletonDoc.Container, expectedSource,
                    clip.Resolution?.Selected?.Source.LeadingBones ?? 0);
                var sourceSkeleton = AnimationSkeleton.Materialize(sourceDescription, arena);
                originalBinding = doc.Container->Bindings[clip.BindingIndex].ptr;
                originalAnimation = originalBinding->Animation.ptr;
                if (clip.BindingFingerprint.Length > 0 && AnimationNative.Fingerprint(originalBinding) != clip.BindingFingerprint)
                    throw new InvalidDataException("The selected animation binding changed. Refresh the capture.");
                if (doc.Container->Animations[clip.BindingIndex].ptr != originalAnimation)
                    throw new InvalidDataException("PAP animation and binding order disagree.");
                // Repair maps source bone names onto a standard skeleton (the game's
                // own, IVCS or IVCS + YAS), never the live rig: skeleton mods that
                // pile every group into one rig move bones to indices far beyond what
                // other players' skeletons have, and sync plugins refuse such files.
                // Bones the chosen standard lacks are left out by that choice. A timeline
                // save repairs too when it names a standard.
                var repairing = AnimationBakeRules.Repairs(request);
                targetDescription = repairing
                    ? (request.RepairTarget is { } standard ? AnimationBones.Standard(clip, standard)?.Skeleton : null)
                        ?? throw new InvalidDataException("The skeleton to repair onto was not found. Refresh the capture.")
                    : sourceDescription;
                AnimationSkeleton.Validate(targetDescription);
                // Unticked bones are named, so they resolve on whichever skeleton the
                // output is written against - the source's own, or the standard on repair.
                excluded = AnimationBones.Indices(targetDescription, request.ExcludedBones);
                retarget = sourceDescription.Fingerprint == targetDescription.Fingerprint ? null :
                    new AnimationRetarget(sourceDescription, targetDescription, AnimationSkeleton.Channels(originalBinding),
                        dropMissing: repairing);
                skeleton = retarget == null ? sourceSkeleton : AnimationSkeleton.Materialize(targetDescription, arena);
                sampler = new AnimationNative.Sampler(sourceSkeleton, originalBinding);
                var rawBinding = arena.BorrowBinding(originalBinding);
                rawBinding->BlendHint.Storage = 0;
                rawSampler = new AnimationNative.Sampler(sourceSkeleton, rawBinding);
                outputMetadata = arena.BorrowBinding(originalBinding);
                if (retarget != null)
                {
                    outputMetadata->OriginalSkeletonName = AnimationSkeleton.String(targetDescription.Name, arena);
                    outputMetadata->FloatTrackToFloatSlotIndices = arena.Copy<short>(retarget.FloatMap);
                    outputMetadata->PartitionIndices = arena.Copy<short>(retarget.PartitionMap);
                }
                // Own a normal, one-frame destination pose/control for the existing offset and IK adapter.
                var poseAnimation = arena.Alloc<AnimationNative.Interleaved>(); native.Initialize(poseAnimation, originalAnimation);
                poseAnimation->Animation.NumberOfTransformTracks = skeleton->Bones.Length; poseAnimation->Animation.NumberOfFloatTracks = 0;
                poseAnimation->Transforms = arena.Copy<hkQsTransformf>(new ReadOnlySpan<hkQsTransformf>(skeleton->ReferencePose.Data, skeleton->Bones.Length));
                var poseBinding = arena.BorrowBinding(originalBinding); poseBinding->BlendHint.Storage = 0; poseBinding->Animation.ptr = &poseAnimation->Animation;
                poseBinding->TransformTrackToBoneIndices = arena.Copy<short>(Enumerable.Range(0, skeleton->Bones.Length).Select(i => (short)i).ToArray());
                poseBinding->FloatTrackToFloatSlotIndices = default; poseBinding->PartitionIndices = default;
                targetSampler = new AnimationNative.Sampler(skeleton, poseBinding);
                sourceDuration = originalAnimation->Duration;
                duration = request.Operation == AnimationOperation.EditTimeline && request.Timeline?.DurationSeconds is { } length
                    ? length : sourceDuration;
                if (duration != sourceDuration)
                {
                    if (!AnimationPoseRules.ValidDuration(duration)) throw new InvalidDataException("The new length is outside 1 frame to 10 minutes.");
                    if (sourceDuration <= 0) throw new InvalidDataException("This animation has no length to change.");
                    // Root motion and annotations are timed in seconds and have no writer here,
                    // so they would keep the old length while the motion changes.
                    if (originalAnimation->ExtractedMotion.ptr != null)
                        throw new InvalidDataException("This animation moves your character, so its length can't be changed yet.");
                    for (var i = 0; i < originalAnimation->AnnotationTracks.Length; i++)
                        if (originalAnimation->AnnotationTracks[i].Annotations.Length > 0)
                            throw new InvalidDataException("This animation has Havok annotations, so its length can't be changed yet.");
                    // Validation compares against this copy of the source's metadata, so it
                    // must carry the new length too.
                    var metadataAnimation = arena.Alloc<AnimationNative.Interleaved>();
                    native.Initialize(metadataAnimation, originalAnimation);
                    metadataAnimation->Animation.Duration = duration;
                    outputMetadata->Animation = new hkRefPtr<hkaAnimation> { ptr = &metadataAnimation->Animation };
                }
                var sourceFrames = AnimationNative.SourceFrameCount(originalAnimation);
                frames = AnimationPoseRules.SampleCount(duration, sourceFrames);
                if ((long)frames * (skeleton->Bones.Length * sizeof(hkQsTransformf) + (skeleton->FloatSlots.Length + sourceSkeleton->FloatSlots.Length) * 4) > AnimationPap.MaxFileSize)
                    throw new InvalidDataException("This animation exceeds the bake memory limit.");
                floatSlots = new ReadOnlySpan<short>(originalBinding->FloatTrackToFloatSlotIndices.Data,
                    originalBinding->FloatTrackToFloatSlotIndices.Length).ToArray();
                motion = request.Operation == AnimationOperation.EditTimeline && request.Timeline is { } edit ? edit.Motion : [];
                if (MotionEvaluator.Changes(motion))
                {
                    // Refuse before sampling: a layer naming a bone this skeleton lacks would fail only at the end.
                    MotionEvaluator.Validate(motion);
                    MotionEvaluator.CheckBones(motion, targetDescription);
                    if ((long)frames * targetDescription.Bones.Length * MotionClip.Stride * sizeof(float) > MotionClip.MaxBytes)
                        throw new InvalidDataException("This animation is too long to edit its motion.");
                }
                originalPrints = Enumerable.Range(0, doc.Container->Bindings.Length).Select(i => AnimationNative.Fingerprint(doc.Container->Bindings[i].ptr)).ToArray();
                originalMotionPrints = Enumerable.Range(0, doc.Container->Bindings.Length).Select(i => AnimationNative.ExtractedMotionFingerprint(doc.Container->Bindings[i].ptr, motionPath)).ToArray();
                sourceTracks = new ReadOnlySpan<short>(originalBinding->TransformTrackToBoneIndices.Data,
                    originalBinding->TransformTrackToBoneIndices.Length).ToArray();
                blendHint = originalBinding->BlendHint.Storage;
                foreach (var bone in retarget?.MapTracks(sourceTracks) ?? sourceTracks) affected.Add(bone);
                var applicable = request.Capture.Pose.Bones.Where(b => b.Id.Partial == clip.Partial && request.SelectedBones.Contains(b.Id)).ToArray();
                if (request.Operation == AnimationOperation.BakeOffsets && applicable.Length == 0) throw new InvalidDataException("No selected offsets belong to this animation's partial skeleton.");
            }
            catch { Dispose(); throw; }
        }
        private float Time(int index) => index == frames - 1 ? duration : duration * index / (frames - 1);
        private float SourceTime(int index) => index == frames - 1 ? sourceDuration : sourceDuration * index / (frames - 1);
        public void Save(string path) => doc!.Save(path);
        public void ValidateUnchanged(byte[] bytes)
        {
            using var check = new AnimationNative.Document(bytes);
            if (check.Container->Bindings.Length != originalPrints.Length) throw new InvalidDataException("Havok round trip changed the binding count.");
            for (var i = 0; i < originalPrints.Length; i++)
                if (AnimationNative.Fingerprint(check.Container->Bindings[i].ptr) != originalPrints[i] ||
                    AnimationNative.ExtractedMotionFingerprint(check.Container->Bindings[i].ptr, motionPath) != originalMotionPrints[i])
                    throw new InvalidDataException("Havok no-change round trip altered animation data.");
        }
        public bool SampleBatch(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                sampler!.Sample(SourceTime(frame)); rawSampler!.Sample(SourceTime(frame));
                var sourceValues = new ReadOnlySpan<hkQsTransformf>(sampler.Transforms, sampler.BoneCount).ToArray();
                var mappedValues = retarget == null ? sourceValues : retarget.Map(sourceValues.Select(AnimationSkeleton.Transform).ToArray())
                    .Select(AnimationSkeleton.Transform).ToArray();
                targetSampler!.SetPose(mappedValues);
                if (request.Operation == AnimationOperation.BakeOffsets)
                    native.Apply(targetSampler, request.Capture.Pose.Bones, clip.Partial, request.Components, request.SelectedBones);
                var targetValues = new hkQsTransformf[targetSampler.BoneCount];
                for (var i = 0; i < targetValues.Length; i++)
                {
                    targetValues[i] = targetSampler.Pose->LocalPose[i];
                    AnimationNative.CheckTransform(targetValues[i]);
                }
                var values = targetValues;
                for (var i = 0; i < values.Length; i++)
                {
                    AnimationNative.CheckTransform(values[i]);
                    if (!AnimationNative.Near(values[i], mappedValues[i], 0.000001f)) affected.Add(i);
                    // A kept bone the source never tracked still moves when a moving bone
                    // the target lacks sat above it; only a track keeps that motion.
                    if (retarget != null && !AnimationNative.Near(mappedValues[i], skeleton->ReferencePose[i], 0.000001f)) affected.Add(i);
                    if (frame > 0 && Quaternion.Dot(AnimationNative.Rotation(expected[^1][i]), AnimationNative.Rotation(values[i])) < 0)
                    { var q = AnimationNative.Rotation(values[i]); fixed (hkQsTransformf* p = &values[i]) AnimationNative.SetRotation(p, new Quaternion(-q.X, -q.Y, -q.Z, -q.W)); }
                }
                expected.Add(values);
                var floats = new ReadOnlySpan<float>(skeleton->ReferenceFloats.Data, skeleton->FloatSlots.Length).ToArray();
                for (var i = 0; i < originalBinding->FloatTrackToFloatSlotIndices.Length; i++)
                {
                    var src = originalBinding->FloatTrackToFloatSlotIndices[i]; var dst = outputMetadata->FloatTrackToFloatSlotIndices[i];
                    floats[dst] = retarget == null ? sampler.Floats[src] : AnimationRetarget.MapFloat(sampler.Floats[src],
                        sourceDescription.ReferenceFloats[src], targetDescription.ReferenceFloats[dst], originalBinding->BlendHint.Storage);
                }
                var sourceFloats = new ReadOnlySpan<float>(rawSampler.Floats, sourceDescription.FloatNames.Length).ToArray();
                expectedFloats.Add(floats);
                rawFloats.Add(sourceFloats);
                frame++;
            } while (frame < frames && watch.ElapsedMilliseconds < 3);
            return frame == frames;
        }
        /// <summary>Whether the edit's bone layers change the motion, so <see cref="ApplyMotion"/> must run before the build.</summary>
        public bool EditsMotion => MotionEvaluator.Changes(motion);

        /// <summary>
        /// Applies the edit's bone layers to every sampled pose, between sampling and the build: the
        /// layers see the motion at the output's times, and every bone they change gets a track. Touches
        /// managed state only, so it may run off the framework thread.
        /// </summary>
        public void ApplyMotion(CancellationToken token)
        {
            if (!EditsMotion) return;
            if (expected.Count != frames) throw new InvalidOperationException("The animation must be sampled before its bone layers apply.");
            var clip = new MotionClip(targetDescription, duration, frames) { Loops = this.clip.IsLoop };
            for (var f = 0; f < frames; f++)
                for (var b = 0; b < expected[f].Length; b++)
                    clip.Set(f, b, FromHavok(expected[f][b]));
            MotionEvaluator.Apply(clip, motion, token);
            for (var f = 0; f < frames; f++)
            {
                token.ThrowIfCancellationRequested();
                for (var b = 0; b < expected[f].Length; b++)
                {
                    var value = ToHavok(clip.Get(f, b));
                    AnimationNative.CheckTransform(value);
                    if (AnimationNative.Near(value, expected[f][b], 0.000001f)) continue;
                    affected.Add(b);
                    expected[f][b] = value;
                }
            }
            // As sampling does: each rotation on its previous sample's hemisphere, so compression takes the short way.
            for (var f = 1; f < frames; f++)
                for (var b = 0; b < expected[f].Length; b++)
                    if (Quaternion.Dot(AnimationNative.Rotation(expected[f - 1][b]), AnimationNative.Rotation(expected[f][b])) < 0)
                        fixed (hkQsTransformf* p = &expected[f][b]) AnimationNative.SetRotation(p, -AnimationNative.Rotation(*p));
        }

        /// <summary>The sampled motion as owned data, with each sample's float track values. Touches managed state only.</summary>
        public MotionClip Decode()
        {
            if (expected.Count != frames) throw new InvalidOperationException("The animation must be sampled before it can be read.");
            var floats = new float[(long)frames * floatSlots.Length];
            for (var f = 0; f < frames; f++)
                for (var t = 0; t < floatSlots.Length; t++)
                    floats[f * floatSlots.Length + t] = rawFloats[f][floatSlots[t]];
            // A decode never retargets, so the source's tracks are the decoded skeleton's bones.
            var clip = new MotionClip(targetDescription, duration, frames, floats, floatSlots.Length)
            {
                SourceTracks = retarget == null ? [.. sourceTracks] : [.. retarget.MapTracks(sourceTracks)],
                BlendHint = blendHint,
                Loops = this.clip.IsLoop,
            };
            for (var f = 0; f < frames; f++)
                for (var b = 0; b < expected[f].Length; b++)
                    clip.Set(f, b, FromHavok(expected[f][b]));
            return clip;
        }

        private static MotionTransform FromHavok(hkQsTransformf t) => new(AnimationNative.Translation(t), AnimationNative.Rotation(t), AnimationNative.Scale(t));

        private static hkQsTransformf ToHavok(MotionTransform t)
        {
            hkQsTransformf value = default;
            value.Translation.X = t.Position.X; value.Translation.Y = t.Position.Y; value.Translation.Z = t.Position.Z;
            value.Rotation.X = t.Rotation.X; value.Rotation.Y = t.Rotation.Y; value.Rotation.Z = t.Rotation.Z; value.Rotation.W = t.Rotation.W;
            value.Scale.X = t.Scale.X; value.Scale.Y = t.Scale.Y; value.Scale.Z = t.Scale.Z;
            return value;
        }

        public void Build()
        {
            // Preserve original track ordering; append only bones actually changed by the pose/IK.
            var sourceTracks = new ReadOnlySpan<short>(originalBinding->TransformTrackToBoneIndices.Data,
                originalBinding->TransformTrackToBoneIndices.Length).ToArray();
            var tracks = new List<short>(retarget?.MapTracks(sourceTracks) ?? sourceTracks);
            if (tracks.Distinct().Count() != tracks.Count) throw new InvalidDataException("Duplicate track bindings are unsupported.");
            tracks.AddRange(affected.Order().Where(i => !tracks.Contains((short)i)).Select(i => (short)i));
            // Bones the animation never moved get no track, repair included: a track per
            // bone of the target skeleton would bind bones no animation should drive.
            // Unticked bones get no track at all, whatever added them above, so nothing
            // in this animation drives them and in-game physics can move them again.
            AnimationBones.Exclude(tracks, expected,
                new ReadOnlySpan<hkQsTransformf>(skeleton->ReferencePose.Data, skeleton->Bones.Length), excluded);
            // A reference-only repair still needs a sampled channel for a valid interleaved
            // source. Pin a bone the user kept rather than one they left out.
            if (tracks.Count == 0) tracks.Add((short)Enumerable.Range(0, skeleton->Bones.Length).First(i => !excluded.Contains(i)));
            var animation = arena.Alloc<AnimationNative.Interleaved>();
            native.Initialize(animation, originalAnimation);
            animation->Animation.Duration = duration;
            animation->Animation.NumberOfTransformTracks = tracks.Count;
            animation->Transforms = arena.Array<hkQsTransformf>(checked(frames * tracks.Count));
            animation->Floats = arena.Array<float>(checked(frames * originalAnimation->NumberOfFloatTracks));
            for (var f = 0; f < frames; f++)
            {
                for (var t = 0; t < tracks.Count; t++)
                    animation->Transforms[f * tracks.Count + t] = Encode(expected[f][tracks[t]], skeleton->ReferencePose[tracks[t]], originalBinding->BlendHint.Storage);
                for (var t = 0; t < originalAnimation->NumberOfFloatTracks; t++)
                    animation->Floats[f * originalAnimation->NumberOfFloatTracks + t] = rawFloats[f][originalBinding->FloatTrackToFloatSlotIndices[t]];
            }
            var binding = arena.BorrowBinding(outputMetadata);
            binding->TransformTrackToBoneIndices = arena.Copy<short>(tracks.ToArray());
            hkaAnimation* final = &animation->Animation;
            if (originalAnimation->Type is hkaAnimation.AnimationType.SplineCompressedAnimation or
                hkaAnimation.AnimationType.PredictiveCompressedAnimation)
            {
                // Compress only tracks. Avoid allocating annotation copies or extra motion references
                // that would be lost when reattaching the unchanged metadata from the source graph.
                animation->Animation.ExtractedMotion = default;
                animation->Animation.AnnotationTracks = default;
                final = compressed = native.Compress(arena, animation);
            }
            // The compressor must not discard these original channels.
            final->ExtractedMotion = originalAnimation->ExtractedMotion;
            final->AnnotationTracks = originalAnimation->AnnotationTracks;
            binding->Animation = new hkRefPtr<hkaAnimation> { ptr = final };
            // Release samplers before redirecting pointers in the resource graph.
            rawSampler!.Dispose(); rawSampler = null; sampler!.Dispose(); sampler = null;
            doc!.Container->Animations[clip.BindingIndex] = new hkRefPtr<hkaAnimation> { ptr = final };
            doc.Container->Bindings[clip.BindingIndex] = new hkRefPtr<hkaAnimationBinding> { ptr = binding };
            replaced = true;
        }
        public void BeginValidation(byte[] bytes)
        {
            verification = new AnimationNative.Document(bytes);
            if (verification.Container->Bindings.Length != originalPrints.Length) throw new InvalidDataException("Baking changed the binding count.");
            for (var i = 0; i < originalPrints.Length; i++)
            {
                if (i != clip.BindingIndex && AnimationNative.Fingerprint(verification.Container->Bindings[i].ptr) != originalPrints[i])
                    throw new InvalidDataException("Baking changed an unselected animation.");
                if (AnimationNative.ExtractedMotionFingerprint(verification.Container->Bindings[i].ptr, motionPath) != originalMotionPrints[i])
                    throw new InvalidDataException("Baking changed extracted motion.");
            }
            var binding = verification.Container->Bindings[clip.BindingIndex].ptr;
            if (AnimationNative.MetadataFingerprint(binding) != AnimationNative.MetadataFingerprint(outputMetadata))
                throw new InvalidDataException("Baking changed annotations or binding metadata.");
            if (binding->Animation.ptr->Duration != duration || binding->BlendHint.Storage != originalBinding->BlendHint.Storage ||
                binding->Animation.ptr->NumberOfFloatTracks != originalAnimation->NumberOfFloatTracks ||
                binding->Animation.ptr->AnnotationTracks.Length != originalAnimation->AnnotationTracks.Length)
                throw new InvalidDataException("Baking changed required animation metadata.");
            verifySampler = new AnimationNative.Sampler(skeleton, binding);
        }
        public bool ValidateBatch(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                verifySampler!.Sample(Time(validationFrame));
                for (var i = 0; i < verifySampler.BoneCount; i++)
                    if (!AnimationNative.Near(verifySampler.Transforms[i], expected[validationFrame][i], 0.002f))
                        throw new InvalidDataException($"Baked animation failed pose validation at frame {validationFrame}, bone {skeleton->Bones[i].Name.String}. No files were replaced.");
                for (var i = 0; i < skeleton->FloatSlots.Length; i++)
                    if (!float.IsFinite(verifySampler.Floats[i]) || Math.Abs(verifySampler.Floats[i] - expectedFloats[validationFrame][i]) > 0.002f)
                        throw new InvalidDataException("Baking altered a float animation channel.");
                validationFrame++;
            } while (validationFrame < frames && watch.ElapsedMilliseconds < 3);
            return validationFrame == frames;
        }
        public void Dispose()
        {
            verifySampler?.Dispose(); verifySampler = null; verification?.Dispose(); verification = null;
            rawSampler?.Dispose(); rawSampler = null; sampler?.Dispose(); sampler = null;
            targetSampler?.Dispose(); targetSampler = null;
            if (replaced && doc != null)
            {
                doc.Container->Animations[clip.BindingIndex] = new hkRefPtr<hkaAnimation> { ptr = originalAnimation };
                doc.Container->Bindings[clip.BindingIndex] = new hkRefPtr<hkaAnimationBinding> { ptr = originalBinding };
                replaced = false;
            }
            if (compressed != null)
            {
                // These references point into the original graph, which retains ownership.
                compressed->ExtractedMotion = default; compressed->AnnotationTracks = default;
                compressed->VirtDtor(0); compressed = null;
            }
            doc?.Dispose(); doc = null; skeletonDoc?.Dispose(); skeletonDoc = null; arena.Dispose();
        }
    }
}
