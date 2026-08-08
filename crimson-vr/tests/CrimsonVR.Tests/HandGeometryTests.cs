using CrimsonVR;
using Godot;
using Xunit;

namespace CrimsonVR.Tests;

/// <summary>
/// Twist extraction for two-handed widget rotation. This is the sixth degree of
/// freedom — hand POSITIONS pin only two rotational axes, so roll about the
/// hand-to-hand line comes from how far the controllers themselves rolled.
///
/// Worth testing rather than eyeballing: a twist with the sign flipped behaves
/// exactly like a working one until you try to roll something, and an axis
/// convention that is subtly wrong only shows up at particular hand poses.
/// </summary>
public class HandGeometryTests
{
    private const float Tol = 1e-3f;

    [Theory]
    [InlineData(30.0f)]
    [InlineData(-30.0f)]
    [InlineData(90.0f)]
    [InlineData(-120.0f)]
    public void TwistAngle_RecoversRotationAboutTheAxis(float degrees)
    {
        // A pure roll about the axis must come back as exactly that roll,
        // right-handed and signed.
        var axis = new Vector3(0.0f, 0.0f, 1.0f);
        var delta = new Basis(axis, Mathf.DegToRad(degrees));
        Assert.Equal(Mathf.DegToRad(degrees), HandGeometry.TwistAngle(delta, axis), 3);
    }

    [Theory]
    [InlineData(0.0f, 1.0f, 0.0f)]
    [InlineData(1.0f, 0.0f, 0.0f)]
    [InlineData(0.577f, 0.577f, 0.577f)]
    public void TwistAngle_WorksOnAnyAxisIncludingTheUpDegenerate(float x, float y, float z)
    {
        // The perpendicular reference is built by crossing with Up, which is
        // degenerate when the axis IS Up — the fallback has to hold there, since
        // a vertically-separated pair of hands is a completely ordinary grab.
        var axis = new Vector3(x, y, z).Normalized();
        var delta = new Basis(axis, Mathf.DegToRad(45.0f));
        Assert.Equal(Mathf.DegToRad(45.0f), HandGeometry.TwistAngle(delta, axis), 3);
    }

    [Fact]
    public void TwistAngle_IgnoresRotationPerpendicularToTheAxis()
    {
        // Swinging the hand-to-hand line is already handled by the swing term.
        // If twist double-counted it the widget would spin while merely being
        // aimed, so a pure perpendicular rotation must read as zero twist.
        var axis = new Vector3(0.0f, 0.0f, 1.0f);
        var delta = new Basis(new Vector3(0.0f, 0.0f, 1.0f).Cross(Vector3.Up).Normalized(),
            Mathf.DegToRad(20.0f));
        Assert.Equal(0.0f, HandGeometry.TwistAngle(delta, axis), 3);
    }

    [Fact]
    public void TwistAngle_IsZeroForNoRotation()
    {
        Assert.Equal(0.0f, HandGeometry.TwistAngle(Basis.Identity, Vector3.Up), 5);
    }

    [Fact]
    public void TwistAngle_IsZeroForADegenerateAxis()
    {
        // Both hands on the same point: no axis to roll about, and no NaN.
        float twist = HandGeometry.TwistAngle(new Basis(Vector3.Up, 1.0f), Vector3.Zero);
        Assert.Equal(0.0f, twist, 5);
        Assert.False(float.IsNaN(twist));
    }

    [Fact]
    public void TwistAngle_SignIsRightHandedAboutTheGivenAxis()
    {
        // Reversing the axis must reverse the measured twist: the two hands are
        // interchangeable, so the caller's axis direction is what decides which
        // way "positive" points.
        var axis = new Vector3(0.0f, 1.0f, 0.0f);
        var delta = new Basis(axis, Mathf.DegToRad(35.0f));
        float forward = HandGeometry.TwistAngle(delta, axis);
        float reversed = HandGeometry.TwistAngle(delta, -axis);
        Assert.Equal(forward, -reversed, 3);
    }
}
