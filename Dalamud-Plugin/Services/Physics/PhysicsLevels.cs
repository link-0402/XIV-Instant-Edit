namespace InstantEdit.Services.Physics;

/// <summary>
/// An intensity an area can be set to: its movement settings, taken whole from a popular preset.
/// A null <paramref name="Firmness"/> is a preset without a spring, which only following the animation holds.
/// </summary>
internal sealed record PhysicsLevel(string Name, string Detail, float? Firmness, float Damping, float Swing, float Weight, float Follow,
    float Length, float MaxSpeed);

/// <summary>
/// Firm to very soft for the body areas, each level the settings a popular preset ships for that area
/// and skeleton, ordered by how much they move. Chest levels are tuned at medium size (Rue+ Medium):
/// their firmness follows the breast size the fit measures, as size-based presets do.
/// </summary>
internal static class PhysicsLevels
{
    private static readonly Dictionary<string, PhysicsLevel[]> Levels = new(StringComparer.Ordinal)
    {
        ["chest"] =
        [
            // The game's own.
            new("Firm", "The game's own: a small, quick bounce.", 0.075f, 0.1f, 8f, 0.1f, 0f, 14.5f, 4f),
            // Rue+ "Breasts - Bouncy".
            new("Natural", "A soft bounce that settles fast.", 0.03f, 0.18f, 15f, 0.2f, 0f, 12f, 4f),
            // Cultist physics, large breasts nude.
            new("Soft", "Swings wider and follows your moves.", 0.035f, 0.15f, 40f, 0.2f, 0.02f, 15f, 10f),
            // Ashley's YAS Yiggles, medium.
            new("Very soft", "Heavy and slow to settle, with no swing limit.", 0.035f, 0.01f, 180f, 0.5f, 0f, 12f, 0.6f),
        ],
        ["butt-ivcs"] =
        [
            // Rue+ "Butt - Jiggle".
            new("Firm", "A small jiggle.", 0.03f, 0.1f, 40f, 0.1f, 0f, 11.2f, 10f),
            // Cultist physics.
            new("Natural", "A fuller jiggle, with no swing limit.", 0.015f, 0.1f, 180f, 0.1f, 0f, 11.2f, 10f),
            // Rue+ "Butt - But more".
            new("Soft", "Soft and wobbly.", 0.01f, 0.1f, 40f, 0.1f, 0f, 11.2f, 10f),
            // Ashley's YAS Yiggles.
            new("Very soft", "Heavy and slow to settle.", 0.0125f, 0.005f, 60f, 0.1f, 0.005f, 11.2f, 0.5f),
        ],
        ["butt-yas"] =
        [
            // Cultist physics.
            new("Firm", "A small jiggle.", 0.015f, 0.075f, 90f, 0.1f, 0f, 1f, 10f),
            // Rue+ "Butt - Jiggle".
            new("Natural", "A fuller jiggle.", 0.01f, 0.05f, 80f, 0.1f, 0f, 1f, 10f),
            // Rue+ "Butt - But more".
            new("Soft", "Soft and wobbly.", 0.003f, 0.075f, 80f, 0.1f, 0f, 1f, 10f),
            // Ashley's YAS Yiggles: no spring, the animation holds it a little.
            new("Very soft", "Heavy and slow, held only by your moves.", null, 0.01f, 45f, 0.1f, 0.02f, 7f, 0.3f),
        ],
        ["thighs-ivcs"] =
        [
            // Rue+.
            new("Firm", "Barely moves.", 0.075f, 0.1f, 8f, 0.1f, 0f, 15f, 10f),
            // Cultist physics.
            new("Natural", "A small jiggle.", 0.05f, 0.05f, 8f, 0.1f, 0f, 15f, 10f),
            // Ashley's YAS Yiggles.
            new("Soft", "Wobbly and slow to settle.", 0.03f, 0.03f, 180f, 0.1f, 0f, 15f, 1f),
        ],
        ["thighs-yas"] =
        [
            // Rue+.
            new("Firm", "A small jiggle that settles fast.", 0.03f, 0.15f, 40f, 0.1f, 0f, 11.2f, 10f),
            // Cultist physics.
            new("Natural", "A livelier jiggle.", 0.05f, 0.07f, 90f, 0.1f, 0f, 10.2f, 10f),
            // Ashley's YAS Yiggles.
            new("Soft", "Wobbly and slow to settle.", 0.03f, 0.03f, 180f, 0.1f, 0f, 10.2f, 1f),
        ],
        ["belly-ivcs"] =
        [
            // Cultist physics.
            new("Firm", "Barely moves.", 0.015f, 0.3f, 90f, 0.1f, 0f, 3f, 10f),
            // Rue+.
            new("Natural", "A small jiggle.", 0.01f, 0.1f, 40f, 0.1f, 0f, 3f, 10f),
            // Ashley's YAS Yiggles.
            new("Soft", "Wobbly and slow to settle.", 0.01f, 0f, 40f, 0.1f, 0f, 3f, 1f),
        ],
        ["belly-yas"] =
        [
            // Rue+.
            new("Firm", "A small jiggle that settles fast.", 0.008f, 0.12f, 60f, 0.1f, 0f, 3f, 10f),
            // Cultist physics.
            new("Natural", "Softer and wider.", 0.003f, 0.15f, 90f, 0.1f, 0.01f, 3f, 10f),
            // Ashley's YAS Yiggles.
            new("Soft", "Wobbly and slow to settle.", 0.008f, 0f, 60f, 0.1f, 0f, 3f, 1f),
        ],
    };

    /// <summary> The levels of an area, firm first; none for areas presets don't agree on. </summary>
    public static IReadOnlyList<PhysicsLevel> For(string areaKey) => Levels.TryGetValue(areaKey, out var levels) ? levels : [];

    /// <summary> Areas whose firmness follows the measured size. </summary>
    public static bool SizeFitted(string areaKey) => areaKey == "chest";

    /// <summary>
    /// The level a chain is set to, or null. Where firmness follows the body's size it isn't compared:
    /// the other settings tell those levels apart. A swing below the level's still counts, since the
    /// fit lowers swings that would sink into the body.
    /// </summary>
    public static PhysicsLevel? Match(PhybSimulator simulator, int chain, string areaKey)
    {
        foreach (var level in For(areaKey))
        {
            if (!Near(simulator, chain, PhysicsKnob.Damping, level.Damping, 0.0015f) ||
                Math.Min(PhysicsKnobs.Read(simulator, chain, PhysicsKnob.Swing) ?? 999, 180) > level.Swing + 0.6f ||
                !Near(simulator, chain, PhysicsKnob.Weight, level.Weight, 0.005f) ||
                !Near(simulator, chain, PhysicsKnob.Follow, level.Follow, 0.0005f) ||
                !Near(simulator, chain, PhysicsKnob.Length, level.Length, 0.15f) ||
                !Near(Math.Min(PhysicsKnobs.Read(simulator, chain, PhysicsKnob.MaxSpeed) ?? -1, 10), level.MaxSpeed, 0.05f))
                continue;
            if (SizeFitted(areaKey))
                return level;
            var firmness = PhysicsKnobs.Read(simulator, chain, PhysicsKnob.Firmness) ?? 0;
            var wanted = level.Firmness ?? 0;
            if (Near(firmness, wanted, Math.Max(0.0003f, wanted * 0.05f)))
                return level;
        }
        return null;
    }

    /// <summary>
    /// Sets <paramref name="level"/> on the chains. Firmness is the level's times
    /// <paramref name="firmnessScale"/> where it follows the body's size; a chain without a spring gets
    /// one at its rest place (measured on <paramref name="skeleton"/>), and a level without one turns the
    /// chain's spring off.
    /// </summary>
    public static void Apply(PhybFile file, IEnumerable<(int Simulator, int Chain)> targets, PhysicsLevel level, string areaKey,
        PhysicsSkeleton? skeleton, float firmnessScale = 1)
    {
        var firmness = (level.Firmness ?? 0) * (SizeFitted(areaKey) ? firmnessScale : 1);
        foreach (var (s, c) in targets)
        {
            if (s < 0 || s >= file.Simulators.Count || c < 0 || c >= file.Simulators[s].Chains.Count || file.Simulators[s].Chains[c].Nodes.Count < 2)
                continue;
            var simulator = file.Simulators[s];
            if (PhysicsKnobs.MainSpring(simulator, c) is { } spring)
                spring.Stiffness = firmness;
            else if (firmness > 0 && skeleton != null && PhysicsTemplates.RestSpring(simulator.Chains[c], (short)c, skeleton, firmness) is { } added)
                simulator.Attracts.Add(added);
            PhysicsKnobs.Write(simulator, c, PhysicsKnob.Damping, level.Damping);
            PhysicsKnobs.Write(simulator, c, PhysicsKnob.Swing, level.Swing);
            PhysicsKnobs.Write(simulator, c, PhysicsKnob.Weight, level.Weight);
            PhysicsKnobs.Write(simulator, c, PhysicsKnob.Follow, level.Follow);
            PhysicsKnobs.Write(simulator, c, PhysicsKnob.Length, level.Length);
            PhysicsKnobs.Write(simulator, c, PhysicsKnob.MaxSpeed, level.MaxSpeed);
        }
    }

    private static bool Near(PhybSimulator simulator, int chain, PhysicsKnob knob, float wanted, float tolerance)
        => PhysicsKnobs.Read(simulator, chain, knob) is { } value && Near(value, wanted, tolerance);

    private static bool Near(float value, float wanted, float tolerance) => MathF.Abs(value - wanted) <= tolerance;
}
