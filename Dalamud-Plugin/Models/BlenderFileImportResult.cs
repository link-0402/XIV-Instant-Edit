namespace InstantEdit.Models;

/// <summary>
/// What Blender reported after importing model files from disk: whether it has (false while they
/// are still queued), its message, and whether that message is a warning.
/// </summary>
public sealed record BlenderFileImportResult(bool Imported, string Message, bool Warning);
