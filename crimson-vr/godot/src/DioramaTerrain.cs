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
    // A second scatter-only RT with NO bakes: the surrounding world floor and
    // the arena's margin band sample this, so blood/corpses never mirror
    // outside the playable zone (the baked RT is playfield-only, like native).
    private SubViewport? _groundCleanViewport;
    private GroundStampCanvas? _groundCleanCanvas;
    private Texture2D? _cleanGroundTex;
    private Texture2D? _bakeParticles; // decal art (effect atlas)
    private Texture2D? _bakeBodyset;   // corpse frames (4x4)

    /// <summary>The bake-free ground texture for surfaces OUTSIDE the playable
    /// zone (world floor, margin band); null until generated.</summary>
    public Texture2D? CleanGroundTexture => _cleanGroundTex;

    /// <summary>True when terrain FX can bake permanently into the ground RT
    /// (native behavior) instead of the ring-buffered quad fallback.</summary>
    private bool GroundBakeReady => _groundCanvas != null && _groundViewport != null && _bakeParticles != null;

    /// <summary>Queue one blood/scorch decal into the ground RT
    /// (terrain_render.py bake_decals: centre-origin quad, alpha blend).</summary>
    private void BakeDecal(in Sim.TerrainDecalSnap d)
    {
        if (_bakeParticles == null || !_effectUv.TryGetValue(d.EffectId, out Vector3 uv))
        {
            return;
        }
        float tw = _bakeParticles.GetWidth();
        float th = _bakeParticles.GetHeight();
        var src = new Rect2(uv.X * tw, uv.Y * th, uv.Z * tw, uv.Z * th);
        _groundCanvas!.Enqueue(new BakedStamp(_bakeParticles, src,
            new Vector2(d.X, d.Y), new Vector2(d.Width, d.Height), d.Rotation,
            new Color(d.R, d.G, d.B, d.A)));
    }

    private Rect2 CorpseSrc(int frame)
    {
        float cw = _bakeBodyset!.GetWidth() * 0.25f;
        float ch = _bakeBodyset.GetHeight() * 0.25f;
        frame &= 0xF;
        return new Rect2(frame % 4 * cw, frame / 4 * ch, cw, ch);
    }

    /// <summary>Queue a corpse SHADOW stamp (terrain_render.py
    /// _draw_corpse_shadow_pass): the native ZERO/INV_SRC_ALPHA darken equals
    /// alpha-blending BLACK at the sprite's alpha x 0.5 — size x1.064,
    /// offset -0.5px, rotated heading - 90 deg.</summary>
    private void BakeCorpseShadow(in Sim.TerrainCorpseSnap c, int frame)
    {
        float size = c.Scale * 1.064f;
        _groundCanvas!.Enqueue(new BakedStamp(_bakeBodyset!, CorpseSrc(frame),
            new Vector2(c.X - 0.5f + size * 0.5f, c.Y - 0.5f + size * 0.5f),
            new Vector2(size, size), c.Rotation - Mathf.Pi * 0.5f,
            new Color(0.0f, 0.0f, 0.0f, c.A * 0.5f)));
    }

    /// <summary>Queue a corpse COLOR stamp (_draw_corpse_color_pass).</summary>
    private void BakeCorpseColor(in Sim.TerrainCorpseSnap c, int frame)
    {
        _groundCanvas!.Enqueue(new BakedStamp(_bakeBodyset!, CorpseSrc(frame),
            new Vector2(c.X + c.Scale * 0.5f, c.Y + c.Scale * 0.5f),
            new Vector2(c.Scale, c.Scale), c.Rotation - Mathf.Pi * 0.5f,
            new Color(c.R, c.G, c.B, c.A)));
    }

    /// <summary>Re-render the ground RT with the newly queued bakes.</summary>
    private void FlushGroundBakes()
    {
        if (_groundCanvas != null && _groundViewport != null)
        {
            _groundCanvas.QueueRedraw();
            _groundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
    }

    /// <summary>An Android app-resume can recreate the render targets (their
    /// contents are lost while the headset sleeps); replay the full ground
    /// state — scatter plus every accumulated stamp — into the fresh RTs.</summary>
    public override void _Notification(int what)
    {
        if (what != NotificationApplicationResumed)
        {
            return;
        }
        if (_groundCanvas != null && _groundViewport != null)
        {
            _groundCanvas.InvalidateBake();
            _groundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
        if (_groundCleanCanvas != null && _groundCleanViewport != null)
        {
            _groundCleanCanvas.QueueRedraw();
            _groundCleanViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
    }

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

        if (_groundCleanViewport == null)
        {
            _groundCleanViewport = new SubViewport
            {
                Size = new Vector2I(size, size),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
                Disable3D = true,
            };
            AddChild(_groundCleanViewport);
            _groundCleanCanvas = new GroundStampCanvas();
            _groundCleanViewport.AddChild(_groundCleanCanvas);
        }

        _groundViewport.Size = new Vector2I(size, size);
        _groundCleanViewport.Size = new Vector2I(size, size);
        _groundCanvas!.Configure(size, info.TerrainSeed, baseTex, overlayTex, detailTex);
        _groundCleanCanvas!.Configure(size, info.TerrainSeed, baseTex, overlayTex, detailTex);
        _groundCanvas.QueueRedraw();
        _groundCleanCanvas.QueueRedraw();
        // Re-render the one-shot targets for this generation.
        _groundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        _groundCleanViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        _cleanGroundTex = _groundCleanViewport.GetTexture();
        _bakeParticles ??= LoadSprite("particles");
        _bakeBodyset ??= LoadSprite("bodyset");
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

    /// <summary>One decal/corpse stamp queued for a permanent bake into the
    /// ground RT (positions/sizes in ground pixels = game units).</summary>
    private readonly struct BakedStamp
    {
        public readonly Texture2D Tex;
        public readonly Rect2 Src;      // source region in texture pixels
        public readonly Vector2 Center; // ground px
        public readonly Vector2 Size;   // ground px
        public readonly float Rotation; // radians, about the centre
        public readonly Color Tint;

        public BakedStamp(Texture2D tex, Rect2 src, Vector2 center, Vector2 size, float rotation, Color tint)
        {
            Tex = tex;
            Src = src;
            Center = center;
            Size = size;
            Rotation = rotation;
            Tint = tint;
        }
    }

    /// <summary>The 2D canvas that stamps the ground. The render-target buffer
    /// persists between one-shot renders (ClearMode.Once), so a redraw only
    /// needs to record the stamps not yet in the RT: the base (clear + scatter)
    /// is drawn when _pendingStart == 0 (new run / resume), and per-kill
    /// flushes record just the NEW stamps — the previous full-state redraw made
    /// every bloody tick cost O(entire run's stamps), a measurable frame spike
    /// late in a run. Pending stamps are only retired once a frame has actually
    /// rendered the recorded list (FramePostDraw): a consume-once flag here
    /// previously left an EMPTY command list when _Draw ran twice before the
    /// one-shot render, wiping the ground to grey.</summary>
    private sealed partial class GroundStampCanvas : Node2D
    {
        // Far beyond any realistic run (the old quad rings were 4096 + 1024);
        // a hard FIFO cap so a marathon can't grow the list unbounded. With the
        // incremental path the full list is only replayed on a resume redraw.
        private const int BakedCap = 24000;

        private int _size = 1024;
        private uint _seed;
        private Texture2D? _base;
        private Texture2D? _overlay;
        private Texture2D? _detail;
        private readonly System.Collections.Generic.List<BakedStamp> _baked = new();
        private int _pendingStart;         // first stamp not yet rendered into the RT
        private int _recordedThrough = -1; // stamp count captured by the last _Draw, -1 = none

        public override void _EnterTree() => RenderingServer.FramePostDraw += CommitRendered;

        public override void _ExitTree() => RenderingServer.FramePostDraw -= CommitRendered;

        /// <summary>The frame finished rendering, so the command list recorded
        /// by the last _Draw is in the render target; those stamps never need
        /// recording again (until a full invalidation).</summary>
        private void CommitRendered()
        {
            if (_recordedThrough >= 0)
            {
                _pendingStart = _recordedThrough;
                _recordedThrough = -1;
            }
        }

        /// <summary>Force the next redraw to record the full state (clear +
        /// scatter + all stamps). Needed when the RT contents are gone: a new
        /// run's Configure, or an Android app-resume that recreated the RT.</summary>
        public void InvalidateBake()
        {
            _pendingStart = 0;
            _recordedThrough = -1;
            QueueRedraw();
        }

        public void Configure(int size, uint seed, Texture2D baseTex, Texture2D overlayTex, Texture2D detailTex)
        {
            _size = size;
            _seed = seed;
            _base = baseTex;
            _overlay = overlayTex;
            _detail = detailTex;
            _baked.Clear();
            _pendingStart = 0;
            _recordedThrough = -1;
        }

        public void Enqueue(in BakedStamp stamp)
        {
            _baked.Add(stamp);
            if (_baked.Count > BakedCap)
            {
                int drop = _baked.Count - BakedCap;
                _baked.RemoveRange(0, drop);
                _pendingStart = System.Math.Max(0, _pendingStart - drop);
                if (_recordedThrough >= 0)
                {
                    _recordedThrough = System.Math.Max(0, _recordedThrough - drop);
                }
            }
        }

        public override void _Draw()
        {
            if (_base == null || _overlay == null || _detail == null)
            {
                return;
            }
            if (_pendingStart == 0)
            {
                DrawRect(new Rect2(0.0f, 0.0f, _size, _size), TerrainClearColor);
                var rng = new TerrainGen.CrtRand(_seed);
                Scatter(_base, TerrainBaseTint, ref rng, TerrainGen.DensityBase);
                Scatter(_overlay, TerrainOverlayTint, ref rng, TerrainGen.DensityOverlay);
                Scatter(_detail, TerrainDetailTint, ref rng, TerrainGen.DensityDetail);
            }
            for (int i = _pendingStart; i < _baked.Count; i++)
            {
                BakedStamp s = _baked[i];
                DrawSetTransform(s.Center, s.Rotation, Vector2.One);
                DrawTextureRectRegion(s.Tex,
                    new Rect2(-s.Size * 0.5f, s.Size), s.Src, s.Tint);
            }
            DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
            _recordedThrough = _baked.Count;
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
