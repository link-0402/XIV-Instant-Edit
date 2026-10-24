namespace InstantEdit.Ui.Pose;

/// <summary>What the gizmo on the GPose character does to the primary bone.</summary>
internal enum GizmoMode : byte
{
    Turn,
    Move,
    /// <summary>Moves the bone and bends the limb above it to follow (offset and pose layers).</summary>
    Drag,
}
