using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>A picked key of a bone layer: the whole key at <paramref name="Frame"/>, or with <paramref name="Bone"/> that bone's value in it.</summary>
internal readonly record struct KeyPick(int Frame, string? Bone = null);

/// <summary>
/// Keys copied from a layer of <paramref name="Kind"/>, frames counted from the first: a whole key as it was, a bone's
/// value alone in a key of its own without an ease.
/// </summary>
internal sealed record KeyCopy(MotionLayerKind Kind, ImmutableArray<MotionKey> Keys);

/// <summary>
/// Every change the editor makes to a document's bone layers, as <see cref="TimelineEdits"/> does for
/// its events: each takes a document and returns a new one, or the same instance when nothing changes.
/// Layers stay on the timeline and at least a frame long, fades fit inside their layer, and keys keep
/// their order. No ImGui or Dalamud dependencies.
/// </summary>
internal static class MotionEdits
{
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

    /// <summary>
    /// <paramref name="layer"/> as the timeline draws, grabs and snaps to it on a clip <paramref name="length"/> frames
    /// long: a match over the frames it blends (<see cref="MotionFixes.MatchSpan"/>), without fades; any other as it is.
    /// </summary>
    public static MotionLayer Shown(MotionLayer layer, int length) =>
        IsMatch(layer) && MotionFixes.MatchSpan(layer, length) is var (start, end) ? layer with { Start = start, End = end, FadeIn = 0, FadeOut = 0 } : layer;

    /// <summary>Whether <paramref name="layer"/> is a Match startup to loop, which ends on the clip's last frame.</summary>
    public static bool IsMatch(MotionLayer layer) => layer.Fix?.Kind == MotionFixKind.MatchLoop;

    /// <summary>
    /// A match on a clip <paramref name="length"/> frames long: its blend length kept, at least a frame and at most the clip,
    /// ending on the last frame. Cuts, stretches and pastes put it there, as its stored frames only carry that length.
    /// </summary>
    public static MotionLayer Anchored(MotionLayer layer, int length) =>
        layer with { Start = Math.Max(0, length - Math.Max(1, layer.End - layer.Start)), End = length };

    public static string KindName(MotionLayerKind kind) => kind switch
    {
        MotionLayerKind.Offset => "Offset",
        MotionLayerKind.Amplitude => "Amplitude",
        MotionLayerKind.Symmetry => "Symmetry",
        MotionLayerKind.Pin => "Pin",
        MotionLayerKind.Fix => "Fix",
        MotionLayerKind.Idle => "Idle motion",
        _ => "Pose",
    };

    /// <summary>The largest amount a kind takes: a pose, a symmetry or a pin blends at most fully, the others go up to 400%.</summary>
    public static float MaxAmount(MotionLayerKind kind) => MotionEvaluator.BlendsAtMostFully(kind) ? 1 : MotionEvaluator.MaxAmount;

    /// <summary>
    /// Adds a layer of <paramref name="kind"/> from <paramref name="start"/> to <paramref name="end"/> after
    /// the others, so it applies last, named "Offset 2" and so on, with no fades, so a loop
    /// stays seamless. An amplitude starts at 150%, every other kind at full strength.
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
        var keys = doc.Motion.Select(layer => layer.Key).ToHashSet(StringComparer.Ordinal);
        var next = Math.Max(1, doc.NextMotionKey);
        do key = "m" + (next++).ToString(CultureInfo.InvariantCulture);
        while (!keys.Add(key));
        var number = doc.Motion.Count(layer => layer.Kind == kind) + 1;
        var added = new MotionLayer(key, kind, kind == MotionLayerKind.Symmetry ? MotionSymmetry.DefaultName(MotionSide.Left) : $"{KindName(kind)} {number}",
            start, end, 0, 0,
            kind == MotionLayerKind.Amplitude ? 1.5f : 1,
            kind switch
            {
                MotionLayerKind.Offset => PoseComponents.All,
                MotionLayerKind.Symmetry => PoseComponents.Rotation,
                _ => PoseComponents.Rotation | PoseComponents.Position,
            },
            MotionInterpolation.Smooth);
        return doc with { Motion = doc.Motion.Add(added), NextMotionKey = next };
    }

    /// <summary>
    /// Adds a symmetry layer over the whole clip copying <paramref name="from"/> onto the other side for
    /// <paramref name="bones"/> and everything below them: what Make symmetrical adds. Unchanged with an
    /// error when no bone is given or the document has no room for another layer.
    /// </summary>
    public static TimelineDocument AddSymmetry(TimelineDocument doc, IEnumerable<string> bones, MotionSide from, out string key, out string? error)
    {
        key = string.Empty;
        ImmutableArray<string> picked = [.. bones.Where(bone => !string.IsNullOrEmpty(bone)).Distinct(StringComparer.Ordinal)];
        if (picked.IsEmpty)
        {
            error = "Select a bone with a twin on the other side.";
            return doc;
        }
        var added = Add(doc, MotionLayerKind.Symmetry, 0, Math.Max(TimelineDocument.MinLength, doc.Length), out key, out error);
        if (error != null)
            return doc;
        var index = IndexOf(added, key);
        var layer = added.Motion[index] with { Bones = picked, From = from, Name = MotionSymmetry.DefaultName(from) };
        return added with { Motion = added.Motion.SetItem(index, layer) };
    }

    /// <summary>
    /// Adds a pin layer over the whole clip named "Pin feet" that plants both feet, turn held: they stay
    /// where the motion has them down. Unchanged with an error when the document has no room for another layer.
    /// </summary>
    public static TimelineDocument AddPinFeet(TimelineDocument doc, out string key, out string? error)
    {
        var added = Add(doc, MotionLayerKind.Pin, 0, Math.Max(TimelineDocument.MinLength, doc.Length), out key, out error);
        if (error != null)
            return doc;
        var index = IndexOf(added, key);
        var layer = added.Motion[index] with { Name = "Pin feet", Pins = [new(PinChains.FootL, Planted: true), new(PinChains.FootR, Planted: true)] };
        return added with { Motion = added.Motion.SetItem(index, layer) };
    }

    /// <summary>The frames a new Match startup to loop blends over.</summary>
    public const int DefaultFixLength = 8;

    /// <summary>
    /// Adds a fix layer holding <paramref name="fix"/>: a match named "Match loop" over the clip's last
    /// <see cref="DefaultFixLength"/> frames, or its game fade when that is longer (from frame 0 on a shorter clip), at
    /// full blend, a smooth named "Smooth jitter" over the whole clip at half strength, or a hand shape named "Hand shape"
    /// over the whole clip at full amount, rotation only. Unchanged with an error when
    /// the document has no room for another layer.
    /// </summary>
    public static TimelineDocument AddFix(TimelineDocument doc, MotionFix fix, out string key, out string? error)
    {
        var length = Math.Max(TimelineDocument.MinLength, doc.Length);
        var match = fix.Kind == MotionFixKind.MatchLoop;
        var blend = Math.Max(DefaultFixLength, fix.Fade);
        var hand = fix.Kind == MotionFixKind.HandShape;
        var added = Add(doc, MotionLayerKind.Fix, match ? Math.Max(0, length - blend) : 0, length, out key, out error);
        if (error != null)
            return doc;
        var index = IndexOf(added, key);
        var layer = added.Motion[index] with
        {
            Name = match ? "Match loop" : hand ? "Hand shape" : "Smooth jitter", Amount = match || hand ? 1 : .5f, Fix = fix,
            Components = hand ? PoseComponents.Rotation : added.Motion[index].Components,
        };
        return added with { Motion = added.Motion.SetItem(index, layer) };
    }

    /// <summary>
    /// Sets how many frames the match layer <paramref name="key"/> blends over (at least one): it ends on the
    /// clip's last frame and starts that many before, or at frame 0.
    /// </summary>
    public static TimelineDocument SetFixLength(TimelineDocument doc, string key, int frames)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || doc.Motion[index] is not { Fix.Kind: MotionFixKind.MatchLoop } layer)
            return doc;
        var length = Math.Max(TimelineDocument.MinLength, doc.Length);
        var start = Math.Max(0, length - Math.Max(1, frames));
        return layer.Start == start && layer.End == length ? doc
            : doc with { Motion = doc.Motion.SetItem(index, Normalize(layer with { Start = start, End = length })) };
    }

    /// <summary>
    /// Replaces the loop pose the match layer <paramref name="key"/> lands on, and the game fade it was read for, by
    /// <paramref name="loopPose"/>'s. A blend shorter than the new fade grows to it, so it still spans the cross-fade.
    /// </summary>
    public static TimelineDocument SetLoopPose(TimelineDocument doc, string key, MotionFix loopPose)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || doc.Motion[index] is not { Fix: { Kind: MotionFixKind.MatchLoop } fix } layer)
            return doc;
        var next = layer with { Fix = fix with { LoopFirst = loopPose.LoopFirst, LoopSecond = loopPose.LoopSecond, Fade = loopPose.Fade } };
        if (next.End - next.Start < loopPose.Fade)
        {
            var length = Math.Max(TimelineDocument.MinLength, doc.Length);
            next = Normalize(next with { Start = Math.Max(0, length - loopPose.Fade), End = length });
        }
        return Same(layer, next) ? doc : doc with { Motion = doc.Motion.SetItem(index, next) };
    }

    /// <summary>Pins <paramref name="bones"/> in the pin layer <paramref name="key"/>, each once, keyed and holding its turn; bones already pinned stay as they are.</summary>
    public static TimelineDocument PinBones(TimelineDocument doc, string key, IEnumerable<string> bones)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || doc.Motion[index] is not { Kind: MotionLayerKind.Pin } layer)
            return doc;
        var pins = layer.Pins.ToBuilder();
        foreach (var bone in bones)
            if (!string.IsNullOrEmpty(bone) && !pins.Any(pin => pin.Bone == bone))
                pins.Add(new MotionPin(bone));
        return pins.Count == layer.Pins.Length ? doc : doc with { Motion = doc.Motion.SetItem(index, layer with { Pins = pins.ToImmutable() }) };
    }

    /// <summary>Unpins <paramref name="bone"/> in the pin layer <paramref name="key"/>, taking its keys out too (keys left empty go).</summary>
    public static TimelineDocument RemovePin(TimelineDocument doc, string key, string bone)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || PinIndex(doc.Motion[index], bone) is not (var at and >= 0))
            return doc;
        var cleared = ClearBone(doc, key, bone);
        var layer = cleared.Motion[index];
        return cleared with { Motion = cleared.Motion.SetItem(index, layer with { Pins = layer.Pins.RemoveAt(at) }) };
    }

    /// <summary>
    /// Replaces the pin of <paramref name="pin"/>'s bone in the pin layer <paramref name="key"/> by
    /// <paramref name="pin"/>. A pin turned planted holds where the motion is down and has no keys, and a
    /// pin that rides with another bone would read its keyed spots in that bone's space, somewhere else:
    /// either way its keys go in the same change.
    /// </summary>
    public static TimelineDocument SetPin(TimelineDocument doc, string key, MotionPin pin)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || PinIndex(doc.Motion[index], pin.Bone) is not (var at and >= 0) || doc.Motion[index].Pins[at] == pin)
            return doc;
        var next = pin.Planted || pin.RelativeTo != doc.Motion[index].Pins[at].RelativeTo ? ClearBone(doc, key, pin.Bone) : doc;
        var layer = next.Motion[index];
        return next with { Motion = next.Motion.SetItem(index, layer with { Pins = layer.Pins.SetItem(at, pin) }) };
    }

    /// <summary>
    /// Sets which side a symmetry layer copies. A layer still named after its old direction is renamed
    /// after the new one; a name the user gave stays.
    /// </summary>
    public static TimelineDocument SetFrom(TimelineDocument doc, string key, MotionSide from)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || doc.Motion[index] is not { Kind: MotionLayerKind.Symmetry } layer || layer.From == from)
            return doc;
        var name = layer.Name == MotionSymmetry.DefaultName(layer.From) ? MotionSymmetry.DefaultName(from) : layer.Name;
        return doc with { Motion = doc.Motion.SetItem(index, layer with { From = from, Name = name }) };
    }

    /// <summary>
    /// Adds an offset layer over the whole clip holding <paramref name="bones"/>' changes in one key, so
    /// they stay the same all the way through: what LivePose adjustments become. Named
    /// <paramref name="name"/>, cut to <see cref="MaxName"/>. Unchanged with an error when
    /// <paramref name="bones"/> is empty or the document has no room for another layer.
    /// </summary>
    public static TimelineDocument AddOffsets(TimelineDocument doc, string name, IReadOnlyDictionary<string, BoneTransform> bones,
        out string key, out string? error)
    {
        key = string.Empty;
        if (bones.Count == 0)
        {
            error = "There are no adjustments to add.";
            return doc;
        }
        var added = Add(doc, MotionLayerKind.Offset, 0, Math.Max(TimelineDocument.MinLength, doc.Length), out key, out error);
        if (error != null)
            return doc;
        var index = IndexOf(added, key);
        var values = ImmutableDictionary.CreateRange(StringComparer.Ordinal, bones);
        name = string.IsNullOrWhiteSpace(name) ? added.Motion[index].Name : name.Trim();
        var layer = added.Motion[index] with
        {
            Name = name.Length > MaxName ? name[..MaxName] : name,
            Interpolation = MotionInterpolation.Linear,
            Keys = [new MotionKey(0, values)],
        };
        return added with { Motion = added.Motion.SetItem(index, layer) };
    }

    /// <summary>
    /// Leaves <paramref name="bones"/> out of the saved animation, or with <paramref name="excluded"/> false
    /// puts them back. The excluded bones stay sorted, so the same set is always the same list.
    /// </summary>
    public static TimelineDocument SetExcluded(TimelineDocument doc, IEnumerable<string> bones, bool excluded)
    {
        var set = doc.ExcludedBones.ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var bone in bones)
            if (!string.IsNullOrEmpty(bone))
                changed |= excluded ? set.Add(bone) : set.Remove(bone);
        return changed ? doc with { ExcludedBones = [.. set.Order(StringComparer.Ordinal)] } : doc;
    }

    /// <summary>Leaves exactly <paramref name="bones"/> out of the saved animation, sorted.</summary>
    public static TimelineDocument ExcludeOnly(TimelineDocument doc, IEnumerable<string> bones)
    {
        ImmutableArray<string> sorted = [.. bones.Where(bone => !string.IsNullOrEmpty(bone)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return sorted.SequenceEqual(doc.ExcludedBones, StringComparer.Ordinal) ? doc : doc with { ExcludedBones = sorted };
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
    /// timeline and the layer's end stays within the clip (or where it already was past it). A match stays
    /// on the clip's last frame.
    /// </summary>
    public static TimelineDocument Move(TimelineDocument doc, string key, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0 || IsMatch(doc.Motion[index]))
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
    /// a frame long or more and its fades shrink to fit. Keys stay where they are. A match's end stays on the
    /// clip's last frame, and its start, from where it is shown, sets its length (<see cref="SetFixLength"/>).
    /// </summary>
    public static TimelineDocument Resize(TimelineDocument doc, string key, bool leftEdge, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0)
            return doc;
        var layer = doc.Motion[index];
        if (IsMatch(layer))
            return leftEdge && Shown(layer, doc.Length) is var shown ? SetFixLength(doc, key, shown.End - shown.Start - delta) : doc;
        var resized = leftEdge
            ? layer with { Start = Math.Clamp(layer.Start + delta, 0, layer.End - 1) }
            : layer with { End = Math.Clamp(layer.End + delta, layer.Start + 1, Math.Max(layer.Start + 1, Limit(doc, layer))) };
        resized = Normalize(resized);
        return Same(layer, resized) ? doc : doc with { Motion = doc.Motion.SetItem(index, resized) };
    }

    /// <summary>
    /// Drags a fade's handle by <paramref name="delta"/> frames: the fade in's handle sits where the fade
    /// in ends, the fade out's where it begins. Together they never exceed the layer. A match has none.
    /// </summary>
    public static TimelineDocument Fade(TimelineDocument doc, string key, bool fadeIn, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0 || IsMatch(doc.Motion[index]))
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

    /// <summary>
    /// Moves one bone's value out of the layer's key at <paramref name="frame"/> by <paramref name="delta"/>
    /// frames, never onto or past that bone's neighbouring keys. It joins the key it lands on, taking that
    /// key's ease, or starts a key there with its old key's ease. Its old key goes when it is left empty.
    /// <paramref name="landed"/> is where it ends up (its frame when nothing moved).
    /// </summary>
    public static TimelineDocument MoveBoneKey(TimelineDocument doc, string key, string bone, int frame, int delta, out int landed)
    {
        landed = frame;
        var index = IndexOf(doc, key);
        if (index < 0)
            return doc;
        var layer = doc.Motion[index];
        var at = KeyIndex(layer, frame);
        if (at < 0 || delta == 0 || !layer.Keys[at].Bones.TryGetValue(bone, out var value))
            return doc;
        int low = 0, high = TimelineDocument.MaxFrames;
        for (var before = at - 1; before >= 0; before--)
            if (layer.Keys[before].Bones.ContainsKey(bone))
            {
                low = layer.Keys[before].Frame + 1;
                break;
            }
        for (var after = at + 1; after < layer.Keys.Length; after++)
            if (layer.Keys[after].Bones.ContainsKey(bone))
            {
                high = layer.Keys[after].Frame - 1;
                break;
            }
        landed = Math.Clamp(frame + delta, low, Math.Max(low, high));
        if (landed == frame)
            return doc;
        var source = layer.Keys[at];
        var moved = source.Moved.Contains(bone);
        var left = source with { Bones = source.Bones.Remove(bone), Moved = source.Moved.Remove(bone) };
        var keys = left.Bones.IsEmpty ? layer.Keys.RemoveAt(at) : layer.Keys.SetItem(at, left);
        var target = -1;
        for (var item = 0; item < keys.Length; item++)
            if (keys[item].Frame == landed)
                target = item;
        if (target >= 0)
        {
            var joined = keys[target];
            keys = keys.SetItem(target, joined with
            {
                Bones = joined.Bones.SetItem(bone, value),
                Moved = moved && !joined.Moved.Contains(bone) ? joined.Moved.Add(bone) : joined.Moved,
            });
        }
        else
        {
            var started = new MotionKey(landed, ImmutableDictionary<string, BoneTransform>.Empty.WithComparers(StringComparer.Ordinal).Add(bone, value),
                moved ? [bone] : [], source.Ease, source.Curve);
            var insert = 0;
            while (insert < keys.Length && keys[insert].Frame < landed)
                insert++;
            keys = keys.Insert(insert, started);
        }
        return doc with { Motion = doc.Motion.SetItem(index, layer with { Keys = keys }) };
    }

    /// <summary>
    /// <paramref name="picks"/> that still name a key of the layer (and for a bone, its value there), once each, by frame
    /// then bone. A whole key's pick takes in picks of its bones.
    /// </summary>
    public static ImmutableArray<KeyPick> Picks(MotionLayer layer, IEnumerable<KeyPick> picks)
    {
        var all = picks.ToHashSet();
        var whole = all.Where(pick => pick.Bone == null && KeyIndex(layer, pick.Frame) >= 0).Select(pick => pick.Frame).ToHashSet();
        return [.. all.Where(pick => pick.Bone == null
                ? whole.Contains(pick.Frame)
                : !whole.Contains(pick.Frame) && KeyIndex(layer, pick.Frame) is var at and >= 0 && layer.Keys[at].Bones.ContainsKey(pick.Bone))
            .OrderBy(pick => pick.Frame).ThenBy(pick => pick.Bone, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Moves the picked keys and bone values of the layer together by <paramref name="delta"/> frames, as far as none
    /// lands on or passes what stays behind: a whole key stops at any key left, a bone's value at that bone's next value.
    /// A bone's value joins a key it lands on. <paramref name="landed"/> is where the picks end up, by frame then bone,
    /// and <paramref name="applied"/> how far they moved.
    /// </summary>
    public static TimelineDocument MoveKeys(TimelineDocument doc, string key, IEnumerable<KeyPick> picks, int delta,
        out ImmutableArray<KeyPick> landed, out int applied)
    {
        applied = 0;
        var index = IndexOf(doc, key);
        landed = index < 0 ? [] : Picks(doc.Motion[index], picks);
        if (landed.IsEmpty || delta == 0)
            return doc;
        var layer = doc.Motion[index];
        var left = LeftBehind(layer, landed);
        int low = int.MinValue, high = int.MaxValue;
        foreach (var pick in landed)
        {
            low = Math.Max(low, -pick.Frame);
            high = Math.Min(high, TimelineDocument.MaxFrames - pick.Frame);
            foreach (var (frame, bones) in left)
                if (pick.Bone == null ? bones.Count > 0 : bones.Contains(pick.Bone))
                {
                    if (frame < pick.Frame)
                        low = Math.Max(low, frame + 1 - pick.Frame);
                    else if (frame > pick.Frame)
                        high = Math.Min(high, frame - 1 - pick.Frame);
                }
        }
        var step = Math.Clamp(delta, Math.Min(0, low), Math.Max(0, high));
        if (step == 0 || Shift(doc, key, landed, frame => frame + step, out var moved) is not { } next)
            return doc;
        landed = moved;
        applied = step;
        return next;
    }

    /// <summary>
    /// Spreads the picked keys and bone values of the layer over <paramref name="frames"/> frames from the first, each
    /// keeping its share of the way (to the nearest frame): fewer frames play them faster, more slower. Unchanged with an
    /// <paramref name="error"/> when two would land on one frame or one would land on or pass what stays behind.
    /// <paramref name="landed"/> is where the picks end up, in the same order.
    /// </summary>
    public static TimelineDocument ScaleKeys(TimelineDocument doc, string key, IEnumerable<KeyPick> picks, int frames,
        out ImmutableArray<KeyPick> landed, out string? error)
    {
        error = null;
        var index = IndexOf(doc, key);
        landed = index < 0 ? [] : Picks(doc.Motion[index], picks);
        if (landed.IsEmpty)
            return doc;
        var first = landed[0].Frame;
        var span = landed[^1].Frame - first;
        if (span == 0)
        {
            error = "Pick keys on two frames or more to spread them.";
            return doc;
        }
        frames = Math.Clamp(frames, 0, TimelineDocument.MaxFrames - first);
        if (frames == span)
            return doc;
        int Target(int frame) => first + (int)Math.Round((frame - first) * (double)frames / span, MidpointRounding.AwayFromZero);
        var origins = landed.Select(pick => pick.Frame).Distinct().ToArray();
        if (origins.Select(Target).Distinct().Count() < origins.Length || Shift(doc, key, landed, Target, out var moved) is not { } next)
        {
            error = frames < span ? "Two keys would land on the same frame, or one on a key that isn't picked." : "A key would pass one that isn't picked.";
            return doc;
        }
        landed = moved;
        return next;
    }

    /// <summary>Deletes the picked keys and bone values of the layer; keys left empty go.</summary>
    public static TimelineDocument DeleteKeys(TimelineDocument doc, string key, IEnumerable<KeyPick> picks)
    {
        if (Find(doc, key) is not { } layer)
            return doc;
        foreach (var pick in Picks(layer, picks))
            doc = pick.Bone is { } bone ? SetKey(doc, key, pick.Frame, bone, null) : DeleteKey(doc, key, pick.Frame);
        return doc;
    }

    /// <summary>The picked keys and bone values of the layer for <see cref="PasteKeys"/>; null when none is left.</summary>
    public static KeyCopy? CopyKeys(TimelineDocument doc, string key, IEnumerable<KeyPick> picks)
    {
        if (Find(doc, key) is not { } layer || Picks(layer, picks) is not { IsEmpty: false } picked)
            return null;
        var first = picked[0].Frame;
        var keys = ImmutableArray.CreateBuilder<MotionKey>();
        foreach (var group in picked.GroupBy(pick => pick.Frame))
        {
            var source = layer.Keys[KeyIndex(layer, group.Key)];
            if (group.Any(pick => pick.Bone == null))
            {
                keys.Add(source with { Frame = group.Key - first });
                continue;
            }
            var bones = group.Select(pick => pick.Bone!).ToHashSet(StringComparer.Ordinal);
            keys.Add(new MotionKey(group.Key - first, source.Bones.Where(bone => bones.Contains(bone.Key)).ToImmutableDictionary(StringComparer.Ordinal),
                [.. source.Moved.Where(bones.Contains)]));
        }
        return new KeyCopy(layer.Kind, keys.ToImmutable());
    }

    /// <summary>
    /// Keys <paramref name="copy"/> from <paramref name="frame"/> on in a layer of the kind it was copied from: each bone's
    /// value goes into the key there, a key it starts takes a whole copied key's ease. <paramref name="pasted"/> picks what
    /// was keyed: a key the paste started whole, else the bones it set. A pin layer takes only its keyed pins' bones.
    /// </summary>
    public static TimelineDocument PasteKeys(TimelineDocument doc, string key, int frame, KeyCopy copy, out ImmutableArray<KeyPick> pasted)
    {
        pasted = [];
        if (Find(doc, key) is not { } layer || layer.Kind != copy.Kind)
            return doc;
        var picks = ImmutableArray.CreateBuilder<KeyPick>();
        foreach (var item in copy.Keys)
        {
            var at = frame + item.Frame;
            if (at < 0 || at > TimelineDocument.MaxFrames)
                continue;
            var started = KeyIndex(Find(doc, key)!, at) < 0;
            var set = new List<KeyPick>();
            foreach (var (bone, value) in item.Bones)
            {
                var next = SetKey(doc, key, at, bone, value, copy.Kind == MotionLayerKind.Pose ? item.Moved.Contains(bone) : null);
                if (!ReferenceEquals(next, doc))
                    set.Add(new KeyPick(at, bone));
                doc = next;
            }
            if (started && set.Count > 0)
            {
                doc = item.Ease is { } ease ? SetEase(doc, key, at, ease, item.Curve) : doc;
                picks.Add(new KeyPick(at));
            }
            else
                picks.AddRange(set);
        }
        pasted = picks.ToImmutable();
        return doc;
    }

    /// <summary>Each key's bones that <paramref name="picks"/> leave where they are, by frame.</summary>
    private static Dictionary<int, HashSet<string>> LeftBehind(MotionLayer layer, ImmutableArray<KeyPick> picks)
    {
        var left = new Dictionary<int, HashSet<string>>();
        foreach (var item in layer.Keys)
            left[item.Frame] = picks.Contains(new KeyPick(item.Frame)) ? [] : item.Bones.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var pick in picks)
            if (pick.Bone != null)
                left[pick.Frame].Remove(pick.Bone);
        return left;
    }

    /// <summary>
    /// Moves each pick to <paramref name="target"/> of its frame with <see cref="MoveKey"/> or <see cref="MoveBoneKey"/>, the
    /// leading edge first so a pick never stops on one that hasn't moved yet; null when one can't land there.
    /// </summary>
    private static TimelineDocument? Shift(TimelineDocument doc, string key, ImmutableArray<KeyPick> picks, Func<int, int> target,
        out ImmutableArray<KeyPick> landed)
    {
        landed = picks;
        var forward = picks.Any(pick => target(pick.Frame) > pick.Frame);
        var order = forward ? picks.OrderByDescending(pick => pick.Frame) : picks.OrderBy(pick => pick.Frame);
        foreach (var pick in order)
        {
            var to = target(pick.Frame);
            int at;
            doc = pick.Bone is { } bone ? MoveBoneKey(doc, key, bone, pick.Frame, to - pick.Frame, out at) : MoveKey(doc, key, pick.Frame, to - pick.Frame, out at);
            if (at != to)
                return null;
        }
        landed = [.. picks.Select(pick => pick with { Frame = target(pick.Frame) })];
        return doc;
    }

    /// <summary>
    /// Sets how the bones go from the layer's key at <paramref name="frame"/> to their next keys: null
    /// for the layer's way, <see cref="MotionInterpolation.Custom"/> with <paramref name="curve"/>.
    /// </summary>
    public static TimelineDocument SetEase(TimelineDocument doc, string key, int frame, MotionInterpolation? ease, MotionCurve? curve = null)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || KeyIndex(doc.Motion[index], frame) is not (var at and >= 0))
            return doc;
        if (ease is { } mode && !Enum.IsDefined(mode))
            return doc;
        curve = ease == MotionInterpolation.Custom ? (curve ?? MotionCurve.Linear).Clamped() : null;
        var layer = doc.Motion[index];
        var old = layer.Keys[at];
        if (old.Ease == ease && Equals(old.Curve, curve))
            return doc;
        return doc with { Motion = doc.Motion.SetItem(index, layer with { Keys = layer.Keys.SetItem(at, old with { Ease = ease, Curve = curve }) }) };
    }

    /// <summary>
    /// The bones the layer's keys name, once each: in <paramref name="order"/> (the skeleton's, parents
    /// first) when known, bones it lacks last, then by name.
    /// </summary>
    public static ImmutableArray<string> KeyBones(MotionLayer layer, IReadOnlyDictionary<string, int>? order = null)
    {
        var bones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in layer.Keys)
            bones.UnionWith(item.Bones.Keys);
        return [.. bones.OrderBy(bone => order != null && order.TryGetValue(bone, out var at) ? at : int.MaxValue).ThenBy(bone => bone, StringComparer.Ordinal)];
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
        // Symmetry, fix and idle-motion layers never hold keys; a pin layer keys only its keyed pins.
        if (index < 0 || string.IsNullOrEmpty(bone) || doc.Motion[index].Kind is MotionLayerKind.Symmetry or MotionLayerKind.Fix or MotionLayerKind.Idle ||
            doc.Motion[index].Kind == MotionLayerKind.Pin && !doc.Motion[index].Pins.Any(pin => pin.Bone == bone && !pin.Planted))
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

    /// <summary>
    /// Adds copies of <paramref name="layers"/>, copied from a clip <paramref name="sourceLength"/> frames long,
    /// after the others with new keys. A layer that covered that whole clip covers this one; the rest keep
    /// their frames, cut to this clip's length. Unchanged with an error when they would pass the layer limit.
    /// </summary>
    public static TimelineDocument Paste(TimelineDocument doc, IReadOnlyList<MotionLayer> layers, int sourceLength, out ImmutableArray<string> created, out string? error)
    {
        created = [];
        error = null;
        if (layers.Count == 0)
            return doc;
        if (doc.Motion.Length + layers.Count > MotionEvaluator.MaxLayers)
        {
            error = $"An animation can have at most {MotionEvaluator.MaxLayers} bone layers.";
            return doc;
        }
        var length = Math.Max(TimelineDocument.MinLength, doc.Length);
        var keys = doc.Motion.Select(layer => layer.Key).ToHashSet(StringComparer.Ordinal);
        var next = Math.Max(1, doc.NextMotionKey);
        var added = ImmutableArray.CreateBuilder<string>(layers.Count);
        var motion = doc.Motion.ToBuilder();
        foreach (var layer in layers)
        {
            string key;
            do key = "m" + (next++).ToString(CultureInfo.InvariantCulture);
            while (!keys.Add(key));
            if (IsMatch(layer))
            {
                motion.Add(Normalize(Anchored(layer with { Key = key }, length)));
                added.Add(key);
                continue;
            }
            var start = Math.Clamp(layer.Start, 0, length - 1);
            var end = layer.Start <= 0 && layer.End >= sourceLength ? length : Math.Clamp(layer.End, start + 1, length);
            motion.Add(Normalize(layer with { Key = key, Start = start, End = end }));
            added.Add(key);
        }
        created = added.ToImmutable();
        return doc with { Motion = motion.ToImmutable(), NextMotionKey = next };
    }

    /// <summary>The frames layer drags stick to: every layer's start, end and keys, except <paramref name="except"/>'s.</summary>
    public static IEnumerable<int> SnapFrames(TimelineDocument doc, string? except)
    {
        foreach (var stored in doc.Motion)
        {
            if (stored.Key == except)
                continue;
            var layer = Shown(stored, doc.Length);
            yield return layer.Start;
            yield return layer.End;
            foreach (var item in layer.Keys)
                yield return item.Frame;
        }
    }

    /// <summary>The index of <paramref name="bone"/>'s pin in the layer, or -1.</summary>
    public static int PinIndex(MotionLayer layer, string bone)
    {
        for (var index = 0; index < layer.Pins.Length; index++)
            if (layer.Pins[index].Bone == bone)
                return index;
        return -1;
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

    /// <summary>Field-by-field equality, keys, bones, pins and the fix's loop pose compared by content.</summary>
    public static bool Same(MotionLayer a, MotionLayer b) =>
        ReferenceEquals(a, b) ||
        a.Key == b.Key && a.Kind == b.Kind && a.Name == b.Name && a.Start == b.Start && a.End == b.End && a.FadeIn == b.FadeIn &&
        a.FadeOut == b.FadeOut && a.Amount.Equals(b.Amount) && a.Components == b.Components && a.Interpolation == b.Interpolation &&
        a.Enabled == b.Enabled && a.From == b.From && a.Bones.SequenceEqual(b.Bones, StringComparer.Ordinal) && a.Pins.SequenceEqual(b.Pins) &&
        (a.Fix == null ? b.Fix == null
            : b.Fix != null && a.Fix.Kind == b.Fix.Kind && a.Fix.Fade == b.Fix.Fade &&
              SameBones(a.Fix.LoopFirst, b.Fix.LoopFirst) && SameBones(a.Fix.LoopSecond, b.Fix.LoopSecond) && SameHand(a.Fix.Hand, b.Fix.Hand)) &&
        SameIdle(a.Idle, b.Idle) &&
        a.Keys.Length == b.Keys.Length &&
        a.Keys.Zip(b.Keys).All(pair => pair.First.Frame == pair.Second.Frame && pair.First.Moved.SequenceEqual(pair.Second.Moved, StringComparer.Ordinal) &&
            pair.First.Ease == pair.Second.Ease && Equals(pair.First.Curve, pair.Second.Curve) && SameBones(pair.First.Bones, pair.Second.Bones));

    private static bool SameHand(MotionHand? a, MotionHand? b) =>
        ReferenceEquals(a, b) ||
        a != null && b != null && a.Side == b.Side && a.Shape == b.Shape && a.Curl.Equals(b.Curl) && a.Spread.Equals(b.Spread) &&
        a.KeepMotion == b.KeepMotion && a.Tips == b.Tips &&
        (a.Turns == null ? b.Turns == null : b.Turns != null && a.Turns.Count == b.Turns.Count &&
            a.Turns.All(bone => b.Turns.TryGetValue(bone.Key, out var other) && other == bone.Value));

    private static bool SameIdle(MotionIdle? a, MotionIdle? b) =>
        ReferenceEquals(a, b) ||
        a != null && b != null && a.Donor == b.Donor && a.Duration.Equals(b.Duration) && a.Samples == b.Samples && a.Spine.Equals(b.Spine) &&
        a.Arms.Equals(b.Arms) && a.Balance.Equals(b.Balance) && a.Tail.Equals(b.Tail) && a.Slide.SequenceEqual(b.Slide) &&
        (ReferenceEquals(a.Turns, b.Turns) || a.Turns.Count == b.Turns.Count &&
            a.Turns.All(bone => b.Turns.TryGetValue(bone.Key, out var other) && other.SequenceEqual(bone.Value)));

    private static bool SameBones(IReadOnlyDictionary<string, BoneTransform>? a, IReadOnlyDictionary<string, BoneTransform>? b) =>
        a == null ? b == null : b != null && a.Count == b.Count && a.All(bone => b.TryGetValue(bone.Key, out var other) && other == bone.Value);
}
