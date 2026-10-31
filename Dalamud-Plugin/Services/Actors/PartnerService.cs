using System.Collections.Immutable;

namespace InstantEdit.Services.Actors;

/// <summary>Where a spawned partner is: waiting to draw, drawn and waiting for its draw object, or done with its links.</summary>
internal enum PartnerStage : byte
{
    Waiting,
    Drawn,
    Ready,
}

/// <summary>A spawned partner as the UI shows it: the name it spawned with, its stage and each link that didn't apply.</summary>
internal sealed record PartnerView(Guid PresetId, string Name, ushort ObjectIndex, PartnerStage Stage, ImmutableArray<string> Problems, Guid? TemporaryProfile);

/// <summary>
/// Spawns partners from presets and despawns them, a few steps each frame on the framework thread. Each spawn records what
/// it changed (the collection assignment, the look, the temporary profile) and the despawn undoes only that.
/// </summary>
internal sealed class PartnerService : IDisposable
{
    public const string NotInGPose = "Enter GPose to spawn a partner.";
    public const string TreatedAsYou = "Penumbra would treat it as you";
    public const string DrawnLate = "Drawn late";

    private static readonly TimeSpan DrawWait = TimeSpan.FromSeconds(1);

    // Not readonly: the Bridge test swaps them.
    private PartnerPresetStore store;
    private IPartnerCollections collections;
    private readonly IPartnerActors actors;
    private readonly IPartnerLooks looks;
    private readonly IPartnerProfiles profiles;
    private readonly Func<TimeSpan> clock;
    private readonly List<Partner> partners = [];

    /// <summary>A spawned partner: the preset as it was at spawn and what the spawn changed.</summary>
    private sealed class Partner(PartnerPreset preset, string name, SpawnedActor actor, TimeSpan started)
    {
        public readonly PartnerPreset Preset = preset;
        public readonly string Name = name;
        public readonly SpawnedActor Actor = actor;
        public TimeSpan Since = started;
        public bool Late;
        public bool Assigned;
        public Guid? Previous;
        public bool Looked;
        public PartnerView View = new(preset.Id, name, actor.ObjectIndex, PartnerStage.Waiting, [], null);

        public void Problem(string text) => View = View with { Problems = View.Problems.Add(text) };
    }

    public PartnerService(PartnerPresetStore store, IPartnerActors actors, IPartnerCollections collections, IPartnerLooks looks, IPartnerProfiles profiles,
        Func<TimeSpan>? clock = null)
    {
        this.store = store;
        this.actors = actors;
        this.collections = collections;
        this.looks = looks;
        this.profiles = profiles;
        this.clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        actors.Updated += Update;
    }

    public PartnerPresetStore Store => store;
    public IPartnerCollections Collections => collections;
    public IPartnerLooks Looks => looks;
    public IPartnerProfiles Profiles => profiles;
    public IPartnerActors Actors => actors;

    /// <summary>The spawned partner of this preset, or null.</summary>
    public PartnerView? Find(Guid presetId) => Tracked(presetId)?.View;

    /// <summary>
    /// Makes the preset's actor and sets its collection; the next frames draw it and apply the look and profile. Why it
    /// was refused, or null (also when it is already spawned).
    /// </summary>
    public string? Spawn(PartnerPreset preset)
    {
        if (!actors.InGPose)
            return NotInGPose;
        if (Tracked(preset.Id) != null)
            return null;
        if (PartnerNames.Problem(preset.Name, [], preset.Id, actors.PlayerName) is { } refused)
            return refused;
        var name = preset.Name.Trim();
        var (made, problem) = actors.Spawn(name);
        if (made is not { } actor)
            return problem ?? "Spawning failed: create";

        var partner = new Partner(preset, name, actor, clock());
        if (!collections.Available)
        {
            if (preset.Collection is { } missing)
                partner.Problem(ProblemText("Collection", "Penumbra", missing, new LinkOutcome(LinkStatus.NotLoaded)));
        }
        else
        {
            var parent = collections.ParentOf(actor.ObjectIndex);
            if (parent != actor.ObjectIndex && parent != -1)
            {
                actors.Delete(actor);
                return TreatedAsYou;
            }
            if (preset.Collection is { } link)
            {
                // A name whose assignment we made and never removed (a crash) has no previous one of the user's to put back.
                var current = collections.Current(actor.ObjectIndex);
                var previous = store.OwnsAssignment(name) || current is not { IndividualSet: true } set ? (Guid?)null : set.Collection;
                var outcome = collections.Assign(actor.ObjectIndex, link.Id, true, true);
                if (outcome.Status == LinkStatus.Applied)
                {
                    partner.Assigned = true;
                    partner.Previous = previous;
                    if (previous == null)
                        store.SetOwnsAssignment(name, true);
                }
                else
                    partner.Problem(ProblemText("Collection", "Penumbra", link, outcome));
            }
        }
        partners.Add(partner);
        return null;
    }

    /// <summary>Undoes what the spawn changed, in reverse, and deletes the actor.</summary>
    public void Despawn(Guid presetId)
    {
        if (Tracked(presetId) is { } partner)
            Undo(partner);
    }

    /// <summary>Despawns the preset's partner, then removes the preset from the store.</summary>
    public void Delete(Guid presetId)
    {
        Despawn(presetId);
        store.Delete(presetId);
    }

    public void DespawnAll()
    {
        while (partners.Count > 0)
            Undo(partners[^1]);
    }

    /// <summary>Each frame: despawns everything once GPose ends, drops lost actors and moves each partner a step on.</summary>
    public void Update()
    {
        if (!actors.InGPose)
        {
            DespawnAll();
            return;
        }
        for (var i = partners.Count - 1; i >= 0; i--)
        {
            var partner = partners[i];
            if (!actors.Exists(partner.Actor))
            {
                // Gone by itself: only the profile can go without the object; the assignment stays recorded as ours.
                partners.RemoveAt(i);
                if (partner.View.TemporaryProfile is { } lost)
                    profiles.Remove(lost);
                continue;
            }
            if (partner.View.Stage == PartnerStage.Waiting && (actors.ReadyToDraw(partner.Actor) || TimedOut(partner)))
            {
                actors.Draw(partner.Actor);
                partner.Since = clock();
                partner.View = partner.View with { Stage = PartnerStage.Drawn };
            }
            // Glamourer can't find an actor without its draw object, so the links wait for it.
            if (partner.View.Stage == PartnerStage.Drawn && (actors.IsDrawn(partner.Actor) || TimedOut(partner)))
                ApplyLinks(partner);
        }
    }

    /// <summary>Whether a second passed since the partner's last step; the first time, notes it was drawn late.</summary>
    private bool TimedOut(Partner partner)
    {
        if (clock() - partner.Since < DrawWait)
            return false;
        if (!partner.Late)
        {
            partner.Late = true;
            partner.Problem(DrawnLate);
        }
        return true;
    }

    private void ApplyLinks(Partner partner)
    {
        var index = partner.Actor.ObjectIndex;
        if (partner.Preset.Design is { } design)
        {
            var outcome = looks.Apply(index, design.Id);
            if (outcome.Status == LinkStatus.Applied)
                partner.Looked = true;
            else
                partner.Problem(ProblemText("Design", "Glamourer", design, outcome));
        }
        Guid? temporary = null;
        if (partner.Preset.Profile is { } profile)
        {
            (var outcome, temporary) = profiles.Apply(index, profile.Id);
            if (outcome.Status != LinkStatus.Applied)
                partner.Problem(ProblemText("Profile", "Customize+", profile, outcome));
        }
        partner.View = partner.View with { Stage = PartnerStage.Ready, TemporaryProfile = temporary };
    }

    /// <summary>Stops tracking the partner first, so nothing the calls set off sees it, then undoes what its spawn recorded.</summary>
    private void Undo(Partner partner)
    {
        partners.Remove(partner);
        var index = partner.Actor.ObjectIndex;
        if (partner.View.TemporaryProfile is { } temporary)
            profiles.Remove(temporary);
        if (partner.Looked)
            looks.Release(index);
        if (partner.Assigned)
        {
            if (partner.Previous is { } previous)
                collections.Assign(index, previous, false, false);
            else if (collections.Assign(index, null, false, true).Status == LinkStatus.Applied)
                store.SetOwnsAssignment(partner.Name, false);
        }
        actors.Delete(partner.Actor);
    }

    private Partner? Tracked(Guid presetId)
    {
        foreach (var partner in partners)
            if (partner.Preset.Id == presetId)
                return partner;
        return null;
    }

    /// <summary>The line a link that didn't apply shows: the plugin isn't loaded, the linked item is gone, or the plugin's error code.</summary>
    public static string ProblemText(string kind, string plugin, PartnerLink link, LinkOutcome outcome) => outcome.Status switch
    {
        LinkStatus.NotLoaded => $"{plugin} isn't loaded",
        LinkStatus.Missing => $"{kind} {link.Name} no longer exists",
        _ => $"{plugin} error {outcome.Code}",
    };

    /// <summary>Despawns every partner on the framework thread and stops the frame updates.</summary>
    public void Dispose()
    {
        actors.OnFramework(DespawnAll);
        actors.Updated -= Update;
    }
}
