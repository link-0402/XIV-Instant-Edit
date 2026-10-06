using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Every change the editor makes to a document's bone layers, as <see cref="TimelineEdits"/> does for
/// its events: each takes a document and returns a new one, or the same instance when nothing changes.
/// Layers stay on the timeline and at least a frame long, fades fit inside their layer, and keys keep
/// their order. No ImGui or Dalamud dependencies.
/// </summary>
internal static class MotionEdits
{
    /// <summary>How long a layer added at a frame lasts: one second, or to the end of the clip.</summary>
    public const int SectionLength = TimelineDocument.Fps;

    /// <summary>The longest fade a new section gets on each side.</summary>
    public const int SectionFade = 10;

    /// <summary>The longest name a layer keeps.</summary>
    public const int MaxName = 48;

    public static int IndexOf(TimelineDocument doc, string? key)
    {
        if (key == null)
            return -1;
        for (var index = 0; index < doc.Motion.Length; index++)
            if (doc.Motion[index].Key == key)
                return index;
        return -1;
    }

    public static MotionLayer? Find(TimelineDocument doc, string? key) => IndexOf(doc, key) is var index and >= 0 ? doc.Motion[index] : null;

    public static string KindName(MotionLayerKind kind) => kind switch
    {
        MotionLayerKind.Offset => "Offset",
        MotionLayerKind.Amplitude => "Amplitude",
        _ => "Pose",
    };

    /// <summary>The largest amount a kind takes: a pose blends at most fully, the others go up to 400%.</summary>
    public static float MaxAmount(MotionLayerKind kind) => kind == MotionLayerKind.Pose ? 1 : MotionEvaluator.MaxAmount;

    /// <summary>
    /// Adds a layer of <paramref name="kind"/> from <paramref name="start"/> to <paramref name="end"/> after
    /// the others, so it applies last, named "Offset 2" and so on. A layer over the whole clip gets no
    /// fades, so a loop stays seamless; a section eases in and out over a quarter of itself, at most
    /// <see cref="SectionFade"/> frames. An amplitude starts at 150%, every other kind at full strength.
    /// </summary>
    public static TimelineDocument Add(TimelineDocument doc, MotionLayerKind kind, int start, int end, out string key, out string? error)
    {
        key = string.Empty;
        error = null;
        if (doc.Motion.Length >= MotionEvaluator.MaxLayers)
        {
            error = $"An animation can have at most {MotionEvaluator.MaxLayers} bone layers.";
            return doc;
        }
        start = Math.Clamp(start, 0, TimelineDocument.MaxFrames - 1);
        end = Math.Clamp(end, start + 1, TimelineDocument.MaxFrames);
        var whole = start == 0 && end >= doc.Length;
        var fade = whole ? 0 : Math.Min(SectionFade, (end - start) / 4);
        var keys = doc.Motion.Select(layer => layer.Key).ToHashSet(StringComparer.Ordinal);
        var next = Math.Max(1, doc.NextMotionKey);
        do key = "m" + (next++).ToString(CultureInfo.InvariantCulture);
        while (!keys.Add(key));
        var number = doc.Motion.Count(layer => layer.Kind == kind) + 1;
        var added = new MotionLayer(key, kind, $"{KindName(kind)} {number}", start, end, fade, fade,
            kind == MotionLayerKind.Amplitude ? 1.5f : 1,
            kind == MotionLayerKind.Offset ? PoseComponents.All : PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth);
        return doc with { Motion = doc.Motion.Add(added), NextMotionKey = next };
    }

    public static TimelineDocument Remove(TimelineDocument doc, string key) =>
        IndexOf(doc, key) is var index and >= 0 ? doc with { Motion = doc.Motion.RemoveAt(index) } : doc;

    /// <summary>
    /// Replaces the layer with <paramref name="layer"/>'s key by <paramref name="layer"/>, kept valid: on the
    /// timeline, a frame long or more, fades that fit, an amount in its kind's range, a short name.
    /// </summary>
    public static TimelineDocument Replace(TimelineDocument doc, MotionLayer layer)
    {
        var index = IndexOf(doc, layer.Key);
        if (index < 0)
            return doc;
        var old = doc.Motion[index];
        var next = Normalize(layer with { Kind = old.Kind, Name = string.IsNullOrWhiteSpace(layer.Name) ? old.Name : layer.Name });
        return Same(old, next) ? doc : doc with { Motion = doc.Motion.SetItem(index, next) };
    }

    /// <summary>
    /// Moves a layer and its keys by <paramref name="delta"/> frames, as far as they all stay on the
    /// timeline and the layer's end stays within the clip (or where it already was past it).
    /// </summary>
    public static TimelineDocument Move(TimelineDocument doc, string key, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0)
            return doc;
        var layer = doc.Motion[index];
        var low = -layer.Start;
        var high = Limit(doc, layer) - layer.End;
        foreach (var item in layer.Keys)
        {
            low = Math.Max(low, -item.Frame);
            high = Math.Min(high, TimelineDocument.MaxFrames - item.Frame);
        }
        delta = Math.Clamp(delta, Math.Min(0, low), Math.Max(0, high));
        if (delta == 0)
            return doc;
        var moved = layer with
        {
            Start = layer.Start + delta, End = layer.End + delta,
            Keys = [.. layer.Keys.Select(item => item with { Frame = item.Frame + delta })],
        };
        return doc with { Motion = doc.Motion.SetItem(index, moved) };
    }

    /// <summary>
    /// Drags one end of a layer by <paramref name="delta"/> frames; the other stays put, the layer stays
    /// a frame long or more and its fades shrink to fit. Keys stay where they are.
    /// </summary>
    public static TimelineDocument Resize(TimelineDocument doc, string key, bool leftEdge, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0)
            return doc;
        var layer = doc.Motion[index];
        var resized = leftEdge
            ? layer with { Start = Math.Clamp(layer.Start + delta, 0, layer.End - 1) }
            : layer with { End = Math.Clamp(layer.End + delta, layer.Start + 1, Math.Max(layer.Start + 1, Limit(doc, layer))) };
        resized = Normalize(resized);
        return Same(layer, resized) ? doc : doc with { Motion = doc.Motion.SetItem(index, resized) };
    }

    /// <summary>
    /// Drags a fade's handle by <paramref name="delta"/> frames: the fade in's handle sits where the fade
    /// in ends, the fade out's where it begins. Together they never exceed the layer.
    /// </summary>
    public static TimelineDocument Fade(TimelineDocument doc, string key, bool fadeIn, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0)
            return doc;
        var layer = doc.Motion[index];
        var length = layer.End - layer.Start;
        var faded = fadeIn
            ? layer with { FadeIn = Math.Clamp(layer.FadeIn + delta, 0, length - layer.FadeOut) }
            : layer with { FadeOut = Math.Clamp(layer.FadeOut - delta, 0, length - layer.FadeIn) };
        return Same(layer, faded) ? doc : doc with { Motion = doc.Motion.SetItem(index, faded) };
    }

    /// <summary>
    /// Moves the key at <paramref name="frame"/> by <paramref name="delta"/> frames, never onto or past its
    /// neighbours. <paramref name="landed"/> is where it ends up (its frame when nothing moved).
    /// </summary>
    public static TimelineDocument MoveKey(TimelineDocument doc, string key, int frame, int delta, out int landed)
    {
        landed = frame;
        var index = IndexOf(doc, key);
        if (index < 0)
            return doc;
        var layer = doc.Motion[index];
        var at = KeyIndex(layer, frame);
        if (at < 0 || delta == 0)
            return doc;
        var low = at > 0 ? layer.Keys[at - 1].Frame + 1 : 0;
        var high = at < layer.Keys.Length - 1 ? layer.Keys[at + 1].Frame - 1 : TimelineDocument.MaxFrames;
        landed = Math.Clamp(frame + delta, low, Math.Max(low, high));
        if (landed == frame)
            return doc;
        var moved = layer with { Keys = layer.Keys.SetItem(at, layer.Keys[at] with { Frame = landed }) };
        return doc with { Motion = doc.Motion.SetItem(index, moved) };
    }

    public static TimelineDocument DeleteKey(TimelineDocument doc, string key, int frame)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || KeyIndex(doc.Motion[index], frame) is not (var at and >= 0))
            return doc;
        var layer = doc.Motion[index];
        return doc with { Motion = doc.Motion.SetItem(index, layer with { Keys = layer.Keys.RemoveAt(at) }) };
    }

    /// <summary>
    /// Sets <paramref name="bone"/>'s value in the layer's key at <paramref name="frame"/>, adding the key
    /// when there is none. Null <paramref name="value"/> takes the bone out of that key, and an empty key
    /// goes. <paramref name="moved"/> marks whether a pose key sets the bone's position too; null keeps it.
    /// </summary>
    public static TimelineDocument SetKey(TimelineDocument doc, string key, int frame, string bone, BoneTransform? value, bool? moved = null)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || string.IsNullOrEmpty(bone))
            return doc;
        frame = Math.Clamp(frame, 0, TimelineDocument.MaxFrames);
        var layer = doc.Motion[index];
        var at = KeyIndex(layer, frame);
        ImmutableArray<MotionKey> keys;
        if (value == null)
        {
            if (at < 0 || !layer.Keys[at].Bones.ContainsKey(bone))
                return doc;
            var left = layer.Keys[at] with { Bones = layer.Keys[at].Bones.Remove(bone), Moved = layer.Keys[at].Moved.Remove(bone) };
            keys = left.Bones.IsEmpty ? layer.Keys.RemoveAt(at) : layer.Keys.SetItem(at, left);
        }
        else if (at >= 0)
        {
            var old = layer.Keys[at];
            var marked = moved switch
            {
                true when !old.Moved.Contains(bone) => old.Moved.Add(bone),
                false => old.Moved.Remove(bone),
                _ => old.Moved,
            };
            if (old.Bones.TryGetValue(bone, out var had) && had == value && marked == old.Moved)
                return doc;
            keys = layer.Keys.SetItem(at, old with { Bones = old.Bones.SetItem(bone, value), Moved = marked });
        }
        else
        {
            var added = new MotionKey(frame, ImmutableDictionary<string, BoneTransform>.Empty.WithComparers(StringComparer.Ordinal).Add(bone, value),
                moved == true ? [bone] : []);
            var insert = 0;
            while (insert < layer.Keys.Length && layer.Keys[insert].Frame < frame)
                insert++;
            keys = layer.Keys.Insert(insert, added);
        }
        return doc with { Motion = doc.Motion.SetItem(index, layer with { Keys = keys }) };
    }

    /// <summary>Takes <paramref name="bone"/> out of every key of the layer; keys left empty go.</summary>
    public static TimelineDocument ClearBone(TimelineDocument doc, string key, string bone)
    {
        var index = IndexOf(doc, key);
        if (index < 0)
            return doc;
        var layer = doc.Motion[index];
        if (!layer.Keys.Any(item => item.Bones.ContainsKey(bone)))
            return doc;
        ImmutableArray<MotionKey> keys = [.. layer.Keys
            .Select(item => item with { Bones = item.Bones.Remove(bone), Moved = item.Moved.Remove(bone) })
            .Where(item => !item.Bones.IsEmpty)];
        return doc with { Motion = doc.Motion.SetItem(index, layer with { Keys = keys }) };
    }

    /// <summary>Limits the layer to <paramref name="bones"/> (distinct, in order); empty lifts the limit.</summary>
    public static TimelineDocument SetBones(TimelineDocument doc, string key, IEnumerable<string> bones)
    {
        var index = IndexOf(doc, key);
        if (index < 0)
            return doc;
        ImmutableArray<string> limited = [.. bones.Where(bone => !string.IsNullOrEmpty(bone)).Distinct(StringComparer.Ordinal)];
        var layer = doc.Motion[index];
        return layer.Bones.SequenceEqual(limited, StringComparer.Ordinal) ? doc : doc with { Motion = doc.Motion.SetItem(index, layer with { Bones = limited }) };
    }

    public static TimelineDocument SetEnabled(TimelineDocument doc, string key, bool enabled)
    {
        var index = IndexOf(doc, key);
        return index < 0 || doc.Motion[index].Enabled == enabled ? doc
            : doc with { Motion = doc.Motion.SetItem(index, doc.Motion[index] with { Enabled = enabled }) };
    }

    /// <summary>Moves a layer <paramref name="delta"/> places in the stack; later layers apply on top of earlier ones.</summary>
    public static TimelineDocument Reorder(TimelineDocument doc, string key, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0)
            return doc;
        var target = Math.Clamp(index + delta, 0, doc.Motion.Length - 1);
        if (target == index)
            return doc;
        var layer = doc.Motion[index];
        return doc with { Motion = doc.Motion.RemoveAt(index).Insert(target, layer) };
    }

    /// <summary>Copies a layer, keys and all, right after itself with a new key and "copy" in its name.</summary>
    public static TimelineDocument Duplicate(TimelineDocument doc, string key, out string created)
    {
        created = string.Empty;
        var index = IndexOf(doc, key);
        if (index < 0 || doc.Motion.Length >= MotionEvaluator.MaxLayers)
            return doc;
        var keys = doc.Motion.Select(layer => layer.Key).ToHashSet(StringComparer.Ordinal);
        var next = Math.Max(1, doc.NextMotionKey);
        do created = "m" + (next++).ToString(CultureInfo.InvariantCulture);
        while (!keys.Add(created));
        var original = doc.Motion[index];
        var name = original.Name.EndsWith(" copy", StringComparison.Ordinal) ? original.Name : original.Name + " copy";
        var copy = original with { Key = created, Name = name.Length > MaxName ? name[..MaxName] : name };
        return doc with { Motion = doc.Motion.Insert(index + 1, copy), NextMotionKey = next };
    }

    /// <summary>The frames layer drags stick to: every layer's start, end and keys, except <paramref name="except"/>'s.</summary>
    public static IEnumerable<int> SnapFrames(TimelineDocument doc, string? except)
    {
        foreach (var layer in doc.Motion)
        {
            if (layer.Key == except)
                continue;
            yield return layer.Start;
            yield return layer.End;
            foreach (var item in layer.Keys)
                yield return item.Frame;
        }
    }

    /// <summary>The index of the layer's key at <paramref name="frame"/>, or -1.</summary>
    public static int KeyIndex(MotionLayer layer, int frame)
    {
        for (var index = 0; index < layer.Keys.Length; index++)
            if (layer.Keys[index].Frame == frame)
                return index;
        return -1;
    }

    /// <summary>The last frame a layer may reach by dragging: the clip's end, or where it already reaches past it.</summary>
    private static int Limit(TimelineDocument doc, MotionLayer layer) =>
        Math.Min(TimelineDocument.MaxFrames, Math.Max(Math.Max(doc.Length, TimelineDocument.MinLength), layer.End));

    private static MotionLayer Normalize(MotionLayer layer)
    {
        var start = Math.Clamp(layer.Start, 0, TimelineDocument.MaxFrames - 1);
        var end = Math.Clamp(layer.End, start + 1, TimelineDocument.MaxFrames);
        var length = end - start;
        var fadeIn = Math.Clamp(layer.FadeIn, 0, length);
        var fadeOut = Math.Clamp(layer.FadeOut, 0, length - fadeIn);
        var amount = float.IsFinite(layer.Amount) ? Math.Clamp(layer.Amount, 0, MaxAmount(layer.Kind)) : 1;
        var name = layer.Name.Trim();
        return layer with
        {
            Start = start, End = end, FadeIn = fadeIn, FadeOut = fadeOut, Amount = amount,
            Components = layer.Components & PoseComponents.All,
            Name = name.Length > MaxName ? name[..MaxName] : name,
        };
    }

    /// <summary>Field-by-field equality, keys and bones compared by content.</summary>
    public static bool Same(MotionLayer a, MotionLayer b) =>
        ReferenceEquals(a, b) ||
        a.Key == b.Key && a.Kind == b.Kind && a.Name == b.Name && a.Start == b.Start && a.End == b.End && a.FadeIn == b.FadeIn &&
        a.FadeOut == b.FadeOut && a.Amount.Equals(b.Amount) && a.Components == b.Components && a.Interpolation == b.Interpolation &&
        a.Enabled == b.Enabled && a.Bones.SequenceEqual(b.Bones, StringComparer.Ordinal) && a.Keys.Length == b.Keys.Length &&
        a.Keys.Zip(b.Keys).All(pair => pair.First.Frame == pair.Second.Frame && pair.First.Moved.SequenceEqual(pair.Second.Moved, StringComparer.Ordinal) &&
            pair.First.Bones.Count == pair.Second.Bones.Count &&
            pair.First.Bones.All(bone => pair.Second.Bones.TryGetValue(bone.Key, out var other) && other == bone.Value));
}
