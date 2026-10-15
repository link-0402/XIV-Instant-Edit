using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Main;

/// <summary> The animation editing services the plugin started, or why they did not start. Shared by the parts of the main window that use them. </summary>
internal sealed class AnimationIntegration
{
    public AnimationEditService? Service { get; private set; }

    public string? Error { get; private set; }

    /// <summary> What to show while the services are missing. </summary>
    public string Unavailable => Error ?? "Animation integration is unavailable.";

    public void Attach(AnimationEditService? service, string? error)
    {
        Service = service;
        Error = error;
    }
}
