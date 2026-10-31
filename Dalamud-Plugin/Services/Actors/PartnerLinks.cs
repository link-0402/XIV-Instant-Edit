namespace InstantEdit.Services.Actors;

/// <summary>How a link's plugin call went.</summary>
internal enum LinkStatus : byte
{
    Applied,
    NotLoaded,
    Missing,
    Failed,
}

/// <summary>A link's plugin call: its status and, when it failed, the plugin's own error code.</summary>
internal readonly record struct LinkOutcome(LinkStatus Status, int Code = 0);

/// <summary>An object's Penumbra assignment: whether an individual one is set, and its collection when it is.</summary>
internal readonly record struct CollectionAssignment(bool IndividualSet, Guid Collection);

/// <summary>An actor the spawner made: its client slot, object index and address, checked each frame to see it still stands.</summary>
internal readonly record struct SpawnedActor(uint Slot, ushort ObjectIndex, nint Address);

/// <summary>The game calls a partner needs. Every member runs on the framework thread.</summary>
internal interface IPartnerActors
{
    /// <summary>Raised once per frame on the framework thread.</summary>
    event Action? Updated;

    /// <summary>Whether GPose is on.</summary>
    bool InGPose { get; }

    /// <summary>Your character's name, or null when there is none.</summary>
    string? PlayerName { get; }

    /// <summary>Makes an actor named <paramref name="name"/> from your GPose character and adds it to GPose: the actor, or null and why not.</summary>
    (SpawnedActor? Actor, string? Problem) Spawn(string name);

    /// <summary>Whether the actor's index still holds the object made for it.</summary>
    bool Exists(SpawnedActor actor);

    /// <summary>Whether the game can draw the actor now.</summary>
    bool ReadyToDraw(SpawnedActor actor);

    void Draw(SpawnedActor actor);

    /// <summary>Whether the actor has its draw object.</summary>
    bool IsDrawn(SpawnedActor actor);

    /// <summary>Takes the actor out of GPose and deletes it.</summary>
    void Delete(SpawnedActor actor);

    /// <summary>Runs <paramref name="action"/> on the framework thread: now when already on it.</summary>
    void OnFramework(Action action);
}

/// <summary>Penumbra: the collections and an object's individual assignment.</summary>
internal interface IPartnerCollections
{
    bool Available { get; }

    IReadOnlyList<PartnerLink> List();

    /// <summary>The index of the object Penumbra takes this object's identity from: -1 for no parent; int.MinValue when the parent can't be read (the spawn is refused).</summary>
    int ParentOf(int objectIndex);

    /// <summary>The object's assignment; null means no individual one.</summary>
    CollectionAssignment? Current(int objectIndex);

    /// <summary>Sets the object's individual assignment to <paramref name="collection"/>, or removes it when null.</summary>
    LinkOutcome Assign(int objectIndex, Guid? collection, bool allowCreate, bool allowDelete);
}

/// <summary>Glamourer: the designs, applied and locked under Instant Edit's key.</summary>
internal interface IPartnerLooks
{
    bool Available { get; }

    IReadOnlyList<PartnerLink> List();

    LinkOutcome Apply(int objectIndex, Guid design);

    /// <summary>Unlocks the object's state and reverts it.</summary>
    void Release(int objectIndex);
}

/// <summary>Customize+: the profiles, put on an object as a temporary profile.</summary>
internal interface IPartnerProfiles
{
    bool Available { get; }

    IReadOnlyList<PartnerLink> List();

    /// <summary>Puts <paramref name="profile"/> on the object; the temporary profile's Guid, kept for <see cref="Remove"/>.</summary>
    (LinkOutcome Outcome, Guid? Temporary) Apply(ushort objectIndex, Guid profile);

    void Remove(Guid temporary);
}
