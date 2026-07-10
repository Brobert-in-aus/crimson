using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

/// <summary>
/// Golden tests for the ground-generator math against the Python reference
/// (grim/rand.py CrtRand + grim/terrain_render.py _scatter_texture). Golden
/// values were computed by running the reference directly:
///   CrtRand(415139642): 26500, 19169, 15724, 11478, 29358, 26962, 24464, 5705
///   CrtRand(1):         41, 18467, 6334, 26500, 19169, 15724, 11478, 29358
/// and replaying the three scatter passes at size 1024 for seed 415139642
/// (the boot session's terrain_seed).
/// </summary>
public class TerrainGenTests
{
    [Fact]
    public void CrtRandMatchesMsvcSequences()
    {
        var r = new TerrainGen.CrtRand(415139642u);
        Assert.Equal(new[] { 26500, 19169, 15724, 11478, 29358, 26962, 24464, 5705 },
            new[] { r.Next(), r.Next(), r.Next(), r.Next(), r.Next(), r.Next(), r.Next(), r.Next() });

        var r1 = new TerrainGen.CrtRand(1u);
        Assert.Equal(new[] { 41, 18467, 6334, 26500, 19169, 15724, 11478, 29358 },
            new[] { r1.Next(), r1.Next(), r1.Next(), r1.Next(), r1.Next(), r1.Next(), r1.Next(), r1.Next() });
    }

    [Fact]
    public void PassCountsMatchReference()
    {
        Assert.Equal(1600, TerrainGen.PassCount(1024, TerrainGen.DensityBase));
        Assert.Equal(70, TerrainGen.PassCount(1024, TerrainGen.DensityOverlay));
        Assert.Equal(30, TerrainGen.PassCount(1024, TerrainGen.DensityDetail));
    }

    [Fact]
    public void StampSequenceMatchesReference()
    {
        var rng = new TerrainGen.CrtRand(415139642u);
        const int size = 1024;

        // Base pass, stamps 0 and 1: (x, y, angle) = (684,673,1.24), (402,494,1.74).
        TerrainGen.Stamp s = TerrainGen.NextStamp(ref rng, size);
        Assert.Equal(684.0f, s.X);
        Assert.Equal(673.0f, s.Y);
        Assert.Equal(1.24f, s.AngleRad, 3);
        s = TerrainGen.NextStamp(ref rng, size);
        Assert.Equal(402.0f, s.X);
        Assert.Equal(494.0f, s.Y);
        Assert.Equal(1.74f, s.AngleRad, 3);

        // Skip to the overlay pass (base = 1600 stamps total).
        for (int i = 2; i < 1600; i++)
        {
            TerrainGen.NextStamp(ref rng, size);
        }
        s = TerrainGen.NextStamp(ref rng, size);
        Assert.Equal(1046.0f, s.X);
        Assert.Equal(724.0f, s.Y);
        Assert.Equal(2.61f, s.AngleRad, 3);

        // Skip to the detail pass (overlay = 70 stamps total).
        for (int i = 1; i < 70; i++)
        {
            TerrainGen.NextStamp(ref rng, size);
        }
        s = TerrainGen.NextStamp(ref rng, size);
        Assert.Equal(678.0f, s.X);
        Assert.Equal(94.0f, s.Y);
        Assert.Equal(2.68f, s.AngleRad, 3);
        // Detail stamp 1 lands partly off-canvas (overscan): x = -27.
        s = TerrainGen.NextStamp(ref rng, size);
        Assert.Equal(-27.0f, s.X);
        Assert.Equal(525.0f, s.Y);
        Assert.Equal(1.22f, s.AngleRad, 3);
    }
}
