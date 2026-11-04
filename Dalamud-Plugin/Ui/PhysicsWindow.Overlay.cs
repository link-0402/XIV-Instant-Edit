using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Physics;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui;

/// <summary>
/// The area being edited, drawn on the character for <see cref="PhysicsOverlay"/>: each chain's
/// bones and tip, the balls on its moving points, the bone's swing cone, where its spring pulls, and
/// the collision shapes (lit when the chains bump into them, dim otherwise). The control under the
/// mouse lights its part, so a slider shows what it changes; a part under the mouse on the character
/// lights too, and a click opens its group in the builder (another area's chain picks that area).
/// Drawn on your posed copy in GPose, else on your character, whenever the builder is open and the eye is on.
/// </summary>
internal sealed partial class PhysicsWindow
{
    private const int RingPoints = 24;
    private const float DashPixels = 6, GapPixels = 4, ReachPixels = 8;
    private const int FlashMs = 1000;

    /// <summary> The builder's groups of settings, one per part on the character. </summary>
    private enum EditorGroup
    {
        None,
        Bones,
        Tip,
        Swing,
        Spring,
        Collision,
    }

    private readonly PhysicsOverlay overlayWindow;
    private readonly ActorPose overlayPose = new();
    // The control the mouse was over while the builder drew this frame; the overlay reads it a frame later.
    private PhysicsOverlayHover overlayHover = PhysicsOverlayHover.None;
    private PhysicsOverlayHover overlayLit = PhysicsOverlayHover.None;
    // The part under the mouse on the character, found while the overlay drew; the builder frames its group a frame later.
    private PhysicsOverlayTarget? overlayPick;
    // A click on the character: the group to open and scroll to, or the shape row to scroll to, and what flashes until when.
    private EditorGroup jumpGroup;
    private string? jumpShape;
    private EditorGroup flashGroup;
    private string? flashShape;
    private long flashUntil;

    internal PhysicsOverlay Overlay => overlayWindow;

    /// <summary> Whether the overlay draws now: the builder is open on a file, the character is found and the eye is on. </summary>
    internal bool OverlayWanted => IsOpen && document != null && target != null && config.PhysicsShowOverlay;

    /// <summary> Lights <paramref name="part"/> on the character while the last control is hovered. </summary>
    private void Lights(PhysicsOverlayPart? part, string? shape = null)
    {
        if (part is { } lit && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            overlayHover = new PhysicsOverlayHover(lit, shape);
    }

    /// <summary> Hands this frame's hovered control to the overlay and starts over. </summary>
    private void TakeHover()
    {
        overlayLit = overlayHover;
        overlayHover = PhysicsOverlayHover.None;
    }

    private const string OverlayPopup = "##physics-overlay-popup";

    /// <summary> The eye: what the builder draws on your character, with a tick per part. </summary>
    private void DrawOverlayToggle()
    {
        var on = config.PhysicsShowOverlay;
        if (Widgets.IconButton("##physics-overlay", on ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, "What to show on your character"))
            ImGui.OpenPopup(OverlayPopup);
        using var popup = ImRaii.Popup(OverlayPopup);
        if (!popup.Success)
            return;
        if (ImGui.Checkbox("Show on your character##overlay-on", ref on))
        {
            config.PhysicsShowOverlay = on;
            saveConfig();
        }
        ImGui.Separator();
        using (ImRaii.Disabled(!on))
        {
            OverlayTick(PhysicsOverlayShown.Chain, "Bones", "Each bone of the area and its tip.", Theme.Accent);
            OverlayTick(PhysicsOverlayShown.Tip, "Tip", "The balls that bump into shapes.", Theme.Accent);
            OverlayTick(PhysicsOverlayShown.Cone, "Swing limit", "How far each bone may swing.", Theme.Info);
            OverlayTick(PhysicsOverlayShown.Spring, "Spring", "Where the spring pulls the tip.", Theme.Warning);
            OverlayTick(PhysicsOverlayShown.Shapes, "Shapes it collides with", "The shapes ticked under Collision with, in green.", Theme.Success);
            OverlayTick(PhysicsOverlayShown.OtherShapes, "Other shapes", "Every other shape in the file, in grey.", Theme.Inactive);
            OverlayTick(PhysicsOverlayShown.OtherAreas, "Other areas", "The file's other areas, in grey.", Theme.Hint);
        }
        Widgets.Hint("A hovered setting always shows its part.");
        Widgets.Hint("Click a part on your character to edit it.");
    }

    private void OverlayTick(PhysicsOverlayShown part, string label, string tip, Vector4 colour)
    {
        PartDot(colour);
        var shown = (PhysicsOverlayShown)config.PhysicsOverlayShown;
        var on = shown.HasFlag(part);
        if (ImGui.Checkbox($"{label}##overlay-{part}", ref on))
        {
            config.PhysicsOverlayShown = (int)(on ? shown | part : shown & ~part);
            saveConfig();
        }
        Tip(tip);
    }

    /// <summary> A dot in a part's colour on the character, starting the line. </summary>
    private static void PartDot(Vector4 colour)
    {
        var height = ImGui.GetFrameHeight();
        var at = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(at + new Vector2(Theme.Scaled(5), height / 2), Theme.Scaled(4), ImGui.GetColorU32(colour));
        ImGui.Dummy(new Vector2(Theme.Scaled(12), height));
        ImGui.SameLine(0, 0);
    }

    private static EditorGroup GroupOf(PhysicsOverlayPart part) => part switch
    {
        PhysicsOverlayPart.Chain => EditorGroup.Bones,
        PhysicsOverlayPart.Tip => EditorGroup.Tip,
        PhysicsOverlayPart.Cone => EditorGroup.Swing,
        PhysicsOverlayPart.Spring => EditorGroup.Spring,
        PhysicsOverlayPart.Shape or PhysicsOverlayPart.OtherShape => EditorGroup.Collision,
        _ => EditorGroup.None,
    };

    private bool Flashing => Environment.TickCount64 < flashUntil;

    /// <summary>
    /// A group's header: a dot in its part's colour, framed while that part is under the mouse on the character,
    /// flashing after a click there opened it. Hovered, it lights all its parts. Returns whether it's open.
    /// </summary>
    private bool GroupHeader(EditorGroup group, string label, PhysicsOverlayPart part, Vector4 colour, string? tip = null)
    {
        if (jumpGroup == group)
            ImGui.SetNextItemOpen(true);
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Header, Theme.WithAlpha(colour, .35f), flashGroup == group && Flashing))
            open = ImGui.CollapsingHeader($"   {label}##group-{group}", ImGuiTreeNodeFlags.DefaultOpen);
        if (tip != null)
            Tip(tip);
        Lights(part);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        drawList.AddCircleFilled(new Vector2(min.X + style.FramePadding.X * 2 + ImGui.GetFontSize() + Theme.Scaled(4), (min.Y + max.Y) / 2), Theme.Scaled(4),
            ImGui.GetColorU32(colour));
        if (OverlayWanted && overlayPick is { } pick && GroupOf(pick.Part) == group)
            drawList.AddRect(min, max, ImGui.GetColorU32(colour), style.FrameRounding, ImDrawFlags.None, Theme.Scaled(1.5f));
        if (jumpGroup == group)
        {
            if (jumpShape == null)
                ImGui.SetScrollHereY(0);
            jumpGroup = EditorGroup.None;
        }
        return open;
    }

    /// <summary> A click on the character: another area's chain picks that area; a part opens its group and flashes it, a shape its row. </summary>
    private void Pick(PhysicsOverlayTarget hit)
    {
        flashUntil = Environment.TickCount64 + FlashMs;
        flashGroup = EditorGroup.None;
        flashShape = jumpShape = null;
        jumpGroup = EditorGroup.None;
        if (hit.Part == PhysicsOverlayPart.OtherArea)
        {
            selected = hit.Simulator;
            return;
        }
        if (hit.Part is PhysicsOverlayPart.Shape or PhysicsOverlayPart.OtherShape)
        {
            flashShape = jumpShape = hit.Shape;
            if (selected != ShapesView)
                jumpGroup = EditorGroup.Collision;
            return;
        }
        // With the sides unlinked, a side's part picks its chain.
        if (!bothSides && hit.Simulator == selected && hit.Chain >= 0)
            chainChoice = hit.Chain;
        flashGroup = jumpGroup = GroupOf(hit.Part);
    }

    /// <summary> Whether the mouse is free for the character: over no window, field, popup or drag. </summary>
    private static bool MouseOnCharacter()
        => !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsAnyItemActive() &&
           !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) &&
           (!ImGui.IsAnyMouseDown() || ImGui.IsMouseClicked(ImGuiMouseButton.Left));

    /// <summary> The chains of the file's other areas, drawn faint with "Other areas" ticked. </summary>
    private static List<(int Simulator, int Chain)> OtherChains(PhybFile file, int except)
    {
        var all = new List<(int, int)>();
        for (var s = 0; s < file.Simulators.Count; s++)
            if (s != except && !file.Simulators[s].Excluded)
                for (var c = 0; c < file.Simulators[s].Chains.Count; c++)
                    if (file.Simulators[s].Chains[c].Nodes.Count >= 2)
                        all.Add((s, c));
        return all;
    }

    /// <summary> The overlay's whole content; called from its draw, on the framework thread. </summary>
    internal void DrawOverlay()
    {
        var picked = overlayPick;
        overlayPick = null;
        if (document is not { } current || target is not { } found || !service.ReadShownPose(overlayPose) || GameCamera.Read() is not { } camera)
            return;
        var shapesView = selected == ShapesView;
        var targets = shapesView ? AllChains(current.File) : selected >= 0 && selected < current.File.Simulators.Count ? Targets(current.File, selected) : [];
        // The shapes view is about the shapes, so every one draws there.
        var shown = shapesView ? PhysicsOverlayShown.All : (PhysicsOverlayShown)config.PhysicsOverlayShown;
        var others = !shapesView && shown.HasFlag(PhysicsOverlayShown.OtherAreas) ? OtherChains(current.File, selected) : null;
        // A hovered control wins; else the part under the mouse on the character the frame before.
        var lit = overlayLit.Part != null ? overlayLit : picked?.Hover ?? PhysicsOverlayHover.None;
        var shapes = PhysicsOverlayModel.Build(current.File, targets, shapesView, lit, found.Character.Skeleton, Frame, shown, others);
        if (shapes.IsEmpty)
            return;
        var picker = new PhysicsOverlayPicker(ImGui.GetMousePos(), Theme.Scaled(ReachPixels));
        var viewport = ImGui.GetMainViewport();
        var world = PoseGizmo.World(overlayPose.Position, overlayPose.Rotation, overlayPose.Scale);
        var toClip = camera.View * camera.Projection;
        var right = GameCamera.Right(camera.View);
        var drawList = ImGui.GetWindowDrawList();
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, .5f));

        bool Project(Vector3 model, out Vector2 screen)
            => PoseGizmo.Project(Vector3.Transform(model, world), toClip, viewport.Pos, viewport.Size, out screen);

        // A radius in model space as pixels on the screen, measured across the camera at the point.
        float Pixels(Vector3 model, float radius)
        {
            var at = Vector3.Transform(model, world);
            return PoseGizmo.Project(at, toClip, viewport.Pos, viewport.Size, out var center) &&
                   PoseGizmo.Project(at + right * radius * overlayPose.Scale.X, toClip, viewport.Pos, viewport.Size, out var edge)
                ? Vector2.Distance(center, edge)
                : 0;
        }

        void Line(Vector2 a, Vector2 b, uint colour, float width)
        {
            drawList.AddLine(a, b, shadow, width + Theme.Scaled(1.5f));
            drawList.AddLine(a, b, colour, width);
        }

        void Dashes(Vector2 a, Vector2 b, uint colour, float width)
        {
            var length = Vector2.Distance(a, b);
            if (length < 1)
                return;
            var step = Vector2.Normalize(b - a);
            for (var at = 0f; at < length; at += Theme.Scaled(DashPixels + GapPixels))
                Line(a + step * at, a + step * Math.Min(length, at + Theme.Scaled(DashPixels)), colour, width);
        }

        foreach (var segment in shapes.Segments)
        {
            if (!Project(segment.From, out var a) || !Project(segment.To, out var b))
                continue;
            var (colour, width) = Style(segment.Part, segment.Lit);
            if (segment.Dashed)
                Dashes(a, b, colour, width);
            else
                Line(a, b, colour, width);
            picker.Segment(a, b, new PhysicsOverlayTarget(segment.Part, segment.Simulator, segment.Chain));
        }
        foreach (var capsule in shapes.Capsules)
        {
            if (!Project(capsule.A, out var a) || !Project(capsule.B, out var b))
                continue;
            var (colour, width) = Style(capsule.Part, capsule.Lit);
            var ra = Pixels(capsule.A, capsule.Radius);
            var rb = Pixels(capsule.B, capsule.Radius);
            var shape = new PhysicsOverlayTarget(capsule.Part, Shape: capsule.Name);
            drawList.AddCircle(a, ra, colour, 0, width);
            drawList.AddCircle(b, rb, colour, 0, width);
            picker.Ring(a, ra, shape);
            picker.Ring(b, rb, shape);
            var along = b - a;
            if (along.LengthSquared() > 1)
            {
                var side = Vector2.Normalize(new Vector2(-along.Y, along.X));
                drawList.AddLine(a + side * ra, b + side * rb, colour, width);
                drawList.AddLine(a - side * ra, b - side * rb, colour, width);
                picker.Segment(a + side * ra, b + side * rb, shape);
                picker.Segment(a - side * ra, b - side * rb, shape);
            }
            if (capsule.Lit)
                Label((a + b) / 2, PhysicsShapeNames.Label(current.File.Collision, capsule.Name, found.Character.Skeleton), colour);
        }
        foreach (var sphere in shapes.Spheres)
        {
            if (!Project(sphere.Center, out var at))
                continue;
            var (colour, width) = Style(sphere.Part, sphere.Lit);
            var radius = Pixels(sphere.Center, sphere.Radius);
            drawList.AddCircle(at, radius, colour, 0, width);
            picker.Ring(at, radius, new PhysicsOverlayTarget(sphere.Part, Shape: sphere.Name));
            if (sphere.Lit)
                Label(at, PhysicsShapeNames.Label(current.File.Collision, sphere.Name, found.Character.Skeleton), colour);
        }
        foreach (var circle in shapes.Circles)
        {
            var (colour, width) = Style(circle.Part, circle.Lit);
            var owner = new PhysicsOverlayTarget(circle.Part, circle.Simulator, circle.Chain);
            if (circle.Normal is not { } normal)
            {
                if (Project(circle.Center, out var at))
                {
                    var radius = Pixels(circle.Center, circle.Radius);
                    drawList.AddCircle(at, radius, colour, 0, width);
                    picker.Disc(at, radius, owner);
                }
                continue;
            }
            // A ring in the world, drawn point by point so it reads as the ellipse the camera sees.
            var (u, v) = PhysicsOverlayModel.Basis(normal);
            Vector2? first = null, previous = null;
            for (var i = 0; i < RingPoints; i++)
            {
                var t = i * MathF.PI * 2 / RingPoints;
                if (!Project(circle.Center + (u * MathF.Cos(t) + v * MathF.Sin(t)) * circle.Radius, out var point))
                {
                    previous = null;
                    continue;
                }
                if (previous is { } last)
                {
                    drawList.AddLine(last, point, colour, width);
                    picker.Segment(last, point, owner);
                }
                first ??= point;
                previous = point;
            }
            if (first is { } start && previous is { } end)
            {
                drawList.AddLine(end, start, colour, width);
                picker.Segment(end, start, owner);
            }
        }
        foreach (var mark in shapes.Marks)
        {
            if (!Project(mark.At, out var at))
                continue;
            var (colour, _) = Style(mark.Part, mark.Lit);
            var radius = Theme.Scaled(mark.Lit ? 4 : 3);
            drawList.AddCircleFilled(at, radius + Theme.Scaled(1.5f), shadow);
            drawList.AddCircleFilled(at, radius, colour);
            picker.Point(at, new PhysicsOverlayTarget(mark.Part, mark.Simulator, mark.Chain));
            if (mark.Lit && mark.Label is { } text)
                Label(at, text, colour);
        }

        // The part under the mouse takes it from the game: its name and value in a tooltip, and a click edits it.
        if (!MouseOnCharacter() || picker.Hit is not { } hit)
            return;
        overlayPick = hit;
        ImGui.SetNextFrameWantCaptureMouse(true);
        if (PhysicsOverlayText.Describe(current.File, hit, found.Character.Skeleton, shapesView) is { Length: > 0 } tooltip)
            ImGui.SetTooltip(tooltip);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            Pick(hit);

        void Label(Vector2 at, string text, uint colour)
        {
            var offset = new Vector2(Theme.Scaled(8), -ImGui.GetTextLineHeight() / 2);
            drawList.AddText(at + offset + new Vector2(1, 1), shadow, text);
            drawList.AddText(at + offset, colour, text);
        }
    }

    /// <summary> Every chain in the file, for the shapes view, where the shapes matter and the chains are only context. </summary>
    private static List<(int Simulator, int Chain)> AllChains(PhybFile file)
    {
        var all = new List<(int, int)>();
        for (var s = 0; s < file.Simulators.Count; s++)
            for (var c = 0; c < file.Simulators[s].Chains.Count; c++)
                if (!file.Simulators[s].Excluded && file.Simulators[s].Chains[c].Nodes.Count >= 2)
                    all.Add((s, c));
        return all;
    }

    /// <summary> A bone's place in the shown character's model space, as it's drawn now; null for a bone it lacks. </summary>
    private PhysicsFrame? Frame(string bone)
        => overlayPose.Index.TryGetValue(bone, out var at) ? new PhysicsFrame(overlayPose.Positions[at], overlayPose.Rotations[at], Vector3.One) : null;

    /// <summary> A part's colour and line width: lit parts are bright and thick, dim shapes faint. </summary>
    private static (uint Colour, float Width) Style(PhysicsOverlayPart part, bool lit)
    {
        var colour = part switch
        {
            PhysicsOverlayPart.Chain or PhysicsOverlayPart.Tip => Theme.Accent,
            PhysicsOverlayPart.Cone => Theme.Info,
            PhysicsOverlayPart.Spring => Theme.Warning,
            PhysicsOverlayPart.Shape => Theme.Success,
            PhysicsOverlayPart.OtherArea => Theme.Hint,
            _ => Theme.Inactive,
        };
        var alpha = lit ? 1f : part is PhysicsOverlayPart.OtherShape or PhysicsOverlayPart.OtherArea ? .5f : .7f;
        return (ImGui.GetColorU32(Theme.WithAlpha(colour, alpha)), Theme.Scaled(lit ? 2.5f : 1.5f));
    }
}
