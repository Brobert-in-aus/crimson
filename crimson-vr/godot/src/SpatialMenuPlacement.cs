using Godot;

namespace CrimsonVR;

/// <summary>Single placement contract for player-facing spatial UI.
///
/// ArenaRoot is recentered 0.30 m in front of the player. The original menu
/// plane added 0.10 m (0.25 of the 0.40 m reference side); headset testing
/// found that plane too close. Keep every menu, perk offer, keyboard and result
/// screen on the same plane, 0.15 m farther away, so transitions never move a
/// newly-visible poke target toward a hand that was already in the old screen.
/// </summary>
public static class SpatialMenuPlacement
{
    public const float DefaultAdditionalDistanceMeters = 0.15f;
    public const float MinimumAdditionalDistanceMeters = 0.05f;
    public const float MaximumAdditionalDistanceMeters = 0.45f;
    private const float BaseDepthFactor = 0.25f;

    public static Vector3 PlayerFacing(float referenceSideMeters, float heightFactor = 0.85f,
        float horizontalOffset = 0.0f, float additionalDistanceMeters = DefaultAdditionalDistanceMeters)
        => new(horizontalOffset, referenceSideMeters * heightFactor,
            referenceSideMeters * BaseDepthFactor + ClampAdditionalDistance(additionalDistanceMeters));

    public static float ClampAdditionalDistance(float distanceMeters)
        => Mathf.Clamp(distanceMeters, MinimumAdditionalDistanceMeters, MaximumAdditionalDistanceMeters);

    /// <summary>Apply only recenter-local depth from a layout grab.</summary>
    public static float AdjustAdditionalDistance(float startingDistanceMeters, Vector3 localGrabDelta)
        => ClampAdditionalDistance(startingDistanceMeters + localGrabDelta.Z);
}
