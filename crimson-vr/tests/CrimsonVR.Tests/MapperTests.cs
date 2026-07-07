using CrimsonVR;
using Godot;
using Xunit;

namespace CrimsonVR.Tests;

public class MapperTests
{
    private const float Side = 1.0f;
    private const float World = 1024.0f;

    [Fact]
    public void ProjectVertically_FlattensToPlane()
    {
        Vector3 hit = Mapper.ProjectVertically(new Vector3(0.3f, 1.4f, -0.2f), planeY: 0.75f);
        Assert.Equal(0.3f, hit.X, 5);
        Assert.Equal(0.75f, hit.Y, 5);
        Assert.Equal(-0.2f, hit.Z, 5);
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
}
