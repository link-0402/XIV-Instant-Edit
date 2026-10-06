namespace InstantEdit.Services.Physics;

/// <summary>
/// The range most presets use for each area's sliders, so the builder can show where a value sits
/// among them. Surveyed on 3,207 body physics files of a large mod library and the game's 27 body
/// files: one sample per distinct setting of an area (a setting copied into a thousand files counts
/// once), clamped to the slider, and the range from the 10th to the 90th percentile. It shows what is
/// common, not what is stable: no damping is common and never calms down.
/// </summary>
internal static class PhysicsRanges
{
    private static readonly Dictionary<string, Dictionary<PhysicsKnob, PhysicsRange>> Ranges = new(StringComparer.Ordinal)
    {
        // 31 distinct settings
        ["chest"] = new()
        {
            [PhysicsKnob.Firmness] = new(0f, 0.075f), [PhysicsKnob.Damping] = new(0.01f, 0.18f), [PhysicsKnob.Swing] = new(8f, 180f),
            [PhysicsKnob.Weight] = new(0.1f, 0.5f), [PhysicsKnob.Follow] = new(0f, 0.025f), [PhysicsKnob.Length] = new(12f, 20f),
            [PhysicsKnob.MaxSpeed] = new(0.6f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(1f, 6.4f),
            [PhysicsKnob.Friction] = new(0f, 0.5f), [PhysicsKnob.BumpDamping] = new(0f, 0.5f), [PhysicsKnob.PushBack] = new(0f, 1f),
        },
        // 6 distinct settings
        ["pecs-ivcs"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.004f, 0.02f), [PhysicsKnob.Damping] = new(0.001f, 0.001f), [PhysicsKnob.Swing] = new(45f, 180f),
            [PhysicsKnob.Weight] = new(0.1f, 0.1f), [PhysicsKnob.Follow] = new(0f, 0f), [PhysicsKnob.Length] = new(10f, 20f),
            [PhysicsKnob.MaxSpeed] = new(4f, 4f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 0f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 0f),
        },
        // 25 distinct settings
        ["butt-ivcs"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.003f, 0.04f), [PhysicsKnob.Damping] = new(0f, 0.5f), [PhysicsKnob.Swing] = new(20f, 180f),
            [PhysicsKnob.Weight] = new(0.05f, 1f), [PhysicsKnob.Follow] = new(0f, 0.05f), [PhysicsKnob.Length] = new(5f, 11.2f),
            [PhysicsKnob.MaxSpeed] = new(0.5f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 4f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 1f),
        },
        // 8 distinct settings
        ["butt-yas"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.003f, 0.05f), [PhysicsKnob.Damping] = new(0f, 0.075f), [PhysicsKnob.Swing] = new(60f, 89f),
            [PhysicsKnob.Weight] = new(0.1f, 1f), [PhysicsKnob.Follow] = new(0f, 0.05f), [PhysicsKnob.Length] = new(1f, 15f),
            [PhysicsKnob.MaxSpeed] = new(4f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 4f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 1f),
        },
        // 11 distinct settings
        ["belly-ivcs"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.003f, 0.04f), [PhysicsKnob.Damping] = new(0f, 0.1f), [PhysicsKnob.Swing] = new(40f, 180f),
            [PhysicsKnob.Weight] = new(0.1f, 1f), [PhysicsKnob.Follow] = new(0f, 0.05f), [PhysicsKnob.Length] = new(3f, 4.5f),
            [PhysicsKnob.MaxSpeed] = new(1f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 0f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 0f),
        },
        // 8 distinct settings
        ["belly-yas"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.003f, 0.04f), [PhysicsKnob.Damping] = new(0f, 0.12f), [PhysicsKnob.Swing] = new(60f, 180f),
            [PhysicsKnob.Weight] = new(0f, 0.1f), [PhysicsKnob.Follow] = new(0f, 0.05f), [PhysicsKnob.Length] = new(3f, 4.5f),
            [PhysicsKnob.MaxSpeed] = new(1f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 0f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 0f),
        },
        // 10 distinct settings
        ["thighs-ivcs"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.015f, 0.055f), [PhysicsKnob.Damping] = new(0.001f, 0.08f), [PhysicsKnob.Swing] = new(8f, 180f),
            [PhysicsKnob.Weight] = new(0.1f, 1f), [PhysicsKnob.Follow] = new(0f, 0.1f), [PhysicsKnob.Length] = new(3f, 15f),
            [PhysicsKnob.MaxSpeed] = new(4f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 0f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 0.5f),
        },
        // 6 distinct settings
        ["thighs-yas"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.03f, 0.05f), [PhysicsKnob.Damping] = new(0f, 0.07f), [PhysicsKnob.Swing] = new(40f, 180f),
            [PhysicsKnob.Weight] = new(0.1f, 0.1f), [PhysicsKnob.Follow] = new(0f, 0f), [PhysicsKnob.Length] = new(7.2f, 11.2f),
            [PhysicsKnob.MaxSpeed] = new(1f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 0f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(0f, 0.5f),
        },
        // 6 distinct settings
        ["groin-ivcs"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.001f, 0.015f), [PhysicsKnob.Damping] = new(0.025f, 0.08f), [PhysicsKnob.Swing] = new(25f, 180f),
            [PhysicsKnob.Weight] = new(0f, 1f), [PhysicsKnob.Follow] = new(0f, 0f), [PhysicsKnob.Length] = new(4.1f, 20f),
            [PhysicsKnob.MaxSpeed] = new(1f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 0f),
            [PhysicsKnob.Friction] = new(0f, 0.2f), [PhysicsKnob.BumpDamping] = new(0f, 0.01f), [PhysicsKnob.PushBack] = new(0f, 0.5f),
        },
        // 7 distinct settings; too few have a spring for a firmness range
        ["groin-ivcs-shaft"] = new()
        {
            [PhysicsKnob.Damping] = new(0.01f, 0.1f), [PhysicsKnob.Swing] = new(5f, 120f), [PhysicsKnob.Weight] = new(0.01f, 0.1f),
            [PhysicsKnob.Follow] = new(0f, 0.1f), [PhysicsKnob.Length] = new(3.3f, 35f), [PhysicsKnob.MaxSpeed] = new(4f, 5f),
            [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 1f), [PhysicsKnob.Friction] = new(0f, 0.5f),
            [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(1f, 1f),
        },
        // 8 distinct settings
        ["groin-ivcs-soft"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.003f, 0.01f), [PhysicsKnob.Damping] = new(0.05f, 0.1f), [PhysicsKnob.Swing] = new(160f, 180f),
            [PhysicsKnob.Weight] = new(1f, 1f), [PhysicsKnob.Follow] = new(0f, 0f), [PhysicsKnob.Length] = new(4f, 6f),
            [PhysicsKnob.MaxSpeed] = new(1f, 10f), [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 7f),
            [PhysicsKnob.Friction] = new(0f, 0.2f), [PhysicsKnob.BumpDamping] = new(0f, 0.2f), [PhysicsKnob.PushBack] = new(1f, 1f),
        },
        // 11 distinct settings; the game's skirt has no spring
        ["skirt"] = new()
        {
            [PhysicsKnob.Damping] = new(0.075f, 0.075f), [PhysicsKnob.Swing] = new(60f, 60f), [PhysicsKnob.Weight] = new(0.5f, 0.5f),
            [PhysicsKnob.Follow] = new(0.005f, 0.0075f), [PhysicsKnob.Length] = new(35.6f, 50f), [PhysicsKnob.MaxSpeed] = new(4f, 4f),
            [PhysicsKnob.Wind] = new(0.02f, 0.02f), [PhysicsKnob.Size] = new(3f, 6f), [PhysicsKnob.Friction] = new(0f, 0f),
            [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(1f, 1f),
        },
        // 10 distinct settings
        ["hair"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.005f, 0.2f), [PhysicsKnob.Damping] = new(0.125f, 0.125f), [PhysicsKnob.Swing] = new(40f, 45f),
            [PhysicsKnob.Weight] = new(0.5f, 0.5f), [PhysicsKnob.Follow] = new(0f, 0f), [PhysicsKnob.Length] = new(8.9f, 11.5f),
            [PhysicsKnob.MaxSpeed] = new(4f, 4f), [PhysicsKnob.Wind] = new(0.005f, 0.005f), [PhysicsKnob.Size] = new(2.5f, 4f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(1f, 1f),
        },
        // 10 distinct settings
        ["front-hair"] = new()
        {
            [PhysicsKnob.Firmness] = new(0.0025f, 0.0025f), [PhysicsKnob.Damping] = new(0.1f, 0.15f), [PhysicsKnob.Swing] = new(50f, 50f),
            [PhysicsKnob.Weight] = new(1f, 1f), [PhysicsKnob.Follow] = new(0f, 0f), [PhysicsKnob.Length] = new(16f, 21.5f),
            [PhysicsKnob.MaxSpeed] = new(4f, 4f), [PhysicsKnob.Wind] = new(0.0075f, 0.0075f), [PhysicsKnob.Size] = new(2.5f, 4f),
            [PhysicsKnob.Friction] = new(0f, 0f), [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(1f, 1f),
        },
        // 12 distinct settings; the game's tail has no spring
        ["tail"] = new()
        {
            [PhysicsKnob.Damping] = new(0.05f, 0.15f), [PhysicsKnob.Swing] = new(60f, 90f), [PhysicsKnob.Weight] = new(0f, 1f),
            [PhysicsKnob.Follow] = new(0.005f, 1f), [PhysicsKnob.Length] = new(5f, 15f), [PhysicsKnob.MaxSpeed] = new(4f, 4f),
            [PhysicsKnob.Wind] = new(0f, 0f), [PhysicsKnob.Size] = new(0f, 5f), [PhysicsKnob.Friction] = new(0f, 0f),
            [PhysicsKnob.BumpDamping] = new(0f, 0f), [PhysicsKnob.PushBack] = new(1f, 1f),
        },
    };

    /// <summary> The range most presets use for <paramref name="knob"/> on an area, or null when too few presets have the area. </summary>
    public static PhysicsRange? Band(string areaKey, PhysicsKnob knob)
        => Ranges.TryGetValue(areaKey, out var area) && area.TryGetValue(knob, out var range) ? range : null;

    /// <summary> The areas with ranges, for checks. </summary>
    internal static IEnumerable<string> Areas => Ranges.Keys;
}
