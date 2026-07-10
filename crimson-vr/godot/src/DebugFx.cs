namespace CrimsonVR;

/// <summary>
/// Runtime debug FX force-toggles, flipped from the in-headset DebugMenu while
/// the debug setting is on. All presentation-side only: they force the render
/// passes on regardless of sim state (perks, timers, wire flags), so effects
/// can be eyeballed on demand — pause, toggle, look — without relaunching or
/// grinding for the triggering pickup/perk. The sim is never touched; the one
/// sim-side debug behavior left is the weapon cycle on reload
/// (debug_fx_showcase, session-create flag).
/// </summary>
public static class DebugFx
{
    /// <summary>Sharpshooter laser sight along the aim heading.</summary>
    public static bool LaserSight;

    /// <summary>Radioactive green aura under the player.</summary>
    public static bool RadioactiveAura;

    /// <summary>Counter-rotating shield-ring pair (as if a shield were up).</summary>
    public static bool ShieldRing;

    /// <summary>Monster-vision yellow aura over every creature.</summary>
    public static bool MonsterVision;

    /// <summary>Poison/plague auras painted on 1-in-10 creatures.</summary>
    public static bool CreatureAuras;
}
