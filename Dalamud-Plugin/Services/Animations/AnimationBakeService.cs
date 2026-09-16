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
            status("Checking the original animation round trip…");
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

    public async Task<byte[]> CreateStartupAsync(AnimationStartupSource loop, AnimationStartupSource startup,
        AnimationStartupSource? idle, AnimationStartupOptions options, string outputDirectory, Action<string> status,
        CancellationToken token, Action validateRuntime)
    {
        native.EnsureAvailable();
        StartupSession? session = null;
        var path = Path.Combine(outputDirectory, Guid.NewGuid().ToString("N") + ".hkx");
        try
        {
            session = await framework.RunOnFrameworkThread(() =>
            {
                Check(token);
                validateRuntime();
                var created = new StartupSession(native, loop, startup, idle, options, path + ".motion");
                active.Add(created); return created;
            });
            status("Checking the original startup animation round trip…");
            await framework.RunOnFrameworkThread(() => { Check(token); session.Save(path); });
            var unchanged = await File.ReadAllBytesAsync(path, token);
            await framework.RunOnFrameworkThread(() => { Check(token); session.ValidateUnchanged(unchanged); });
            var completed = false;
            while (!completed)
            {
                token.ThrowIfCancellationRequested();
                completed = await framework.RunOnTick(() => { Check(token); validateRuntime(); return session.SampleBatch(token); }, delayTicks: 1);
                status($"Generating startup transition: {session.Progress:P0}");
            }
            token.ThrowIfCancellationRequested();
            await framework.RunOnFrameworkThread(() => { Check(token); validateRuntime(); session.Build(); session.Save(path); });
            var baked = await File.ReadAllBytesAsync(path, token);
            await framework.RunOnFrameworkThread(() => { Check(token); session.BeginValidation(baked); });
            completed = false;
            while (!completed)
            {
                token.ThrowIfCancellationRequested();
                completed = await framework.RunOnTick(() => { Check(token); validateRuntime(); return session.ValidateBatch(token); }, delayTicks: 1);
                status($"Verifying startup transition: {session.ValidationProgress:P0}");
            }
            return new AnimationPap(startup.Pap).ReplaceHavok(baked);
        }
        finally
        {
            if (session != null && !disposed) await framework.RunOnFrameworkThread(() => { session.Dispose(); active.Remove(session); });
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private unsafe sealed class StartupSession : IDisposable
    {
        private readonly AnimationNative native;
        private readonly AnimationNative.Arena arena = new();
        private readonly Source startup = null!;
        private readonly Source loop = null!;
        private readonly Source? idle;
        private readonly AnimationStartupOptions options;
        private readonly SkeletonDescription targetDescription;
        private readonly hkaSkeleton* targetSkeleton;
        private readonly hkaAnimationBinding* originalStartupBinding;
        private readonly hkaAnimation* originalStartupAnimation;
        private readonly string[] originalPrints;
        private readonly string[] originalMotionPrints;
        private readonly string motionPath;
        private readonly hkQsTransformf[] startPose;
        private readonly hkQsTransformf[] endPose;
        private readonly float[] startFloats;
        private readonly float[] endFloats;
        private readonly List<hkQsTransformf[]> expected = [];
        private readonly List<float[]> expectedFloats = [];
        private readonly HashSet<int> affected = [];
        private hkaAnimationBinding* outputMetadata;
        private hkaAnimation* compressed;
        private AnimationNative.Sampler? verifySampler;
        private AnimationNative.Document? verification;
        private int frame, validationFrame;
        private bool replaced;
        private readonly int frames;

        public float Progress => frames == 0 ? 1 : (float)frame / frames;
        public float ValidationProgress => frames == 0 ? 1 : (float)validationFrame / frames;

        public StartupSession(AnimationNative native, AnimationStartupSource loop, AnimationStartupSource startup,
            AnimationStartupSource? idle, AnimationStartupOptions options, string motionPath)
        {
            try
            {
                this.native = native; this.options = options; this.motionPath = motionPath;
                if (!AnimationPoseRules.ValidStartupDuration(options.DurationSeconds))
                    throw new InvalidDataException("Startup transition duration must be between 0 and 2 seconds.");
                targetDescription = startup.Clip.TargetSkeleton ?? throw new InvalidDataException("The live target skeleton is unavailable.");
                AnimationSkeleton.Validate(targetDescription);
                if (loop.Clip.Partial != startup.Clip.Partial || idle?.Clip.Partial != startup.Clip.Partial ||
                    loop.Clip.TargetSkeleton?.Fingerprint != targetDescription.Fingerprint ||
                    idle?.Clip.TargetSkeleton?.Fingerprint != targetDescription.Fingerprint)
                    throw new InvalidDataException("The loop and startup target skeletons are incompatible.");
                targetSkeleton = AnimationSkeleton.Materialize(targetDescription, arena);
                this.startup = new Source(startup, targetDescription, arena, false);
                this.loop = new Source(loop, targetDescription, arena, true);
                this.idle = idle == null ? null : new Source(idle, targetDescription, arena, true);
                originalStartupBinding = this.startup.Binding;
                originalStartupAnimation = this.startup.Animation;
                outputMetadata = arena.BorrowBinding(originalStartupBinding);
                if (this.startup.Retarget is { } startupRetarget)
                {
                    outputMetadata->OriginalSkeletonName = AnimationSkeleton.String(targetDescription.Name, arena);
                    outputMetadata->FloatTrackToFloatSlotIndices = arena.Copy<short>(startupRetarget.FloatMap);
                    outputMetadata->PartitionIndices = arena.Copy<short>(startupRetarget.PartitionMap);
                }
                originalPrints = Enumerable.Range(0, this.startup.Document.Container->Bindings.Length)
                    .Select(i => AnimationNative.Fingerprint(this.startup.Document.Container->Bindings[i].ptr)).ToArray();
                originalMotionPrints = Enumerable.Range(0, this.startup.Document.Container->Bindings.Length)
                    .Select(i => AnimationNative.ExtractedMotionFingerprint(this.startup.Document.Container->Bindings[i].ptr, motionPath)).ToArray();
                (startPose, startFloats) = this.idle == null ? ReferencePose() : Sample(this.idle);
                (endPose, endFloats) = Sample(this.loop);
                for (var i = 0; i < targetDescription.Bones.Length; i++)
                    if (!AnimationNative.Near(startPose[i], targetSkeleton->ReferencePose[i], 0.000001f) ||
                        !AnimationNative.Near(endPose[i], targetSkeleton->ReferencePose[i], 0.000001f))
                        affected.Add(i);
                frames = AnimationPoseRules.StartupSampleCount(options.DurationSeconds);
                if ((long)frames * (targetDescription.Bones.Length * sizeof(hkQsTransformf) + targetDescription.FloatNames.Length * sizeof(float)) > AnimationPap.MaxFileSize)
                    throw new InvalidDataException("The generated startup animation exceeds the bake memory limit.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private (hkQsTransformf[] Pose, float[] Floats) ReferencePose()
        {
            var pose = new hkQsTransformf[targetDescription.Bones.Length];
            for (var i = 0; i < pose.Length; i++) pose[i] = targetSkeleton->ReferencePose[i];
            return (pose, targetDescription.ReferenceFloats.ToArray());
        }

        private (hkQsTransformf[] Pose, float[] Floats) Sample(Source source)
        {
            source.Sampler!.Sample(0);
            var raw = new ReadOnlySpan<hkQsTransformf>(source.Sampler.Transforms, source.Sampler.BoneCount)
                .ToArray();
            var pose = source.Retarget == null
                ? raw
                : source.Retarget.Map(raw.Select(AnimationSkeleton.Transform).ToArray())
                    .Select(AnimationSkeleton.Transform).ToArray();
            var floats = targetDescription.ReferenceFloats.ToArray();
            for (var i = 0; i < source.Binding->FloatTrackToFloatSlotIndices.Length; i++)
            {
                var sourceSlot = source.Binding->FloatTrackToFloatSlotIndices[i];
                var targetSlot = source.Retarget?.FloatMap[i] ?? sourceSlot;
                if (targetSlot < 0 || targetSlot >= floats.Length)
                    throw new InvalidDataException("The sampled float channel does not fit the target skeleton.");
                floats[targetSlot] = source.Retarget == null ? source.Sampler.Floats[sourceSlot] :
                    AnimationRetarget.MapFloat(source.Sampler.Floats[sourceSlot],
                        source.Description.ReferenceFloats[sourceSlot], targetDescription.ReferenceFloats[targetSlot],
                        source.Binding->BlendHint.Storage);
            }
            for (var i = 0; i < pose.Length; i++) AnimationNative.CheckTransform(pose[i]);
            if (floats.Any(value => !float.IsFinite(value))) throw new InvalidDataException("The sampled float channel is invalid.");
            return (pose, floats);
        }

        public void Save(string path) => startup.Document.Save(path);

        public void ValidateUnchanged(byte[] bytes)
        {
            using var check = new AnimationNative.Document(bytes);
            if (check.Container->Bindings.Length != originalPrints.Length)
                throw new InvalidDataException("Havok round trip changed the startup binding count.");
            for (var i = 0; i < originalPrints.Length; i++)
                if (AnimationNative.Fingerprint(check.Container->Bindings[i].ptr) != originalPrints[i] ||
                    AnimationNative.ExtractedMotionFingerprint(check.Container->Bindings[i].ptr, motionPath) != originalMotionPrints[i])
                    throw new InvalidDataException("Havok no-change round trip altered startup animation data.");
        }

        public bool SampleBatch(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                // A zero-duration transition is an immediate handoff: both
                // serialized samples are the loop endpoint so compressed
                // sources retain a valid two-sample representation.
                var alpha = options.DurationSeconds == 0 ? 1 : frames <= 1 ? 1 : (float)frame / (frames - 1);
                var pose = new hkQsTransformf[targetDescription.Bones.Length];
                for (var i = 0; i < pose.Length; i++)
                {
                    pose[i] = AnimationSkeleton.Transform(AnimationPoseRules.Blend(
                        AnimationSkeleton.Transform(startPose[i]), AnimationSkeleton.Transform(endPose[i]), alpha));
                    AnimationNative.CheckTransform(pose[i]);
                }
                var floats = new float[targetDescription.FloatNames.Length];
                for (var i = 0; i < floats.Length; i++)
                {
                    floats[i] = AnimationPoseRules.BlendFloat(startFloats[i], endFloats[i], alpha);
                    if (!float.IsFinite(floats[i])) throw new InvalidDataException("The generated float channel is invalid.");
                }
                expected.Add(pose); expectedFloats.Add(floats); frame++;
            } while (frame < frames && watch.ElapsedMilliseconds < 3);
            return frame == frames;
        }

        public void Build()
        {
            var tracks = new List<short>();
            var originalTracks = new ReadOnlySpan<short>(originalStartupBinding->TransformTrackToBoneIndices.Data,
                originalStartupBinding->TransformTrackToBoneIndices.Length);
            if (startup.Retarget == null)
                tracks.AddRange(originalTracks.ToArray());
            else
                foreach (var sourceBone in originalTracks)
                {
                    if (sourceBone < 0 || sourceBone >= startup.Retarget.BoneMap.Length)
                        throw new InvalidDataException("The startup transform track does not fit its source skeleton.");
                    var targetBone = startup.Retarget.BoneMap[sourceBone];
                    if (targetBone >= 0 && !tracks.Contains((short)targetBone)) tracks.Add((short)targetBone);
                }
            if (tracks.Distinct().Count() != tracks.Count)
                throw new InvalidDataException("Duplicate startup track bindings are unsupported.");
            tracks.AddRange(affected.Order().Where(i => !tracks.Contains((short)i)).Select(i => (short)i));
            if (tracks.Count == 0) tracks.Add(0);
            var animation = arena.Alloc<AnimationNative.Interleaved>();
            native.Initialize(animation, originalStartupAnimation);
            animation->Animation.NumberOfTransformTracks = tracks.Count;
            animation->Animation.NumberOfFloatTracks = originalStartupAnimation->NumberOfFloatTracks;
            animation->Animation.Duration = options.DurationSeconds;
            animation->Transforms = arena.Array<hkQsTransformf>(checked(frames * tracks.Count));
            animation->Floats = arena.Array<float>(checked(frames * originalStartupAnimation->NumberOfFloatTracks));
            for (var f = 0; f < frames; f++)
            {
                for (var t = 0; t < tracks.Count; t++)
                    animation->Transforms[f * tracks.Count + t] = Encode(expected[f][tracks[t]],
                        targetSkeleton->ReferencePose[tracks[t]], originalStartupBinding->BlendHint.Storage);
                for (var t = 0; t < originalStartupAnimation->NumberOfFloatTracks; t++)
                {
                    var targetSlot = outputMetadata->FloatTrackToFloatSlotIndices[t];
                    if (targetSlot < 0 || targetSlot >= expectedFloats[f].Length)
                        throw new InvalidDataException("The generated float binding does not fit the target skeleton.");
                    animation->Floats[f * originalStartupAnimation->NumberOfFloatTracks + t] = expectedFloats[f][targetSlot];
                }
            }
            var binding = arena.BorrowBinding(outputMetadata);
            binding->TransformTrackToBoneIndices = arena.Copy<short>(tracks.ToArray());
            hkaAnimation* final = &animation->Animation;
            if (originalStartupAnimation->Type is hkaAnimation.AnimationType.SplineCompressedAnimation or
                hkaAnimation.AnimationType.PredictiveCompressedAnimation)
            {
                animation->Animation.ExtractedMotion = default;
                animation->Animation.AnnotationTracks = default;
                final = compressed = native.Compress(arena, animation);
            }
            final->ExtractedMotion = originalStartupAnimation->ExtractedMotion;
            final->AnnotationTracks = originalStartupAnimation->AnnotationTracks;
            binding->Animation = new hkRefPtr<hkaAnimation> { ptr = final };
            outputMetadata = binding;
            startup.Document.Container->Animations[startup.Input.Clip.BindingIndex] = new hkRefPtr<hkaAnimation> { ptr = final };
            startup.Document.Container->Bindings[startup.Input.Clip.BindingIndex] = new hkRefPtr<hkaAnimationBinding> { ptr = binding };
            replaced = true;
        }

        public void BeginValidation(byte[] bytes)
        {
            verification = new AnimationNative.Document(bytes);
            if (verification.Container->Bindings.Length != originalPrints.Length)
                throw new InvalidDataException("Generating startup changed the binding count.");
            for (var i = 0; i < originalPrints.Length; i++)
            {
                if (i != startup.Input.Clip.BindingIndex &&
                    AnimationNative.Fingerprint(verification.Container->Bindings[i].ptr) != originalPrints[i])
                    throw new InvalidDataException("Generating startup changed an unselected binding.");
                if (AnimationNative.ExtractedMotionFingerprint(verification.Container->Bindings[i].ptr, motionPath) != originalMotionPrints[i])
                    throw new InvalidDataException("Generating startup changed extracted motion.");
            }
            var binding = verification.Container->Bindings[startup.Input.Clip.BindingIndex].ptr;
            if (AnimationNative.MetadataFingerprint(binding) != AnimationNative.MetadataFingerprint(outputMetadata))
                throw new InvalidDataException("Generating startup changed required binding metadata.");
            if (binding->Animation.ptr->Duration != options.DurationSeconds ||
                binding->BlendHint.Storage != originalStartupBinding->BlendHint.Storage ||
                binding->Animation.ptr->NumberOfFloatTracks != originalStartupAnimation->NumberOfFloatTracks ||
                binding->Animation.ptr->AnnotationTracks.Length != originalStartupAnimation->AnnotationTracks.Length)
                throw new InvalidDataException("Generating startup changed required animation metadata.");
            verifySampler = new AnimationNative.Sampler(targetSkeleton, binding);
        }

        public bool ValidateBatch(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                var time = frames <= 1 ? options.DurationSeconds : options.DurationSeconds * validationFrame / (frames - 1);
                verifySampler!.Sample(time);
                for (var i = 0; i < verifySampler.BoneCount; i++)
                    if (!AnimationNative.Near(verifySampler.Transforms[i], expected[validationFrame][i], 0.002f))
                        throw new InvalidDataException($"Generated startup failed pose validation at frame {validationFrame}, bone {targetDescription.Bones[i].Name}.");
                for (var i = 0; i < targetDescription.FloatNames.Length; i++)
                    if (!float.IsFinite(verifySampler.Floats[i]) || Math.Abs(verifySampler.Floats[i] - expectedFloats[validationFrame][i]) > 0.002f)
                        throw new InvalidDataException("Generating startup altered a float animation channel.");
                validationFrame++;
            } while (validationFrame < frames && watch.ElapsedMilliseconds < 3);
            return validationFrame == frames;
        }

        public void Dispose()
        {
            verifySampler?.Dispose(); verifySampler = null;
            verification?.Dispose(); verification = null;
            loop?.Dispose(); idle?.Dispose();
            if (replaced && startup != null && startup.Document != null)
            {
                startup.Document.Container->Animations[startup.Input.Clip.BindingIndex] = new hkRefPtr<hkaAnimation> { ptr = originalStartupAnimation };
                startup.Document.Container->Bindings[startup.Input.Clip.BindingIndex] = new hkRefPtr<hkaAnimationBinding> { ptr = originalStartupBinding };
                replaced = false;
            }
            if (compressed != null)
            {
                compressed->ExtractedMotion = default; compressed->AnnotationTracks = default;
                compressed->VirtDtor(0); compressed = null;
            }
            startup?.Dispose(); arena.Dispose();
        }

        private unsafe sealed class Source : IDisposable
        {
            public AnimationStartupSource Input { get; }
            public AnimationNative.Document Document { get; private set; } = null!;
            private AnimationNative.Document SkeletonDocument { get; set; } = null!;
            public hkaAnimationBinding* Binding { get; private set; }
            public hkaAnimation* Animation { get; private set; }
            public SkeletonDescription Description { get; private set; } = null!;
            public hkaSkeleton* Skeleton { get; private set; }
            public AnimationRetarget? Retarget { get; private set; }
            public AnimationNative.Sampler? Sampler { get; private set; }

            public Source(AnimationStartupSource input, SkeletonDescription target, AnimationNative.Arena arena, bool sample)
            {
                Input = input;
                try
                {
                    var pap = new AnimationPap(input.Pap);
                    Document = new AnimationNative.Document(pap.Havok);
                    SkeletonDocument = new AnimationNative.Document(AnimationPap.SkeletonHavok(input.Skeleton));
                    if (input.Clip.BindingIndex < 0 || input.Clip.BindingIndex >= Document.Container->Bindings.Length ||
                        input.Clip.BindingIndex >= Document.Container->Animations.Length)
                        throw new InvalidDataException("The startup source binding is unavailable.");
                    Binding = Document.Container->Bindings[input.Clip.BindingIndex].ptr;
                    Animation = Binding->Animation.ptr;
                    if (Document.Container->Animations[input.Clip.BindingIndex].ptr != Animation)
                        throw new InvalidDataException("PAP animation and binding order disagree.");
                    var expected = input.Clip.Resolution?.Selected?.Skeleton.Fingerprint ?? input.Clip.SkeletonFingerprint;
                    Description = AnimationSkeleton.SelectSource(
                        AnimationSkeleton.DescribeSources(SkeletonDocument.Root, SkeletonDocument.Container), expected);
                    Skeleton = AnimationSkeleton.Materialize(Description, arena);
                    AnimationNative.Sampler.Validate(Skeleton, Binding);
                    if (input.Clip.BindingFingerprint.Length > 0 && AnimationNative.Fingerprint(Binding) != input.Clip.BindingFingerprint)
                        throw new InvalidDataException("The selected animation source changed. Refresh the capture.");
                    Retarget = Description.Fingerprint == target.Fingerprint ? null :
                        new AnimationRetarget(Description, target, AnimationSkeleton.Channels(Binding));
                    if (sample) Sampler = new AnimationNative.Sampler(Skeleton, Binding);
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                Sampler?.Dispose(); Sampler = null;
                Document?.Dispose(); Document = null!;
                SkeletonDocument?.Dispose(); SkeletonDocument = null!;
            }
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
        private readonly string[] originalPrints;
        private readonly string[] originalMotionPrints;
        private readonly string motionPath;
        private readonly int frames;
        private readonly float duration;
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
                sourceDescription = AnimationSkeleton.SelectSource(AnimationSkeleton.DescribeSources(skeletonDoc.Root, skeletonDoc.Container), expectedSource);
                var sourceSkeleton = AnimationSkeleton.Materialize(sourceDescription, arena);
                originalBinding = doc.Container->Bindings[clip.BindingIndex].ptr;
                originalAnimation = originalBinding->Animation.ptr;
                if (clip.BindingFingerprint.Length > 0 && AnimationNative.Fingerprint(originalBinding) != clip.BindingFingerprint)
                    throw new InvalidDataException("The selected animation binding changed. Refresh the capture.");
                if (doc.Container->Animations[clip.BindingIndex].ptr != originalAnimation)
                    throw new InvalidDataException("PAP animation and binding order disagree.");
                // Repair maps source bone names onto the complete live rig. YAS
                // and other inserted helpers can shift numeric indices even when
                // every original game bone is still present.
                targetDescription = request.Operation == AnimationOperation.RepairSkeleton
                    ? clip.TargetSkeleton ?? throw new InvalidDataException("The live target skeleton is unavailable.")
                    : sourceDescription;
                AnimationSkeleton.Validate(targetDescription);
                retarget = sourceDescription.Fingerprint == targetDescription.Fingerprint ? null :
                    new AnimationRetarget(sourceDescription, targetDescription, AnimationSkeleton.Channels(originalBinding));
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
                duration = originalAnimation->Duration;
                var sourceFrames = AnimationNative.SourceFrameCount(originalAnimation);
                frames = AnimationPoseRules.SampleCount(duration, sourceFrames);
                if ((long)frames * (skeleton->Bones.Length * sizeof(hkQsTransformf) + (skeleton->FloatSlots.Length + sourceSkeleton->FloatSlots.Length) * 4) > AnimationPap.MaxFileSize)
                    throw new InvalidDataException("This animation exceeds the bake memory limit.");
                originalPrints = Enumerable.Range(0, doc.Container->Bindings.Length).Select(i => AnimationNative.Fingerprint(doc.Container->Bindings[i].ptr)).ToArray();
                originalMotionPrints = Enumerable.Range(0, doc.Container->Bindings.Length).Select(i => AnimationNative.ExtractedMotionFingerprint(doc.Container->Bindings[i].ptr, motionPath)).ToArray();
                var sourceTracks = new ReadOnlySpan<short>(originalBinding->TransformTrackToBoneIndices.Data,
                    originalBinding->TransformTrackToBoneIndices.Length).ToArray();
                foreach (var bone in retarget?.MapTracks(sourceTracks) ?? sourceTracks) affected.Add(bone);
                var applicable = request.Capture.Pose.Bones.Where(b => b.Id.Partial == clip.Partial && request.SelectedBones.Contains(b.Id)).ToArray();
                if (request.Operation == AnimationOperation.BakeOffsets && applicable.Length == 0) throw new InvalidDataException("No selected offsets belong to this animation's partial skeleton.");
            }
            catch { Dispose(); throw; }
        }
        private float Time(int index) => index == frames - 1 ? duration : duration * index / (frames - 1);
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
                sampler!.Sample(Time(frame)); rawSampler!.Sample(Time(frame));
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
        public void Build()
        {
            // Preserve original track ordering; append only bones actually changed by the pose/IK.
            var sourceTracks = new ReadOnlySpan<short>(originalBinding->TransformTrackToBoneIndices.Data,
                originalBinding->TransformTrackToBoneIndices.Length).ToArray();
            var tracks = new List<short>(retarget?.MapTracks(sourceTracks) ?? sourceTracks);
            if (tracks.Distinct().Count() != tracks.Count) throw new InvalidDataException("Duplicate track bindings are unsupported.");
            tracks.AddRange(affected.Order().Where(i => !tracks.Contains((short)i)).Select(i => (short)i));
            // Repair retargets onto the live rig, which usually has far more bones than
            // the source ever animated (YAS/IVCS physics and prop chains). Left without
            // any track at all, those bones are whatever the game's own bone-physics
            // system makes of an undriven reference pose, which is where the reported
            // warping comes from. Give every such bone an explicit, pinned reference
            // track instead of omitting it.
            if (retarget != null && request.Operation == AnimationOperation.RepairSkeleton)
                tracks.AddRange(Enumerable.Range(0, skeleton->Bones.Length).Where(i => !tracks.Contains((short)i)).Select(i => (short)i));
            // A reference-only repair still needs a sampled channel for a valid interleaved source.
            if (tracks.Count == 0) tracks.Add(0);
            var animation = arena.Alloc<AnimationNative.Interleaved>();
            native.Initialize(animation, originalAnimation);
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
