using Godot;

namespace CrimsonVR;

/// <summary>
/// Pure coordinate mapping between headset world space, square-plane-local
/// space, and Crimsonland game space. See crimson-vr/PLAN.md §4-§5 for the spec.
/// Game space: origin top-left, x right, y down, side length worldSize.
/// Plane-local space: origin at the square's center, +x right, +z AWAY from the
/// player (the far edge), plane at y = 0. (The header previously said +z was
/// toward the player; the recenter yaw makes local +z the head's forward, and
/// both Diorama's edge labels and the Hud's far-edge anchor agree it is far.)
/// Two such planes exist: the CONTROL
/// rectangle the player's hands hover over, and the ARENA the playfield is drawn
/// on. They share this mapping but no longer share a transform (see Main).
/// </summary>
public static class Mapper
{
    /// <summary>Drop the plane-normal (local +y) component of a plane-local
    /// point, projecting it onto the square's surface. Callers pass a point
    /// already in plane-local space, so a tilted plane projects along its own
    /// normal and the mapping stays 1:1 with the rectangle the player sees;
    /// for a level plane this is exactly a straight-down world projection.
    /// Controller orientation is ignored by design — hand POSITION is the
    /// input, so the projection never depends on how the controller is held.</summary>
    public static Vector3 FlattenToPlane(Vector3 planeLocal)
        => new(planeLocal.X, 0.0f, planeLocal.Z);

    /// <summary>Plane-local (meters, origin at center) to game coordinates,
    /// clamped to the playfield. Game +y maps to plane +z.</summary>
    public static Vector2 ArenaLocalToGame(Vector3 arenaLocal, float arenaSideMeters, float worldSize)
    {
        float k = worldSize / arenaSideMeters;
        float gx = (arenaLocal.X + arenaSideMeters * 0.5f) * k;
        float gy = (arenaLocal.Z + arenaSideMeters * 0.5f) * k;
        return new Vector2(
            Mathf.Clamp(gx, 0.0f, worldSize),
            Mathf.Clamp(gy, 0.0f, worldSize));
    }

    /// <summary>Plane-local (meters, origin at center) to game coordinates
    /// WITHOUT clamping — may fall outside 0..worldSize when the hand is off
    /// the plane. Pair with <see cref="ClampGameTowards"/>. This is the control
    /// path's map: it is fed CONTROL-rectangle local coords and its rect side,
    /// so hand travel is sized by the control rectangle, never by how large the
    /// arena happens to be drawn.</summary>
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
