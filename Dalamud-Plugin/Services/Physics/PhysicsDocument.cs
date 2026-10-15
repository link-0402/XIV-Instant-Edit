namespace InstantEdit.Services.Physics;

/// <summary> Where a physics document's starting file came from. </summary>
internal enum PhysicsSourceKind
{
    /// <summary> The game's own file for the race. </summary>
    Vanilla,
    /// <summary> The file the character's collection loads now. </summary>
    Loaded,
    /// <summary> One of the builder's own mods, loaded now or saved into. </summary>
    Mine,
    /// <summary> A file picked from disk. </summary>
    File,
}

/// <summary>
/// The starting file of a document: its kind, a label to show and where it was read from. <paramref name="Mod"/>
/// is the folder of the builder's mod it came from or was saved into, which saving suggests again.
/// </summary>
internal sealed record PhysicsSource(PhysicsSourceKind Kind, string Label, string Path, string Mod = "");

/// <summary> A state undo or redo goes to, and the name of the change between, like "Fit to my body", when it has one. </summary>
internal sealed record PhysicsStep(PhybFile File, string? Name);

/// <summary>
/// A physics file being edited: the working copy, undo and redo, and what was last saved or loaded, so
/// the builder knows when there is something to save. Simulators can be left out without deleting
/// them. The file written is the working copy without them; Dawntrail's extension block is kept only
/// while the simulators still have the layout they were read with, since it describes them by position.
/// </summary>
internal sealed class PhysicsDocument
{
    private const int MaxUndo = 200;
    private readonly List<PhysicsStep> undo = [];
    private readonly List<PhysicsStep> redo = [];
    private readonly string layout;
    private byte[] baseline;
    private int outputRevision = -1;
    private byte[] output = [];

    public PhysicsDocument(PhybFile file, PhysicsSource source, ushort race)
    {
        File = file;
        Start = file.Clone();
        Source = source;
        Race = race;
        layout = Layout(file);
        baseline = OutputBytes();
    }

    public PhybFile File { get; private set; }

    /// <summary> The file as it was opened, which the sliders mark their starting values from. </summary>
    public PhybFile Start { get; }
    public PhysicsSource Source { get; private set; }
    public ushort Race { get; }

    /// <summary> Counts every change, for views that cache what they show. </summary>
    public int Revision { get; private set; }

    /// <summary> Counts every change to the undo and redo lists, for views that describe the next step. </summary>
    public int History { get; private set; }

    /// <summary> Differs from what was loaded or last saved. </summary>
    public bool Dirty => !OutputBytes().AsSpan().SequenceEqual(baseline);

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>
    /// Call before a change, once per change: a slider's whole drag is one undo step. A name says what
    /// a change of many settings was, such as a fit.
    /// </summary>
    public void Checkpoint(string? name = null)
    {
        undo.Add(new PhysicsStep(File.Clone(), name));
        if (undo.Count > MaxUndo)
            undo.RemoveAt(0);
        redo.Clear();
        History++;
    }

    /// <summary> Call after the working copy changed. </summary>
    public void Changed() => Revision++;

    public bool Undo() => Step(undo, redo);
    public bool Redo() => Step(redo, undo);

    /// <summary> Where undo would go and what it changes back, or null when there is nothing to undo. </summary>
    public PhysicsStep? NextUndo() => Peek(undo);

    /// <summary> Where redo would go and what it changes again, or null when there is nothing to redo. </summary>
    public PhysicsStep? NextRedo() => Peek(redo);

    /// <summary> Moves one step, skipping steps that change nothing, such as a slider clicked without moving it. </summary>
    private bool Step(List<PhysicsStep> from, List<PhysicsStep> to)
    {
        var current = State(File);
        while (from.Count > 0 && State(from[^1].File).AsSpan().SequenceEqual(current))
            from.RemoveAt(from.Count - 1);
        History++;
        if (from.Count == 0)
            return false;
        var step = from[^1];
        to.Add(step with { File = File });
        File = step.File;
        from.RemoveAt(from.Count - 1);
        Revision++;
        return true;
    }

    /// <summary> The step <see cref="Step"/> would take, skipping the same steps. </summary>
    private PhysicsStep? Peek(List<PhysicsStep> from)
    {
        if (from.Count == 0)
            return null;
        var current = State(File);
        for (var i = from.Count - 1; i >= 0; i--)
            if (!State(from[i].File).AsSpan().SequenceEqual(current))
                return from[i];
        return null;
    }

    /// <summary> A file's bytes and which simulators are off: everything a step can change. </summary>
    private static byte[] State(PhybFile file)
    {
        byte[] bytes;
        try { bytes = file.ToBytes(); }
        catch (InvalidDataException) { return Guid.NewGuid().ToByteArray(); }
        var state = new byte[bytes.Length + file.Simulators.Count];
        bytes.CopyTo(state, 0);
        for (var i = 0; i < file.Simulators.Count; i++)
            state[bytes.Length + i] = file.Simulators[i].Excluded ? (byte)1 : (byte)0;
        return state;
    }

    /// <summary>
    /// The file that was saved is now the state to compare with, and its mod the source. Changes made
    /// while it was saving still count as unsaved.
    /// </summary>
    public void MarkSaved(PhysicsSource source, byte[] saved)
    {
        Source = source;
        baseline = saved;
    }

    /// <summary> The file to write: the working copy without the simulators left out. </summary>
    public PhybFile Output()
    {
        var file = File.Clone();
        file.Simulators.RemoveAll(simulator => simulator.Excluded);
        if (file.Extension != null && Layout(file) != layout)
            file.Extension = null;
        // The data type says which sections the file has: the game's files with simulators say 3 with
        // shapes and 2 without. It follows the shapes, so ones imported into a file that had none are read.
        if (file.Version != 0 && file.Simulators.Count > 0)
            file.DataType = file.Collision.IsEmpty ? 2u : 3u;
        // With every area off or removed, the file still lists none instead of ending where its simulators would start.
        if (file.Simulators.Count == 0 && file.DataType is 2 or 3)
            file.EmptySimulatorList = true;
        return file;
    }

    public byte[] OutputBytes()
    {
        if (outputRevision != Revision)
        {
            output = Output().ToBytes();
            outputRevision = Revision;
        }
        return output;
    }

    /// <summary> The extension block only matches simulators, chains and nodes it was written for. </summary>
    private static string Layout(PhybFile file)
        => string.Join("|", file.Simulators.Where(s => !s.Excluded).Select(s =>
            string.Join(";", s.Chains.Select(c => string.Join(",", c.Nodes.Select(n => n.Bone.Text))))));

    // ---- Area operations (callers checkpoint first) ----

    /// <summary> Adds an area built for the skeleton; null when it has none of the area's bones. </summary>
    public int? AddArea(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
    {
        if (PhysicsTemplates.Build(kind, skeleton) is not { } simulator)
            return null;
        File.Simulators.Add(simulator);
        EnsureSimulationData();
        Changed();
        return File.Simulators.Count - 1;
    }

    public void RemoveArea(int simulator)
    {
        File.Simulators.RemoveAt(simulator);
        Changed();
    }

    /// <summary>
    /// Copies a simulator of another file into this one, with the collision shapes it uses. A shape
    /// this file already has under the same name is used when it is the same shape, and the copy is
    /// renamed otherwise. Returns the new simulator's index.
    /// </summary>
    public int ImportArea(PhybFile from, int simulator)
    {
        var copy = from.Simulators[simulator].Clone();
        copy.Excluded = false;
        var renames = new Dictionary<string, PhybName>(StringComparer.Ordinal);
        foreach (var name in ShapeNames(copy).Distinct())
            if (CopyShape(from.Collision, File.Collision, name) is { } renamed && renamed.Text != name)
                renames[name] = renamed;
        if (renames.Count > 0)
        {
            foreach (var reference in copy.Collisions.Concat(copy.CollisionConnectors).Concat(copy.Chains.SelectMany(c => c.Collisions)))
                if (renames.TryGetValue(reference.Name.Text, out var renamed))
                    reference.Name = renamed;
            foreach (var alignment in copy.PostAlignments)
                if (renames.TryGetValue(alignment.CollisionName.Text, out var renamed))
                    alignment.CollisionName = renamed;
        }
        File.Simulators.Add(copy);
        EnsureSimulationData();
        Changed();
        return File.Simulators.Count - 1;
    }

    private void EnsureSimulationData()
    {
        if (File.Version == 0)
        {
            File.Version = PhybFile.CurrentVersion;
            File.DataType = 2;
        }
        else if (File.DataType is 0 or 1)
            File.DataType = File.Collision.IsEmpty ? 2u : 3u;
    }

    private static IEnumerable<string> ShapeNames(PhybSimulator simulator)
        => simulator.Collisions.Concat(simulator.CollisionConnectors).Concat(simulator.Chains.SelectMany(c => c.Collisions))
            .Select(reference => reference.Name.Text).Concat(simulator.PostAlignments.Select(a => a.CollisionName.Text));

    /// <summary> Copies a shape by name; returns the name it has in the target, or null when the source lacks it. </summary>
    private static PhybName? CopyShape(PhybCollision from, PhybCollision to, string name)
    {
        PhybName Free()
        {
            for (var i = 2; ; i++)
            {
                var candidate = name.Length > PhybName.MaxLength - 3 ? $"{name[..(PhybName.MaxLength - 3)]}_{i}" : $"{name}_{i}";
                if (!to.Names.Any(existing => existing.Text == candidate))
                    return PhybName.From(candidate);
            }
        }

        if (from.Capsules.FirstOrDefault(s => s.Name.Text == name) is { } capsule)
        {
            if (to.Capsules.FirstOrDefault(s => s.Name.Text == name) is { } same && Same(same, capsule))
                return same.Name;
            var copy = capsule.Clone();
            if (to.Names.Any(existing => existing.Text == name))
                copy.Name = Free();
            to.Capsules.Add(copy);
            return copy.Name;
        }
        if (from.Spheres.FirstOrDefault(s => s.Name.Text == name) is { } sphere)
        {
            if (to.Spheres.FirstOrDefault(s => s.Name.Text == name) is { } same && Same(same, sphere))
                return same.Name;
            var copy = sphere.Clone();
            if (to.Names.Any(existing => existing.Text == name))
                copy.Name = Free();
            to.Spheres.Add(copy);
            return copy.Name;
        }
        if (from.Ellipsoids.FirstOrDefault(s => s.Name.Text == name) is { } ellipsoid)
        {
            var copy = ellipsoid.Clone();
            if (to.Names.Any(existing => existing.Text == name))
                copy.Name = Free();
            to.Ellipsoids.Add(copy);
            return copy.Name;
        }
        if (from.NormalPlanes.FirstOrDefault(s => s.Name.Text == name) is { } plane)
        {
            var copy = plane.Clone();
            if (to.Names.Any(existing => existing.Text == name))
                copy.Name = Free();
            to.NormalPlanes.Add(copy);
            return copy.Name;
        }
        if (from.ThreePointPlanes.FirstOrDefault(s => s.Name.Text == name) is { } threePoint)
        {
            var copy = threePoint.Clone();
            if (to.Names.Any(existing => existing.Text == name))
                copy.Name = Free();
            to.ThreePointPlanes.Add(copy);
            return copy.Name;
        }
        return null;
    }

    private static bool Same(PhybCapsule a, PhybCapsule b)
        => a.StartBone.Equals(b.StartBone) && a.EndBone.Equals(b.EndBone) && a.StartOffset == b.StartOffset && a.EndOffset == b.EndOffset && a.Radius == b.Radius;

    private static bool Same(PhybSphere a, PhybSphere b)
        => a.Bone.Equals(b.Bone) && a.Offset == b.Offset && a.Thickness == b.Thickness;
}
