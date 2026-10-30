using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The strip under the lanes that draws the selected bone's turns and moves over the clip, on the canvas's frame axis.
/// Motion shows the motion before the layers (faint) and after them (solid, from <see cref="CurveBake"/>), with the
/// selected layer's keys marked; Layer shows the selected offset or pose layer's own values, a dot per key, and a dot
/// dragged up or down changes that channel of its key. Self-contained: it owns its switches, its height and its caches.
/// </summary>
internal sealed class CurveView(TimelineSession session, MotionDecode decode, BonePosing posing)
{
    private const string TypePopup = "##curve-type";
    private static readonly string[] ChannelNames = ["Turn X", "Turn Y", "Turn Z", "Move X", "Move Y", "Move Z"];
    private static readonly string[] Axes = ["X", "Y", "Z"];

    private readonly CurveBake bake = new();
    // Which channels show: the three turns at first.
    private readonly bool[] channels = [true, true, true, false, false, false];
    private readonly List<Vector2> points = [];
    private bool layerMode;
    // The strip's height with its splitter; 0 until the first draw.
    private float stripHeight;
    // A bone's curves from one clip (before layers, or after), kept while the clip and bone stay.
    private (MotionClip Clip, int Bone, CurveSamples Samples)? beforeCurve, afterCurve;
    // The selected layer's curve for a bone, kept while the layer (any edit replaces it), the bone and the loop stay.
    private (MotionLayer Layer, string Bone, float Loop, int Length, KeyCurve? Curve)? keyCurve;
    // The dot being dragged: its key, channel and value when the drag began, the mouse then, and the scale it began on.
    private (string Layer, int Frame, string Bone, CurveChannel Channel, MotionTransform Value, Vector3 Shown, float Amount, float MouseY,
        float Low, float High)? drag;
    // The dot Ctrl+click opened the typing popup for, and what is typed.
    private (string Layer, int Frame, string Bone, CurveChannel Channel, MotionTransform Value, Vector3 Shown)? typing;
    private float typed;
    private bool scrubbing;

    /// <summary>The toolbar's Curves switch.</summary>
    public bool Shown { get; set; }

    /// <summary>Drops the curves and the bake, for another clip.</summary>
    public void Forget()
    {
        bake.Forget();
        (beforeCurve, afterCurve, keyCurve, drag, typing) = (null, null, null, null, null);
    }

    /// <summary>How tall the strip and its splitter are in a body <paramref name="total"/> tall, leaving the lanes room.</summary>
    public float Height(float total)
    {
        if (stripHeight <= 0)
            stripHeight = Theme.Scaled(170);
        return Math.Clamp(stripHeight, Theme.Scaled(110), Math.Max(Theme.Scaled(110), total - Theme.Scaled(140)));
    }

    /// <summary>The splitter above the strip, then the strip, <paramref name="size"/> together; the width is the canvas's.</summary>
    public void Draw(TimelineEditor current, Vector2 size)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var grip = Theme.Scaled(6);
        DrawSplitter(size.X, grip);
        var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var background = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.PanelBg);
        using var child = ImRaii.Child("##timeline-curve-strip",new Vector2(size.X, Math.Max(1, size.Y - grip - spacing)), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        background.Dispose();
        padding.Dispose();
        if (!child.Success)
            return;
        var origin = ImGui.GetCursorScreenPos();
        var area = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        var legend = TimelineCanvas.LegendWidth(area.X);
        var content = Content(current);
        DrawLegend(origin, legend, content.Bone);
        ImGui.SetCursorScreenPos(origin + new Vector2(legend, 0));
        ImGui.InvisibleButton("##curves", new Vector2(Math.Max(1, area.X - legend), area.Y), ImGuiButtonFlags.MouseButtonLeft);
        var track = new Track(origin + new Vector2(legend, 0), origin + area, current.View, Theme.Scaled(8));
        var range = drag is { } held ? (held.Low, held.High) : Range(content);
        HandlePointer(current, content, track, range);
        DrawTrack(current, content, track, drag is { } now ? (now.Low, now.High) : range);
        DrawTypePopup(current);
    }

    private void DrawSplitter(float width, float grip)
    {
        ImGui.InvisibleButton("##timeline-curves-splitter", new Vector2(Math.Max(1, width), grip));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (active)
            stripHeight = Math.Max(Theme.Scaled(110), stripHeight - ImGui.GetIO().MouseDelta.Y);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var colour = active ? Theme.Accent : hovered ? Theme.Muted : Theme.WithAlpha(Theme.Muted, .25f);
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(min.X, min.Y + Theme.Scaled(2)), new Vector2(max.X, max.Y - Theme.Scaled(2)), TimelineUi.U32(colour));
    }

    /// <summary>The track's corners, the canvas's view and the room kept above and below the curves.</summary>
    private readonly record struct Track(Vector2 Min, Vector2 Max, TimelineView View, float Pad)
    {
        public float X(float frame) => Min.X + View.X(frame);
        public float Top => Min.Y + Pad;
        public float Bottom => Max.Y - Pad;
        public float Y(float value, (float Low, float High) range) => Top + (range.High - value) / Math.Max(1e-6f, range.High - range.Low) * Math.Max(1, Bottom - Top);
    }

    /// <summary>What the strip shows: the bone, a message in place of curves, and the curves of the mode.</summary>
    private readonly record struct Strip(string? Bone, string? Message, CurveSamples? Before, CurveSamples? After, bool Reading,
        MotionLayer? Layer, KeyCurve? Keys);

    private Strip Content(TimelineEditor current)
    {
        var bone = posing.Selection.Primary;
        if (bone == null)
            return new Strip(null, "Select a bone.", null, null, false, null, null);
        if (session.Source is not { } loaded)
            return new Strip(bone, null, null, null, false, null, null);
        var doc = current.Document;
        if (layerMode)
        {
            if (current.SelectedLayerValue is not { } layer)
                return new Strip(bone, "Select a layer.", null, null, false, null, null);
            if (layer.Kind is not (MotionLayerKind.Offset or MotionLayerKind.Pose))
                return new Strip(bone, "This layer has no keys to show.", null, null, false, layer, null);
            var loop = BonePosing.LoopFrames(loaded, doc);
            if (keyCurve is not { } cached || !ReferenceEquals(cached.Layer, layer) || cached.Bone != bone || cached.Loop != loop || cached.Length != doc.Length)
                keyCurve = cached = (layer, bone, loop, doc.Length, MotionCurves.OfKeys(layer, bone, 0, doc.Length, loop));
            return cached.Curve == null
                ? new Strip(bone, "This bone has no keys in this layer.", null, null, false, layer, null)
                : new Strip(bone, null, null, null, false, layer, cached.Curve);
        }
        if (decode.DecodedPose(loaded) is not { } decoded)
            return new Strip(bone, decode.FailedFor(loaded) ? "The motion could not be read." : decode.Status, null, null, !decode.FailedFor(loaded), null, null);
        var output = decode.OutputMotion(current, decoded);
        if (!posing.BoneTree(output.Skeleton).Index.TryGetValue(bone, out var index))
            return new Strip(bone, "This animation's skeleton has no such bone.", null, null, false, null, null);
        var before = Curve(ref beforeCurve, output, index, null);
        // A field drag holds history open without a pointer gesture; both edit the document every frame.
        var gesture = current.Gesture != TimelineGesture.None || current.History.InGesture;
        var baked = bake.After(output, doc.Motion, gesture, out var stale, out var failed);
        // The after curve starts in the before curve's turn, so the two line up where the layers change little.
        var after = ReferenceEquals(baked, output) ? before
            : (baked ?? stale) is { } shown ? Curve(ref afterCurve, shown, index, before.Frames.Length > 0 ? (Vector3?)before.Turn(0) : null) : null;
        // Held during a gesture nothing builds, so "Reading" waits for the drop.
        return new Strip(bone, failed ? "The layers could not be applied." : null, before, after, baked == null && !failed && !gesture,
            current.SelectedLayerValue, null);
    }

    private static CurveSamples Curve(ref (MotionClip Clip, int Bone, CurveSamples Samples)? cache, MotionClip clip, int bone, Vector3? seed)
    {
        if (cache is { } cached && ReferenceEquals(cached.Clip, clip) && cached.Bone == bone)
            return cached.Samples;
        var samples = MotionCurves.OfClip(clip, bone, seed);
        cache = (clip, bone, samples);
        return samples;
    }

    /// <summary>The vertical scale fitting the shown channels of every curve shown, turns at least 2 degrees and moves 0.5 cm tall.</summary>
    private (float Low, float High) Range(Strip content)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        var turns = false;
        foreach (var samples in new[] { content.Before, content.After, content.Keys?.Samples })
        {
            if (samples == null)
                continue;
            for (var c = 0; c < MotionCurves.Channels; c++)
                if (channels[c] && samples.Values[c] != null)
                {
                    min = Math.Min(min, samples.Min((CurveChannel)c));
                    max = Math.Max(max, samples.Max((CurveChannel)c));
                    turns |= c < 3;
                }
        }
        return MotionCurves.Fit(min, max, turns ? 2 : .5f);
    }

    /// <summary>Motion and Layer, the turn and move switches and the bone's name, in the legend column.</summary>
    private void DrawLegend(Vector2 origin, float legend, string? bone)
    {
        var gap = Theme.Scaled(2);
        ImGui.SetCursorScreenPos(origin + new Vector2(Theme.Scaled(6), Theme.Scaled(4)));
        using (ImRaii.Group())
        {
            if (Chip("Motion##curve-motion", !layerMode, null, "The bone's motion before the layers (faint) and after them."))
                layerMode = false;
            ImGui.SameLine(0, gap);
            if (Chip("Layer##curve-layer", layerMode, null, "The selected layer's keys for the bone. Drag a dot to change it."))
                layerMode = true;
            for (var row = 0; row < 2; row++)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, row == 0 ? "Turn" : "Move");
                ImGui.SameLine(Theme.Scaled(6) + Theme.Scaled(36));
                for (var axis = 0; axis < 3; axis++)
                {
                    var c = row * 3 + axis;
                    if (axis > 0)
                        ImGui.SameLine(0, gap);
                    if (Chip($"{Axes[axis]}##curve-channel-{c}", channels[c], AxisColour(axis), $"Show {ChannelNames[c]}" + (row == 0 ? ", in degrees." : ", in cm.")))
                        channels[c] = !channels[c];
                }
            }
        }
        if (bone == null)
            return;
        var drawList = ImGui.GetWindowDrawList();
        var y = ImGui.GetItemRectMax().Y + Theme.Scaled(4);
        drawList.PushClipRect(new Vector2(origin.X, y), new Vector2(origin.X + legend - Theme.Scaled(3), y + ImGui.GetFontSize() + 2), true);
        drawList.AddText(new Vector2(origin.X + Theme.Scaled(6), y), TimelineUi.U32(Theme.Label), AnimationPresentation.BoneName(bone));
        drawList.PopClipRect();
    }

    /// <summary>A small switch button, filled while on; its text in <paramref name="colour"/> while on when given.</summary>
    private static bool Chip(string label, bool on, Vector4? colour, string tooltip)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, on && colour == null)
            .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), on && colour == null)
            .Push(ImGuiCol.Text, colour ?? Theme.Text, on && colour != null)
            .Push(ImGuiCol.Text, Theme.Hint, !on);
        var pressed = ImGui.SmallButton(label);
        TimelineUi.Tip(tooltip);
        return pressed;
    }

    /// <summary>The gizmo's axis colours: X red, Y green, Z blue.</summary>
    private static Vector4 AxisColour(int axis)
    {
        var abgr = RotationBall.Front[axis];
        return new Vector4((abgr & 0xFF) / 255f, (abgr >> 8 & 0xFF) / 255f, (abgr >> 16 & 0xFF) / 255f, 1);
    }

    private static uint Faint(uint colour, float alpha) => (colour & 0x00FFFFFF) | ((uint)(Math.Clamp(alpha, 0, 1) * 255) << 24);

    /// <summary>
    /// A click or drag on the graph moves the playhead; in Layer mode a press on a dot drags its channel (one undo step) or
    /// with Ctrl opens a field to type it. The wheel scrolls and Ctrl+wheel zooms, as on the canvas.
    /// </summary>
    private void HandlePointer(TimelineEditor current, Strip content, Track track, (float Low, float High) range)
    {
        var io = ImGui.GetIO();
        var hovered = ImGui.IsItemHovered();
        var readOnly = session.ReadOnly;
        var hot = hovered && drag == null ? DotAt(content, track, range, io.MousePos) : null;
        if (ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hot is { } dot && content.Layer is { } layer && !readOnly)
                Press(current, content, layer, dot, io.KeyCtrl, io.MousePos.Y, range);
            else
                scrubbing = true;
        }
        if (drag is { } held)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || current.Gesture != TimelineGesture.None)
            {
                drag = null;
                current.EndFieldEdit();
            }
            else if (MotionEdits.Find(current.Document, held.Layer) != null)
            {
                var amount = held.Amount - (io.MousePos.Y - held.MouseY) * (held.High - held.Low) / Math.Max(1, track.Bottom - track.Top);
                var value = MotionCurves.WithChannel(held.Value, held.Shown, held.Channel, amount);
                current.Change(current.WithReach(MotionEdits.SetKey(current.Document, held.Layer, held.Frame, held.Bone, value.ToBone())));
            }
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        }
        else if (scrubbing)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                current.Playhead = (int)MathF.Round(track.View.FrameAt(io.MousePos.X - track.Min.X));
            else
                scrubbing = false;
        }
        if (hovered)
        {
            if (io.MouseWheel != 0)
            {
                if (io.KeyCtrl)
                    current.Zoom(io.MousePos.X - track.Min.X, MathF.Pow(1.2f, io.MouseWheel));
                else
                    current.Pan(io.MouseWheel * Theme.Scaled(60));
            }
            if (io.MouseWheelH != 0)
                current.Pan(-io.MouseWheelH * Theme.Scaled(60));
        }
        if (hot is { } over && drag == null && content.Keys is { } keys)
        {
            ImGui.SetMouseCursor(readOnly ? ImGuiMouseCursor.Arrow : ImGuiMouseCursor.ResizeNs);
            if (Widgets.HoverDelay($"curve-dot:{over.Key.Frame}:{over.Channel}"))
                ImGui.SetTooltip($"Frame {over.Key.Frame} · {ChannelNames[(int)over.Channel]} {Text(keys.Samples.Values[(int)over.Channel]![over.Key.Sample], over.Channel)}" +
                                 (readOnly ? "" : "\nDrag to change it. Ctrl+click to type."));
        }
    }

    /// <summary>A press on a dot: the drag starts, or with Ctrl the field to type it opens.</summary>
    private void Press(TimelineEditor current, Strip content, MotionLayer layer, (CurveKey Key, CurveChannel Channel) dot, bool ctrl, float mouseY,
        (float Low, float High) range)
    {
        if (MotionEdits.KeyIndex(layer, dot.Key.Frame) is not (var at and >= 0) || !layer.Keys[at].Bones.TryGetValue(content.Bone!, out var stored))
            return;
        var value = MotionTransform.From(stored);
        var shown = content.Keys!.Samples.Turn(dot.Key.Sample);
        var amount = MotionCurves.Value(value, shown, dot.Channel);
        if (ctrl)
        {
            typing = (layer.Key, dot.Key.Frame, content.Bone!, dot.Channel, value, shown);
            typed = amount;
            ImGui.OpenPopup(TypePopup);
            return;
        }
        current.BeginFieldEdit();
        drag = (layer.Key, dot.Key.Frame, content.Bone!, dot.Channel, value, shown, amount, mouseY, range.Low, range.High);
    }

    /// <summary>The dot under <paramref name="mouse"/>: a key of the shown layer curve on a shown channel.</summary>
    private (CurveKey Key, CurveChannel Channel)? DotAt(Strip content, Track track, (float Low, float High) range, Vector2 mouse)
    {
        if (content.Keys is not { } keys)
            return null;
        var reach = Theme.Scaled(6);
        (CurveKey, CurveChannel)? best = null;
        var nearest = reach * reach;
        foreach (var key in keys.Keys)
        {
            var x = track.X(key.Frame);
            if (MathF.Abs(x - mouse.X) > reach)
                continue;
            for (var c = 0; c < MotionCurves.Channels; c++)
            {
                if (!channels[c] || keys.Samples.Values[c] is not { } values || c >= 3 && !key.Moves)
                    continue;
                var distance = Vector2.DistanceSquared(new Vector2(x, track.Y(values[key.Sample], range)), mouse);
                if (distance <= nearest)
                    (best, nearest) = ((key, (CurveChannel)c), distance);
            }
        }
        return best;
    }

    private static string Text(float value, CurveChannel channel) =>
        value.ToString("0.#", CultureInfo.InvariantCulture) + (MotionCurves.IsTurn(channel) ? "°" : " cm");

    private void DrawTrack(TimelineEditor current, Strip content, Track track, (float Low, float High) range)
    {
        var drawList = ImGui.GetWindowDrawList();
        var view = track.View;
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        drawList.PushClipRect(track.Min, track.Max, true);
        // The grid at the canvas's labelled ticks, the shade past the end and the zero line.
        var grid = TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .28f));
        var major = view.MajorStep(Theme.Scaled(64));
        for (var frame = view.FirstTick(major); frame <= view.Last; frame += major)
        {
            var x = TimelineUi.Crisp(track.X(frame));
            drawList.AddLine(new Vector2(x, track.Min.Y), new Vector2(x, track.Max.Y), grid, line);
        }
        var endX = track.X(current.Document.Length);
        if (endX < track.Max.X)
            drawList.AddRectFilled(new Vector2(Math.Max(endX, track.Min.X), track.Min.Y), track.Max, TimelineUi.U32(Theme.PastEnd));
        if (content.Message == null || content.Before != null)
        {
            if (range.Low < 0 && range.High > 0)
            {
                var zero = TimelineUi.Crisp(track.Y(0, range));
                drawList.AddLine(new Vector2(track.Min.X, zero), new Vector2(track.Max.X, zero), TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .6f)), line);
            }
            DrawCurves(drawList, current, content, track, range);
            bool turns = channels[0] || channels[1] || channels[2], moves = channels[3] || channels[4] || channels[5];
            var unit = turns ? moves ? "" : "°" : " cm";
            var hint = TimelineUi.U32(Theme.Hint);
            drawList.AddText(new Vector2(track.Min.X + Theme.Scaled(4), track.Min.Y + Theme.Scaled(1)), hint, Label(range.High, unit));
            drawList.AddText(new Vector2(track.Min.X + Theme.Scaled(4), track.Max.Y - ImGui.GetFontSize() - Theme.Scaled(1)), hint, Label(range.Low, unit));
        }
        if (content.Message is { } message || content.Reading)
        {
            var text = content.Message ?? "Reading";
            var size = ImGui.CalcTextSize(text);
            var at = new Vector2(track.Max.X - size.X - Theme.Scaled(6), track.Min.Y + Theme.Scaled(1));
            if (content.Before == null)
                at = new Vector2(track.Min.X + Theme.Scaled(8), (track.Min.Y + track.Max.Y - size.Y) / 2);
            drawList.AddText(at, TimelineUi.U32(content.Before == null ? Theme.Muted : Theme.Hint), text);
        }
        var playheadX = TimelineUi.Crisp(track.X(current.Playhead));
        drawList.AddLine(new Vector2(playheadX, track.Min.Y), new Vector2(playheadX, track.Max.Y), TimelineUi.U32(Theme.Playhead), line);
        drawList.PopClipRect();
    }

    private static string Label(float value, string unit) => value.ToString("0.#", CultureInfo.InvariantCulture) + unit;

    private void DrawCurves(ImDrawListPtr drawList, TimelineEditor current, Strip content, Track track, (float Low, float High) range)
    {
        var thin = Math.Max(1, Theme.Scale);
        var thick = Theme.Scaled(1.6f);
        if (content.Keys is { } keys)
        {
            for (var c = 0; c < MotionCurves.Channels; c++)
                if (channels[c] && keys.Samples.Values[c] is { } values)
                    Stroke(drawList, keys.Samples.Frames, values, track, range, RotationBall.Front[c % 3], thick);
            DrawDots(drawList, keys, track, range);
            return;
        }
        // The selected layer's keys for the bone, as faint lines.
        if (content.Layer is { } layer && content.Bone is { } bone)
        {
            var tick = TimelineUi.U32(Theme.WithAlpha(Theme.Accent, .35f));
            foreach (var key in layer.Keys)
                if (key.Bones.ContainsKey(bone))
                {
                    var x = TimelineUi.Crisp(track.X(key.Frame));
                    drawList.AddLine(new Vector2(x, track.Min.Y), new Vector2(x, track.Max.Y), tick, thin);
                }
        }
        for (var c = 0; c < MotionCurves.Channels; c++)
        {
            if (!channels[c])
                continue;
            var colour = RotationBall.Front[c % 3];
            if (content.Before is { Values: var before } && before[c] is { } first && !ReferenceEquals(content.Before, content.After))
                Stroke(drawList, content.Before.Frames, first, track, range, Faint(colour, .35f), thin);
            if (content.After is { } after && after.Values[c] is { } values)
                Stroke(drawList, after.Frames, values, track, range, colour, thick);
        }
    }

    /// <summary>Each key's dot on the shown channels; moves only where the key sets them.</summary>
    private void DrawDots(ImDrawListPtr drawList, KeyCurve keys, Track track, (float Low, float High) range)
    {
        var radius = Theme.Scaled(3.5f);
        var outline = TimelineUi.U32(Theme.PanelBg);
        foreach (var key in keys.Keys)
        {
            var x = track.X(key.Frame);
            if (x < track.Min.X - radius || x > track.Max.X + radius)
                continue;
            for (var c = 0; c < MotionCurves.Channels; c++)
            {
                if (!channels[c] || keys.Samples.Values[c] is not { } values || c >= 3 && !key.Moves)
                    continue;
                var dragged = drag is { } held && held.Frame == key.Frame && (int)held.Channel == c;
                var centre = new Vector2(x, track.Y(values[key.Sample], range));
                drawList.AddCircleFilled(centre, radius + Theme.Scaled(1.5f), dragged ? TimelineUi.U32(Theme.Text) : outline);
                drawList.AddCircleFilled(centre, radius, RotationBall.Front[c % 3]);
            }
        }
    }

    /// <summary>One channel's line over the frames in view, about a point per pixel.</summary>
    private void Stroke(ImDrawListPtr drawList, float[] frames, float[] values, Track track, (float Low, float High) range, uint colour, float thickness)
    {
        if (frames.Length == 0)
            return;
        var view = track.View;
        var first = Math.Max(0, Lower(frames, view.First) - 1);
        var last = Math.Min(frames.Length - 1, Lower(frames, view.Last) + 1);
        if (last < first)
            return;
        var step = Math.Max(1, (int)((last - first) / Math.Max(1, track.Max.X - track.Min.X)));
        points.Clear();
        for (var i = first; i <= last; i += step)
            points.Add(new Vector2(track.X(frames[i]), track.Y(values[i], range)));
        if ((last - first) % step != 0)
            points.Add(new Vector2(track.X(frames[last]), track.Y(values[last], range)));
        if (points.Count == 1)
            points.Add(points[0] + new Vector2(1, 0));
        foreach (var point in points)
            drawList.PathLineTo(point);
        drawList.PathStroke(colour, ImDrawFlags.None, thickness);
    }

    /// <summary>The first index whose frame is at or past <paramref name="frame"/>.</summary>
    private static int Lower(float[] frames, float frame)
    {
        int low = 0, high = frames.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (frames[middle] < frame) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    /// <summary>Ctrl+click on a dot: the channel's value to type; Enter writes it as one undo step.</summary>
    private void DrawTypePopup(TimelineEditor current)
    {
        using var popup = ImRaii.Popup(TypePopup);
        if (!popup.Success || typing is not { } t)
            return;
        ImGui.TextColored(Theme.Label, $"{ChannelNames[(int)t.Channel]} at frame {t.Frame}");
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(Theme.Scaled(110));
        ImGui.InputFloat("##curve-value", ref typed, 0, 0, MotionCurves.IsTurn(t.Channel) ? "%.2f°" : "%.2f cm");
        if (!ImGui.IsItemDeactivatedAfterEdit())
            return;
        var value = MotionCurves.WithChannel(t.Value, t.Shown, t.Channel, typed);
        current.Commit(current.WithReach(MotionEdits.SetKey(current.Document, t.Layer, t.Frame, t.Bone, value.ToBone())));
        typing = null;
        ImGui.CloseCurrentPopup();
    }
}
