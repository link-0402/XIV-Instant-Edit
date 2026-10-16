using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Materials;

namespace InstantEdit.Ui.Material;

/// <summary>
/// The live preview: the loaded material on the character takes value edits the same frame. The
/// window draws on the framework thread, so the writes happen right here. After a redraw the
/// material is found again and the edits not in the loaded file are written once more.
/// </summary>
internal sealed partial class MaterialWindow
{
    private const int LiveCheckMs = 500;

    // What the game loaded for the material: the opened or saved file, or the last structural preview.
    private MaterialDocument? loadedDoc;
    // What the bound materials show now: the loaded file plus the live writes.
    private MaterialDocument? shownDoc;
    private bool everBound;
    private long nextLiveCheck;

    /// <summary>Binds every place the material sits on the character; new places get the edits not in the loaded file.</summary>
    private bool BindLive(MaterialSession current)
    {
        nextLiveCheck = Environment.TickCount64 + LiveCheckMs;
        var found = LiveMaterial.Find(objects, current.Source.ObjectIndex, current.Source.GamePath);
        var added = live.Bind(found);
        if (!live.Bound)
            return false;
        everBound = true;
        if (added > 0 && loadedDoc != null)
        {
            WriteDiff(loadedDoc, current.Current);
            shownDoc = current.Current;
        }
        return live.IsValid();
    }

    /// <summary>Writes a value edit into the loaded material; false when it can't be shown live (not on screen, or rows changed).</summary>
    private bool WriteLive(MaterialDocument doc)
    {
        if (session is not { } current)
            return false;
        if (!live.IsValid() && !BindLive(current))
            return false;
        var from = shownDoc ?? loadedDoc ?? doc;
        if (MaterialSession.LiveDiff(from, doc).Table)
            return false;
        WriteDiff(from, doc);
        shownDoc = doc;
        return true;
    }

    private void WriteDiff(MaterialDocument from, MaterialDocument to)
    {
        var diff = MaterialSession.LiveDiff(from, to);
        var facts = ShaderPackages.Get(to.ShaderPackage);
        foreach (var id in diff.Constants)
            if ((to.Constant(id)?.Values ?? ShaderPackages.Param(id)?.Default) is { } values)
                live.SetConstant(id, values);
        foreach (var id in diff.Keys)
            if ((to.KeyValue(id) ?? facts?.Key(id)?.Default) is { } value)
                live.SetKey(id, value);
        if (diff.Flags)
            live.SetFlags(to.ShaderFlags);
        foreach (var id in diff.Samplers)
            live.SetSamplerFlags(id, to.Slots.First(slot => slot.SamplerId == id).SamplerSettings);
    }

    /// <summary>Every half second: finds the material again after a redraw, GPose or zone change.</summary>
    private void PumpLive(MaterialSession current)
    {
        if (Environment.TickCount64 < nextLiveCheck || working != null)
            return;
        BindLive(current);
    }

    /// <summary>Puts the loaded material's own values back and forgets where it sits.</summary>
    private void RestoreLive()
    {
        live.Restore();
        shownDoc = null;
    }

    private string LiveState(out System.Numerics.Vector4 colour)
    {
        if (working != null && previewDue == 0)
        {
            colour = Theme.Info;
            return "Redrawing";
        }
        if (live.Bound)
        {
            colour = Theme.Success;
            return preview.Active ? "Live, preview shown" : "Live";
        }
        colour = Theme.Muted;
        return "Not on screen";
    }

    /// <summary>The window's state for the XIV Bridge test tool <c>material</c>; framework thread.</summary>
    internal JsonObject TestState(uint? constant)
    {
        var current = session;
        var state = new JsonObject
        {
            ["open"] = IsOpen,
            ["busy"] = Busy,
            ["message"] = message,
            ["previewDue"] = previewDue != 0,
            ["preview"] = new JsonObject
            {
                ["active"] = preview.Active,
                ["shared"] = preview.Shared,
                ["collection"] = preview.CollectionName,
                ["sessionShown"] = current?.PreviewShown ?? false,
            },
        };
        if (current == null)
            return state;
        state["source"] = new JsonObject
        {
            ["gamePath"] = current.Source.GamePath,
            ["actualPath"] = current.Source.ActualPath,
            ["state"] = current.Source.State.ToString(),
            ["mod"] = current.Source.ModDirectory,
            ["objectIndex"] = current.Source.ObjectIndex,
            ["sha256"] = current.Source.Sha256,
        };
        state["document"] = new JsonObject
        {
            ["shader"] = current.Current.ShaderPackage,
            ["flags"] = $"0x{current.Current.ShaderFlags:X8}",
            ["dirty"] = current.History.Dirty,
            ["undo"] = current.History.UndoLabel,
            ["redo"] = current.History.RedoLabel,
            ["keys"] = new JsonArray(current.Current.Keys.Select(key => (JsonNode)$"0x{key.Id:X8}=0x{key.Value:X8}").ToArray()),
            ["slots"] = current.Current.Slots.Count,
            ["table"] = current.Current.Table?.Kind.ToString() ?? "None",
        };
        var valid = live.IsValid();
        var liveState = new JsonObject
        {
            ["bound"] = live.Bound,
            ["valid"] = valid,
            ["everBound"] = everBound,
            ["bindings"] = new JsonArray(live.Bindings.Select(binding => (JsonNode)$"{binding.Kind} model {binding.ModelSlot} material {binding.MaterialSlot}").ToArray()),
            ["snapshotBytes"] = live.SnapshotBytes,
            ["lastWrite"] = live.LastWrite,
            ["matchesSnapshot"] = valid && live.MatchesSnapshot(),
            ["shownIsCurrent"] = ReferenceEquals(shownDoc, current.Current),
        };
        if (constant is { } id && valid && live.ReadConstant(id) is { } values)
        {
            liveState["constant"] = $"0x{id:X8}";
            liveState["loaded"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray());
            liveState["document"] = new JsonArray((current.Current.Constant(id)?.Values ?? []).Select(value => (JsonNode)value).ToArray());
        }
        state["live"] = liveState;
        return state;
    }
}
