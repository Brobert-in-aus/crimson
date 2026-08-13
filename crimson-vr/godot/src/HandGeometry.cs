using Godot;

namespace CrimsonVR;

/// <summary>
/// Pure geometry for two-handed manipulation. Kept free of any node type so it
/// can be unit-tested: these are the parts of the gesture where a sign error or
/// a bad axis convention behaves exactly like working code right up until the
/// moment someone tries the gesture in a headset.
/// </summary>
public static class HandGeometry
{
    /// <summary>Signed rotation of <paramref name="delta"/> about
    /// <paramref name="axis"/>, right-handed, in radians.
    ///
    /// This is the TWIST half of a swing-twist split. Two hands define a line,
    /// which pins only two rotational axes; roll about that line has to come
    /// from how far the controllers themselves rolled. Done geometrically —
    /// carry a perpendicular reference through the rotation, flatten it back
    /// onto the plane, read the signed angle — because pulling an angle out of
    /// a quaternion invites sign and wrap mistakes that are invisible until a
    /// specific pose hits them.
    ///
    /// Returns 0 for a degenerate axis (both hands at one point) rather than
    /// NaN, and for a rotation with no component about the axis.</summary>
    public static float TwistAngle(Basis delta, Vector3 axis)
    {
        if (axis.LengthSquared() < 1e-8f)
        {
            return 0.0f;
        }
        axis = axis.Normalized();

        // Any vector perpendicular to the axis works as the reference. Crossing
        // with Up is degenerate when the axis IS up — which is an ordinary grab
        // (one hand above the other), not an edge case — so fall back then.
        Vector3 perp = axis.Cross(Vector3.Up);
        if (perp.LengthSquared() < 1e-6f)
        {
            perp = axis.Cross(Vector3.Right);
        }
        perp = perp.Normalized();

        Vector3 rotated = delta * perp;
        rotated -= axis * rotated.Dot(axis);   // flatten onto the plane
        if (rotated.LengthSquared() < 1e-8f)
        {
            return 0.0f;
        }
        rotated = rotated.Normalized();
        return Mathf.Atan2(axis.Dot(perp.Cross(rotated)), perp.Dot(rotated));
    }

    /// <summary>Map controller roll to the page rotation used by a grabbed
    /// diegetic widget. The widget faces back toward the player, so its visual
    /// forward/back pitch has the opposite right-handed sign to the controller
    /// delta around the left-to-right hand axis.</summary>
    public static float DirectManipulationTwistAngle(Basis delta, Vector3 axis)
        => -TwistAngle(delta, axis);
}
