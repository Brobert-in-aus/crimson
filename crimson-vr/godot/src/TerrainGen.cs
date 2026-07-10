namespace CrimsonVR;

/// <summary>
/// Pure ground-generation math (grim/terrain_render.py + grim/rand.py),
/// separated from the Godot canvas so the golden tests can pin it against the
/// reference. The base game scatters three terrain slots over the 1024x1024
/// ground with an MSVC-CRT rand() LCG seeded by terrain_seed; per patch the
/// exe consumes RNG as ROTATION, then Y, then X.
/// </summary>
public static class TerrainGen
{
    // grim/terrain_render.py:17-27 (exe constants).
    public const float PatchSize = 128.0f;
    public const float PatchOverscan = 64.0f;
    public const int DensityBase = 800;
    public const int DensityOverlay = 0x23;
    public const int DensityDetail = 0x0F;
    public const int DensityShift = 19;
    public const int RotationMax = 0x13A;

    /// <summary>MSVCRT-compatible rand(): seed = seed*214013+2531011,
    /// return (seed &gt;&gt; 16) &amp; 0x7fff — grim/rand.py CrtRand.</summary>
    public struct CrtRand
    {
        private uint _state;
        public CrtRand(uint seed) => _state = seed;

        public int Next()
        {
            _state = _state * 214013u + 2531011u;
            return (int)((_state >> 16) & 0x7FFF);
        }
    }

    /// <summary>One scattered patch: top-left corner (game px, may be negative
    /// by up to the overscan) and rotation about the patch centre.</summary>
    public readonly struct Stamp
    {
        public readonly float X;
        public readonly float Y;
        public readonly float AngleRad;

        public Stamp(float x, float y, float angleRad)
        {
            X = x;
            Y = y;
            AngleRad = angleRad;
        }
    }

    /// <summary>Patch count for one scatter pass: (area * density) >> 19.</summary>
    public static int PassCount(int size, int density)
        => (int)(((long)size * size * density) >> DensityShift);

    /// <summary>Consume one patch from the RNG (rotation, then Y, then X — the
    /// exe's draw order).</summary>
    public static Stamp NextStamp(ref CrtRand rng, int size)
    {
        int span = size + (int)(PatchOverscan * 2.0f);
        float angle = rng.Next() % RotationMax * 0.01f % (2.0f * System.MathF.PI);
        float y = rng.Next() % span - PatchOverscan;
        float x = rng.Next() % span - PatchOverscan;
        return new Stamp(x, y, angle);
    }
}
