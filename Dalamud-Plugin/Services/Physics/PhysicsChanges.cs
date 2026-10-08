using System.Globalization;
using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// What a step of undo or redo changes, in the words and units of the builder's controls, such as
/// "Chest: Firmness 0.0200 to 0.0350". It compares two states of a file, so it names a change however
/// it was made: a slider, a level, the fit or a fix.
/// </summary>
internal static class PhysicsChanges
{
    /// <summary> The sliders' settings, named and rounded as the sliders show them. </summary>
    private static readonly (PhysicsKnob Knob, string Name, int Decimals, string Unit)[] Knobs =
    [
        (PhysicsKnob.Firmness, "Firmness", 4, ""),
        (PhysicsKnob.Damping, "Damping", 3, ""),
        (PhysicsKnob.Swing, "Swing", 0, "°"),
        (PhysicsKnob.Weight, "Weight", 2, ""),
        (PhysicsKnob.Follow, "Follow animation", 3, ""),
        (PhysicsKnob.Length, "Length", 1, " cm"),
        (PhysicsKnob.MaxSpeed, "Max speed", 2, ""),
        (PhysicsKnob.Wind, "Wind", 4, ""),
        (PhysicsKnob.Size, "Size", 1, " cm"),
        (PhysicsKnob.Friction, "Friction", 3, ""),
        (PhysicsKnob.BumpDamping, "Bump damping", 3, ""),
        (PhysicsKnob.PushBack, "Push back", 3, ""),
    ];

    private static readonly (PhybSimulatorFlags Flag, string Name)[] Flags =
    [
        (PhybSimulatorFlags.CollisionsHandled, "Bumps"),
        (PhybSimulatorFlags.ContinuousCollisions, "Fast bumps"),
        (PhybSimulatorFlags.UsingGroundPlane, "Ground"),
        (PhybSimulatorFlags.FixedLength, "Fixed length"),
        (PhybSimulatorFlags.Simulating, "Simulating"),
    ];

    /// <summary> The changes from <paramref name="from"/> to <paramref name="to"/>, a line each, at most <paramref name="most"/> lines. </summary>
    public static IReadOnlyList<string> Describe(PhybFile from, PhybFile to, int most = 8)
    {
        var lines = new List<string>();
        Areas(from, to, lines);
        Shapes(from.Collision, to.Collision, lines);
        var distinct = lines.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count <= most)
            return distinct;
        var kept = distinct.Take(Math.Max(1, most - 1)).ToList();
        kept.Add($"and {distinct.Count - kept.Count} more");
        return kept;
    }

    // ---- Areas ----

    /// <summary> Pairs the areas of both states by the bones they move; the rest were added or deleted. </summary>
    private static void Areas(PhybFile from, PhybFile to, List<string> lines)
    {
        var before = from.Simulators.Select(Bones).ToList();
        var after = to.Simulators.Select(Bones).ToList();
        var fromLabels = PhysicsAreas.Labels(from);
        var toLabels = PhysicsAreas.Labels(to);
        // The longest run of areas both states keep, in order.
        var common = new int[before.Count + 1, after.Count + 1];
        for (var i = before.Count - 1; i >= 0; i--)
            for (var j = after.Count - 1; j >= 0; j--)
                common[i, j] = before[i] == after[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        int s = 0, t = 0;
        while (s < before.Count || t < after.Count)
        {
            if (s < before.Count && t < after.Count && before[s] == after[t])
            {
                Area(from, s, to, t, toLabels[t], lines);
                s++;
                t++;
            }
            else if (t < after.Count && (s == before.Count || common[s, t + 1] >= common[s + 1, t]))
                lines.Add($"Adds {toLabels[t++]}");
            else
                lines.Add($"Deletes {fromLabels[s++]}");
        }
    }

    private static string Bones(PhybSimulator simulator) => string.Join(",", PhysicsAreas.RootBones(simulator));

    private static void Area(PhybFile fromFile, int s, PhybFile toFile, int t, string label, List<string> lines)
    {
        var a = fromFile.Simulators[s];
        var b = toFile.Simulators[t];
        if (a.Excluded != b.Excluded)
            lines.Add($"{label}: turned {(b.Excluded ? "off" : "on")}");
        if (a.Group != b.Group)
            lines.Add($"{label}: Turns off with {PhysicsGroups.Label(a.Group)} to {PhysicsGroups.Label(b.Group)}");
        var settings = KnobLines(a, b, label);
        lines.AddRange(settings.Count > 0 ? settings : PointLines(a, b, label));
        SimulatorLines(a, b, label, lines);
        BumpLines(fromFile, s, toFile, t, label, lines);
    }

    /// <summary> The sliders that changed, once for the chains that changed alike. </summary>
    private static List<string> KnobLines(PhybSimulator a, PhybSimulator b, string label)
    {
        var lines = new List<string>();
        var chains = Math.Min(a.Chains.Count, b.Chains.Count);
        var moving = Enumerable.Range(0, chains).Count(c => b.Chains[c].Nodes.Count >= 2);
        foreach (var (knob, name, decimals, unit) in Knobs)
        {
            var changed = new List<(int Chain, string From, string To)>();
            for (var c = 0; c < chains; c++)
            {
                var x = PhysicsKnobs.Read(a, c, knob);
                var y = PhysicsKnobs.Read(b, c, knob);
                if (!Same(x, y))
                {
                    var (fromText, toText) = Texts(x, y, decimals, unit);
                    changed.Add((c, fromText, toText));
                }
            }
            foreach (var group in changed.GroupBy(change => (change.From, change.To)))
            {
                var which = group.Count() == moving ? string.Empty : $" ({Chains(b, group.Select(change => change.Chain))})";
                lines.Add($"{label}{which}: {name} {group.Key.From} to {group.Key.To}");
            }
        }
        return lines;
    }

    /// <summary> Settings the sliders don't show, point by point: the points table, springs, tip places and point shapes. </summary>
    private static List<string> PointLines(PhybSimulator a, PhybSimulator b, string label)
    {
        var lines = new List<string>();
        var changes = new List<(string Setting, string From, string To, string Where)>();
        if (a.Chains.Count != b.Chains.Count)
            lines.Add($"{label}: {Count(a.Chains.Count, "chain")} to {Count(b.Chains.Count, "chain")}");
        for (var c = 0; c < Math.Min(a.Chains.Count, b.Chains.Count); c++)
        {
            var x = a.Chains[c];
            var y = b.Chains[c];
            var chain = Chains(b, [c]);
            if (x.Type != y.Type)
                changes.Add(("Point shape", x.Type == 1 ? "capsules" : "spheres", y.Type == 1 ? "capsules" : "spheres", chain));
            if (x.LastBoneOffset != y.LastBoneOffset)
                changes.Add(("Tip place", Vector(x.LastBoneOffset * 100, 1, " cm"), Vector(y.LastBoneOffset * 100, 1, " cm"), chain));
            if (x.Nodes.Count != y.Nodes.Count)
            {
                changes.Add(("Points", x.Nodes.Count.ToString(CultureInfo.InvariantCulture), y.Nodes.Count.ToString(CultureInfo.InvariantCulture), chain));
                continue;
            }
            for (var n = 0; n < x.Nodes.Count; n++)
            {
                var p = x.Nodes[n];
                var q = y.Nodes[n];
                var where = n == y.Nodes.Count - 1 ? $"{chain} tip" : y.Nodes[n].Bone.IsEmpty ? $"{chain} point {n + 1}" : y.Nodes[n].Bone.Text;
                Add(changes, "Size", p.Radius * 100, q.Radius * 100, 1, " cm", where);
                Add(changes, "Follow animation", p.AttractByAnimation, q.AttractByAnimation, 3, "", where);
                Add(changes, "Wind", p.WindScale, q.WindScale, 4, "", where);
                Add(changes, "Weight", p.GravityScale, q.GravityScale, 2, "", where);
                Add(changes, "Swing", PhysicsTemplates.Degrees(p.ConeMaxAngle), PhysicsTemplates.Degrees(q.ConeMaxAngle), 0, "°", where);
                if (p.ConeAxisOffset != q.ConeAxisOffset)
                    changes.Add(("Axis", Vector(p.ConeAxisOffset, 3, ""), Vector(q.ConeAxisOffset, 3, ""), where));
            }
        }
        if (a.Attracts.Count != b.Attracts.Count)
            lines.Add($"{label}: {Count(a.Attracts.Count, "spring")} to {Count(b.Attracts.Count, "spring")}");
        else
            for (var i = 0; i < a.Attracts.Count; i++)
            {
                var p = a.Attracts[i];
                var q = b.Attracts[i];
                var where = q.Chain >= 0 && q.Chain < b.Chains.Count ? $"{Chains(b, [q.Chain])} spring" : $"spring {i + 1}";
                Add(changes, "Strength", p.Stiffness, q.Stiffness, 4, "", where);
                if (p.Offset != q.Offset)
                    changes.Add(("Point", Vector(p.Offset * 100, 1, " cm"), Vector(q.Offset * 100, 1, " cm"), where));
                if (!p.Bone.Equals(q.Bone))
                    changes.Add(("Toward", p.Bone.Text, q.Bone.Text, where));
            }
        foreach (var group in changes.GroupBy(change => (change.Setting, change.From, change.To)))
        {
            var places = group.Select(change => change.Where).Distinct(StringComparer.Ordinal).ToList();
            var where = places.Count <= 2 ? string.Join(" and ", places) : $"{places.Count} points";
            lines.Add($"{label}, {where}: {group.Key.Setting} {group.Key.From} to {group.Key.To}");
        }
        return lines;
    }

    /// <summary> The whole area's settings: gravity, wind, steps and switches. </summary>
    private static void SimulatorLines(PhybSimulator a, PhybSimulator b, string label, List<string> lines)
    {
        if (a.Gravity != b.Gravity)
            lines.Add($"{label}: Gravity {Vector(a.Gravity, 2, "")} to {Vector(b.Gravity, 2, "")}");
        if (a.Wind != b.Wind)
            lines.Add($"{label}: Wind {Vector(a.Wind, 2, "")} to {Vector(b.Wind, 2, "")}");
        if (a.ConstraintLoop != b.ConstraintLoop || a.CollisionLoop != b.CollisionLoop)
            lines.Add($"{label}: Steps {a.ConstraintLoop} and {a.CollisionLoop} to {b.ConstraintLoop} and {b.CollisionLoop}");
        foreach (var (flag, name) in Flags)
            if (a.Flags.HasFlag(flag) != b.Flags.HasFlag(flag))
                lines.Add($"{label}: {name} turned {(b.Flags.HasFlag(flag) ? "on" : "off")}");
    }

    /// <summary> The shapes the area's points bump into, and how. </summary>
    private static void BumpLines(PhybFile fromFile, int s, PhybFile toFile, int t, string label, List<string> lines)
    {
        var a = fromFile.Simulators[s];
        var b = toFile.Simulators[t];
        var fromChains = Enumerable.Range(0, a.Chains.Count).Select(c => (s, c)).ToList();
        var toChains = Enumerable.Range(0, b.Chains.Count).Select(c => (t, c)).ToList();
        foreach (var name in References(a).Concat(References(b)).Select(reference => reference.Name.Text).Distinct(StringComparer.Ordinal))
        {
            var was = PhysicsCollisions.State(fromFile, fromChains, name);
            var now = PhysicsCollisions.State(toFile, toChains, name);
            var shape = ShapeLabel(fromFile, toFile, name);
            if (was != now)
                lines.Add(now switch
                {
                    PhysicsBumps.None => $"{label}: no longer collides with {shape}",
                    PhysicsBumps.Some => $"{label}: collides with {shape} with some points",
                    _ => was == PhysicsBumps.Some ? $"{label}: collides with {shape} with every point" : $"{label}: collides with {shape}",
                });
            if (References(a).FirstOrDefault(reference => reference.Name.Text == name) is { } before &&
                References(b).FirstOrDefault(reference => reference.Name.Text == name) is { } after && before.Type != after.Type)
                lines.Add($"{label}: {shape} {Kind(before.Type)} to {Kind(after.Type)}");
        }
    }

    /// <summary> "Left thigh (asi_l_a)", from whichever state still has the shape. </summary>
    private static string ShapeLabel(PhybFile from, PhybFile to, string name)
        => PhysicsShapeNames.Part(to.Collision, name, null) is not null
            ? PhysicsShapeNames.Label(to.Collision, name, null)
            : PhysicsShapeNames.Label(from.Collision, name, null);

    private static IEnumerable<PhybCollisionRef> References(PhybSimulator simulator)
        => simulator.Collisions.Concat(simulator.Chains.SelectMany(chain => chain.Collisions));

    private static string Kind(PhybCollisionType type) => type switch
    {
        PhybCollisionType.Outside => "stay outside",
        PhybCollisionType.Inside => "stay inside",
        _ => "stick to surface",
    };

    // ---- Collision shapes ----

    private static void Shapes(PhybCollision a, PhybCollision b, List<string> lines)
    {
        var before = a.Names.Select(name => name.Text).ToHashSet(StringComparer.Ordinal);
        var after = b.Names.Select(name => name.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var name in a.Names.Concat(b.Names).Select(name => name.Text).Distinct(StringComparer.Ordinal))
        {
            if (!before.Contains(name))
            {
                lines.Add($"Adds shape {name}");
                continue;
            }
            if (!after.Contains(name))
            {
                lines.Add($"Deletes shape {name}");
                continue;
            }
            if (a.Capsules.FirstOrDefault(shape => shape.Name.Text == name) is { } p && b.Capsules.FirstOrDefault(shape => shape.Name.Text == name) is { } q)
            {
                Shape(lines, name, "Radius", p.Radius * 100, q.Radius * 100);
                if (p.StartOffset != q.StartOffset)
                    lines.Add($"Shape {name}: Start {Vector(p.StartOffset * 100, 1, " cm")} to {Vector(q.StartOffset * 100, 1, " cm")}");
                if (p.EndOffset != q.EndOffset)
                    lines.Add($"Shape {name}: End {Vector(p.EndOffset * 100, 1, " cm")} to {Vector(q.EndOffset * 100, 1, " cm")}");
                if (!p.StartBone.Equals(q.StartBone) || !p.EndBone.Equals(q.EndBone))
                    lines.Add($"Shape {name}: {p.StartBone.Text} to {p.EndBone.Text}, now {q.StartBone.Text} to {q.EndBone.Text}");
            }
            else if (a.Spheres.FirstOrDefault(shape => shape.Name.Text == name) is { } x && b.Spheres.FirstOrDefault(shape => shape.Name.Text == name) is { } y)
            {
                Shape(lines, name, "Size", x.Thickness * 100, y.Thickness * 100);
                if (x.Offset != y.Offset)
                    lines.Add($"Shape {name}: Place {Vector(x.Offset * 100, 1, " cm")} to {Vector(y.Offset * 100, 1, " cm")}");
                if (!x.Bone.Equals(y.Bone))
                    lines.Add($"Shape {name}: on {x.Bone.Text}, now on {y.Bone.Text}");
            }
        }
    }

    private static void Shape(List<string> lines, string name, string setting, float from, float to)
    {
        if (Same(from, to))
            return;
        var (fromText, toText) = Texts(from, to, 1, " cm");
        lines.Add($"Shape {name}: {setting} {fromText} to {toText}");
    }

    // ---- Text ----

    private static void Add(List<(string, string, string, string)> changes, string setting, float from, float to, int decimals, string unit, string where)
    {
        if (Same(from, to))
            return;
        var (fromText, toText) = Texts(from, to, decimals, unit);
        changes.Add((setting, fromText, toText, where));
    }

    private static bool Same(float? a, float? b)
        => a == b || a is { } x && b is { } y && float.IsNaN(x) && float.IsNaN(y);

    /// <summary> Both values as the control shows them, with more decimals when that would show them alike. </summary>
    private static (string From, string To) Texts(float? from, float? to, int decimals, string unit)
    {
        for (var shown = decimals; ; shown++)
        {
            var a = Text(from, shown, unit);
            var b = Text(to, shown, unit);
            if (a != b || shown >= decimals + 4)
                return (a, b);
        }
    }

    private static string Text(float? value, int decimals, string unit)
        => value is { } number ? number.ToString("F" + decimals, CultureInfo.InvariantCulture) + unit : "none";

    private static string Vector(Vector3 value, int decimals, string unit)
    {
        var format = "F" + decimals;
        return $"({value.X.ToString(format, CultureInfo.InvariantCulture)}, {value.Y.ToString(format, CultureInfo.InvariantCulture)}, " +
               $"{value.Z.ToString(format, CultureInfo.InvariantCulture)}){unit}";
    }

    /// <summary> Chains by their bone: "j_mune_l", "j_mune_l and j_mune_r", or "3 chains". </summary>
    private static string Chains(PhybSimulator simulator, IEnumerable<int> chains)
    {
        var names = chains.Select(c => c >= 0 && c < simulator.Chains.Count && simulator.Chains[c].Nodes.Count > 0 && !simulator.Chains[c].Nodes[0].Bone.IsEmpty
            ? simulator.Chains[c].Nodes[0].Bone.Text
            : $"chain {c + 1}").ToList();
        return names.Count <= 2 ? string.Join(" and ", names) : $"{names.Count} chains";
    }

    private static string Count(int count, string thing) => count == 1 ? $"1 {thing}" : $"{count} {thing}s";
}
