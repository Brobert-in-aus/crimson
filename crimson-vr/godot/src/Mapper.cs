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

    /// <summary>Arena-local (meters, origin at center) to game coordinates
    /// WITHOUT clamping — may fall outside 0..worldSize when the hand is off
    /// the playfield. Pair with <see cref="ClampGameTowards"/>.</summary>
    public static Vector2 ArenaLocalToGameUnclamped(Vector3 arenaLocal, float arenaSideMeters, float worldSize)
    {
        float k = worldSize / arenaSideMeters;
        return new Vector2(
            (arenaLocal.X + arenaSideMeters * 0.5f) * k,
            (arenaLocal.Z + arenaSideMeters * 0.5f) * k);
    }

    /// <summary>Clamp a (possibly outside) game point to the playfield along
    /// the LINE from <paramref name="anchor"/> (the player character) toward
    /// it, rather than per-axis: an off-arena hand keeps the cursor on the
    /// player-to-hand line at the boundary, preserving the aim direction
    /// (per-axis clamping dragged it sideways into the nearest corner).</summary>
    public static Vector2 ClampGameTowards(Vector2 anchor, Vector2 point, float worldSize)
    {
        anchor = new Vector2(
            Mathf.Clamp(anchor.X, 0.0f, worldSize),
            Mathf.Clamp(anchor.Y, 0.0f, worldSize));
        Vector2 d = point - anchor;
        const float Eps = 1e-6f;
        float t = 1.0f;
        if (d.X > Eps)
        {
            t = Mathf.Min(t, (worldSize - anchor.X) / d.X);
        }
        else if (d.X < -Eps)
        {
            t = Mathf.Min(t, -anchor.X / d.X);
        }
        if (d.Y > Eps)
        {
            t = Mathf.Min(t, (worldSize - anchor.Y) / d.Y);
        }
        else if (d.Y < -Eps)
        {
            t = Mathf.Min(t, -anchor.Y / d.Y);
        }
        return anchor + d * Mathf.Max(t, 0.0f);
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
