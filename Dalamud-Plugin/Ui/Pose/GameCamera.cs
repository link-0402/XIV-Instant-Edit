using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace InstantEdit.Ui.Pose;

/// <summary> The game's active camera, for drawing on the world: its view and a projection ImGuizmo and projected points can use. </summary>
internal static class GameCamera
{
    /// <summary> The active camera's view and projection; null without a camera. </summary>
    public static unsafe (Matrix4x4 View, Matrix4x4 Projection)? Read()
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return null;
        var camera = manager->GetActiveCamera();
        if (camera == null)
            return null;
        var render = camera->CameraBase.SceneCamera.RenderCamera;
        if (render == null)
            return null;
        Matrix4x4 view = camera->CameraBase.SceneCamera.ViewMatrix;
        // The game keeps something else in the last element.
        view.M44 = 1;
        var near = render->NearPlane;
        var far = render->FarPlane;
        if (!float.IsFinite(near) || near <= 0)
            return null;
        if (!float.IsFinite(far) || far <= near)
            far = near * 100000;
        Matrix4x4 projection = render->ProjectionMatrix;
        return (view, PoseGizmo.Projection(projection, near, far));
    }

    /// <summary> The camera's right-hand direction in the world: the view matrix's first column, since its rotation is its own inverse transposed. </summary>
    public static Vector3 Right(Matrix4x4 view) => Vector3.Normalize(new Vector3(view.M11, view.M21, view.M31));
}
