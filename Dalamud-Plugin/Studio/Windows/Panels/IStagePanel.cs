using Dalamud.Interface;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Windows.Panels;

/// <summary>
/// One workflow stage of the main window. The canvas (middle) lists the stage's objects
/// and lets the user pick one; the inspector (right) edits whichever one is picked.
/// Panels keep only transient view state — the document lives in <see cref="PortSession"/>.
/// </summary>
internal interface IStagePanel
{
    StageId         Id    { get; }
    string          Title { get; }
    FontAwesomeIcon Icon  { get; }

    /// <summary>Tooltip for the stage tab: what the user does on this stage.</summary>
    string Purpose { get; }

    void DrawCanvas();
    void DrawInspector();

    /// <summary>Modals the stage owns, drawn at the window root every frame so canvas and inspector can both open them.</summary>
    void DrawPopups() { }
}
