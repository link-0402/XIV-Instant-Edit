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
/// mouse lights its part, so a slider shows what it changes. Drawn on your posed copy in GPose, else
/// on your character, whenever the builder is open and the eye is on.
/// </summary>
internal sealed partial class PhysicsWindow
{
    private const int RingPoints = 24;
    private const float DashPixels = 6, GapPixels = 4;

    private readonly PhysicsOverlay overlayWindow;
    private readonly ActorPose overlayPose = new();
    // The control the mouse was over while the builder drew this frame; the overlay reads it a frame later.
    private PhysicsOverlayHover overlayHover = PhysicsOverlayHover.None;
    private PhysicsOverlayHover overlayLit = PhysicsOverlayHover.None;

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
            OverlayTick(PhysicsOverlayShown.Chain, "Area", "Each bone of the area and its tip.");
            OverlayTick(PhysicsOverlayShown.Tip, "Tip size", "The balls that bump into shapes.");
            OverlayTick(PhysicsOverlayShown.Cone, "Swing limit", "How far each bone may swing.");
            OverlayTick(PhysicsOverlayShown.Spring, "Spring", "Where the spring pulls the tip.");
            OverlayTick(PhysicsOverlayShown.Shapes, "Shapes it collides with", "The shapes ticked under Collision with, in green.");
            OverlayTick(PhysicsOverlayShown.OtherShapes, "Other shapes", "Every other shape in the file, in grey.");
        }
        Widgets.Hint("A hovered setting always shows its part.");
    }

    private void OverlayTick(PhysicsOverlayShown part, string label, string tip)
    {
        var shown = (PhysicsOverlayShown)config.PhysicsOverlayShown;
        var on = shown.HasFlag(part);
        if (ImGui.Checkbox($"{label}##overlay-{part}", ref on))
        {
            config.PhysicsOverlayShown = (int)(on ? shown | part : shown & ~part);
            saveConfig();
        }
        Tip(tip);
    }

    /// <summary> The overlay's whole content; called from its draw, on the framework thread. </summary>
    internal void DrawOverlay()
    {
        if (document is not { } current || target is not { } found || !service.ReadShownPose(overlayPose) || GameCamera.Read() is not { } camera)
            return;
        var targets = selected == ShapesView ? AllChains(current.File) : selected >= 0 && selected < current.File.Simulators.Count ? Targets(current.File, selected) : [];
        // The shapes view is about the shapes, so every one draws there.
        var shown = selected == ShapesView ? PhysicsOverlayShown.All : (PhysicsOverlayShown)config.PhysicsOverlayShown;
        var shapes = PhysicsOverlayModel.Build(current.File, targets, selected == ShapesView, overlayLit, found.Character.Skeleton, Frame, shown);
        if (shapes.IsEmpty)
            return;
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
        }
        foreach (var capsule in shapes.Capsules)
        {
            if (!Project(capsule.A, out var a) || !Project(capsule.B, out var b))
                continue;
            var (colour, width) = Style(capsule.Part, capsule.Lit);
            var ra = Pixels(capsule.A, capsule.Radius);
            var rb = Pixels(capsule.B, capsule.Radius);
            drawList.AddCircle(a, ra, colour, 0, width);
            drawList.AddCircle(b, rb, colour, 0, width);
            var along = b - a;
            if (along.LengthSquared() > 1)
            {
                var side = Vector2.Normalize(new Vector2(-along.Y, along.X));
                drawList.AddLine(a + side * ra, b + side * rb, colour, width);
                drawList.AddLine(a - side * ra, b - side * rb, colour, width);
            }
            if (capsule.Lit)
                Label((a + b) / 2, PhysicsShapeNames.Label(current.File.Collision, capsule.Name, found.Character.Skeleton), colour);
        }
        foreach (var sphere in shapes.Spheres)
        {
            if (!Project(sphere.Center, out var at))
                continue;
            var (colour, width) = Style(sphere.Part, sphere.Lit);
            drawList.AddCircle(at, Pixels(sphere.Center, sphere.Radius), colour, 0, width);
            if (sphere.Lit)
                Label(at, PhysicsShapeNames.Label(current.File.Collision, sphere.Name, found.Character.Skeleton), colour);
        }
        foreach (var circle in shapes.Circles)
        {
            var (colour, width) = Style(circle.Part, circle.Lit);
            if (circle.Normal is not { } normal)
            {
                if (Project(circle.Center, out var at))
                    drawList.AddCircle(at, Pixels(circle.Center, circle.Radius), colour, 0, width);
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
                    drawList.AddLine(last, point, colour, width);
                first ??= point;
                previous = point;
            }
            if (first is { } start && previous is { } end)
                drawList.AddLine(end, start, colour, width);
        }
        foreach (var mark in shapes.Marks)
        {
            if (!Project(mark.At, out var at))
                continue;
            var (colour, _) = Style(mark.Part, mark.Lit);
            var radius = Theme.Scaled(mark.Lit ? 4 : 3);
            drawList.AddCircleFilled(at, radius + Theme.Scaled(1.5f), shadow);
            drawList.AddCircleFilled(at, radius, colour);
            if (mark.Lit && mark.Label is { } text)
                Label(at, text, colour);
        }

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
            _ => Theme.Inactive,
        };
        var alpha = lit ? 1f : part == PhysicsOverlayPart.OtherShape ? .5f : .7f;
        return (ImGui.GetColorU32(Theme.WithAlpha(colour, alpha)), Theme.Scaled(lit ? 2.5f : 1.5f));
    }
}
