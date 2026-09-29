using System.Text.Json.Serialization;
using InstantEdit.Models;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.CharacterSend;

/// <summary> The pose a character send leaves its armature in. </summary>
public enum CharacterPose
{
    /// <summary> The skeleton's rest pose: no action. </summary>
    Rest,
    /// <summary> The character's live pose, as a one-frame recording. </summary>
    Current,
    /// <summary> The animation the character is playing, sampled from its file. </summary>
    Animation,
}

/// <summary> What a model of a character send is bound to in Blender. </summary>
internal enum CharacterModelRole
{
    /// <summary> Moved by the character's own skeleton: body, face, hair, tail or ears, and gear. </summary>
    Body,
    /// <summary> A weapon, with a skeleton of its own. </summary>
    Weapon,
}

/// <summary>
/// The <c>character</c> field of an import request (see Blender-Addon/instant_edit/character.py):
/// the send the model belongs to, the character it shows (a key to recognize its earlier sends by,
/// and a name), the name the send's armature gets, and whether the model is a weapon.
/// </summary>
public sealed record CharacterImportEntry(
    [property: JsonPropertyName("sendId")] string SendId,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("armatureName")] string ArmatureName,
    [property: JsonPropertyName("attach")] CharacterAttach? Attach = null)
{
    public const string BodyRole = "body";
    public const string WeaponRole = "weapon";
}

/// <summary>
/// The character bone a weapon hangs from, and where the weapon's root sits relative to it, in the
/// game's Y-up space: translation, rotation x/y/z/w and scale, as in a skeleton's bones.
/// </summary>
public sealed record CharacterAttach(
    [property: JsonPropertyName("bone")] string Bone,
    [property: JsonPropertyName("offset")] float[] Offset);

/// <summary> A model of the character to send, and what it binds to. </summary>
internal sealed record CharacterSendModel(ResourceNode Node, CharacterModelRole Role)
{
    public string FileName => CharacterSendPlan.FileName(Node.GamePath);
}

/// <summary> What a send reads of the character in game: its name, home world, and where its weapons hang. </summary>
internal sealed record CharacterSnapshot(string Name, uint HomeWorld, IReadOnlyList<CharacterWeaponPlacement> Weapons);

/// <summary> A model that reached Blender: its file, what its send warned about, and whether it was racially scaled. </summary>
internal sealed record CharacterSentModel(string FileName, IReadOnlyList<string> Notes, bool Scaled);

/// <summary> What a whole send did, for its summary. </summary>
/// <param name="Failed">Models that could not be sent, each as "file: reason".</param>
/// <param name="PoseResult">What Blender did with the pose or animation, when it got one.</param>
/// <param name="PoseError">Why the pose or animation was not keyed.</param>
/// <param name="RecordingWarning">What a current-pose recording carries that it shouldn't, such as Customize+.</param>
internal sealed record CharacterSendOutcome(string Name, CharacterPose Pose, IReadOnlyList<CharacterSentModel> Sent,
    IReadOnlyList<string> Failed, string? PoseResult, string? PoseError, string? RecordingWarning,
    IReadOnlyList<string> WeaponNotes, IReadOnlyList<string> SkeletonWarnings);

/// <summary> A send's status line, and whether anything in it deserves a warning. </summary>
internal sealed record CharacterSendSummary(string Text, bool Warned);

/// <summary>
/// Which of a character's models a send takes, and the names it uses. Dalamud-free, so the
/// selection can be tested without the game.
/// </summary>
internal static class CharacterSendPlan
{
    private static readonly string[] CharacterFolders = ["chara/human/", "chara/equipment/", "chara/accessory/"];
    private const string WeaponFolder = "chara/weapon/";
    /// <summary> Blender cuts object names at 63 bytes; the add-on refuses longer armature names. </summary>
    public const int MaximumArmatureName = 63;

    /// <summary>
    /// The models to send from a character's resource tree: every model in the character's own
    /// folders (<c>chara/human</c>, <c>chara/equipment</c>, <c>chara/accessory</c>) and, when asked
    /// for, its weapons, each file once, in the order On Screen lists them, with weapons last. Only
    /// models On Screen can edit are taken: files of a loaded mod, and game data.
    /// </summary>
    public static IReadOnlyList<CharacterSendModel> Models(IEnumerable<ResourceNode> roots, bool includeWeapons)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<(CharacterSendModel Model, int Order)>();
        foreach (var node in Flatten(roots))
        {
            if (!IsSendable(node) || RoleOf(node.GamePath) is not { } role || role == CharacterModelRole.Weapon && !includeWeapons)
                continue;
            if (seen.Add($"{node.GamePath}\n{node.ActualPath}"))
                models.Add((new CharacterSendModel(node, role), models.Count));
        }
        return models
            .OrderBy(item => item.Model.Role)
            .ThenBy(item => SectionOrder(item.Model.Node.ResourceSection))
            .ThenBy(item => item.Model.Node.SortOrder)
            .ThenBy(item => item.Order)
            .Select(item => item.Model)
            .ToArray();
    }

    /// <summary> Whether a character model at this game path is a weapon or moves with the body; null for other files. </summary>
    public static CharacterModelRole? RoleOf(string gamePath)
    {
        var path = gamePath.Replace('\\', '/').ToLowerInvariant();
        if (!path.EndsWith(".mdl", StringComparison.Ordinal))
            return null;
        if (path.StartsWith(WeaponFolder, StringComparison.Ordinal))
            return CharacterModelRole.Weapon;
        return CharacterFolders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal)) ? CharacterModelRole.Body : null;
    }

    /// <summary> The rule the On Screen edit action uses: a rooted file of a loaded mod, or a safe game path of game data. </summary>
    public static bool IsSendable(ResourceNode node)
    {
        if (!PenumbraService.IsSafeGamePath(node.GamePath) || !node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            !node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return node.SourceState switch
        {
            ResourceSourceState.LoadedMod => Path.IsPathRooted(node.ActualPath) && !string.IsNullOrWhiteSpace(node.SourceModDirectory),
            ResourceSourceState.GameData => !Path.IsPathRooted(node.ActualPath) && PenumbraService.IsSafeGamePath(node.ActualPath),
            _ => false,
        };
    }

    /// <summary> The game path of a weapon's model, from its model set and body. </summary>
    public static string WeaponModelPath(ushort set, ushort body)
        => $"chara/weapon/w{set:D4}/obj/body/b{body:D4}/model/w{set:D4}b{body:D4}.mdl";

    /// <summary> The weapon's model set and body from its model's game path or file name, or null for other models. </summary>
    public static (ushort Set, ushort Body)? WeaponOf(string modelPath)
        => ModelSkeletonPaths.Parse(modelPath) is { Id: ['w', ..] id, Kind: 'b' } key && ushort.TryParse(id.AsSpan(1), out var set)
            ? (set, (ushort)key.Set)
            : null;

    /// <summary> How Blender recognizes this character's earlier sends: its name and home world. </summary>
    public static string Key(string name, uint homeWorld) => homeWorld == 0 ? name.Trim() : $"{name.Trim()}@{homeWorld}";

    /// <summary> The name the send's armature gets: the armature animations are sent to, so later animations find it. </summary>
    public static string ArmatureName(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "Skeleton" : configured.Trim();
        return name.Length <= MaximumArmatureName ? name : name[..MaximumArmatureName];
    }

    public static string FileName(string gamePath)
    {
        var normalized = gamePath.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The status line of a send: how many models went over and how they are posed, then what
    /// went wrong or needs a look. Racially scaled models are mentioned without warning, as for
    /// single sends.
    /// </summary>
    public static CharacterSendSummary Summary(CharacterSendOutcome outcome)
    {
        var count = outcome.Sent.Count;
        var parts = new List<string>
        {
            $"Sent {count} model{(count == 1 ? "" : "s")} of {outcome.Name} to Blender on one armature" +
            (outcome.Pose == CharacterPose.Rest ? ", in its rest pose." : "."),
        };
        var warned = false;
        void Warn(string text)
        {
            parts.Add(text);
            warned = true;
        }

        if (outcome.PoseResult is not null)
            parts.Add(outcome.PoseResult);
        if (outcome.PoseError is not null)
            Warn($"The pose was not keyed: {outcome.PoseError}");
        if (outcome.RecordingWarning is not null)
            Warn(outcome.RecordingWarning);
        var scaled = outcome.Sent.Count(model => model.Scaled);
        if (scaled > 0)
            parts.Add(scaled == 1
                ? "1 model was reshaped for the character's race for preview, so it can't be exported."
                : $"{scaled} models were reshaped for the character's race for preview, so they can't be exported.");
        if (outcome.Failed.Count > 0)
            Warn($"{outcome.Failed.Count} could not be sent: {Listed(outcome.Failed)}.");
        if (outcome.SkeletonWarnings.Count > 0)
            Warn($"Skeleton: {outcome.SkeletonWarnings[0]}");
        if (outcome.WeaponNotes.Count > 0)
            Warn(Listed(outcome.WeaponNotes));
        var notes = outcome.Sent.SelectMany(model => model.Notes.Select(note => $"{model.FileName}: {note}")).ToList();
        if (notes.Count > 0)
            Warn(Listed(notes));
        return new CharacterSendSummary(string.Join(" ", parts), warned);
    }

    private static string Listed(IReadOnlyList<string> items, int shown = 3)
        => string.Join("; ", items.Take(shown)) + (items.Count > shown ? $"; and {items.Count - shown} more" : "");

    private static int SectionOrder(ResourceSection section) => section switch
    {
        ResourceSection.CharacterFeatures => 0,
        ResourceSection.Gear => 1,
        _ => 2,
    };

    private static IEnumerable<ResourceNode> Flatten(IEnumerable<ResourceNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }
}
