using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace InstantEdit.Services.Actors;

/// <summary>
/// Makes partner actors with the game's own calls: a client battle character copied from your GPose character, named,
/// put on its spot and added to GPose. Addresses are kept as identity keys only; each call looks the object up again and
/// touches it only while its client slot still holds that address. Every member runs on the framework thread.
/// </summary>
internal sealed unsafe class ActorSpawner : IPartnerActors, IDisposable
{
    // GPose copies the player into this slot.
    public const ushort GPoseCharacter = 201;
    private const uint NoSlot = 0xFFFFFFFF;
    private static readonly TimeSpan FrameworkWait = TimeSpan.FromSeconds(2);

    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;

    public ActorSpawner(IFramework framework, IClientState clientState, IObjectTable objects, IPluginLog log)
    {
        _framework = framework;
        _clientState = clientState;
        _objects = objects;
        _log = log;
        _framework.Update += OnUpdate;
    }

    public event Action? Updated;

    public bool InGPose => _clientState.IsGPosing;

    public string? PlayerName => _objects.LocalPlayer?.Name.TextValue;

    public (SpawnedActor? Actor, string? Problem) Spawn(string name)
    {
        var source = _objects[GPoseCharacter]?.Address ?? 0;
        if (source == 0 || ((GameObject*)source)->DrawObject == null)
            return (null, "Your GPose character isn't drawn");

        SpawnedActor actor;
        try
        {
            var manager = ClientObjectManager.Instance();
            if (manager == null)
                return (null, "Spawning failed: create");
            var slot = manager->CreateBattleCharacter();
            if (slot == NoSlot)
                return (null, "No free actor slot");
            var made = manager->GetObjectByIndex((ushort)slot);
            if (made == null)
                return (null, "Spawning failed: create");
            actor = new SpawnedActor(slot, made->GameObject.ObjectIndex, (nint)made);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Partner spawn failed: create");
            return (null, "Spawning failed: create");
        }

        var step = "copy";
        try
        {
            var character = (Character*)actor.Address;
            // The copy onto itself makes Penumbra record the actor as its own parent, so it gets its own identity.
            character->CharacterSetup.CopyFromCharacter((Character*)source, CharacterSetupContainer.CopyFlags.WeaponHiding);
            character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);

            step = "name";
            character->GameObject.SetName(name);

            // The game object's spot, not the draw object's: that one carries your heels offset.
            step = "place";
            var from = (GameObject*)source;
            character->GameObject.Position = from->Position;
            character->GameObject.DefaultPosition = from->Position;
            character->GameObject.Rotation = from->Rotation;
            character->GameObject.DefaultRotation = from->Rotation;

            step = "GPose";
            var events = EventFramework.Instance();
            if (events == null)
                throw new InvalidOperationException("No event framework.");
            events->EventSceneModule.EventGPoseController.AddCharacterToGPose(character);
            return (actor, null);
        }
        catch (Exception e)
        {
            _log.Warning(e, $"Partner spawn failed: {step}");
            Delete(actor);
            return (null, $"Spawning failed: {step}");
        }
    }

    public bool Exists(SpawnedActor actor)
    {
        try { return Held(actor) != null && _objects[actor.ObjectIndex]?.Address == actor.Address; }
        catch (Exception e)
        {
            _log.Warning(e, "Partner check failed");
            return false;
        }
    }

    public bool ReadyToDraw(SpawnedActor actor)
    {
        var character = Held(actor);
        return character != null && character->GameObject.IsReadyToDraw();
    }

    public void Draw(SpawnedActor actor)
    {
        var character = Held(actor);
        if (character != null)
            character->GameObject.EnableDraw();
    }

    public bool IsDrawn(SpawnedActor actor)
    {
        var character = Held(actor);
        return character != null && character->GameObject.DrawObject != null;
    }

    /// <summary>Takes the actor out of GPose and deletes it while its slot still holds it; never throws.</summary>
    public void Delete(SpawnedActor actor)
    {
        try
        {
            // Only the slot is checked: an actor made this frame may not be in the object table yet.
            var character = Held(actor);
            if (character == null)
                return;
            var events = EventFramework.Instance();
            if (events != null)
                events->EventSceneModule.EventGPoseController.RemoveCharacterFromGPose(character);
            ClientObjectManager.Instance()->DeleteObjectByIndex((ushort)actor.Slot, 0);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Partner delete failed");
        }
    }

    public void OnFramework(Action action)
    {
        if (_framework.IsInFrameworkUpdateThread)
            action();
        else
            _framework.RunOnFrameworkThread(action).Wait(FrameworkWait);
    }

    /// <summary>The actor, or null when its client slot no longer holds the recorded address.</summary>
    private static Character* Held(SpawnedActor actor)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return null;
        var character = manager->GetObjectByIndex((ushort)actor.Slot);
        return (nint)character == actor.Address && character != null ? (Character*)character : null;
    }

    private void OnUpdate(IFramework framework)
    {
        try { Updated?.Invoke(); }
        catch (Exception e) { _log.Warning(e, "Partner update failed"); }
    }

    public void Dispose() => _framework.Update -= OnUpdate;
}
