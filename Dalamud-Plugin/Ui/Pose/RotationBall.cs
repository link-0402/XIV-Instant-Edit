using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace InstantEdit.Ui.Pose;

/// <summary>What the rotation ball did this frame: a drag began, a turn was made, the drag ended.</summary>
internal readonly record struct BallResult(bool Started, Quaternion? Turn, bool Ended);

/// <summary>
/// LivePose's rotation ball, adapted from Brio's ImBrioGizmo.DrawRotation (GPL-3.0, see
/// THIRD-PARTY-NOTICES.md): three rings for a frame's X, Y and Z axes, seen the way the game's camera
/// sees them. Dragging along a ring, or turning the wheel over it, turns about its axis; Shift turns
/// finer and a right click locks the ring. The turn it returns is about the frame's own axes.
/// </summary>
internal static class RotationBall
{
    private const int Points = 144;
    private const float HoverDistance = 10;
    // ImGui colours are ABGR: X red, Y green, Z blue; faint where a ring runs behind the ball.
    private static readonly uint[] Front = [0xFF3333FF, 0xFF33FF33, 0xFFFF3333];
    private static readonly uint[] Back = [0x103333FF, 0x1033FF33, 0x10FF3333];
    private const uint LockedFront = 0xFFFFFFFF;
    private const uint LockedBack = 0x10FFFFFF;
    private const uint Shade = 0x50000000;

    private static int dragAxis = -1;
    private static Vector2 dragFrom;
    private static Vector2 dragAt;
    private static float dragDistance;
    private static int? lockedAxis;

    /// <summary>Whether a ring is being dragged.</summary>
    public static bool Dragging => dragAxis >= 0;

    /// <summary>
    /// Draws the ball, <paramref name="size"/> wide, for a frame turned <paramref name="frame"/> in the world,
    /// seen through a camera turned <paramref name="view"/> (the rotation of the game's view matrix).
    /// </summary>
    public static BallResult Draw(string id, float size, Quaternion frame, Quaternion view)
    {
        var topLeft = ImGui.GetCursorScreenPos();
        // The button takes the clicks, so a drag on the ball never moves the window.
        ImGui.InvisibleButton(id, new Vector2(size));
        var hovered = ImGui.IsItemHovered();
        var activated = ImGui.IsItemActivated();
        var active = ImGui.IsItemActive();
        var deactivated = ImGui.IsItemDeactivated();
        var center = topLeft + new Vector2(size / 2);
        var radius = Math.Max(8, size / 2 - Theme.Scaled(12));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(center, radius, Shade);

        // The game's camera looks with X mirrored on screen, as Brio found.
        var viewMatrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(view)) * Matrix4x4.CreateScale(-1, 1, 1);
        var frameMatrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(frame));
        var mouse = ImGui.GetMousePos();
        var closest = float.MaxValue;
        var closestAxis = -1;
        Vector2 closestAt = default, closestFrom = default;
        for (var axis = 0; axis < 3; axis++)
        {
            var previous = Project(RingPoint(axis, 0, radius), frameMatrix, viewMatrix);
            for (var i = 1; i < Points; i++)
            {
                var point = Project(RingPoint(axis, i / (float)(Points - 1) * MathF.Tau, radius), frameMatrix, viewMatrix);
                var visible = point.Z < 0;
                var from = center + new Vector2(previous.X, previous.Y);
                var to = center + new Vector2(point.X, point.Y);
                var locked = lockedAxis == axis;
                var colour = locked ? visible ? LockedFront : LockedBack : visible ? Front[axis] : Back[axis];
                drawList.AddLine(from, to, colour, Theme.Scaled(2));
                if (visible && (lockedAxis == null || locked))
                {
                    var distance = Vector2.Distance(to, mouse);
                    if (distance <= closest)
                    {
                        closest = distance;
                        closestAxis = axis;
                        closestAt = to;
                        closestFrom = from;
                    }
                }
                previous = point;
            }
        }

        var started = false;
        var ended = false;
        Quaternion? turn = null;
        var near = closestAxis >= 0 && (closest < Theme.Scaled(HoverDistance) || lockedAxis != null);
        if (activated && near)
        {
            dragAxis = closestAxis;
            dragAt = closestAt;
            dragFrom = closestFrom;
            dragDistance = 0;
            started = true;
        }
        if (dragAxis >= 0 && active)
        {
            // Along the ring where it was grabbed: 200 pixels turn one radian.
            var along = dragAt - dragFrom;
            if (along.LengthSquared() > 1e-6f)
            {
                var distance = Vector2.Dot(mouse - dragAt, Vector2.Normalize(along));
                var angle = (distance - dragDistance) / Theme.Scaled(200);
                dragDistance = distance;
                if (ImGui.GetIO().KeyShift)
                    angle /= 10;
                if (angle != 0)
                    turn = AxisTurn(dragAxis, angle);
            }
            drawList.AddCircleFilled(dragAt, Theme.Scaled(4), Front[dragAxis]);
        }
        else if (hovered && near)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                lockedAxis = lockedAxis == null ? closestAxis : null;
            else if (ImGui.GetIO().MouseWheel is var wheel and not 0)
                turn = AxisTurn(closestAxis, wheel / (ImGui.GetIO().KeyShift ? 1000f : 100f));
            drawList.AddCircle(closestAt, Theme.Scaled(HoverDistance), Front[closestAxis]);
        }
        if (dragAxis >= 0 && (deactivated || !active))
        {
            dragAxis = -1;
            ended = true;
        }
        return new BallResult(started, turn, ended);
    }

    /// <summary>A turn of <paramref name="angle"/> radians about a ring's axis, the way Brio's rings turn.</summary>
    private static Quaternion AxisTurn(int axis, float angle) => axis switch
    {
        0 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle),
        1 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -angle),
        _ => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle),
    };

    private static Vector3 RingPoint(int axis, float angle, float radius) => axis switch
    {
        0 => new Vector3(0, radius * MathF.Cos(angle), radius * MathF.Sin(angle)),
        1 => new Vector3(radius * MathF.Cos(angle), 0, radius * MathF.Sin(angle)),
        _ => new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0),
    };

    private static Vector3 Project(Vector3 point, Matrix4x4 frame, Matrix4x4 view) => Vector3.Transform(Vector3.Transform(point, frame), view);
}
