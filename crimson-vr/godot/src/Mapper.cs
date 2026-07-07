using Godot;

namespace CrimsonVR;

/// <summary>
/// Pure coordinate mapping between headset world space, arena-local space,
/// and Crimsonland game space. See crimson-vr/PLAN.md §4-§5 for the spec.
/// Game space: origin top-left, x right, y down, side length worldSize.
/// Arena-local space: origin at table center, +x right, +z toward the player
/// ("south" edge), plane at y = 0.
/// </summary>
public static class Mapper
{
    /// <summary>Project a world-space point straight down (world -Y) onto the
    /// horizontal plane at planeY. Controller orientation is ignored by design.</summary>
    public static Vector3 ProjectVertically(Vector3 worldPoint, float planeY)
        => new(worldPoint.X, planeY, worldPoint.Z);

    /// <summary>Arena-local (meters, origin at center) to game coordinates,
    /// clamped to the playfield. Game +y maps to arena +z.</summary>
    public static Vector2 ArenaLocalToGame(Vector3 arenaLocal, float arenaSideMeters, float worldSize)
    {
        float k = worldSize / arenaSideMeters;
        float gx = (arenaLocal.X + arenaSideMeters * 0.5f) * k;
        float gy = (arenaLocal.Z + arenaSideMeters * 0.5f) * k;
        return new Vector2(
            Mathf.Clamp(gx, 0.0f, worldSize),
            Mathf.Clamp(gy, 0.0f, worldSize));
    }

    /// <summary>Game coordinates to arena-local meters (y = 0 plane).</summary>
    public static Vector3 GameToArenaLocal(Vector2 game, float arenaSideMeters, float worldSize)
    {
        float k = arenaSideMeters / worldSize;
        return new Vector3(
            game.X * k - arenaSideMeters * 0.5f,
            0.0f,
            game.Y * k - arenaSideMeters * 0.5f);
    }

    /// <summary>True when the world point hovers within the arena footprint
    /// (before clamping).</summary>
    public static bool IsOverArena(Vector3 arenaLocal, float arenaSideMeters)
        => Mathf.Abs(arenaLocal.X) <= arenaSideMeters * 0.5f
           && Mathf.Abs(arenaLocal.Z) <= arenaSideMeters * 0.5f;
}
