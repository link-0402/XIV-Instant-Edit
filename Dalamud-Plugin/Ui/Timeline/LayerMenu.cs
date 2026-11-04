using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>One entry of the layer menu (see <see cref="LayerMenu.Items"/>).</summary>
internal enum LayerMenuItem : byte
{
    Separator, NeutralFace, Pose, Offset, LookAt, HandShape, Pin, MotionKeys, Amplitude, Smooth, Swing, Noise, Spring, Repeat, Idle, Symmetry, Link, Fix, Balance, Sway,
}

/// <summary>
/// The layer menu that "+ Add layer" under the layers, a right-click on the layer rows and the Bones window's Add layer open:
/// the layer kinds by how often they are used, grouped, with pop-outs for an offset layer from a .livepose file, Pin feet and
/// the fix kinds. A face gets Neutral face and the kinds a face takes. Each layer covers the whole clip (a face's, the whole
/// face); items that can't apply now stay greyed with their reason.
/// </summary>
internal sealed class LayerMenu(TimelineSession session, LivePosePanel livePose, BlenderMotionPanel blenderMotion)
{
    private static readonly ImmutableArray<LayerMenuItem> BodyItems =
    [
        LayerMenuItem.Pose, LayerMenuItem.Offset, LayerMenuItem.LookAt, LayerMenuItem.HandShape, LayerMenuItem.Pin, LayerMenuItem.Balance, LayerMenuItem.MotionKeys,
        LayerMenuItem.Separator,
        LayerMenuItem.Amplitude, LayerMenuItem.Smooth, LayerMenuItem.Swing, LayerMenuItem.Noise, LayerMenuItem.Spring, LayerMenuItem.Sway, LayerMenuItem.Repeat,
        LayerMenuItem.Idle, LayerMenuItem.Symmetry,
        LayerMenuItem.Separator,
        LayerMenuItem.Link,
        LayerMenuItem.Separator,
        LayerMenuItem.Fix,
    ];

    private static readonly ImmutableArray<LayerMenuItem> FaceItems =
    [
        LayerMenuItem.NeutralFace, LayerMenuItem.Pose, LayerMenuItem.Offset,
        LayerMenuItem.Separator,
        LayerMenuItem.Amplitude, LayerMenuItem.Smooth, LayerMenuItem.Swing, LayerMenuItem.Noise, LayerMenuItem.Spring, LayerMenuItem.Repeat,
        LayerMenuItem.Symmetry,
        LayerMenuItem.Separator,
        LayerMenuItem.Fix,
    ];

    /// <summary>The menu's entries in their order: a face's (<paramref name="face"/>) or a body clip's.</summary>
    public static ImmutableArray<LayerMenuItem> Items(bool face) => face ? FaceItems : BodyItems;

    /// <summary>The layer kind an entry adds; null for those that add something else (a fix, Motion keys) or open a pop-out of their own.</summary>
    public static MotionLayerKind? KindOf(LayerMenuItem item) => item switch
    {
        LayerMenuItem.Pose => MotionLayerKind.Pose,
        LayerMenuItem.Offset => MotionLayerKind.Offset,
        LayerMenuItem.LookAt => MotionLayerKind.LookAt,
        LayerMenuItem.Pin => MotionLayerKind.Pin,
        LayerMenuItem.Amplitude => MotionLayerKind.Amplitude,
        LayerMenuItem.Smooth => MotionLayerKind.Smooth,
        LayerMenuItem.Swing => MotionLayerKind.Swing,
        LayerMenuItem.Noise => MotionLayerKind.Noise,
        LayerMenuItem.Spring => MotionLayerKind.Spring,
        LayerMenuItem.Sway => MotionLayerKind.Sway,
        LayerMenuItem.Repeat => MotionLayerKind.Repeat,
        LayerMenuItem.Idle => MotionLayerKind.Idle,
        LayerMenuItem.Symmetry => MotionLayerKind.Symmetry,
        LayerMenuItem.Link => MotionLayerKind.Link,
        LayerMenuItem.Balance => MotionLayerKind.Balance,
        _ => null,
    };

    /// <summary>The menu's items, drawn into the popup or menu that is open; <paramref name="bones"/> opens a link's picker in the Bones window.</summary>
    public void Draw(TimelineEditor current, bool bones = false)
    {
        var readOnly = session.ReadOnly;
        var face = current.Document.Face;
        foreach (var item in Items(face != null))
            switch (item)
            {
                case LayerMenuItem.Separator:
                    ImGui.Separator();
                    break;
                case LayerMenuItem.NeutralFace:
                    DrawNeutralFace(current);
                    break;
                case LayerMenuItem.Offset when face == null:
                    using (var menu = ImRaii.Menu("Offset layer", !readOnly))
                    {
                        if (menu.Success)
                        {
                            DrawKind(current, MotionLayerKind.Offset, readOnly, face);
                            livePose.DrawItems();
                        }
                    }
                    break;
                case LayerMenuItem.Pin:
                    using (var menu = ImRaii.Menu("Pin layer", !readOnly))
                    {
                        if (menu.Success)
                        {
                            DrawKind(current, MotionLayerKind.Pin, readOnly, face);
                            DrawPinFeet(current, readOnly);
                        }
                    }
                    break;
                case LayerMenuItem.HandShape:
                    if (ImGui.MenuItem(TimelineWindowViews.HandShapeName, "", false, !readOnly))
                        current.AddFix(new MotionFix(MotionFixKind.HandShape, Hand: new MotionHand(MotionHandSide.Right, "relaxed")));
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(TimelineWindowViews.HandShapeHint);
                    break;
                case LayerMenuItem.MotionKeys:
                    // As the clip row's Edit motion keys: without markers the layer would have nothing to fade out over.
                    var markers = current.ShownMarkers != null;
                    if (ImGui.MenuItem(MotionEdits.MotionKeysName, "", false, !readOnly && markers))
                        current.EditMotionKeys(current.Playhead);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(TimelineWindowViews.MotionKeysTip(markers));
                    break;
                case LayerMenuItem.Fix:
                    using (var menu = ImRaii.Menu("Fix layer", !readOnly))
                    {
                        if (menu.Success)
                            session.LoopPose.DrawFixItems(current, readOnly, (label, enabled) => ImGui.MenuItem(label, "", false, enabled));
                    }
                    if (readOnly && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(TimelineToolbar.Busy);
                    break;
                case LayerMenuItem.Link:
                    // A link needs its partner: the item opens the link picker.
                    session.LinkPicker?.DrawAddItem($"{MotionEdits.KindName(MotionLayerKind.Link)} layer", readOnly,
                        TimelineWindowViews.LayerKindHint(MotionLayerKind.Link), bones);
                    break;
                default:
                    if (KindOf(item) is { } kind)
                        DrawKind(current, kind, readOnly, face);
                    break;
            }
    }

    /// <summary>A layer kind's item: the layer over the whole clip, or over the whole face for a face.</summary>
    private static void DrawKind(TimelineEditor current, MotionLayerKind kind, bool readOnly, TimelineFace? face)
    {
        if (ImGui.MenuItem($"{MotionEdits.KindName(kind)} layer", "", false, !readOnly))
        {
            if (face != null)
                current.AddLayer(kind, face.Anchor, face.Anchor + Math.Max(TimelineDocument.MinLength, face.Frames));
            else
                current.AddLayer(kind, 0, Math.Max(TimelineDocument.MinLength, current.Document.Length));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(readOnly ? TimelineToolbar.Busy : TimelineWindowViews.LayerKindHint(kind));
    }

    /// <summary>Pin feet: a pin layer that plants both feet, when the skeleton has them.</summary>
    private void DrawPinFeet(TimelineEditor current, bool readOnly)
    {
        var skeleton = session.EditingSkeleton();
        var feet = TimelineWindowViews.CanPinFeet(skeleton);
        if (ImGui.MenuItem(TimelineWindowViews.PinFeetName, "", false, !readOnly && feet))
            current.AddPinFeet(skeleton);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.PinFeetTip(skeleton));
    }

    /// <summary>Neutral face: the face at rest in place of its motion, with a pose layer to key the new face.</summary>
    private void DrawNeutralFace(TimelineEditor current)
    {
        var unavailable = blenderMotion.FaceUnavailable(current);
        if (ImGui.MenuItem("Neutral face", "", false, unavailable == null))
            blenderMotion.NeutralFace(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(unavailable ?? "Start from the face at rest, and add a pose layer to key the new face.");
    }
}
