using Godot;

namespace CrimsonVR;

/// <summary>
/// Faithful ground generation (grim/terrain_render.py `_generate_texture`):
/// the base game builds the 1024x1024 ground render target by scattering three
/// terrain atlas slots — base, overlay, detail — as rotated 128px patches whose
/// placement comes from an MSVC-CRT rand() LCG seeded with `terrain_seed`.
/// Reproduced here with a one-shot SubViewport: a canvas stamps the exact same
/// sequence (rotation, then Y, then X per patch — the exe's draw order), and
/// the viewport texture becomes the diorama floor (and the surrounding world
/// floor). Regenerated per run: quests carry per-level slots.
/// </summary>
public sealed partial class Diorama
{
    // Tints/clear from grim/terrain_render.py:19-22; the scatter math lives in
    // TerrainGen.cs (pure, golden-tested against the reference).
    private static readonly Color TerrainClearColor = new(63 / 255f, 56 / 255f, 25 / 255f);
    private static readonly Color TerrainBaseTint = new(178 / 255f, 178 / 255f, 178 / 255f, 230 / 255f);
    private static readonly Color TerrainOverlayTint = TerrainBaseTint;
    private static readonly Color TerrainDetailTint = new(178 / 255f, 178 / 255f, 178 / 255f, 153 / 255f);

    private SubViewport? _groundViewport;
    private GroundStampCanvas? _groundCanvas;

    /// <summary>Generate the ground into the shared viewport texture. Returns
    /// null when any slot sheet is missing (caller falls back to tiling).</summary>
    private Texture2D? GenerateGround(in Sim.TerrainInfo info)
    {
        Texture2D? baseTex = LoadTerrainSlot(info.Slot0);
        Texture2D? overlayTex = LoadTerrainSlot(info.Slot1);
        Texture2D? detailTex = LoadTerrainSlot(info.Slot2);
        if (baseTex == null || overlayTex == null || detailTex == null)
        {
            return null;
        }

        int size = Mathf.Max(1, info.TerrainSize);
        if (_groundViewport == null)
        {
            _groundViewport = new SubViewport
            {
                Size = new Vector2I(size, size),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
                RenderTargetClearMode = SubViewport.ClearMode.Once,
                Disable3D = true,
            };
            AddChild(_groundViewport);
            _groundCanvas = new GroundStampCanvas();
            _groundViewport.AddChild(_groundCanvas);
        }

        _groundViewport.Size = new Vector2I(size, size);
        _groundCanvas!.Configure(size, info.TerrainSeed, baseTex, overlayTex, detailTex);
        _groundCanvas.QueueRedraw();
        // Re-render the one-shot target for this generation.
        _groundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        return _groundViewport.GetTexture();
    }

    private Texture2D? LoadTerrainSlot(int slot)
    {
        if (!_terrainSlots.TryGetValue(slot, out string? file))
        {
            return null;
        }
        string path = SpriteDir + file;
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    /// <summary>The 2D canvas that stamps the ground once per generation.</summary>
    private sealed partial class GroundStampCanvas : Node2D
    {
        private int _size = 1024;
        private uint _seed;
        private Texture2D? _base;
        private Texture2D? _overlay;
        private Texture2D? _detail;

        public void Configure(int size, uint seed, Texture2D baseTex, Texture2D overlayTex, Texture2D detailTex)
        {
            _size = size;
            _seed = seed;
            _base = baseTex;
            _overlay = overlayTex;
            _detail = detailTex;
        }

        public override void _Draw()
        {
            if (_base == null || _overlay == null || _detail == null)
            {
                return;
            }
            DrawRect(new Rect2(0.0f, 0.0f, _size, _size), TerrainClearColor);

            var rng = new TerrainGen.CrtRand(_seed);
            Scatter(_base, TerrainBaseTint, ref rng, TerrainGen.DensityBase);
            Scatter(_overlay, TerrainOverlayTint, ref rng, TerrainGen.DensityOverlay);
            Scatter(_detail, TerrainDetailTint, ref rng, TerrainGen.DensityDetail);
            DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
        }

        // One scatter pass: the full slot texture drawn as 128x128 patches
        // rotated about their centres, sequence from TerrainGen (golden-tested).
        private void Scatter(Texture2D tex, Color tint, ref TerrainGen.CrtRand rng, int density)
        {
            int count = TerrainGen.PassCount(_size, density);
            float half = TerrainGen.PatchSize * 0.5f;
            var rect = new Rect2(-half, -half, TerrainGen.PatchSize, TerrainGen.PatchSize);
            for (int i = 0; i < count; i++)
            {
                TerrainGen.Stamp s = TerrainGen.NextStamp(ref rng, _size);
                DrawSetTransform(new Vector2(s.X + half, s.Y + half), s.AngleRad, Vector2.One);
                DrawTextureRect(tex, rect, tile: false, tint);
            }
        }
    }
}
