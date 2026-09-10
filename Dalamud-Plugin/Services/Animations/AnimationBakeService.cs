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
    private readonly HashSet<Session> active = [];
    private bool disposed;
    private void Check(CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (disposed) throw new OperationCanceledException(); }
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

    private unsafe sealed class Session : IDisposable
    {
        private readonly AnimationNative native;
        private readonly AnimationNative.Arena arena = new();
        private AnimationNative.Document? doc, skeletonDoc, verification;
        private AnimationNative.Sampler? sampler, rawSampler, verifySampler;
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
                if (skeletonDoc.Container->Skeletons.Length != 1 || clip.BindingIndex >= doc.Container->Bindings.Length ||
                    clip.BindingIndex >= doc.Container->Animations.Length)
                    throw new InvalidDataException("The animation or skeleton selection is ambiguous.");
                skeleton = skeletonDoc.Container->Skeletons[0].ptr;
                if (AnimationRuntime.SkeletonFingerprint(skeleton) != clip.SkeletonFingerprint)
                    throw new InvalidDataException("The captured partial skeleton changed before sampling.");
                originalBinding = doc.Container->Bindings[clip.BindingIndex].ptr;
                originalAnimation = originalBinding->Animation.ptr;
                if (doc.Container->Animations[clip.BindingIndex].ptr != originalAnimation)
                    throw new InvalidDataException("PAP animation and binding order disagree.");
                sampler = new AnimationNative.Sampler(skeleton, originalBinding);
                var rawBinding = arena.Alloc<hkaAnimationBinding>(); *rawBinding = *originalBinding;
                rawBinding->BlendHint.Storage = 0; rawBinding->MemSizeAndRefCount = 0x00010000;
                rawSampler = new AnimationNative.Sampler(skeleton, rawBinding);
                duration = originalAnimation->Duration;
                var sourceFrames = originalAnimation->Type switch
                {
                    hkaAnimation.AnimationType.SplineCompressedAnimation => ((AnimationNative.Spline*)originalAnimation)->NumFrames,
                    hkaAnimation.AnimationType.InterleavedAnimation => Math.Max(
                        ((AnimationNative.Interleaved*)originalAnimation)->Transforms.Length / Math.Max(1, originalAnimation->NumberOfTransformTracks),
                        ((AnimationNative.Interleaved*)originalAnimation)->Floats.Length / Math.Max(1, originalAnimation->NumberOfFloatTracks)),
                    _ => throw new InvalidDataException("Unsupported source animation encoding."),
                };
                frames = AnimationPoseRules.SampleCount(duration, sourceFrames);
                if ((long)frames * (sampler.BoneCount * sizeof(hkQsTransformf) + skeleton->FloatSlots.Length * 8) > AnimationPap.MaxFileSize)
                    throw new InvalidDataException("This animation exceeds the bake memory limit.");
                originalPrints = Enumerable.Range(0, doc.Container->Bindings.Length).Select(i => AnimationNative.Fingerprint(doc.Container->Bindings[i].ptr)).ToArray();
                originalMotionPrints = Enumerable.Range(0, doc.Container->Bindings.Length).Select(i => AnimationNative.ExtractedMotionFingerprint(doc.Container->Bindings[i].ptr, motionPath)).ToArray();
                for (var i = 0; i < originalBinding->TransformTrackToBoneIndices.Length; i++) affected.Add(originalBinding->TransformTrackToBoneIndices[i]);
                var applicable = request.Capture.Pose.Bones.Where(b => b.Id.Partial == clip.Partial && request.SelectedBones.Contains(b.Id)).ToArray();
                if (applicable.Length == 0) throw new InvalidDataException("No selected offsets belong to this animation's partial skeleton.");
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
                native.Apply(sampler, request.Capture.Pose.Bones, clip.Partial, request.Components, request.SelectedBones);
                var values = new hkQsTransformf[sampler.BoneCount];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = sampler.Pose->LocalPose[i];
                    AnimationNative.CheckTransform(values[i]);
                    if (!AnimationNative.Near(values[i], sampler.Transforms[i], 0.000001f)) affected.Add(i);
                    if (frame > 0 && Quaternion.Dot(AnimationNative.Rotation(expected[^1][i]), AnimationNative.Rotation(values[i])) < 0)
                    { var q = AnimationNative.Rotation(values[i]); fixed (hkQsTransformf* p = &values[i]) AnimationNative.SetRotation(p, new Quaternion(-q.X, -q.Y, -q.Z, -q.W)); }
                }
                expected.Add(values);
                expectedFloats.Add(new ReadOnlySpan<float>(sampler.Floats, skeleton->FloatSlots.Length).ToArray());
                rawFloats.Add(new ReadOnlySpan<float>(rawSampler.Floats, skeleton->FloatSlots.Length).ToArray());
                frame++;
            } while (frame < frames && watch.ElapsedMilliseconds < 3);
            return frame == frames;
        }
        public void Build()
        {
            // Preserve original track ordering; append only bones actually changed by the pose/IK.
            var tracks = new List<short>();
            for (var i = 0; i < originalBinding->TransformTrackToBoneIndices.Length; i++) tracks.Add(originalBinding->TransformTrackToBoneIndices[i]);
            if (tracks.Distinct().Count() != tracks.Count) throw new InvalidDataException("Duplicate track bindings are unsupported.");
            tracks.AddRange(affected.Order().Where(i => !tracks.Contains((short)i)).Select(i => (short)i));
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
            var binding = arena.Alloc<hkaAnimationBinding>(); *binding = *originalBinding; binding->MemSizeAndRefCount = 0x00010000;
            binding->TransformTrackToBoneIndices = arena.Copy<short>(tracks.ToArray());
            hkaAnimation* final = &animation->Animation;
            if (originalAnimation->Type == hkaAnimation.AnimationType.SplineCompressedAnimation)
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
        private static hkQsTransformf Encode(hkQsTransformf pose, hkQsTransformf reference, sbyte hint)
        {
            if (hint == 0) return pose;
            if (hint is not (1 or 2)) throw new InvalidDataException("Unsupported animation blend hint.");
            var scale = AnimationNative.Scale(reference);
            if (Math.Abs(scale.X * scale.Y * scale.Z) < 1e-12f) throw new InvalidDataException("Cannot encode additive animation against a singular reference pose.");
            var result = pose;
            result.Translation.X -= reference.Translation.X; result.Translation.Y -= reference.Translation.Y; result.Translation.Z -= reference.Translation.Z;
            result.Scale.X /= scale.X; result.Scale.Y /= scale.Y; result.Scale.Z /= scale.Z;
            var inverse = Quaternion.Inverse(AnimationNative.Rotation(reference));
            AnimationNative.SetRotation(&result, Quaternion.Normalize(hint == 2 ? inverse * AnimationNative.Rotation(pose) : AnimationNative.Rotation(pose) * inverse));
            return result;
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
            if (AnimationNative.MetadataFingerprint(binding) != AnimationNative.MetadataFingerprint(originalBinding))
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
