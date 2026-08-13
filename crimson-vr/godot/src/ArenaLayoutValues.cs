using Godot;

namespace CrimsonVR;

/// <summary>Pure value mapping for the player-facing Arena &amp; Layout controls.
/// Kept outside the node tree so direction/default regressions are unit-testable.</summary>
public static class ArenaLayoutValues
{
    private const float HeightReferenceMeters = 1.0f;

    public static float SnapTabletopScale(float scale)
        => scale < 0.875f ? 0.75f : scale < 1.125f ? 1.0f : 1.25f;

    /// <summary>Convert the legacy head-relative downward drop to the displayed
    /// floor-up height: 0 m is floor and 1 m is one metre above it.</summary>
    public static float HeightFromDrop(float drop)
        => Mathf.Clamp(HeightReferenceMeters - drop, 0.0f, HeightReferenceMeters);

    public static float DropFromHeight(float height)
        => Mathf.Clamp(HeightReferenceMeters - height, 0.0f, HeightReferenceMeters);
}
