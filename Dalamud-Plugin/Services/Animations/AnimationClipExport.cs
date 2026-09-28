using System.Collections.Immutable;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Samples an animation file on the skeleton it was made for, at its own frame rate, into a take
/// for Blender. Bones without a track keep their reference pose, as in game; physics bones are
/// not part of such a take, which is what a physics simulation in Blender starts from.
/// </summary>
internal static class AnimationClipExport
{
    public static async Task<AnimationTake> SampleAsync(IFramework framework, byte[] pap, byte[] skeleton, AnimationClip clip,
        SkeletonCandidate source, string name, IReadOnlyDictionary<string, string> facts, Action<string> status, CancellationToken token)
    {
        Session? session = null;
        try
        {
            session = await framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                return new Session(pap, skeleton, clip, source);
            });
            var complete = false;
            while (!complete)
            {
                token.ThrowIfCancellationRequested();
                complete = await framework.RunOnTick(() => session.SampleBatch(token), delayTicks: 1);
                status($"Sampling {name}: {session.Progress:P0}");
            }
            return session.Build(name, clip.IsLoop, facts);
        }
        finally
        {
            if (session != null) await framework.RunOnFrameworkThread(session.Dispose);
        }
    }

    private sealed unsafe class Session : IDisposable
    {
        private readonly AnimationNative.Arena arena = new();
        private readonly List<float[]> frames = [];
        private readonly SkeletonDescription description;
        private readonly double[] times;
        private AnimationNative.Document? document, skeletonDocument;
        private AnimationNative.Sampler? sampler;

        public Session(byte[] papBytes, byte[] sklb, AnimationClip clip, SkeletonCandidate source)
        {
            try
            {
                var pap = new AnimationPap(papBytes);
                if (!pap.Entries.Any(e => e.Name == clip.Name && e.Binding == clip.BindingIndex))
                    throw new InvalidDataException("The animation file changed since it was captured. Play the animation again.");
                document = new AnimationNative.Document(pap.Havok);
                skeletonDocument = new AnimationNative.Document(AnimationPap.SkeletonHavok(sklb));
                if (clip.BindingIndex < 0 || clip.BindingIndex >= document.Container->Bindings.Length)
                    throw new InvalidDataException("The animation file no longer has the captured clip.");
                description = AnimationSkeleton.SelectSource(skeletonDocument.Container, source.Skeleton.Fingerprint, source.Source.LeadingBones);
                var binding = document.Container->Bindings[clip.BindingIndex].ptr;
                if (clip.BindingFingerprint.Length > 0 && AnimationNative.Fingerprint(binding) != clip.BindingFingerprint)
                    throw new InvalidDataException("The animation changed since it was captured. Play the animation again.");
                sampler = new AnimationNative.Sampler(AnimationSkeleton.Materialize(description, arena), binding);
                var animation = binding->Animation.ptr;
                var duration = animation->Duration;
                // A pose-only clip has no length: one frame describes it.
                var count = duration > 0 ? AnimationPoseRules.SampleCount(duration, AnimationNative.SourceFrameCount(animation)) : 1;
                times = Enumerable.Range(0, count)
                    .Select(i => count == 1 ? 0d : i == count - 1 ? duration : (double)duration * i / (count - 1)).ToArray();
            }
            catch { Dispose(); throw; }
        }

        public float Progress => (float)frames.Count / times.Length;

        /// <summary>Samples frames for up to a few milliseconds. Returns true once every frame is sampled.</summary>
        public bool SampleBatch(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var bones = description.Bones.Length;
            do
            {
                token.ThrowIfCancellationRequested();
                sampler!.Sample((float)times[frames.Count]);
                var frame = new float[bones * AnimationTake.Stride];
                for (var i = 0; i < bones; i++)
                {
                    AnimationNative.CheckTransform(sampler.Transforms[i]);
                    AnimationTake.Write(AnimationSkeleton.Transform(sampler.Transforms[i]), frame.AsSpan(i * AnimationTake.Stride, AnimationTake.Stride));
                }
                frames.Add(frame);
            } while (frames.Count < times.Length && watch.ElapsedMilliseconds < 3);
            return frames.Count == times.Length;
        }

        public AnimationTake Build(string name, bool loop, IReadOnlyDictionary<string, string> facts)
        {
            var bones = description.Bones.Select(b => new AnimationTakeBone(b.Name, b.Parent, b.Reference)).ToImmutableArray();
            var samples = new float[(long)frames.Count * bones.Length * AnimationTake.Stride];
            for (var f = 0; f < frames.Count; f++)
                frames[f].CopyTo(samples, (long)f * bones.Length * AnimationTake.Stride);
            return new AnimationTake(AnimationTake.AnimationKind, name, bones, times, samples, loop, facts);
        }

        public void Dispose()
        {
            sampler?.Dispose(); sampler = null;
            document?.Dispose(); document = null;
            skeletonDocument?.Dispose(); skeletonDocument = null;
            arena.Dispose();
        }
    }
}
