using InstantEdit.Services;
using Penumbra.Api.Enums;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// New vanilla mods are activated through <see cref="PenumbraService.ApplyModToCollection"/> with
/// the redraw skipped; skipping the redraw must never skip enabling the mod.
/// </summary>
internal static class CollectionActivationScenarios
{
    public static void Run()
    {
        var enabled = 0;
        var prioritized = 0;
        var redrawn = 0;

        var withoutRedraw = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => { enabled++; return PenumbraApiEc.Success; },
            () => { prioritized++; return PenumbraApiEc.Success; },
            redraw: null);
        Require(withoutRedraw.Success && enabled == 1 && prioritized == 1 && redrawn == 0 &&
                withoutRedraw.Code == "export_applied",
            "skipping the redraw still enables and prioritizes the mod");

        var withoutPriority = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => { enabled++; return PenumbraApiEc.NothingChanged; },
            setPriority: null,
            redraw: null);
        Require(withoutPriority.Success && enabled == 2 && prioritized == 1,
            "an already enabled mod counts as enabled and priority is skipped when not requested");

        var rejected = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => PenumbraApiEc.ModMissing,
            () => { prioritized++; return PenumbraApiEc.Success; },
            () => { redrawn++; return null; });
        Require(!rejected.Success && prioritized == 1 && redrawn == 0 &&
                rejected.Message.Contains("ModMissing", StringComparison.Ordinal),
            "a rejected enable stops before priority and redraw");

        var priorityRejected = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => PenumbraApiEc.Success,
            () => PenumbraApiEc.CollectionMissing,
            () => { redrawn++; return null; });
        Require(!priorityRejected.Success && redrawn == 0,
            "a rejected priority stops before the redraw");

        var logged = 0;
        var thrown = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => throw new InvalidOperationException("ipc down"),
            null,
            null,
            (message, error) => { if (error.Message == "ipc down" && message.Length > 0) logged++; });
        Require(!thrown.Success && logged == 1 && thrown.Message.Contains("ipc down", StringComparison.Ordinal),
            "an enable exception is logged and becomes a failed result");

        var warned = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => PenumbraApiEc.Success,
            null,
            () => { redrawn++; return "redraw skipped"; });
        Require(warned.Success && redrawn == 1 && warned.Code == "export_applied_with_warnings" &&
                warned.WarningList.Count == 1 && warned.WarningList[0] == "redraw skipped",
            "a redraw warning is reported without failing the activation");

        var clean = PenumbraService.ApplyModToCollection("Mod", "Collection",
            () => PenumbraApiEc.Success,
            null,
            () => { redrawn++; return null; });
        Require(clean.Success && redrawn == 2 && clean.Code == "export_applied" &&
                clean.Message == "Applied Mod to Collection.",
            "a clean redraw reports plain success");
    }
}
