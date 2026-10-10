using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Ui;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>An Instant Edit hand-off a tile offers: its button, and why it can't run right now (null when it can).</summary>
internal sealed record HandoffAction(FontAwesomeIcon Icon, string Label, string? Block, Action Run);

/// <summary>
/// Instant Edit's hand-offs for the files of one mod window: a model to Blender, a texture to the
/// image editor, an animation to Blender. Only the mod's own files are handed off, each with the
/// one option it is shown in, so an export goes back into that option. A file replaced here by one
/// of your own is edited through that file instead, since the next update would write it over.
/// </summary>
internal sealed class HandoffActions
{
    /// <summary>The character imports are made for (skeleton and racial scaling): your own.</summary>
    private const int ImportObjectIndex = 0;

    private readonly ModEditor _editor;
    private readonly string _owner;

    private ResourceGraph? _graph;
    private DateTimeOffset? _started;
    private ActorView? _mod;
    private readonly Dictionary<ResourceNode, ResourceView> _rows = new(ReferenceEqualityComparer.Instance);

    public HandoffActions(ModEditor editor, string owner)
    {
        _editor = editor;
        _owner  = owner;
    }

    /// <summary>The hand-off <paramref name="node"/> offers, or null when it offers none.</summary>
    public HandoffAction? For(ResourceNode node)
    {
        if (_editor.Host.Handoff is not { } handoff || node.Origin != OriginKind.ModFile)
            return null;

        var kind = node.Kind switch
        {
            ResourceKind.Model     => 0,
            ResourceKind.Texture   => 1,
            ResourceKind.Animation => 2,
            _                      => -1,
        };
        if (kind < 0)
            return null;

        var (icon, label) = kind switch
        {
            0 => (FontAwesomeIcon.Pen, "Edit this model in Blender"),
            1 => (FontAwesomeIcon.PaintBrush, "Edit this texture in your image editor"),
            _ => (FontAwesomeIcon.Pen, "Send this animation to Blender"),
        };

        if (node.Replacement != null)
            return new HandoffAction(icon, label, "It comes from a file of your own, which the mod follows. Edit that file, or undo the replacement first.", () => { });
        if (node.EditedElsewhere)
            return new HandoffAction(icon, label, "It was changed elsewhere since it was written here. Choose which to keep first.", () => { });

        var row = RowFor(node);
        if (row == null || _mod == null)
            return null;
        var mod = _mod;
        return kind switch
        {
            0 => new HandoffAction(icon, label, handoff.ModelBlock(row), () => Start(() => handoff.EditModel(row, mod))),
            1 => new HandoffAction(icon, label, handoff.TextureBlock(row), () => Start(() => handoff.EditTexture(row, mod))),
            _ => new HandoffAction(icon, label, handoff.AnimationBlock(row), () => Start(() => handoff.SendAnimation(row, _owner))),
        };
    }

    private void Start(Action run)
    {
        _started = DateTimeOffset.Now;
        run();
    }

    /// <summary>What the last hand-off started from this window reported, for its status bar; null before the first one.</summary>
    public (string Text, InstantEdit.Ui.FeedbackSeverity Severity)? Message
        => _started is { } started && _editor.Host.Handoff?.LatestMessage is { } latest && latest.Time >= started
            ? (latest.Text, latest.Severity)
            : null;

    /// <summary>Draws the dialogs this window's hand-offs opened (the animation send).</summary>
    public void DrawDialogs() => _editor.Host.Handoff?.DrawDialogs(_owner);

    /// <summary>The row of a mod file, from the hand-off view of the current graph (built again when the graph changes).</summary>
    private ResourceView? RowFor(ResourceNode node)
    {
        var graph = _editor.Session.Graph;
        if (!ReferenceEquals(graph, _graph))
            Rebuild(graph);
        return _rows.GetValueOrDefault(node);
    }

    private void Rebuild(ResourceGraph graph)
    {
        _graph = graph;
        _rows.Clear();
        _mod = null;

        var project = _editor.Project;
        var meta = project.Meta;
        var mod = new ModHandoffMod(_editor.Name, project.FolderName, project.FolderPath,
            global::InstantEdit.Services.PenumbraService.ReadModStableIdentifier(project.FolderPath));

        var nodes = graph.Nodes.Values.Where(n => n.Origin == OriginKind.ModFile && n.DiskPath.Length > 0).ToList();
        var files = nodes.Select(node => ModHandoffBridge.File(meta, node.GamePath, node.DiskPath, node.RelPath, node.Option)).ToList();
        _mod = ModHandoffBridge.Mod(mod, files, ImportObjectIndex);
        for (var i = 0; i < nodes.Count; i++)
            _rows[nodes[i]] = _mod.Roots[i];
    }
}
