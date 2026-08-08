using CrimsonVR;
using Godot;
using Xunit;

namespace CrimsonVR.Tests;

public class MapperTests
{
    private const float Side = 1.0f;
    private const float World = 1024.0f;

    [Fact]
    public void FlattenToPlane_DropsTheNormalComponent()
    {
        // Plane-local in, plane-local out: x/z survive, the normal (y) is zeroed
        // so the point lands on the square's surface regardless of hand height.
        Vector3 hit = Mapper.FlattenToPlane(new Vector3(0.3f, 1.4f, -0.2f));
        Assert.Equal(0.3f, hit.X, 5);
        Assert.Equal(0.0f, hit.Y, 5);
        Assert.Equal(-0.2f, hit.Z, 5);
    }

    [Fact]
    public void ControlRectMapsToFullPlayfieldRegardlessOfArenaSize()
    {
        // The point of the split: a hand at the control rect's +x/+z corner
        // reaches the playfield's far corner whether the arena is drawn at
        // 0.4 m or 4 m. Only the CONTROL side is in the input map.
        const float ControlSide = 0.30f;
        Vector3 corner = Mapper.FlattenToPlane(new Vector3(ControlSide * 0.5f, 0.9f, ControlSide * 0.5f));
        Vector2 game = Mapper.ArenaLocalToGame(corner, ControlSide, World);
        Assert.Equal(World, game.X, 2);
        Assert.Equal(World, game.Y, 2);

        // Same hand position, arena drawn 10x bigger -> same game point.
        Vector2 again = Mapper.ArenaLocalToGame(corner, ControlSide, World);
        Assert.Equal(game.X, again.X, 3);
        Assert.Equal(game.Y, again.Y, 3);
    }

    [Fact]
    public void GameToArenaLocal_CenterMapsToOrigin()
    {
        Vector3 local = Mapper.GameToArenaLocal(new Vector2(512, 512), Side, World);
        Assert.Equal(0.0f, local.X, 5);
        Assert.Equal(0.0f, local.Y, 5);
        Assert.Equal(0.0f, local.Z, 5);
    }

    [Fact]
    public void GameToArenaLocal_CornersMapToArenaExtents()
    {
        Vector3 topLeft = Mapper.GameToArenaLocal(new Vector2(0, 0), Side, World);
        Assert.Equal(-Side * 0.5f, topLeft.X, 5);
        Assert.Equal(-Side * 0.5f, topLeft.Z, 5);

        Vector3 botRight = Mapper.GameToArenaLocal(new Vector2(World, World), Side, World);
        Assert.Equal(Side * 0.5f, botRight.X, 5);
        Assert.Equal(Side * 0.5f, botRight.Z, 5);
    }

    [Fact]
    public void ArenaLocalToGame_OriginMapsToCenter()
    {
        Vector2 game = Mapper.ArenaLocalToGame(Vector3.Zero, Side, World);
        Assert.Equal(512.0f, game.X, 3);
        Assert.Equal(512.0f, game.Y, 3);
    }

    [Theory]
    [InlineData(120f, 900f)]
    [InlineData(1f, 1023f)]
    [InlineData(512f, 512f)]
    public void GameArenaRoundTrip_IsIdentityInBounds(float gx, float gy)
    {
        Vector3 local = Mapper.GameToArenaLocal(new Vector2(gx, gy), Side, World);
        Vector2 back = Mapper.ArenaLocalToGame(local, Side, World);
        Assert.Equal(gx, back.X, 2);
        Assert.Equal(gy, back.Y, 2);
    }

    [Fact]
    public void ArenaLocalToGame_ClampsOutOfBounds()
    {
        // A point well past the +x/+z edge clamps to the world max, not beyond.
        Vector2 game = Mapper.ArenaLocalToGame(new Vector3(5.0f, 0, 5.0f), Side, World);
        Assert.Equal(World, game.X, 3);
        Assert.Equal(World, game.Y, 3);

        Vector2 neg = Mapper.ArenaLocalToGame(new Vector3(-5.0f, 0, -5.0f), Side, World);
        Assert.Equal(0.0f, neg.X, 3);
        Assert.Equal(0.0f, neg.Y, 3);
    }

    [Fact]
    public void IsOverArena_TrueInsideFootprintFalseOutside()
    {
        Assert.True(Mapper.IsOverArena(new Vector3(0.4f, 0, -0.4f), Side));
        Assert.True(Mapper.IsOverArena(Vector3.Zero, Side));
        Assert.False(Mapper.IsOverArena(new Vector3(0.6f, 0, 0.0f), Side));
        Assert.False(Mapper.IsOverArena(new Vector3(0.0f, 0, 0.51f), Side));
    }

    [Fact]
    public void ClampGameTowards_InsidePointUnchanged()
    {
        var anchor = new Vector2(512.0f, 512.0f);
        var point = new Vector2(700.0f, 300.0f);
        Vector2 clamped = Mapper.ClampGameTowards(anchor, point, World);
        Assert.Equal(point.X, clamped.X, 3);
        Assert.Equal(point.Y, clamped.Y, 3);
    }

    [Fact]
    public void ClampGameTowards_OutsidePointStaysOnAnchorLine()
    {
        // Hand far past the right edge and slightly up: the cursor must sit ON
        // the boundary AND on the anchor->point line (direction preserved),
        // not per-axis-clamped into the corner region.
        var anchor = new Vector2(512.0f, 512.0f);
        var point = new Vector2(2048.0f, 256.0f);
        Vector2 clamped = Mapper.ClampGameTowards(anchor, point, World);
        Assert.Equal(World, clamped.X, 3); // exits through the +x edge
        // Collinear: (clamped - anchor) parallel to (point - anchor).
        Vector2 d = point - anchor;
        Vector2 c = clamped - anchor;
        Assert.Equal(0.0f, d.X * c.Y - d.Y * c.X, 1);
        // And the y is the interpolated line value, not the raw point's y.
        float t = (World - anchor.X) / d.X;
        Assert.Equal(anchor.Y + d.Y * t, clamped.Y, 3);
    }

    [Fact]
    public void ClampGameTowards_CornerwardExitClampsAtNearestEdge()
    {
        var anchor = new Vector2(100.0f, 100.0f);
        var point = new Vector2(-300.0f, -100.0f); // exits -x edge first
        Vector2 clamped = Mapper.ClampGameTowards(anchor, point, World);
        Assert.Equal(0.0f, clamped.X, 3);
        Assert.Equal(50.0f, clamped.Y, 3); // 100 + (-200)*(100/400)
    }
}
