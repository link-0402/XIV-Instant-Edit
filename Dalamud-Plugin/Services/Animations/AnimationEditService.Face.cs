using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Faces in the Timeline: an expression's face read as a clip of its own PAP, and a face opened on its own from a face
/// event (Edit face), which has no capture of its own. Bone layers, Live and saves then work on the face as on any clip.
/// </summary>
internal sealed partial class AnimationEditService
{
    /// <summary>The PAP entry type of a face (cfxf_), as the game's face files list it; blinks are 18 and lips 19.</summary>
    private const short FaceEntryType = 17;

    /// <summary>
    /// Reads the face <paramref name="motion"/> in <paramref name="gamePath"/>'s PAP, as <paramref name="owner"/>'s collection
    /// resolves it, to edit on its own: its skeleton is matched from the face's folder, and it opens as a still or moving face
    /// held as long as an expression. The capture is <paramref name="owner"/>'s with this face as its clip. Nothing in game changes.
    /// </summary>
    public async Task<AnimationTimelineSource> LoadFaceAsync(AnimationCapture owner, string gamePath, string motion, CancellationToken token)
    {
        var (resource, bytes) = await resources.ReadAsync(owner.CollectionId, gamePath, token);
        var pap = new AnimationPap(bytes);
        if (FaceEntryProblem(pap, motion) is { } problem)
            throw new InvalidDataException(problem);
        var binding = pap.Entries.Single(e => e.Name == motion && e.Type == FaceEntryType).Binding;
        var (fingerprint, seconds) = await framework.RunOnFrameworkThread(() =>
            (AnimationRuntime.InspectPap(bytes).GetValueOrDefault(binding, string.Empty),
             AnimationRuntime.InspectPapDurations(bytes).TryGetValue(binding, out var found) ? found : 0f));
        var clip = FaceClip(gamePath, motion, pap, seconds, fingerprint);
        clip = clip with { Resolution = await skeletons.ResolveAsync(owner.CollectionId, clip, bytes, owner.LoadedResourcePaths, null, null, token) };
        var capture = owner with
        {
            Id = FaceCaptureId(owner.Id, gamePath, motion), DisplayName = TimelineCatalogBuilder.Readable(motion), Clip = clip, Startup = null,
            ExpressionTimeline = null, Sources = [resource], FamilyPaths = [gamePath],
        };
        // Read like any clip; a face capture opens as a face (and opens again so, with Edit again and after an undo).
        return await LoadTimelineAsync(capture, false, token);
    }

    /// <summary>Whether <paramref name="capture"/> is a face opened on its own (<see cref="FaceCaptureId"/>).</summary>
    internal static bool IsFaceCapture(AnimationCapture capture) => capture.Id.Contains("|face|", StringComparison.Ordinal);

    /// <summary>
    /// The face <paramref name="capture"/>'s expression plays, read as a clip of its own PAP; null with the reason when it
    /// can't be read or isn't a face.
    /// </summary>
    private async Task<(AnimationTimelineSource? Face, string? Problem)> ExpressionFaceAsync(AnimationCapture capture, CancellationToken token)
    {
        try
        {
            var face = await LoadTimelineAsync(ExpressionFaceCapture(capture), false, token);
            if (face.Document == null)
                return (null, face.ReadOnlyReason);
            if (FaceEntryProblem(new AnimationPap(face.Pap), face.Clip.Name) is { } problem)
                return (null, problem);
            return (face, null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.Debug(e, "Could not read an expression's face to edit it.");
            return (null, e.Message);
        }
    }

    /// <summary>
    /// The capture an expression's face is read with: the expression's clip without its timeline, played once. The game's
    /// expression rows loop, but a face plays from its first frame to its last (Keep's loop starts it again), as a face
    /// opened on its own does: its bone layers hold their last keys at its end instead of wrapping round to the first.
    /// </summary>
    internal static AnimationCapture ExpressionFaceCapture(AnimationCapture capture) =>
        capture with { ExpressionTimeline = null, Clip = capture.Clip with { IsLoop = false } };

    /// <summary>Where <paramref name="motion"/> starts on an expression's timeline: its first face event, else frame 0.</summary>
    internal static int FaceAnchor(TimelineDocument document, string motion) =>
        document.Events.Where(e => e.Magic == "C010" && e.Path == motion).Select(e => e.Start).DefaultIfEmpty(0).Min();

    /// <summary>The capture id of a face opened on its own from <paramref name="owner"/>'s timeline.</summary>
    internal static string FaceCaptureId(string owner, string gamePath, string motion) => $"{owner}|face|{gamePath}|{motion}";

    /// <summary>Why <paramref name="motion"/> can't be opened from <paramref name="pap"/>: it must be exactly one face entry there.</summary>
    internal static string? FaceEntryProblem(AnimationPap pap, string motion) =>
        pap.Entries.Count(e => e.Name == motion && e.Type == FaceEntryType) switch
        {
            1 => null,
            0 => $"{motion} is not a face in this file.",
            _ => $"This file holds {motion} more than once.",
        };

    /// <summary>The face skeleton a face's file is made for: the one of its race and face folder.</summary>
    internal static string FaceSkeletonPath(string gamePath)
    {
        var parts = gamePath.Replace('\\', '/').ToLowerInvariant().Split('/');
        if (parts.Length < 5 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation" || !parts[4].StartsWith('f'))
            throw new InvalidDataException($"{gamePath} is not a face file.");
        return $"chara/human/{parts[2]}/skeleton/face/{parts[4]}/skl_{parts[2]}{parts[4]}.sklb";
    }

    /// <summary>
    /// Whether saving <paramref name="document"/> writes its face: bone layers, a new length, bones left out or a motion
    /// held still or set to the neutral face.
    /// </summary>
    internal static bool FaceChanged(TimelineDocument document) =>
        document.Face is { } face && (document.MotionChanged || face.LengthChanged || !document.ExcludedBones.IsEmpty || document.Replacement != null);

    /// <summary>
    /// Why saving the face of <paramref name="source"/> can't go in place: the face's file, and with
    /// <paramref name="timelineChanged"/> an expression's own timeline too, must each come from a mod that can be written.
    /// </summary>
    internal static string? FaceInPlaceProblem(AnimationTimelineSource source, bool timelineChanged) =>
        !AnimationResources.CanReplace(source.MotionTarget.Resource) ? "The face has no writable Penumbra source. Choose Create new mod."
        : timelineChanged && source.Face != null && !AnimationResources.CanReplace(source.Resource)
            ? "The expression's timeline has no writable Penumbra source. Choose Create new mod."
            : null;

    /// <summary>
    /// The request saving <paramref name="document"/>'s face makes: the baker reads it, so its layers are in the face's own
    /// frames and its length is the face's (<see cref="TimelineFace.BakeDocument"/>). The face's binding after the save is
    /// filled in once it is built.
    /// </summary>
    private static AnimationBakeRequest FaceRequest(AnimationTimelineSource source, AnimationTimelineSource target, TimelineDocument document,
        TimelineFace face, AnimationDestination destination, string modName)
    {
        var bake = TimelineFace.BakeDocument(document, target.Document ?? document);
        return new(Guid.NewGuid(), source.Capture, destination, modName, false, AnimationOperation.EditTimeline,
            ExcludedBones: document.ExcludedBones,
            Timeline: new AnimationTimelineEdit(source.Startup, source.EntryIndex,
                face.LengthChanged || document.Replacement != null ? face.Frames / (float)TimelineDocument.Fps : null,
                bake.MotionChanged ? bake.Motion : [],
                Face: new AnimationFaceEdit(target.Clip.GamePath, target.EntryIndex, target.Clip.Name, face.Anchor, face.SourceFrames, face.Frames, "")));
    }

    /// <summary>
    /// Writes <paramref name="session"/>'s face into <paramref name="pap"/>, the face's file as the collection gives it: its
    /// layers, length and left-out bones baked on the face skeleton like a clip's, the face's own embedded timeline as it
    /// was (at the face's new length when that changed, see <see cref="FaceTimeline"/>), and every other face in the file as it was.
    /// </summary>
    private async Task<AnimationTimelineBuild> BuildFaceAsync(AnimationBakeRequest request, AnimationTimelineSource face, byte[] pap,
        TimelineDocument session, string workDirectory, CancellationToken token)
    {
        var bake = TimelineFace.BakeDocument(session, face.Document ?? throw new InvalidOperationException(face.ReadOnlyReason ?? "This face can't be edited."));
        var clip = face.Clip;
        var motion = face.BaseMotion ?? pap;
        var baked = AnimationBakeRules.ChangesMotion(bake);
        if (baked)
        {
            native.EnsureAvailable();
            if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
                throw new InvalidOperationException(clip.Resolution?.Reason ?? "Choose the skeleton this face was made for before changing it.");
            await resources.CheckSkeletonsAsync(request.Capture, [clip], token);
            var skeleton = (await resources.ReadSkeletonAsync(request.Capture.CollectionId, selected.Source, token)).Bytes;
            Directory.CreateDirectory(workDirectory);
            motion = await baker.BakeAsync(face.BaseMotion ?? pap, skeleton, face.MotionClip, request,
                workDirectory, message => Status = message, token, () => CheckIdentityOnFramework(request.Capture, true));
        }
        var timeline = session.Face is { LengthChanged: true } edited ? FaceTimeline(face.Tmb, edited.SourceFrames, edited.Frames) : face.Tmb;
        var result = new AnimationPap(motion).WithTimeline(face.EntryIndex, timeline);
        CheckTimelineOutput(pap, result, face.EntryIndex, timeline, !baked && face.BaseMotion == null);
        var fingerprint = face.MotionClip.BindingFingerprint;
        if (baked || face.BaseMotion != null)
        {
            // The other faces in the file stay as the collection gives them now: a base from before an earlier save holds them as they were then.
            var (current, built) = await framework.RunOnFrameworkThread(() => (AnimationRuntime.InspectPap(pap), AnimationRuntime.InspectPap(result)));
            if (OtherFacesProblem(current, built, clip.BindingIndex) is { } other)
                throw new InvalidDataException(other);
            fingerprint = built.GetValueOrDefault(clip.BindingIndex, "");
        }
        return new AnimationTimelineBuild(face.Resource.GamePath, result, fingerprint);
    }

    /// <summary>
    /// A face entry's own timeline (<paramref name="tmb"/>) for a face loaded <paramref name="sourceFrames"/> long and now
    /// <paramref name="frames"/>: its length, and the motion entry (C009) that lasted the whole face, become the new length.
    /// Mods' moving faces say their length there (a vanilla pose says 1 frame); a moving face saved with its timeline still
    /// at 1 frame stayed at its start in game (2026-10-31). Everything else stays; the timeline itself when nothing changes.
    /// </summary>
    internal static byte[] FaceTimeline(byte[] tmb, int sourceFrames, int frames)
    {
        var file = TmbFile.Parse(tmb);
        var loaded = file.Length;
        foreach (var entry in file.Entries.Where(e => e.Magic == "C009" && e.Int32(12) != frames &&
                     (TimelineDocument.SameLength(e.Int32(12), loaded) || TimelineDocument.SameLength(e.Int32(12), sourceFrames))).ToArray())
            file.SetInt32(entry.Id, 12, frames);
        if (loaded != frames)
            file.SetLength(frames);
        if (!file.Edited)
            return tmb;
        var output = file.ToArray(out _);
        _ = TmbFile.Parse(output);
        return output;
    }

    /// <summary>
    /// Why a face built into its file can't be saved: a binding other than its own (<paramref name="binding"/>) would change,
    /// by fingerprint, from the file as the collection gives it (<paramref name="current"/>) to the build (<paramref name="built"/>).
    /// A file many faces share (resident face.pap) is built from the motion from before this face's last save, which
    /// holds the others as they were then. Null when every other binding stays.
    /// </summary>
    internal static string? OtherFacesProblem(IReadOnlyDictionary<int, string> current, IReadOnlyDictionary<int, string> built, int binding) =>
        current.Any(pair => pair.Key != binding && built.GetValueOrDefault(pair.Key) != pair.Value) ||
        built.Keys.Any(key => key != binding && !current.ContainsKey(key))
            ? "Another face in this file changed since these layers were saved. Saving them would undo it."
            : null;

    /// <summary>
    /// The expression's own timeline as a face session saves it: the face's edited length for Keep's loops and its face
    /// events (<see cref="TimelineConvert.RetimeFace"/>), the header as loaded (the canvas grown for the face is only the view).
    /// </summary>
    internal static byte[] ExpressionTimeline(byte[] tmb, TimelineDocument document, IReadOnlyDictionary<string, int>? motionFrames, string motion)
    {
        var face = document.Face ?? throw new InvalidOperationException("This timeline has no face.");
        var frames = new Dictionary<string, int>(motionFrames ?? ImmutableDictionary<string, int>.Empty) { [motion] = face.Frames };
        // A face of a new length loops again at it, a loop saved before included.
        var timeline = TimelineConvert.Apply(tmb, document with { Length = document.SourceLength }, frames,
            face.LengthChanged ? new HashSet<string>(StringComparer.Ordinal) { motion } : null);
        return face.LengthChanged ? TimelineConvert.RetimeFace(timeline, motion, face.Frames) : timeline;
    }

    /// <summary>
    /// <paramref name="loaded"/> with a saved face edit's layers back, in the timeline's frames again, on the face's motion
    /// from before it, with the face's length before and after the edit.
    /// </summary>
    private static AnimationTimelineSource WithSavedFace(AnimationTimelineSource loaded, TimelineDocument document, AnimationTimelineEdit edit,
        AnimationFaceEdit face, byte[] motion, string fingerprint)
    {
        var restored = document with
        {
            Motion = TimelineFace.ToFace(edit.Motion, -face.Anchor),
            Face = new TimelineFace(face.Anchor, face.SourceFrames, face.Frames),
        };
        var target = loaded.MotionTarget with { BaseMotion = motion, BaseFingerprint = fingerprint, BaseHash = edit.MotionBaseHash };
        return loaded.Face != null ? loaded with { Document = restored, Face = target } : target with { Document = restored };
    }

    /// <summary>The clip of face <paramref name="motion"/> in <paramref name="pap"/>: its entry's binding, on the face skeleton.</summary>
    internal static AnimationClip FaceClip(string gamePath, string motion, AnimationPap pap, float seconds, string fingerprint) =>
        new(gamePath, motion, pap.Entries.Single(e => e.Name == motion && e.Type == FaceEntryType).Binding, 1, 0, FaceSkeletonPath(gamePath),
            BindingFingerprint: fingerprint, Duration: seconds);
}
