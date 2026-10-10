namespace InstantEdit.Studio.Services.Check;

/// <summary>Penumbra, as far as changing a mod's files needs it: reading the mod again, and whether it still knows the mod.</summary>
internal interface IModReloader
{
    bool Reload(string folderName);

    bool IsPresent(string folderName);
}
