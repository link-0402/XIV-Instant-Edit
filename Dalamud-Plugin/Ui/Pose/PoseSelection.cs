namespace InstantEdit.Ui.Pose;

/// <summary>
/// The bones chosen in the Bones window and the Timeline window's bone picker, in the order they
/// were picked. The primary bone is the one the value fields show; an edit made to it is made to
/// every selected bone. Pure, so the click rules are tested without ImGui.
/// </summary>
internal sealed class PoseSelection
{
    private readonly List<string> bones = [];

    public string? Primary { get; private set; }
    public IReadOnlyList<string> Bones => bones;
    public int Count => bones.Count;

    public bool Contains(string bone) => bones.Contains(bone, StringComparer.Ordinal);

    /// <summary>
    /// A click on <paramref name="bone"/>: alone it becomes the only selected bone; with
    /// <paramref name="add"/> (Ctrl) it joins the selection as its primary, or leaves it when it was in it.
    /// </summary>
    public void Click(string bone, bool add)
    {
        if (!add)
        {
            Only(bone);
            return;
        }
        var at = bones.FindIndex(name => string.Equals(name, bone, StringComparison.Ordinal));
        if (at < 0)
        {
            bones.Add(bone);
            Primary = bone;
            return;
        }
        bones.RemoveAt(at);
        if (string.Equals(Primary, bone, StringComparison.Ordinal))
            Primary = bones.Count > 0 ? bones[^1] : null;
    }

    public void Only(string bone)
    {
        bones.Clear();
        bones.Add(bone);
        Primary = bone;
    }

    /// <summary>
    /// Selects <paramref name="group"/>: in place of the selection, or with <paramref name="add"/> on top
    /// of it. <paramref name="primary"/> becomes the primary bone when it is selected; otherwise the
    /// primary stays when it is still selected, else it is the group's first bone.
    /// </summary>
    public void Set(IEnumerable<string> group, bool add, string? primary = null)
    {
        if (!add)
            bones.Clear();
        foreach (var bone in group)
            if (!string.IsNullOrEmpty(bone) && !Contains(bone))
                bones.Add(bone);
        if (primary != null && Contains(primary))
            Primary = primary;
        else if (Primary == null || !Contains(Primary))
            Primary = bones.Count > 0 ? bones[0] : null;
    }

    public void Clear()
    {
        bones.Clear();
        Primary = null;
    }

    /// <summary>Drops the bones <paramref name="exists"/> rejects, such as bones another clip's skeleton lacks.</summary>
    public void Keep(Func<string, bool> exists)
    {
        bones.RemoveAll(bone => !exists(bone));
        if (Primary != null && !Contains(Primary))
            Primary = bones.Count > 0 ? bones[0] : null;
    }
}
