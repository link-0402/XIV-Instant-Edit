namespace InstantEdit.Studio.Models;

/// <summary>
/// Where the game's item browser adds what is picked: Build mode's modpack, or the mod open in Edit
/// mode. The browser only asks what is already in it and hands it new subjects.
/// </summary>
internal interface IItemShelf
{
    /// <summary>What it is called in the browser's text: "modpack", "mod".</summary>
    string ShelfName { get; }

    /// <summary>How many items it holds.</summary>
    int ShelfCount { get; }

    /// <summary>The item it has selected (0 for none), so the browser can scroll to it.</summary>
    uint ShelfSelection { get; }

    /// <summary>Whether a subject is in it.</summary>
    bool OnShelf(uint key);

    /// <summary>Whether a set-up is saved for a subject that is not in it now (Build mode keeps them per item).</summary>
    bool HasWork(uint key);

    /// <summary>Adds a subject, or opens it when it is in already.</summary>
    void AddToShelf(PortSubject subject);
}
