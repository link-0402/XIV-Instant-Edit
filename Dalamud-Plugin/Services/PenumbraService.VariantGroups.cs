using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Lumina.Data;
using Lumina.Data.Files;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModCleanup;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;
using static InstantEdit.Services.PenumbraMods.AttributeGroupService;
using static InstantEdit.Services.PenumbraMods.EstEntryService;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    internal static string FormatMashupDescription(
        IReadOnlyList<MashupContributor> contributors,
        IReadOnlyList<string>? requiredExternalMods = null)
    {
        var names = contributors
            .Select(item => item.Context.SourceModName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Replace("\"", "'", StringComparison.Ordinal))
            .ToArray();
        var description = $"Mashup created by XIV Instant Edit from {string.Join(" and ", names.Select(name => $"\"{name}\""))}.";
        return requiredExternalMods is { Count: > 0 }
            ? $"{description}\n\nRequires external mods: {string.Join(", ", requiredExternalMods)}."
            : description;
    }

    private static IReadOnlyList<string> ExternalMashupWarnings(IReadOnlyList<string> requiredExternalMods)
        => requiredExternalMods.Count == 0
            ? Array.Empty<string>()
            : [$"Keep these external mods enabled in the active collection: {string.Join(", ", requiredExternalMods)}."];

}
