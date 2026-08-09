using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Diorama renderer. M3: creatures and the player are drawn as real Crimsonland
/// sprites from their 8x8 sheets (PLAN §6), one MultiMeshInstance3D per creature
/// type so each can carry its own sheet texture; projectiles/secondaries/bonuses
/// remain colored quads for now. Orientation comes from each entity's heading (a
/// yaw applied per instance). Creatures are ANIMATED (slice 2): a sprite shader
/// selects the 8x8 frame per instance from the snapshot's anim_phase + flags
/// (CreatureAnim.SelectFrame) via MultiMesh custom data. The player is a static
/// torso frame (leg animation needs a move-phase ABI field; deferred).
///
/// 2.5D (slice 5, PLAN §6): sprites get a fixed back-tilt toward the player so
/// they read as "standing" from a low angle, and a shared soft drop-shadow blob
/// grounds each creature/player on the plane. Projectiles/secondaries are
/// additive per-type glow streaks with muzzle/explosion bursts (slice 6a);
/// sprite-effect particles (blood/gibs/explosions/casings from the ABI v2
/// particle stream) draw from particles.png over the world (slice 6b).
///
/// Sprites/manifest are produced by crimson-vr/tools/bake_assets.py into
/// res://assets/sprites/ (gitignored). If they're absent the renderer falls
/// back to colored quads, so the project still runs without user assets.
///
/// This node is a child of ArenaRoot, so instance transforms are arena-local
/// meters and inherit the arena's placement, scale, and yaw for free.
/// </summary>
public sealed partial class Diorama : Node3D
{
    private struct Ent
    {
        public Vector2 Game;
        public float Angle;
        public float SizeGame;
        public float AnimPhase;      // creatures only; drives per-instance frame
        public uint Flags;           // creatures only; anim strip/mirror/shock bits
        public int TypeId;           // projectiles/secondaries only; per-type glow tint
        public float MaxHp;          // creatures only; energizer gates on max_hp<500
        public float LifecycleStage; // creatures only; <0 fades the death sprite
        public Color BaseColor;      // creatures only; per-creature tint multiplier
        public float HitFlash;       // creatures only; white hit-flash timer
        public int Frame;            // UvIndexed layers (bonuses); atlas cell index
        public ulong Identity;       // creatures only; pool slot + reuse generation
    }

    private sealed class Layer
    {
        public MultiMesh Mesh = null!;
        public Ent[] Prev;
        public Ent[] Curr;
        public int PrevCount;
        public int CurrCount;
        public float Lift;
        public float SizeScale;
        public float HeadingOffset; // radians; corrects a sheet's baked art facing
        public bool HasStableIdentity;

        // Animation (creature layers): pick the 8x8 frame per instance from
        // anim_phase + flags via CreatureAnim.SelectFrame, written into the
        // MultiMesh instance custom data (UV offset) for the sprite shader.
        public bool Animated;
        public int Grid = 1;
        public int BaseFrame;
        public bool MirrorLong;

        // Streak layers (projectiles/secondaries): additive glow elongated along
        // travel, tinted per type via MultiMesh instance colors.
        public bool Streak;

        // Creature layers: render the sprite at the reference world size —
        // 64 * clamp(size/64, 0.25, 2.0) game units (creature_render_type) — so
        // the sprite matches the sim hit radius instead of overshooting it.
        public bool ClampRefSize;

        // Creature layers: apply the energizer-blue + lifecycle-fade tint per
        // instance (draw.py draw_creatures) via MultiMesh instance colors.
        public bool Tinted;

        // Bonus layer: pick the atlas cell per instance from Ent.Frame (a fixed
        // grid, no anim), written to custom data like the animated path.
        public bool UvIndexed;

        public Layer(int capacity)
        {
            Prev = new Ent[capacity];
            Curr = new Ent[capacity];
        }

        public void BeginPush()
        {
            (Prev, Curr) = (Curr, Prev);
            PrevCount = CurrCount;
            CurrCount = 0;
        }

        public void Add(Vector2 game, float angle, float sizeGame, float animPhase = 0.0f, uint flags = 0, int typeId = 0, float maxHp = 0.0f, float lifecycleStage = 16.0f, Color? baseColor = null, float hitFlash = 0.0f, int frame = 0, ulong identity = 0)
        {
            if (CurrCount >= Curr.Length)
            {
                return;
            }
            Curr[CurrCount++] = new Ent
            {
                Game = game,
                Angle = angle,
                SizeGame = sizeGame,
                AnimPhase = animPhase,
                Flags = flags,
                TypeId = typeId,
                MaxHp = maxHp,
                LifecycleStage = lifecycleStage,
                BaseColor = baseColor ?? Colors.White,
                HitFlash = hitFlash,
                Frame = frame,
                Identity = identity,
            };
        }
    }

    private const int PlayerCap = 4;
    private const int CreatureCapPerType = 1024;
    private const int ProjectileCap = 8192;
    private const int SecondaryCap = 2048;
    private const int BonusCap = 256;
    private const int ParticleCap = 512; // crimson-zig effect_pool_size (0x200)

    private const int EffectDrawFlag = 0x40; // draw_effect_pool gate (flags & 0x40)

    private const string SpriteDir = "res://assets/sprites/";

    // Sim heading convention (math_parity.heading_to_direction_f32): a heading
    // theta points in game direction (sin theta, -cos theta). Each sheet's
    // built-in art facing is corrected per-layer via Layer.HeadingOffset (from
    // the manifest's offset_deg), since sheets aren't all drawn the same way.

    // Debug: draw a magenta needle from each sprite entity along its raw heading,
    // to calibrate the sprite-art vs heading relationship in-headset. Off now
    // that facing is validated correct (needle points along the raw forward,
    // which is 90 deg off the sprite art's baked facing — expected). Flip on to
    // recalibrate a new sheet.
    private bool _debug;
    private const int NeedleCap = 8192;

    /// <summary>Toggle debug overlays (the magenta creature facing needle).</summary>
    public void SetDebug(bool on) => _debug = on;

    // Graphics-detail nodes toggled by the Options slider (1-5): low detail drops
    // the soft drop-shadows and the sprite-effect particles (the heaviest overdraw)
    // like the base game's detail preset thins the eye-candy.
    private MultiMeshInstance3D? _shadowNode;
    private MultiMeshInstance3D? _particleNode;

    /// <summary>Apply the Options graphics-detail level (1-5): shadows at >=3,
    /// particles at >=2. A coarse but faithful "less eye-candy at low detail".</summary>
    private int _graphicsDetail = 5; // current level (projectile glow gating)

    public void SetGraphicsDetail(int level)
    {
        _graphicsDetail = level;
        if (_shadowNode != null)
        {
            _shadowNode.Visible = level >= 3;
        }
        if (_particleNode != null)
        {
            _particleNode.Visible = level >= 2;
        }
        if (_particleAddNode != null)
        {
            _particleAddNode.Visible = level >= 2;
        }
        if (_glowNode != null)
        {
            _glowNode.Visible = level >= 2;
        }
    }

    // 2.5D presentation (PLAN §6). A fixed back-tilt was tried (leaning sprites
    // toward the player so they read as "standing"), but in-headset it looked
    // worse AND lifted sprites off the plane while projectiles stay at ground
    // level, so bullets appeared to emit from below the creature (2026-07-08
    // finding). Flat reads correctly — the drop shadows do the grounding. Kept as
    // a tunable (0 = flat) in case a different scheme is revisited.
    private const float SpriteTiltDegrees = 0.0f;

    // Drop shadows: a soft dark blob on the plane under each creature/player to
    // ground them. One shared MultiMesh (circular, so no heading needed).
    private const int ShadowCap = 8192;
    private const float ShadowScale = 1.15f; // shadow diameter vs sprite footprint
    private const float ShadowLift = 0.0015f; // just above terrain to avoid z-fight

    private float _arenaSideMeters;

    // Proportional multiplier on every entity layer's plane lift. The lifts were
    // chosen against a small, near-level tabletop viewed from almost directly
    // above, where a lift is hidden behind its own sprite. On a large tilted
    // board seen from a shallow angle the same lift opens a visible gap between
    // sprite and shadow and the entities read as floating. Scaling all layers by
    // one factor preserves their relative bands (ground decals and shadows are
    // NOT scaled — they are z-fight guards on the terrain, not visual height).
    private float _heightScale = 1.0f;

    /// <summary>Set the proportional lift multiplier for every entity layer.</summary>
    public void SetHeightScale(float scale) => _heightScale = Mathf.Max(scale, 0.0f);
    private float _worldSize;
    private float _energizerTimer; // global energizer bonus timer (snapshot header)

    private Layer _players = null!;      // torso quads (player lift)
    private Layer _playerLegs = null!;   // leg quads, LOWER: on the creature plane
    private readonly Dictionary<int, Layer> _creatureLayers = new();
    private Layer _creatureFallback = null!;
    private Layer _bonuses = null!;
    private MultiMesh? _needles;
    private int _needleCount;
    private MultiMesh _shadows = null!;
    private int _shadowCount;

    // Effects (slice 6a): additive glow bursts for muzzle flashes (player
    // MuzzleFlashAlpha). Captured at tick time in PushSnapshot, drawn each frame
    // in Interpolate. (The synthetic explosion blobs were retired when the
    // faithful two-quad detonations landed in DioramaProjectiles.cs.)
    private struct Muzzle { public Vector2 Game; public float Heading; public float Alpha; public float SizeGame; }
    private readonly Muzzle[] _muzzles = new Muzzle[PlayerCap];
    private int _muzzleCount;
    private MultiMesh _fx = null!;
    private int _fxCount;
    private const int FxCap = 2048;

    // Sprite-effect pool (slice 6b): blood/gibs/explosions/casings from the ABI
    // particle stream, drawn from particles.png. effect_id -> precomputed UV cell
    // (uv offset + scale) from the bake; per-instance UV + color via the shader.
    private MultiMesh? _particles;      // alpha-pass effects (flags & 0x40): smoke, decals
    private MultiMesh? _particlesAdd;   // additive-pass effects: ring, flash, burst
    private MultiMeshInstance3D? _particleAddNode;
    private readonly Dictionary<int, Vector3> _effectUv = new(); // effect_id -> (offX, offY, scale)

    // Freeze overlay (draw_freeze_overlay): while the global freeze bonus is
    // active (ABI v5 header), each creature wears a FREEZE_SHATTER frame from
    // particles.png. One shared mesh, drawn over the creatures at tick rate.
    private MultiMesh? _freezeMesh;
    private float _freezeTimer;
    private const int FreezeShatterEffectId = 0x0E; // EffectId.FREEZE_SHATTER
    private const int FreezeCap = 1024;

    // Creature overlays (draw_creature_overlays): per-creature auras from the AURA
    // atlas frame, alpha-blended UNDER the creature sprites (priority below the
    // creature layers). Poison (red, flags & 0x01), plague (black, wire bit
    // 0x80000000), monster-vision (yellow, all creatures when the header flag is
    // set). One shared mesh; several overlays can stack on one creature.
    private MultiMesh? _overlays;
    private bool _monsterVision;
    private const int AuraEffectId = 0x10; // EffectId.AURA
    private const uint CreatureWireFlagPlague = 0x8000_0000; // matches exports.zig
    private const uint CreatureFlagPoison = 0x01;            // CreatureFlags.self_damage_tick
    private const int OverlayCap = 3072; // up to 3 auras per creature

    // Flame/bubblegun particle-glow pool (draw_particle_pool) — a SECOND particle
    // system distinct from the effect pool, rendered additively (ABI v7 glow
    // stream). One shared additive mesh.
    private MultiMesh? _glowMesh;
    private MultiMeshInstance3D? _glowNode;
    private const int GlowLargeEffectId = 13; // ambient big glow (fx_detail 1)
    private const int GlowNormalEffectId = 12; // per-particle glow
    private const int GlowBubblegunEffectId = 2; // bubblegun blob
    private const int BubblegunStyleId = 8;      // ParticleStyleId.bubblegun
    private const int BlowTorchStyleId = 1;      // ParticleStyleId.blow_torch
    private const int GlowCap = 384;

    // Terrain FX (ABI v3, PLAN §6): blood/scorch splats + corpse stamps drained
    // per tick (crimson_host_terrain_fx). The originals bake these permanently
    // into the ground; here each is a persistent flat quad accumulated into a
    // ring-buffer MultiMesh (oldest overwritten past capacity). Blood splats reuse
    // particles.png via the SAME effect_id -> UV table as sprite-effects; corpse
    // stamps use bodyset.png (per-type frame). Both draw UNDER the living sprites.
    private MultiMesh? _decals;   // blood/scorch splats (particles.png)
    private int _decalCursor;
    private int _decalCount;
    private const int DecalCap = 4096;
    private const float DecalLift = 0.0016f; // just above terrain, below shadows

    private MultiMesh? _corpses;  // corpse stamps (bodyset.png)
    private int _corpseCursor;
    private int _corpseCount;
    private const int CorpseCap = 1024;
    // Just above the floor plane: the dying-creature sprite drops here while
    // its corpse stamp bakes into the ground BELOW it — a taller lift read as
    // a parallax-separated double corpse in-headset.
    private const float CorpseLift = 0.0004f;
    private int _corpseGrid = 4;
    private float _corpseUvScale = 0.25f;
    private readonly Dictionary<int, int> _corpseFrames = new(); // type_id -> bodyset frame

    // Arena floor (PLAN §6): a ground plane textured with the terrain base slot
    // (ABI terrain-info), so the diorama sits above a real floor in skybox mode
    // rather than a void. Extends a little past the playfield to cover the
    // off-arena spawn margin (§6); grey fog fades the edges. Texture is applied
    // once the session's terrain slots are known (ApplyTerrainInfo).
    private MeshInstance3D? _floor;
    private StandardMaterial3D? _floorMaterial;
    private readonly Dictionary<int, string> _terrainSlots = new(); // slot -> sheet
    // Floor size vs playfield side. PUBLIC: the visible "total square" the HUD
    // and the edge-mounted buttons must align to (the playable zone is smaller).
    public const float FloorMarginScale = 1.3f;
    private const float FloorY = -0.001f;        // just below the decal plane
    private const float FloorTile = 6.0f;        // ground texture repeats across the floor

    // Bonus icons (bonuses.png, 4x4 grid): bonus_id -> icon frame. Rendered on
    // the _bonuses layer via per-instance UV (Layer.UvIndexed). Icon-only for now
    // (the bubble container + pulse/rotate are refinements).
    private readonly Dictionary<int, int> _bonusIcons = new();
    private int _bonusGrid = 4;

    // (Per-type projectile tints/frames now live in DioramaProjectiles.cs with
    // the faithful per-type renderers.)
    private static readonly Basis FlatBasis = Basis.FromEuler(new Vector3(-Mathf.Pi / 2.0f, 0.0f, 0.0f));

    // Fixed back-tilt applied to sprite bases (about arena X so the lean is the
    // same for every sprite regardless of heading). Negative angle leans the top
    // toward -z (the player). Zero when SpriteTiltDegrees is 0.
    private static readonly Basis SpriteTilt =
        new Basis(Vector3.Right, -Mathf.DegToRad(SpriteTiltDegrees));
    private static readonly float SpriteTiltSin = Mathf.Sin(Mathf.DegToRad(SpriteTiltDegrees));

    // ---- Death-cinematic view zoom (VR adaptation) ----
    // While a player death plays out, the view magnifies about the corpse
    // WITHIN the same physical arena window: positions and sizes scale about
    // the centre, anything pushed past the visible floor square is culled
    // (OutsideDrawBounds — the window edge), and the ground shader samples the
    // matching shrunk window. 1.0 = off. Driven per tick by Main.
    private float _viewZoom = 1.0f;
    private Vector2 _viewCenterGame;

    public void SetViewZoom(float zoom, Vector2 centerGame)
    {
        _viewZoom = Mathf.Max(1.0f, zoom);
        _viewCenterGame = centerGame;
        if (_floor?.MaterialOverride is ShaderMaterial sm)
        {
            sm.SetShaderParameter("view_zoom", _viewZoom);
            sm.SetShaderParameter("view_center", _viewCenterGame / _worldSize);
        }
    }

    public void ResetViewZoom() => SetViewZoom(1.0f, new Vector2(_worldSize * 0.5f, _worldSize * 0.5f));

    /// <summary>Discard entity history when the native session is replaced so
    /// the first frame of a new run cannot interpolate from the old run.</summary>
    public void ResetInterpolation()
    {
        static void Clear(Layer layer)
        {
            layer.PrevCount = 0;
            layer.CurrCount = 0;
            layer.Mesh.VisibleInstanceCount = 0;
        }

        Clear(_players);
        Clear(_playerLegs);
        Clear(_bonuses);
        foreach (Layer layer in _creatureLayers.Values)
        {
            Clear(layer);
        }
        Clear(_creatureFallback);
    }

    /// <summary>View-transformed game position (magnified about the zoom centre).</summary>
    private Vector2 ViewGame(Vector2 game) =>
        _viewCenterGame + (game - _viewCenterGame) * _viewZoom;

    /// <summary>Game-units -&gt; metres, including the view magnification.</summary>
    private float ViewK => _arenaSideMeters / _worldSize * _viewZoom;

    public void Configure(float arenaSideMeters, float worldSize)
    {
        _arenaSideMeters = arenaSideMeters;
        _worldSize = worldSize;

        SpriteManifest? manifest = LoadManifest();

        // Player: bodyset sprite if available, else white quad. Priorities below
        // set draw order (higher = on top): bonuses under, then creatures (by
        // type), effects/projectiles above, player on top.
        // sizeScale 1.0 = faithful: the reference draws a sprite at ~its `size`
        // world units (player) / 64*clamp(size/64,.25,2) (creatures), so the sprite
        // matches the sim hit radius. Bigger scales overshoot the hitbox.
        // ABI v15: legs + torso are separate UV-indexed layers (leg frame from
        // move_phase rotated by heading; torso = leg + 16 rotated by aim with
        // the recoil offset — trooper.py:155-196). The LEGS render on the
        // creature plane (0.008) so they read as standing IN the crowd; the
        // torso floats at the player lift (0.012) above the swarm.
        if (manifest?.player is { } pd)
        {
            _playerLegs = BuildPlayerLayer(pd, lift: 0.008f, sizeScale: PlayerLegScale);
            _players = BuildPlayerLayer(pd, lift: 0.012f, sizeScale: 1.0f);
        }
        else
        {
            _playerLegs = BuildColorLayer(PlayerCap, new Color(0.95f, 0.95f, 0.95f), lift: 0.008f, sizeScale: PlayerLegScale, renderPriority: 15);
            _players = BuildColorLayer(PlayerCap, new Color(0.95f, 0.95f, 0.95f), lift: 0.012f, sizeScale: 1.0f, renderPriority: 15);
        }

        // One creature layer per type so each can bind its own sheet texture.
        if (manifest?.creatures is { } creatures)
        {
            foreach (KeyValuePair<string, SpriteDesc> kv in creatures)
            {
                if (int.TryParse(kv.Key, out int typeId))
                {
                    Layer layer =
                        BuildAnimatedSpriteLayer(CreatureCapPerType, kv.Value, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 1.0f);
                    layer.ClampRefSize = true;
                    layer.HasStableIdentity = true;
                    _creatureLayers[typeId] = layer;
                }
            }
        }
        // Fallback for unmapped creature types (e.g. bosses) so nothing vanishes.
        _creatureFallback = BuildColorLayer(CreatureCapPerType, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 1.0f, renderPriority: 8);
        _creatureFallback.ClampRefSize = true;
        _creatureFallback.HasStableIdentity = true;

        // Native pass order (top of the stack): player < projectiles/effects <
        // bonuses/UI. Projectiles/secondaries now use the faithful per-type
        // renderers (DioramaProjectiles.cs) on the player's plane (0.012).
        _bonuses = BuildBonusLayer(manifest);

        BuildFloor(manifest);
        BuildShadows();
        BuildFx();
        BuildParticles(manifest);
        BuildDecals(manifest);
        BuildCorpses(manifest);
        BuildProjectileRenderers(); // after BuildParticles (needs _effectUv)

        // Facing needle: always built (hidden) so the debug toggle can enable it
        // at runtime; emission is gated on _debug (SetDebug).
        _needles = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = NeedleCap,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _needles,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1.0f, 0.0f, 1.0f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        });
    }

    private Layer BuildColorLayer(int capacity, Color color, float lift, float sizeScale, int renderPriority = 8)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // Sprites are a flat 2.5D layer: blend + no depth write so draw order
            // (RenderPriority) fully controls layering — no z-fighting, no
            // physical height needed. Depth test stays on so terrain occludes.
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            RenderPriority = renderPriority,
        };
        return BuildLayer(capacity, material, lift, sizeScale);
    }

    /// <summary>Textured layer showing one static frame of a sheet. Falls back
    /// to a colored layer if the texture can't be loaded (assets not baked).</summary>
    // VR-readability deviation (native = 1.0): the leg art is a ~11px blob in
    // the 64px trooper cell, fully inside the torso footprint, so faithful
    // scale reads as "no legs" from the table view. Modest oversize makes the
    // walk cycle legible under the torso. Set to 1.0f for exact native.
    private const float PlayerLegScale = 1.5f;

    /// <summary>A player part layer: UV-indexed (per-instance frame) from the
    /// trooper sheet, at the given plane lift.</summary>
    private Layer BuildPlayerLayer(SpriteDesc desc, float lift, float sizeScale)
    {
        string path = SpriteDir + desc.sheet;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            GD.PushWarning($"CrimsonVR: sprite sheet missing ({path}); using colored quad");
            return BuildColorLayer(PlayerCap, new Color(0.95f, 0.95f, 0.95f), lift, sizeScale, renderPriority: 15);
        }
        var material = new ShaderMaterial { Shader = SpriteShader, RenderPriority = desc.priority };
        material.SetShaderParameter("sheet", tex);
        Layer layer = BuildLayer(PlayerCap, material, lift, sizeScale, useCustomData: true);
        layer.UvIndexed = true;
        layer.Grid = Mathf.Max(desc.grid, 1);
        layer.HeadingOffset = Mathf.DegToRad(desc.offsetDeg);
        return layer;
    }

    private Layer BuildSpriteLayer(int capacity, SpriteDesc desc, Color fallback, float lift, float sizeScale)
    {
        string path = SpriteDir + desc.sheet;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            GD.PushWarning($"CrimsonVR: sprite sheet missing ({path}); using colored quad");
            return BuildColorLayer(capacity, fallback, lift, sizeScale);
        }

        int grid = Mathf.Max(desc.grid, 1);
        int col = desc.frame % grid;
        int row = desc.frame / grid;
        var material = new StandardMaterial3D
        {
            AlbedoTexture = tex,
            Uv1Scale = new Vector3(1.0f / grid, 1.0f / grid, 1.0f),
            Uv1Offset = new Vector3((float)col / grid, (float)row / grid, 0.0f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            // Alpha blend + no depth write so RenderPriority controls layering
            // (see BuildColorLayer). Nearest keeps sprite pixels crisp.
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            RenderPriority = desc.priority,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        Layer layer = BuildLayer(capacity, material, lift, sizeScale);
        layer.HeadingOffset = Mathf.DegToRad(desc.offsetDeg);
        return layer;
    }

    /// <summary>Animated textured layer: one MultiMesh bound to the sheet, with
    /// per-instance custom data selecting the 8x8 frame's UV cell each tick
    /// (CreatureAnim.SelectFrame). Falls back to a colored layer when the sheet
    /// is missing.</summary>
    private Layer BuildAnimatedSpriteLayer(int capacity, SpriteDesc desc, Color fallback, float lift, float sizeScale)
    {
        string path = SpriteDir + desc.sheet;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            GD.PushWarning($"CrimsonVR: sprite sheet missing ({path}); using colored quad");
            return BuildColorLayer(capacity, fallback, lift, sizeScale);
        }

        // TintedSpriteShader (not SpriteShader) so the per-instance energizer +
        // lifecycle tint (MultiMesh COLOR) modulates the sprite; instances default
        // to white (no tint) when neither effect is active.
        var material = new ShaderMaterial { Shader = TintedSpriteShader, RenderPriority = desc.priority };
        material.SetShaderParameter("sheet", tex);
        Layer layer = BuildLayer(capacity, material, lift, sizeScale, useCustomData: true, useColors: true);
        layer.HeadingOffset = Mathf.DegToRad(desc.offsetDeg);
        layer.Animated = true;
        layer.Tinted = true;
        layer.Grid = Mathf.Max(desc.grid, 1);
        layer.BaseFrame = desc.baseFrame;
        layer.MirrorLong = desc.mirror;
        return layer;
    }

    private Layer BuildLayer(int capacity, Material material, float lift, float sizeScale, bool useCustomData = false, bool useColors = false)
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = useCustomData,
            UseColors = useColors,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = capacity,
            VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D { Multimesh = mesh, MaterialOverride = material };
        AddChild(node);
        return new Layer(capacity) { Mesh = mesh, Lift = lift, SizeScale = sizeScale };
    }

    /// <summary>Additive glow material tinted per-instance (vertex colors) with a
    /// soft round falloff — shared by the projectile streaks and the fx bursts.</summary>
    private ImageTexture? _softCircleTex;

    private StandardMaterial3D AdditiveGlowMaterial(int renderPriority) => new()
    {
        AlbedoTexture = _softCircleTex ??= MakeSoftCircleTexture(64),
        VertexColorUseAsAlbedo = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        RenderPriority = renderPriority,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        // Fog runs after the fragment and ADDS under additive blending, painting
        // the quad footprint as a faint square (see ParticleShaderAdd).
        DisableFog = true,
    };

    /// <summary>Additive glow streak layer (projectiles/secondaries): a soft blob
    /// elongated along travel, tinted per instance by projectile type.</summary>
    private Layer BuildStreakLayer(int capacity, float lift, float sizeScale, int renderPriority)
    {
        Layer layer = BuildLayer(capacity, AdditiveGlowMaterial(renderPriority), lift, sizeScale, useColors: true);
        layer.Streak = true;
        return layer;
    }

    /// <summary>One shared additive MultiMesh for transient fx bursts (muzzle
    /// flashes, explosions), tinted per instance.</summary>
    private void BuildFx()
    {
        _fx = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = FxCap,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _fx, MaterialOverride = AdditiveGlowMaterial(22) });
    }

    // Particle shader: per-instance UV cell (INSTANCE_CUSTOM = off.xy, scale.z)
    // and per-instance rgba tint (COLOR). Alpha-blended (draw_effect_pool uses
    // BLEND_ALPHA), depth-write off so RenderPriority orders it above the world.
    //
    // BLEND-SPACE NOTE: the original composites in sRGB (display) space; Godot
    // blends in linear. The same numbers blended in linear read visibly milkier
    // for bright translucent sources (numerically fitted: mean err 0.086 -> 0.018
    // over arena-toned backgrounds). The sampler takes RAW values (no
    // source_color) so the shader can do the native display-referred tint math
    // (c.rgb * col.rgb, both authored in sRGB), encode the color to linear with
    // pow 2.2 (exact at full alpha), and approximate sRGB-space compositing by
    // warping alpha with a luminance-dependent exponent (bright sources need
    // less alpha in linear, dark sources more; fitted 1.6/0.6 endpoints).
    // Every alpha-blended effect material, so the dst-blind alpha curve can be
    // switched off when the app composites over passthrough. Collected at
    // creation rather than looked up later: these hang off several different
    // MultiMeshInstances and there is no single node to walk.
    private readonly System.Collections.Generic.List<ShaderMaterial> _mrCompositeMats = new();
    private bool _mrComposite;

    private void TrackMrComposite(ShaderMaterial mat)
    {
        _mrCompositeMats.Add(mat);
        mat.SetShaderParameter("mr_composite", _mrComposite ? 1.0f : 0.0f);
    }

    /// <summary>Tell the effect materials whether they are drawing over the real
    /// world. See ParticleShader: its alpha curve assumes an opaque destination
    /// and paints black haloes without one.</summary>
    public void SetMixedRealityComposite(bool mixedReality)
    {
        _mrComposite = mixedReality;
        foreach (ShaderMaterial mat in _mrCompositeMats)
        {
            mat.SetShaderParameter("mr_composite", mixedReality ? 1.0f : 0.0f);
        }
    }

    private Shader? _particleShader;
    private Shader ParticleShader => _particleShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 inst;
            varying vec4 col;
            void vertex() { inst = INSTANCE_CUSTOM; col = COLOR; }
            // 1.0 while compositing over passthrough. The alpha curve below is a
            // DST-BLIND approximation: it lifts the alpha of dark pixels so they
            // darken the arena floor the way native blending does. That assumes
            // an opaque, arena-toned destination. In MR there is none, so those
            // lifted dark pixels land on the player's real room as black haloes
            // around every effect — the exploding freeze ring worst of all.
            // Straight alpha is the honest answer there: transparent stays
            // transparent, and the effect tints the room instead of masking it.
            uniform float mr_composite = 0.0;
            void fragment() {
                // Manifest UV rect = native sample window (cell corner, cell-2px
                // right/bottom clamp) — no shader-side inset.
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                vec3 s = c.rgb * col.rgb;      // display-referred source color
                ALBEDO = pow(s, vec3(2.2));
                float a = clamp(c.a * col.a, 0.0, 1.0);
                float lum = dot(min(s, vec3(1.0)), vec3(0.299, 0.587, 0.114));
                ALPHA = mix(pow(a, mix(0.6, 1.6, lum)), a, mr_composite);
            }
            """,
    };

    // Additive variant for the non-alpha effect pass (flags & 0x40 == 0): the
    // explosion ring, bright flash and shockwave bursts, which draw_effect_pool
    // renders with BLEND_ADDITIVE (GL_SRC_ALPHA, GL_ONE). Same blend-space note
    // as ParticleShader: compute the native display-referred contribution
    // (c.rgb * col.rgb * c.a * col.a, all sRGB-authored) and encode with the
    // fitted pow 1.1 — the best dst-blind approximation of sRGB-space addition
    // over arena-toned backgrounds (mean err 0.053; naive all-linear was 0.156,
    // full pow 2.2 overshoots dim at 0.156-class errors the other way).
    private Shader? _particleShaderAdd;
    private Shader ParticleShaderAdd => _particleShaderAdd ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, blend_add, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 inst;
            varying vec4 col;
            void vertex() { inst = INSTANCE_CUSTOM; col = COLOR; }
            void fragment() {
                // fog_disabled is LOAD-BEARING on every additive material: the
                // pipeline applies distance fog AFTER the fragment shader, lifting
                // even ALBEDO=0 fragments to fog_color*fog_amount — under blend_add
                // that ADDS a faint uniform wash over the quad's whole footprint,
                // which read as a translucent SQUARE around big effects (freeze/
                // pickup ring) and square edges on the pickup burst sparks.
                // Manifest UV rect = native sample window; see ParticleShader.
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                vec3 s = c.rgb * col.rgb * (c.a * col.a); // native additive term
                ALBEDO = pow(s, vec3(1.1));
                ALPHA = 1.0;
            }
            """,
    };

    /// <summary>Sprite-effect layer: particles.png with per-instance UV+color.
    /// Effects draw after creatures/projectiles in the native order, so this sits
    /// on top (RenderPriority 23). Absent assets -> no particles (still runs).</summary>
    private void BuildParticles(SpriteManifest? manifest)
    {
        if (manifest?.effects is not { Count: > 0 } effects || manifest.effects_sheet is not { } sheetName)
        {
            return;
        }
        string path = SpriteDir + sheetName;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            GD.PushWarning($"CrimsonVR: effects sheet missing ({path}); no particles");
            return;
        }
        foreach (KeyValuePair<string, EffectUv> kv in effects)
        {
            if (int.TryParse(kv.Key, out int id) && kv.Value.uv_off is { Length: >= 2 })
            {
                _effectUv[id] = new Vector3(kv.Value.uv_off[0], kv.Value.uv_off[1], kv.Value.uv_scale);
            }
        }

        var material = new ShaderMaterial { Shader = ParticleShader, RenderPriority = 23 };
        TrackMrComposite(material);
        material.SetShaderParameter("sheet", tex);
        _particles = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = ParticleCap,
            VisibleInstanceCount = 0,
        };
        _particleNode = new MultiMeshInstance3D { Multimesh = _particles, MaterialOverride = material };
        AddChild(_particleNode);

        // Additive-pass effects (ring / flash / burst) — same atlas, additive blend,
        // RenderPriority above the alpha smoke so the flash reads on top.
        var addMaterial = new ShaderMaterial { Shader = ParticleShaderAdd, RenderPriority = 26 };
        addMaterial.SetShaderParameter("sheet", tex);
        _particlesAdd = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = ParticleCap,
            VisibleInstanceCount = 0,
        };
        _particleAddNode = new MultiMeshInstance3D { Multimesh = _particlesAdd, MaterialOverride = addMaterial };
        AddChild(_particleAddNode);

        // Freeze-shatter overlay shares particles.png (same UV table); RenderPriority
        // 24 sits just over the particle layer.
        var freezeMat = new ShaderMaterial { Shader = ParticleShader, RenderPriority = 24 };
        TrackMrComposite(freezeMat);
        freezeMat.SetShaderParameter("sheet", tex);
        _freezeMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = FreezeCap,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _freezeMesh, MaterialOverride = freezeMat });

        // Creature auras (draw_creature_overlays): alpha-blended, priority 4 so
        // they sit under the creature sprites (creatures start at priority 6) but
        // over the ground/decals.
        var overlayMat = new ShaderMaterial { Shader = ParticleShader, RenderPriority = 4 };
        TrackMrComposite(overlayMat);
        overlayMat.SetShaderParameter("sheet", tex);
        _overlays = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = OverlayCap,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _overlays, MaterialOverride = overlayMat });

        // Flame/bubblegun glow pool (draw_particle_pool): additive, priority 27 so
        // it reads on top of the additive effect flashes (26).
        var glowMat = new ShaderMaterial { Shader = ParticleShaderAdd, RenderPriority = 27 };
        glowMat.SetShaderParameter("sheet", tex);
        _glowMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = GlowCap,
            VisibleInstanceCount = 0,
        };
        _glowNode = new MultiMeshInstance3D { Multimesh = _glowMesh, MaterialOverride = glowMat };
        AddChild(_glowNode);
    }

    /// <summary>Draw the per-creature aura overlays (draw_creature_overlays):
    /// monster-vision (yellow, all creatures), plague (black), poison (red). All
    /// alpha-blended, under the sprites, using the AURA atlas frame. Alpha fades
    /// with lifecycle (monster_vision_fade_alpha).</summary>
    private void RenderCreatureOverlays(in SnapshotView view)
    {
        if (_overlays == null || !_effectUv.TryGetValue(AuraEffectId, out Vector3 uv))
        {
            return;
        }
        float k = ViewK;
        int n = 0;

        void Emit(Vector2 game, float sizeGame, Color color)
        {
            if (n >= OverlayCap || color.A <= 1e-3f)
            {
                return;
            }
            Vector2 viewGame = ViewGame(game);
            if (_viewZoom > 1.0f && OutsideDrawBounds(viewGame))
            {
                return;
            }
            float m = Mathf.Max(sizeGame * k, 0.001f);
            Vector3 pos = Mapper.GameToArenaLocal(viewGame, _arenaSideMeters, _worldSize)
                + new Vector3(0.0f, 0.0075f, 0.0f); // over decals/shadows, under sprites
            _overlays.SetInstanceTransform(n, new Transform3D(FlatQuadBasis(0.0f, m, m), pos));
            _overlays.SetInstanceColor(n, color);
            _overlays.SetInstanceCustomData(n, new Color(uv.X, uv.Y, uv.Z, 0.0f));
            n++;
        }

        bool monsterVision = _monsterVision || DebugFx.MonsterVision;
        int creatureIdx = 0;
        foreach (Sim.CreatureSnap c in view.Creatures)
        {
            // monster_vision_fade_alpha(lifecycle_stage): 1 while alive, ramps to 0
            // as a corpse fades (stage < 0).
            float fade = c.LifecycleStage >= 0.0f
                ? 1.0f
                : Mathf.Clamp((c.LifecycleStage + 10.0f) * 0.1f, 0.0f, 1.0f);
            int idx = creatureIdx++;
            if (fade <= 1e-3f)
            {
                continue;
            }
            var game = new Vector2(c.X, c.Y);
            // Off-terrain spawns are edge-faded to invisible; their auras
            // must not give them away outside the arena.
            if (EdgeFadeAlpha(game) <= 0.01f)
            {
                continue;
            }
            if (monsterVision)
            {
                Emit(game, 90.0f, new Color(1.0f, 1.0f, 0.0f, fade));
            }
            // Debug toggle: paint 1-in-10 creatures with an aura (alternating
            // poison/plague) so the overlay pass is inspectable on demand.
            bool debugPoison = DebugFx.CreatureAuras && idx % 10 == 0;
            bool debugPlague = DebugFx.CreatureAuras && idx % 10 == 5;
            if ((c.Flags & CreatureWireFlagPlague) != 0 || debugPlague)
            {
                Emit(game, 80.0f, new Color(0.0f, 0.0f, 0.0f, fade));
            }
            if ((c.Flags & CreatureFlagPoison) != 0 || debugPoison)
            {
                Emit(game, 60.0f, new Color(1.0f, 0.0f, 0.0f, fade));
            }
        }
        _overlays.VisibleInstanceCount = n;
    }

    /// <summary>Draw the flame/bubblegun particle-glow pool additively
    /// (draw_particle_pool): a big ambient glow on every other particle, a normal
    /// tinted glow per particle, and bubblegun blobs. Distinct from the effect
    /// pool; fed by the ABI v7 glow stream.</summary>
    private void RenderGlowPool(in SnapshotView view)
    {
        if (_glowMesh == null)
        {
            return;
        }
        if (!_effectUv.TryGetValue(GlowNormalEffectId, out Vector3 uvNormal)
            || !_effectUv.TryGetValue(GlowBubblegunEffectId, out Vector3 uvBubble))
        {
            _glowMesh.VisibleInstanceCount = 0;
            return;
        }
        bool hasLarge = _effectUv.TryGetValue(GlowLargeEffectId, out Vector3 uvLarge);
        float k = ViewK;
        int n = 0;

        void Emit(Vector2 game, float wGame, float hGame, float rot, Color color, Vector3 uv)
        {
            if (n >= GlowCap || (wGame <= 0.0f) || (hGame <= 0.0f))
            {
                return;
            }
            Vector2 viewGame = ViewGame(game);
            if (_viewZoom > 1.0f && OutsideDrawBounds(viewGame))
            {
                return;
            }
            Vector3 pos = Mapper.GameToArenaLocal(viewGame, _arenaSideMeters, _worldSize)
                + new Vector3(0.0f, 0.0078f, 0.0f);
            _glowMesh.SetInstanceTransform(n, new Transform3D(FlatQuadBasis(rot, wGame * k, hGame * k), pos));
            _glowMesh.SetInstanceColor(n, color);
            _glowMesh.SetInstanceCustomData(n, new Color(uv.X, uv.Y, uv.Z, 0.0f));
            n++;
        }

        int idx = 0;
        foreach (Sim.ParticleGlowSnap g in view.Glows)
        {
            var game = new Vector2(g.X, g.Y);
            bool bubblegun = g.StyleId == BubblegunStyleId;

            // Large ambient glow (fx_detail 1): every other non-bubblegun particle.
            if (hasLarge && !bubblegun && (idx % 2) == 0)
            {
                float r = (Mathf.Sin((1.0f - g.Intensity) * Mathf.Pi * 0.5f) + 0.1f) * 55.0f + 4.0f;
                r = Mathf.Max(r, 16.0f);
                float size = r * 2.0f;
                Emit(game, size, size, 0.0f, new Color(1.0f, 1.0f, 1.0f, 0.065f), uvLarge);
            }

            if (bubblegun)
            {
                float wobble = Mathf.Sin(g.Spin) * 3.0f;
                float halfH = (wobble + 15.0f) * g.TintR * 7.0f;
                float halfW = (15.0f - wobble) * g.TintR * 7.0f;
                Emit(game, halfW * 2.0f, halfH * 2.0f, 0.0f, new Color(1.0f, 1.0f, 1.0f, g.Age), uvBubble);
            }
            else
            {
                float r = Mathf.Sin((1.0f - g.Intensity) * Mathf.Pi * 0.5f) * 24.0f;
                if (g.StyleId == BlowTorchStyleId)
                {
                    r *= 0.8f;
                }
                r = Mathf.Max(r, 2.0f);
                float size = r * 2.0f;
                Emit(game, size, size, g.Spin, new Color(g.TintR, g.TintG, g.TintB, g.Age), uvNormal);
            }
            idx++;
        }
        _glowMesh.VisibleInstanceCount = n;
    }

    /// <summary>Draw the freeze-shatter overlay on every creature while the global
    /// freeze bonus is active (draw_freeze_overlay). Tick-rate, like the particle
    /// pass; alpha = clamp(min(freeze,1) * 0.7).</summary>
    private void RenderFreezeOverlay(in SnapshotView view)
    {
        if (_freezeMesh == null)
        {
            return;
        }
        float freeze = _freezeTimer;
        if (freeze <= 0.0f || !_effectUv.TryGetValue(FreezeShatterEffectId, out Vector3 uv))
        {
            _freezeMesh.VisibleInstanceCount = 0;
            return;
        }
        float fade = freeze >= 1.0f ? 1.0f : Mathf.Clamp(freeze, 0.0f, 1.0f);
        float alpha = Mathf.Clamp(fade * 0.7f, 0.0f, 1.0f);
        if (alpha <= 1e-3f)
        {
            _freezeMesh.VisibleInstanceCount = 0;
            return;
        }
        float k = ViewK;
        int n = 0;
        int idx = 0;
        foreach (Sim.CreatureSnap c in view.Creatures)
        {
            if (n >= FreezeCap)
            {
                break;
            }
            Vector2 viewGame = ViewGame(new Vector2(c.X, c.Y));
            if (_viewZoom > 1.0f && OutsideDrawBounds(viewGame))
            {
                idx++;
                continue;
            }
            // Edge-faded off-terrain spawns keep their ice block hidden too.
            if (EdgeFadeAlpha(new Vector2(c.X, c.Y)) <= 0.01f)
            {
                idx++;
                continue;
            }
            float size = Mathf.Max(c.Size * k, 0.001f);
            float rot = idx * 0.01f + c.Heading; // matches draw_freeze_overlay
            Vector3 pos = Mapper.GameToArenaLocal(viewGame, _arenaSideMeters, _worldSize)
                + new Vector3(0.0f, 0.009f, 0.0f); // just over the creature plane
            Basis basis = FlatQuadBasis(rot, size, size);
            _freezeMesh.SetInstanceTransform(n, new Transform3D(basis, pos));
            _freezeMesh.SetInstanceColor(n, new Color(1.0f, 1.0f, 1.0f, alpha));
            _freezeMesh.SetInstanceCustomData(n, new Color(uv.X, uv.Y, uv.Z, 0.0f));
            n++;
            idx++;
        }
        _freezeMesh.VisibleInstanceCount = n;
    }

    /// <summary>Draw the live sprite-effect entries from one snapshot (called at
    /// tick time from PushSnapshot). Effects are short-lived and the pool
    /// reorders, so they aren't interpolated — 60 Hz is fine.</summary>
    private void RenderParticles(in SnapshotView view)
    {
        if (_particles == null)
        {
            return;
        }
        float k = ViewK;
        int n = 0;     // alpha pass
        int na = 0;    // additive pass
        foreach (Sim.ParticleSnap p in view.Particles)
        {
            if (!_effectUv.TryGetValue(p.EffectId, out Vector3 uv))
            {
                continue; // unknown effect id -> no UV cell
            }
            // draw_effect_pool splits by flag: flags & 0x40 -> alpha (smoke/decals),
            // else -> additive (ring / bright flash / shockwave burst). Route each
            // to its own mesh so the additive explosion actually shows.
            bool alpha = (p.Flags & EffectDrawFlag) != 0;
            if ((alpha ? n : na) >= ParticleCap)
            {
                continue;
            }
            Vector2 viewGame = ViewGame(new Vector2(p.X, p.Y));
            if (_viewZoom > 1.0f && OutsideDrawBounds(viewGame))
            {
                continue;
            }
            float w = Mathf.Max(p.HalfWidth * 2.0f * p.Scale * k, 0.001f);
            float h = Mathf.Max(p.HalfHeight * 2.0f * p.Scale * k, 0.001f);
            Vector3 pos = Mapper.GameToArenaLocal(viewGame, _arenaSideMeters, _worldSize)
                + new Vector3(0.0f, 0.007f, 0.0f);
            // Flat on the plane, spun by the effect's rotation. Build the scaled
            // columns directly (local X = width, Y = height, Z = up): Basis.Scaled
            // scales world rows, which would distort a rotated non-uniform quad.
            float c = Mathf.Cos(p.Rotation);
            float s = Mathf.Sin(p.Rotation);
            var basis = new Basis(
                new Vector3(c, 0.0f, s) * w,
                new Vector3(-s, 0.0f, c) * h,
                new Vector3(0.0f, 1.0f, 0.0f));
            var xform = new Transform3D(basis, pos);
            var color = new Color(p.R, p.G, p.B, p.A);
            var custom = new Color(uv.X, uv.Y, uv.Z, 0.0f);
            MultiMesh mesh = alpha ? _particles : _particlesAdd!;
            int i = alpha ? n++ : na++;
            mesh.SetInstanceTransform(i, xform);
            mesh.SetInstanceColor(i, color);
            mesh.SetInstanceCustomData(i, custom);
        }
        _particles.VisibleInstanceCount = n;
        if (_particlesAdd != null)
        {
            _particlesAdd.VisibleInstanceCount = na;
        }
    }

    /// <summary>Bonus pickup layer: one textured quad per bonus, per-instance UV
    /// selecting the icon frame from bonuses.png (4x4). Falls back to the old
    /// cyan colored quad when the sheet is absent.</summary>
    private Layer BuildBonusLayer(SpriteManifest? manifest)
    {
        const float lift = 0.006f;
        if (manifest?.bonuses is not { } bd)
        {
            return BuildColorLayer(BonusCap, new Color(0.3f, 0.85f, 0.95f), lift, sizeScale: 1.0f, renderPriority: 25);
        }
        string path = SpriteDir + bd.sheet;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            return BuildColorLayer(BonusCap, new Color(0.3f, 0.85f, 0.95f), lift, sizeScale: 1.0f, renderPriority: 25);
        }
        _bonusGrid = Mathf.Max(bd.grid, 1);
        foreach (KeyValuePair<string, int> kv in bd.icons)
        {
            if (int.TryParse(kv.Key, out int id))
            {
                _bonusIcons[id] = kv.Value;
            }
        }
        var material = new ShaderMaterial { Shader = SpriteShader, RenderPriority = bd.priority };
        material.SetShaderParameter("sheet", tex);
        // Icons read ~32 game units in the reference; sizeScale scales from `size`
        // which we pass as ~32, so use 1.0 and pass a fixed size in PushSnapshot.
        Layer layer = BuildLayer(BonusCap, material, lift, sizeScale: 1.0f, useCustomData: true);
        layer.UvIndexed = true;
        layer.Grid = _bonusGrid;
        return layer;
    }

    /// <summary>Build the arena floor plane (neutral grey until the terrain
    /// texture is applied via ApplyTerrainInfo). A flat PlaneMesh (normal +Y)
    /// centred on the playfield, extended by FloorMarginScale so entities that
    /// spawn just outside the bounds still stand on ground.</summary>
    private void BuildFloor(SpriteManifest? manifest)
    {
        if (manifest?.terrain?.slots is { } slots)
        {
            foreach (KeyValuePair<string, string> kv in slots)
            {
                if (int.TryParse(kv.Key, out int i))
                {
                    _terrainSlots[i] = kv.Value;
                }
            }
        }
        _floorMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.26f, 0.26f, 0.29f), // neutral fallback ground
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Uv1Scale = new Vector3(FloorTile, FloorTile, 1.0f),
            // Nearest (not Linear) so the pixel-art terrain reads crisp — Linear
            // over-smoothed the base tile into a flat green blur, hiding its
            // grass/dirt detail. (Faithful dirt-patch placement would need the
            // grim ground generator; this at least surfaces the base texture.)
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
        float s = _arenaSideMeters * FloorMarginScale;
        _floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(s, s) },
            MaterialOverride = _floorMaterial,
            Position = new Vector3(0.0f, FloorY, 0.0f),
        };
        AddChild(_floor);
        BuildPlayfieldBorder();
    }

    /// <summary>QUICK-FIX playfield boundary: four thin dim-crimson strips
    /// marking the playable zone's perimeter (the visible floor extends 1.3x
    /// past it, so the edge was invisible). An ELEGANT treatment — fade band /
    /// rim mask tied into the off-arena spawn-margin fix (port-status) — is
    /// documented as future work; this is deliberately minimal.</summary>
    private void BuildPlayfieldBorder()
    {
        float half = _arenaSideMeters * 0.5f;
        float w = _arenaSideMeters * 0.008f;

        // (centerX, centerZ, sizeX, sizeZ, edge) per strip; corners overlap.
        // Edge identifies which playfield boundary the strip marks, so each can
        // fade on the player's distance to ITS OWN edge.
        (float cx, float cz, float sx, float sz, BorderEdge edge)[] strips =
        {
            (0.0f, -half, _arenaSideMeters + w, w, BorderEdge.MinY),
            (0.0f, half, _arenaSideMeters + w, w, BorderEdge.MaxY),
            (-half, 0.0f, w, _arenaSideMeters + w, BorderEdge.MinX),
            (half, 0.0f, w, _arenaSideMeters + w, BorderEdge.MaxX),
        };

        foreach ((float cx, float cz, float sx, float sz, BorderEdge edge) in strips)
        {
            // A material EACH: they shared one, so per-edge fading was
            // impossible without every edge lighting up together.
            var mat = new StandardMaterial3D
            {
                // Dull, and ordered UNDER everything. At the old RenderPriority 2
                // the strips drew after the other transparents, which is why they
                // painted over blood, corpse stamps AND the menus — transparent
                // draws do not write depth, so between two of them the later one
                // simply wins wherever they overlap, however far away it is. A
                // negative priority puts the border first, so ground detail
                // covers it like dirt over paint and any panel in front of it
                // occludes it properly.
                AlbedoColor = new Color(BorderRed, BorderGreen, BorderBlue, 0.0f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                RenderPriority = -8,
            };
            var strip = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(sx, sz) },
                MaterialOverride = mat,
                // Below the corpse stamps (0.0004) so ground detail sits on top,
                // but clear of the floor itself. Sub-mm gaps between coplanar
                // quads resolve per-eye under VR multiview, so this keeps the
                // same ~0.2 mm minimum the decal lifts use.
                Position = new Vector3(cx, 0.0002f, cz),
            };
            AddChild(strip);
            _borderStrips.Add((mat, edge));
        }
    }

    private enum BorderEdge { MinX, MaxX, MinY, MaxY }

    private const float BorderRed = 0.34f, BorderGreen = 0.10f, BorderBlue = 0.08f;

    /// <summary>Peak border opacity, reached when the player is on the line.</summary>
    private const float BorderMaxAlpha = 0.45f;

    /// <summary>Game units from an edge at which its border starts to show. The
    /// border exists to tell the player where the wall is, which only matters
    /// when they are near one — drawn permanently it is a box around the
    /// diorama, competing with the action for attention in every frame of
    /// footage.</summary>
    private const float BorderProximityGame = 150.0f;

    private readonly System.Collections.Generic.List<(StandardMaterial3D Mat, BorderEdge Edge)> _borderStrips = new();

    /// <summary>Fade each border strip on the player's distance to its own edge.
    /// Per-edge rather than global so approaching one wall lights that wall,
    /// not the whole perimeter.</summary>
    public void UpdateBorderProximity(Vector2 playerGame)
    {
        foreach ((StandardMaterial3D mat, BorderEdge edge) in _borderStrips)
        {
            float distance = edge switch
            {
                BorderEdge.MinX => playerGame.X,
                BorderEdge.MaxX => _worldSize - playerGame.X,
                BorderEdge.MinY => playerGame.Y,
                _ => _worldSize - playerGame.Y,
            };
            float t = 1.0f - Mathf.Clamp(distance / BorderProximityGame, 0.0f, 1.0f);
            mat.AlbedoColor = new Color(BorderRed, BorderGreen, BorderBlue, t * BorderMaxAlpha);
        }
    }

    /// <summary>The base terrain-slot texture applied to the arena floor (null
    /// until <see cref="ApplyTerrainInfo"/> succeeds), so the surrounding world
    /// floor can share the same ground.</summary>
    public Texture2D? FloorTexture => CleanGroundTexture ?? _floorMaterial?.AlbedoTexture as Texture2D;

    /// <summary>Texture the floor from the session's terrain info (ABI v3):
    /// generate the faithful ground render target from the three slots + seed
    /// (DioramaTerrain.cs). Falls back to tiling the base slot if any sheet is
    /// missing, and to the grey floor if even that fails. Called per run —
    /// quests carry per-level slots.</summary>
    public void ApplyTerrainInfo(Sim.TerrainInfo info)
    {
        if (_floorMaterial == null)
        {
            return;
        }
        if (GenerateGround(info) is Texture2D ground && _floor != null)
        {
            // Two-texture floor: the PLAYABLE zone samples the baked RT
            // (scatter + permanent blood/corpses); the margin band samples the
            // CLEAN scatter-only RT wrapped — so bakes never mirror outside
            // the playfield (in-headset finding: the wrap duplicated corpses).
            var mat = new ShaderMaterial { Shader = FloorShader };
            mat.SetShaderParameter("baked", ground);
            mat.SetShaderParameter("clean_t", CleanGroundTexture ?? ground);
            mat.SetShaderParameter("margin_scale", FloorMarginScale);
            _floor.MaterialOverride = mat;
            return;
        }
        if (_floor != null)
        {
            _floor.MaterialOverride = _floorMaterial; // fallback path below
        }
        if (_terrainSlots.TryGetValue(info.Slot0, out string? file)
            && ResourceLoader.Exists(SpriteDir + file)
            && ResourceLoader.Load<Texture2D>(SpriteDir + file) is Texture2D tex)
        {
            // Fallback: tile the base slot (pre-generator behavior).
            _floorMaterial.AlbedoTexture = tex;
            _floorMaterial.AlbedoColor = Colors.White;
            _floorMaterial.Uv1Scale = new Vector3(FloorTile, FloorTile, 1.0f);
            _floorMaterial.Uv1Offset = Vector3.Zero;
            _floorMaterial.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
        }
        else
        {
            GD.PushWarning("CrimsonVR: terrain sheets missing; grey floor");
        }
    }

    // Tinted sprite shader (corpses): per-instance UV cell (INSTANCE_CUSTOM) and
    // per-instance rgba tint (COLOR), nearest-filtered (bodyset frames have no
    // cell inset, so linear would bleed adjacent frames). Alpha-blended,
    // depth-write off so RenderPriority orders the flat ground layer.
    private Shader? _tintedSpriteShader;
    private Shader TintedSpriteShader => _tintedSpriteShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never;
            uniform sampler2D sheet : source_color, filter_nearest;
            varying vec4 inst;
            varying vec4 col;
            void vertex() { inst = INSTANCE_CUSTOM; col = COLOR; }
            void fragment() {
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                // inst.w = hit-flash intensity: ADD white (a multiply tint toward
                // white does nothing, so the flash was invisible). Brightens the
                // opaque sprite toward white on hit.
                ALBEDO = c.rgb * col.rgb + vec3(inst.w) * c.a;
                ALPHA = c.a * col.a;
            }
            """,
    };

    /// <summary>Persistent blood/scorch splat layer (terrain-fx decals). Draws
    /// from particles.png via the same effect_id -> UV table as the sprite-effect
    /// particles, so it needs no extra assets. Absent particles -> no decals.</summary>
    private void BuildDecals(SpriteManifest? manifest)
    {
        if (_effectUv.Count == 0 || manifest?.effects_sheet is not { } sheetName)
        {
            return; // BuildParticles ran first and found no particles.png
        }
        string path = SpriteDir + sheetName;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            return;
        }
        var material = new ShaderMaterial { Shader = ParticleShader, RenderPriority = -3 };
        TrackMrComposite(material);
        material.SetShaderParameter("sheet", tex);
        _decals = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = DecalCap,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _decals, MaterialOverride = material });
    }

    /// <summary>Persistent corpse-stamp layer (terrain-fx corpses) from
    /// bodyset.png (4x4 grid; per-type frame + &0xF fallback). Absent bodyset ->
    /// no corpses (still runs).</summary>
    private void BuildCorpses(SpriteManifest? manifest)
    {
        if (manifest?.corpses is not { } cd)
        {
            return;
        }
        string path = SpriteDir + cd.sheet;
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<Texture2D>(path) is not Texture2D tex)
        {
            GD.PushWarning($"CrimsonVR: corpse sheet missing ({path}); no corpses");
            return;
        }
        _corpseGrid = Mathf.Max(cd.grid, 1);
        _corpseUvScale = 1.0f / _corpseGrid;
        foreach (KeyValuePair<string, int> kv in cd.frames)
        {
            if (int.TryParse(kv.Key, out int typeId))
            {
                _corpseFrames[typeId] = kv.Value;
            }
        }
        var material = new ShaderMaterial { Shader = TintedSpriteShader, RenderPriority = cd.priority };
        material.SetShaderParameter("sheet", tex);
        _corpses = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = CorpseCap,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _corpses, MaterialOverride = material });
    }

    /// <summary>Append the terrain FX (blood/scorch splats + corpse stamps)
    /// emitted this tick into the persistent decal/corpse ring buffers. Call once
    /// per sim tick with the drained TerrainFxView; these are one-shot events, so
    /// they accumulate rather than reset each tick.</summary>
    public void RenderTerrainFx(in TerrainFxView fx)
    {
        // Native behavior: decals/corpses bake PERMANENTLY into the ground RT
        // (terrain_render.py bake_decals / bake_corpse_decals — no cap, no
        // pop-out). The ring-buffered quads below remain as the fallback when
        // the generated ground isn't available. Corpses run as two batch
        // passes (all shadows, then all colors) like the native two-pass bake.
        if (GroundBakeReady)
        {
            // Most ticks drain nothing — only re-render the RT on ticks that
            // actually add stamps (each redraw replays the full scatter+bakes).
            if (fx.Decals.Length > 0 || fx.Corpses.Length > 0)
            {
                foreach (Sim.TerrainDecalSnap d in fx.Decals)
                {
                    BakeDecal(d);
                }
                if (_bakeBodyset != null)
                {
                    foreach (Sim.TerrainCorpseSnap c in fx.Corpses)
                    {
                        BakeCorpseShadow(c, CorpseFrameFor(c.CreatureTypeId));
                    }
                    foreach (Sim.TerrainCorpseSnap c in fx.Corpses)
                    {
                        BakeCorpseColor(c, CorpseFrameFor(c.CreatureTypeId));
                    }
                }
                FlushGroundBakes();
            }
            return;
        }

        float k = _arenaSideMeters / _worldSize;
        if (_decals != null)
        {
            foreach (Sim.TerrainDecalSnap d in fx.Decals)
            {
                if (!_effectUv.TryGetValue(d.EffectId, out Vector3 uv))
                {
                    continue;
                }
                Vector3 pos = Mapper.GameToArenaLocal(new Vector2(d.X, d.Y), _arenaSideMeters, _worldSize)
                    + new Vector3(0.0f, DecalLift, 0.0f);
                Basis basis = FlatQuadBasis(d.Rotation, Mathf.Max(d.Width * k, 0.001f), Mathf.Max(d.Height * k, 0.001f));
                _decals.SetInstanceTransform(_decalCursor, new Transform3D(basis, pos));
                _decals.SetInstanceColor(_decalCursor, new Color(d.R, d.G, d.B, d.A));
                _decals.SetInstanceCustomData(_decalCursor, new Color(uv.X, uv.Y, uv.Z, 0.0f));
                _decalCursor = (_decalCursor + 1) % DecalCap;
                if (_decalCount < DecalCap)
                {
                    _decalCount++;
                }
            }
            _decals.VisibleInstanceCount = _decalCount;
        }

        if (_corpses != null)
        {
            foreach (Sim.TerrainCorpseSnap c in fx.Corpses)
            {
                int frame = CorpseFrameFor(c.CreatureTypeId);
                // Reference centres the corpse at top_left + scale*0.5 and rotates
                // by heading - 90deg (grim.terrain_render corpse pass). Orientation
                // convention needs an in-headset check (flag with the streaks).
                Vector2 center = new(c.X + c.Scale * 0.5f, c.Y + c.Scale * 0.5f);
                Vector3 pos = Mapper.GameToArenaLocal(center, _arenaSideMeters, _worldSize)
                    + new Vector3(0.0f, CorpseLift, 0.0f);
                float size = Mathf.Max(c.Scale * k, 0.002f);
                Basis basis = FlatQuadBasis(c.Rotation - Mathf.Pi * 0.5f, size, size);
                _corpses.SetInstanceTransform(_corpseCursor, new Transform3D(basis, pos));
                _corpses.SetInstanceColor(_corpseCursor, new Color(c.R, c.G, c.B, c.A));
                _corpses.SetInstanceCustomData(_corpseCursor, new Color(
                    (frame % _corpseGrid) * _corpseUvScale,
                    (frame / _corpseGrid) * _corpseUvScale,
                    _corpseUvScale,
                    0.0f));
                _corpseCursor = (_corpseCursor + 1) % CorpseCap;
                if (_corpseCount < CorpseCap)
                {
                    _corpseCount++;
                }
            }
            _corpses.VisibleInstanceCount = _corpseCount;
        }
    }

    // Playable zone = baked RT (blood/corpses); margin band = clean scatter,
    // wrapped. Unshaded like the old floor material; fog still applies.
    private Shader? _floorShader;
    private Shader FloorShader => _floorShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled;
            uniform sampler2D baked : source_color;
            uniform sampler2D clean_t : source_color, repeat_enable;
            uniform float margin_scale = 1.3;
            // Death-cinematic view zoom: sample a shrunk window about
            // view_center (uv space) so the ground magnifies in lockstep with
            // the sprite view transform. 1.0 = off.
            uniform float view_zoom = 1.0;
            uniform vec2 view_center = vec2(0.5);
            // Edge vignette: darkens the floor OUTSIDE the playfield so the
            // spawn margin reads as off-stage instead of as more arena. This is
            // the treatment PLAN section 6 has owed since M3 -- survival
            // creatures spawn 40 game units (~0.039 of the side) beyond the
            // playfield and walk in, which the flat game's camera cropped
            // entirely.
            //
            // Start just inside the playable square, then reach black two thirds
            // of the way across the margin to the diorama edge. The outer third
            // stays black, cleanly separating the board from the real room in MR.
            // Deriving the distance from margin_scale keeps that 2/3 proportion
            // stable if the floor margin changes.
            uniform float vignette_span = 0.6666667;
            uniform float vignette_inset = 0.02;
            uniform float vignette_strength = 1.0;
            void fragment() {
                vec2 uv = UV * margin_scale - vec2((margin_scale - 1.0) * 0.5);
                uv = view_center + (uv - view_center) / view_zoom;
                bool inside = uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0;
                vec3 c = inside ? texture(baked, uv).rgb : texture(clean_t, fract(uv)).rgb;
                // 0 on the playfield boundary, growing outward, negative inside.
                float outside = max(max(-uv.x, uv.x - 1.0), max(-uv.y, uv.y - 1.0));
                float margin = (margin_scale - 1.0) * 0.5;
                float band = max(margin * vignette_span + vignette_inset, 0.0001);
                float v = clamp((outside + vignette_inset) / band, 0.0, 1.0);
                ALBEDO = c * (1.0 - v * vignette_strength);
            }
            """,
    };

    /// <summary>Bodyset frame for a creature type (manifest map, id fallback).</summary>
    private int CorpseFrameFor(int creatureTypeId)
    {
        int frame = _corpseFrames.TryGetValue(creatureTypeId, out int f) ? f : (creatureTypeId & 0xF);
        return frame & (_corpseGrid * _corpseGrid - 1);
    }

    /// <summary>Clear the accumulated terrain FX (blood/corpses). Call on session
    /// restart so a fresh arena starts clean.</summary>
    public void ResetTerrainFx()
    {
        _decalCursor = _decalCount = 0;
        _corpseCursor = _corpseCount = 0;
        if (_decals != null)
        {
            _decals.VisibleInstanceCount = 0;
        }
        if (_corpses != null)
        {
            _corpses.VisibleInstanceCount = 0;
        }
    }

    /// <summary>A flat quad basis lying on the plane (normal +Y), rotated by
    /// <paramref name="angle"/> about the up axis, with local width/height.
    ///
    /// FLAT-SPRITE ORIENTATION CONVENTION (read this before adding flat sprites):
    /// the player sits on the arena's near (-z) edge and looks toward +z and down.
    /// For a texture to read UPRIGHT from that view, its top must point to the FAR
    /// (+z) edge. This basis does that (local +Y -> world +z), so use it for any
    /// flat textured sprite on the plane (decals, corpses, bonuses, freeze). The
    /// `RotX(-90)` FlatBasis maps texture-top to the NEAR (-z) edge -> UPSIDE DOWN;
    /// it is only safe for solid-colour or radially-symmetric quads. Diegetic
    /// panels/buttons that stand up or lie flat have the same gotcha (e.g. the flat
    /// pause toggle needs an extra 180 about local Z to read upright).</summary>
    private static Basis FlatQuadBasis(float angle, float width, float height)
    {
        float c = Mathf.Cos(angle);
        float s = Mathf.Sin(angle);
        return new Basis(
            new Vector3(c, 0.0f, s) * width,
            new Vector3(-s, 0.0f, c) * height,
            new Vector3(0.0f, 1.0f, 0.0f));
    }

    /// <summary>One shared MultiMesh of soft round blobs drawn flat on the plane
    /// under the creature/player sprites to ground them (PLAN §6). Drawn first
    /// (lowest RenderPriority) so every sprite sits on top.</summary>
    private void BuildShadows()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.0f, 0.0f, 0.0f, 0.55f),
            AlbedoTexture = MakeSoftCircleTexture(64),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            RenderPriority = 1, // under all sprite layers (creatures start at 6)
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _shadows = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = ShadowCap,
            VisibleInstanceCount = 0,
        };
        _shadowNode = new MultiMeshInstance3D { Multimesh = _shadows, MaterialOverride = material };
        AddChild(_shadowNode);
    }

    /// <summary>A radial-gradient blob texture (opaque-ish centre fading to a
    /// transparent edge) used to tint the shadow quads into soft ovals.</summary>
    private static ImageTexture MakeSoftCircleTexture(int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2((x - c) / c, (y - c) / c).Length();
                float a = Mathf.Clamp(1.0f - d, 0.0f, 1.0f);
                a *= a; // soften the falloff
                img.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    // Sprite shader for animated layers: samples one 8x8 cell chosen per instance
    // via INSTANCE_CUSTOM = (uv_offset_x, uv_offset_y, uv_scale, unused). Unshaded,
    // alpha-blended, depth-write off so material RenderPriority alone orders the
    // flat 2.5D layers (matches the StandardMaterial path in BuildSpriteLayer).
    private Shader? _spriteShader;
    private Shader SpriteShader => _spriteShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never;
            uniform sampler2D sheet : source_color, filter_nearest;
            varying vec4 inst;
            void vertex() { inst = INSTANCE_CUSTOM; }
            void fragment() {
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                ALBEDO = c.rgb;
                ALPHA = c.a;
            }
            """,
    };

    /// <summary>Copy one sim snapshot into the layers' current buffers, rolling
    /// the previous current into prev. Call once per sim tick.</summary>
    public void PushSnapshot(in SnapshotView view)
    {
        _players.BeginPush();
        _playerLegs.BeginPush();
        _muzzleCount = 0;
        foreach (Sim.PlayerSnap p in view.Players)
        {
            // trooper.py:155-196 — LEGS from the walk phase (frame = clamp(
            // int(move_phase+0.5), 0, 14)) rotated by the MOVE heading; TORSO
            // = leg frame + 16 rotated by the AIM heading with the recoil
            // offset (muzzle_flash_alpha * 12 units along aim + 90 deg).
            var game = new Vector2(p.X, p.Y);
            if (p.Health <= 0.0f)
            {
                // Dead: the corpse frame ramp (trooper.py:276 — frame = 32 +
                // int((16 - death_timer) * 1.25), clamped 32..52, rotated by
                // aim). One quad on the torso layer; no legs, no recoil. The
                // low lifecycle stage drops it to ground level like a dying
                // creature so it lies flat on the arena.
                int corpseFrame = Mathf.Clamp(32 + (int)((16.0f - p.DeathTimer) * 1.25f), 32, 52);
                _players.Add(game, p.AimHeading, p.Size, lifecycleStage: 0.0f, frame: corpseFrame);
                continue;
            }
            int legFrame = Mathf.Clamp((int)(p.MovePhase + 0.5f), 0, 14);
            _playerLegs.Add(game, p.Heading, p.Size, frame: legFrame);
            float recoilDir = p.AimHeading + Mathf.Pi * 0.5f;
            Vector2 recoil = new Vector2(Mathf.Cos(recoilDir), Mathf.Sin(recoilDir))
                * (p.MuzzleFlashAlpha * 12.0f);
            _players.Add(game + recoil, p.AimHeading, p.Size, frame: legFrame + 16);
            // Muzzle flash at the gun, along aim, while it's firing.
            if (p.MuzzleFlashAlpha > 0.01f && _muzzleCount < _muzzles.Length)
            {
                _muzzles[_muzzleCount++] = new Muzzle
                {
                    Game = new Vector2(p.X, p.Y),
                    Heading = p.AimHeading,
                    Alpha = p.MuzzleFlashAlpha,
                    SizeGame = p.Size,
                };
            }
        }

        foreach (Layer layer in _creatureLayers.Values)
        {
            layer.BeginPush();
        }
        _creatureFallback.BeginPush();
        _energizerTimer = view.Header.EnergizerTimer;
        foreach (Sim.CreatureSnap c in view.Creatures)
        {
            Layer target = _creatureLayers.TryGetValue(c.TypeId, out Layer? l) ? l : _creatureFallback;
            target.Add(new Vector2(c.X, c.Y), c.Heading, c.Size, c.AnimPhase, c.Flags,
                maxHp: c.MaxHp, lifecycleStage: c.LifecycleStage,
                baseColor: new Color(c.R, c.G, c.B, c.A), hitFlash: c.HitFlashTimer,
                identity: ((ulong)c.Generation << 32) | (uint)c.PoolIndex);
        }

        // Projectiles + secondaries: faithful per-type renderers (tick-rate; the
        // handlers draw their own fade stages for life_timer < 0.4, so the old
        // blanket stopped-projectile cull is gone — the fade visuals it dropped
        // are back). Detonations render their two-quad core+halo here too,
        // replacing the synthetic EmitFx explosion blobs (and the possible
        // double-draw the audit flagged).
        RenderProjectiles(view);

        _bonuses.BeginPush();
        foreach (Sim.BonusSnap b in view.Bonuses)
        {
            // Icon frame from bonus_id (bonuses.png 4x4); POINTS@1000 uses +1;
            // WEAPON (and any unmapped id) falls back to frame 0 (the bubble).
            int frame = _bonusIcons.TryGetValue(b.BonusId, out int f) ? f : 0;
            if (b.BonusId == 1 && b.Amount == 1000)
            {
                frame += 1;
            }
            // Icons read ~32 game units in the reference (bonus_icon_src, 32*scale).
            _bonuses.Add(new Vector2(b.X, b.Y), 0.0f, 32.0f, frame: frame);
        }

        _freezeTimer = view.Header.FreezeTimer;
        _monsterVision = view.Header.MonsterVision != 0;
        RenderParticles(view);
        RenderGlowPool(view);
        RenderFreezeOverlay(view);
        RenderCreatureOverlays(view);
        UpdateTargetHealthBar(view);
    }

    // ---- Target (enemy) health bar — Doctor perk (base_gameplay_mode.py
    // _draw_target_health_bar). While the player has Doctor, the first creature
    // within 12 game units of the aim point gets a floating bar 32 units below
    // it (game +y): 64 units wide, colour lerping red -> green with HP ratio,
    // deliberately subtle alphas (fg 0.2 / bg 0.08) like the original.
    private MeshInstance3D? _targetBarBg;
    private MeshInstance3D? _targetBarFg;
    private StandardMaterial3D? _targetBarBgMat;
    private StandardMaterial3D? _targetBarFgMat;
    private const float TargetBarLift = 0.013f;

    private MeshInstance3D BuildTargetBarQuad(int priority, out StandardMaterial3D mat)
    {
        mat = new StandardMaterial3D
        {
            AlbedoColor = Colors.White,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            RenderPriority = priority,
        };
        var node = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            MaterialOverride = mat,
            Visible = false,
        };
        AddChild(node);
        return node;
    }

    private void UpdateTargetHealthBar(in SnapshotView view)
    {
        _targetBarBg ??= BuildTargetBarQuad(30, out _targetBarBgMat);
        _targetBarFg ??= BuildTargetBarQuad(31, out _targetBarFgMat);

        bool found = false;
        if (view.Header.PlayerCount > 0)
        {
            Sim.PlayerSnap p = view.Players[0];
            if ((p.PerkFlags & Sim.PlayerSnap.PerkFlagDoctor) != 0 && p.Health > 0.0f)
            {
                var aim = new Vector2(p.AimX, p.AimY);
                foreach (Sim.CreatureSnap c in view.Creatures)
                {
                    // creature_find_in_radius: first pool-order creature within
                    // 12 units of the aim point.
                    if (c.MaxHp <= 0.0f || (new Vector2(c.X, c.Y) - aim).LengthSquared() > 12.0f * 12.0f)
                    {
                        continue;
                    }
                    float ratio = Mathf.Clamp(c.Hp / c.MaxHp, 0.0f, 1.0f);
                    PlaceTargetBar(new Vector2(c.X, c.Y), ratio);
                    found = true;
                    break;
                }
            }
        }
        _targetBarBg.Visible = found;
        _targetBarFg.Visible = found;
    }

    private void PlaceTargetBar(Vector2 creatureGame, float ratio)
    {
        float k = _arenaSideMeters / _worldSize;
        // Bar rect in game units: 64 wide x 4 tall at (creature + (0, +32)),
        // inner fill 2 tall x (62 * ratio) left-aligned (hud.py:220-254).
        var barCentre = new Vector2(creatureGame.X, creatureGame.Y + 32.0f);
        float r = (1.0f - ratio) * 0.9f + 0.1f;
        float g = ratio * 0.9f + 0.1f;

        Vector3 basePos = Mapper.GameToArenaLocal(barCentre, _arenaSideMeters, _worldSize)
            + new Vector3(0.0f, TargetBarLift * _heightScale, 0.0f);
        _targetBarBg!.Position = basePos;
        _targetBarBg.Basis = FlatBasis.Scaled(new Vector3(64.0f * k, 1.0f, 4.0f * k));
        _targetBarBgMat!.AlbedoColor = new Color(r * 0.6f, g * 0.6f, 0.7f * 0.6f, 0.2f * 0.4f);

        float innerW = Mathf.Max(0.0f, 62.0f * ratio);
        // Left-aligned fill: shift the centre so the fill grows from the left edge.
        float offX = (-31.0f + innerW * 0.5f) * k;
        _targetBarFg!.Position = basePos + new Vector3(offX, 0.0005f, 0.0f);
        _targetBarFg.Basis = FlatBasis.Scaled(new Vector3(innerW * k, 1.0f, 2.0f * k));
        _targetBarFgMat!.AlbedoColor = new Color(r, g, 0.7f, 0.2f);
    }

    /// <summary>Write interpolated instance transforms for the current frame.
    /// <paramref name="frac"/> is 0..1 between the last two ticks.</summary>
    public void Interpolate(float frac)
    {
        frac = Mathf.Clamp(frac, 0.0f, 1.0f);
        _needleCount = 0;
        _shadowCount = 0;
        // Legs cast the ground shadow (they're the part standing on it); the
        // floating torso doesn't add a second blob.
        InterpolateLayer(_playerLegs, frac, sprite: true, castShadow: true);
        InterpolateLayer(_players, frac, sprite: true, castShadow: false);
        foreach (Layer layer in _creatureLayers.Values)
        {
            InterpolateLayer(layer, frac, sprite: true, castShadow: true);
        }
        InterpolateLayer(_creatureFallback, frac, sprite: false, castShadow: true);
        InterpolateLayer(_bonuses, frac, sprite: false);
        _shadows.VisibleInstanceCount = _shadowCount;
        EmitFx();
        if (_needles != null)
        {
            _needles.VisibleInstanceCount = _needleCount;
        }
    }

    /// <summary>Draw the captured muzzle-flash bursts into the shared additive
    /// fx mesh (positions are tick-captured; transient, not interpolated).</summary>
    private void EmitFx()
    {
        float k = ViewK;
        _fxCount = 0;
        for (int i = 0; i < _muzzleCount; i++)
        {
            Muzzle m = _muzzles[i];
            Vector2 viewGame = ViewGame(m.Game);
            if (_viewZoom > 1.0f && OutsideDrawBounds(viewGame))
            {
                continue;
            }
            // At the gun barrel, a touch ahead of the player along aim (was ~4
            // sprite-lengths out; the flash blob is large so it still reads as the
            // muzzle even sitting close to the player).
            Vector3 dir = ForwardFromHeading(m.Heading);
            Vector3 arena = Mapper.GameToArenaLocal(viewGame, _arenaSideMeters, _worldSize)
                + dir * (m.SizeGame * k * 0.2f) + new Vector3(0.0f, 0.012f, 0.0f);
            float s = Mathf.Max(m.SizeGame * k * 2.2f * m.Alpha, 0.002f);
            var color = new Color(1.0f, 0.85f, 0.5f, m.Alpha);
            AddFx(arena, s, color);
        }
        _fx.VisibleInstanceCount = _fxCount;
    }

    private void AddFx(Vector3 pos, float size, Color color)
    {
        if (_fxCount >= FxCap)
        {
            return;
        }
        Basis basis = FlatBasis.Scaled(new Vector3(size, size, size));
        _fx.SetInstanceColor(_fxCount, color);
        _fx.SetInstanceTransform(_fxCount, new Transform3D(basis, pos));
        _fxCount++;
    }

    private void InterpolateLayer(Layer layer, float frac, bool sprite, bool castShadow = false)
    {
        float k = ViewK;
        // Interpolate only when the active set is unchanged (equal counts);
        // dense arrays have no stable ids so a spawn/death frame reorders them,
        // which would streak tokens across the arena for a frame (M2 finding).
        bool interp = layer.PrevCount == layer.CurrCount;
        for (int i = 0; i < layer.CurrCount; i++)
        {
            Ent cur = layer.Curr[i];
            Vector2 game = cur.Game;
            float angle = cur.Angle;
            float sizeGame = cur.SizeGame;
            if (interp && (!layer.HasStableIdentity || layer.Prev[i].Identity == cur.Identity))
            {
                Ent prev = layer.Prev[i];
                game = prev.Game.Lerp(cur.Game, frac);
                angle = LerpAngle(prev.Angle, cur.Angle, frac);
                sizeGame = Mathf.Lerp(prev.SizeGame, cur.SizeGame, frac);
            }

            // Death-cinematic zoom: magnified positions past the visible floor
            // square (the zoomed window's edge) collapse to nothing.
            Vector2 viewGame = ViewGame(game);
            if (_viewZoom > 1.0f && OutsideDrawBounds(viewGame))
            {
                layer.Mesh.SetInstanceTransform(i, new Transform3D(
                    new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), Vector3.Zero));
                continue;
            }

            Vector3 arena = Mapper.GameToArenaLocal(viewGame, _arenaSideMeters, _worldSize);
            // Flat on the plane at a constant per-layer lift; layering is by draw
            // order (RenderPriority), not physical height. HeightScale multiplies
            // every layer's lift together so the bands keep their relative order
            // and spacing — the whole stack rises or settles as one.
            Vector3 pos = arena + new Vector3(0.0f, layer.Lift * _heightScale, 0.0f);
            // Faithful creature world size: 64 * clamp(size/64, 0.25, 2.0) game
            // units (creature_render_type). Others scale linearly from size.
            float sizeUnits = layer.ClampRefSize
                ? 64.0f * Mathf.Clamp(sizeGame / 64.0f, 0.25f, 2.0f)
                : sizeGame;
            float meters = Mathf.Max(sizeUnits * k * layer.SizeScale, 0.002f);

            // Edge fade applies to creature layers only (players/bonuses never
            // leave the terrain): shadows shrink with it, sprites fade via the
            // tint alpha below.
            float edgeFade = layer.HasStableIdentity ? EdgeFadeAlpha(game) : 1.0f;

            if (castShadow)
            {
                AddShadow(arena, meters * edgeFade);
            }

            Basis basis;
            if (sprite)
            {
                // Face the sim direction directly (no RotY handedness flip),
                // plus the sheet's art-facing correction, then a fixed back-tilt
                // (2.5D). Raise the centre so the tilted base stays near the plane.
                basis = SpriteTilt * FlatFacingBasis(ForwardFromHeading(angle + layer.HeadingOffset), meters);
                pos.Y += meters * 0.5f * SpriteTiltSin;
            }
            else if (layer.UvIndexed)
            {
                // Textured icons (bonuses). FlatQuadBasis is a reflection (negative
                // determinant), which mirrors the art ("text backwards"); negate the
                // width to flip it back to a proper, non-mirrored, upright icon
                // (texture top -> +z far edge from the player's downward view).
                basis = FlatQuadBasis(angle, -meters, meters);
            }
            else
            {
                basis = (new Basis(Vector3.Up, angle) * FlatBasis).Scaled(new Vector3(meters, meters, meters));
            }
            // Death staging: the moment a creature starts dying (lifecycle_stage
            // drops below 16) drop it to ground level so its death frames + fade
            // play flat on the arena, aligned with the terrain corpse stamp —
            // otherwise the dying sprite fades at the raised enemy lift while the
            // corpse stamps at ground, reading as two vertically-separated bodies.
            if (cur.LifecycleStage < 16.0f)
            {
                pos.Y = CorpseLift;
            }
            layer.Mesh.SetInstanceTransform(i, new Transform3D(basis, pos));

            if (layer.Animated)
            {
                // Frame from the current tick's phase (not interpolated: the
                // phase wraps, so lerping across the seam would glitch; 60 Hz is
                // already smooth). Custom data = (uvOffX, uvOffY, uvScale, 0).
                int grid = layer.Grid;
                // Death staging (draw.py draw_creatures): a long-strip creature
                // plays a death frame sequence driven by lifecycle_stage as it
                // ramps 16 -> 0 (phase = base+15 - stage - 0.5), then a negative
                // stage selects the corpse fallback frame (phase = -1). Mirroring
                // is off while dying (stage < 16).
                float phase = cur.AnimPhase;
                if (CreatureAnim.IsLongStrip(cur.Flags))
                {
                    if (cur.LifecycleStage < 0.0f)
                    {
                        phase = -1.0f;
                    }
                    else if (cur.LifecycleStage < 16.0f)
                    {
                        phase = layer.BaseFrame + 0x0F - cur.LifecycleStage - 0.5f;
                    }
                }
                bool mirror = layer.MirrorLong && cur.LifecycleStage >= 16.0f;
                int frame = CreatureAnim.SelectFrame(phase, layer.BaseFrame, mirror, cur.Flags);
                int cells = grid * grid;
                frame = frame < 0 ? 0 : (frame >= cells ? cells - 1 : frame);
                float inv = 1.0f / grid;
                // Custom data = (uvOffX, uvOffY, uvScale, hitFlash). The w channel
                // drives the additive white hit-flash in TintedSpriteShader.
                float flash = cur.HitFlash > 0.0f ? Mathf.Clamp(cur.HitFlash / 0.2f, 0.0f, 1.0f) : 0.0f;
                layer.Mesh.SetInstanceCustomData(i, new Color(
                    (frame % grid) * inv,
                    (frame / grid) * inv,
                    inv,
                    flash));
            }

            if (layer.UvIndexed)
            {
                // Fixed atlas cell per instance (bonuses): custom data = UV cell.
                int grid = layer.Grid;
                int cells = grid * grid;
                int frame = cur.Frame < 0 ? 0 : (cur.Frame >= cells ? cells - 1 : cur.Frame);
                float inv = 1.0f / grid;
                layer.Mesh.SetInstanceCustomData(i, new Color(
                    (frame % grid) * inv,
                    (frame / grid) * inv,
                    inv,
                    0.0f));
            }

            if (layer.Tinted)
            {
                Color tint = CreatureTint(cur);
                tint.A *= edgeFade;
                layer.Mesh.SetInstanceColor(i, tint);
            }

            if (sprite && _debug && _needles != null)
            {
                AddNeedle(arena, angle);
            }
        }
        layer.Mesh.VisibleInstanceCount = layer.CurrCount;
    }

    // ---- Arena-edge spawn treatment (PLAN §6, presentation-only) ----
    // Survival creatures spawn up to 40 game units OUTSIDE the terrain and walk
    // in (rand_survival_spawn_pos: -40 / size+40). The flat game's camera crops
    // that, but the diorama shows the whole plane, so they popped into
    // existence standing on the margin band. Fade them in across a short band
    // inside the bounds instead; fully outside = invisible. Spawn positions
    // are exact sim state — only the presentation fades.
    /// <summary>How far outside the playfield survival creatures are spawned
    /// (`rand_survival_spawn_pos`: edge = -40 or terrain+40). The flat game's
    /// camera crops this margin entirely; the diorama shows the whole plane.</summary>
    private const float SpawnMarginGame = 40.0f;

    private const float EdgeFadeBandGame = SpawnMarginGame;

    /// <summary>Creature opacity near the arena edge: 0 at the spawn line,
    /// ramping to 1 by the time they reach the playfield.
    ///
    /// The band deliberately sits ENTIRELY OUTSIDE the playfield. It used to
    /// ramp from the playfield edge inward, which hid the spawn itself but then
    /// played the whole materialisation in plain view a few units onto the
    /// board — you watched creatures assemble out of nothing mid-arena. Running
    /// it across the spawn margin instead means they finish fading before they
    /// arrive, which is the behaviour the original got for free by cropping the
    /// margin off-screen.
    ///
    /// Still an interim treatment: PLAN section 6 owes a rim mask / vignette so
    /// the margin reads as off-stage rather than as visible floor. This only
    /// moves where the fade happens; it does not hide the margin.</summary>
    private float EdgeFadeAlpha(Vector2 game)
    {
        // Signed distance inside the playfield; negative out in the margin.
        float edge = Mathf.Min(
            Mathf.Min(game.X, _worldSize - game.X),
            Mathf.Min(game.Y, _worldSize - game.Y));
        return Mathf.Clamp((edge + SpawnMarginGame) / EdgeFadeBandGame, 0.0f, 1.0f);
    }

    /// <summary>Per-creature draw tint (draw.py draw_creatures): the spawn-template
    /// base tint (ABI v4 CreatureSnap color), then the energizer blend (weak
    /// creatures, max_hp &lt; 500, lerp toward (0.5,0.5,1,1) while the bonus is
    /// active), then a negative-lifecycle alpha fade, then the white hit-flash
    /// brighten while the hit timer is running.</summary>
    private Color CreatureTint(in Ent e)
    {
        Color c = e.BaseColor;
        float r = c.R, g = c.G, b = c.B, a = c.A;
        if (_energizerTimer > 0.0f && e.MaxHp < 500.0f)
        {
            // Native clamps the timer to [0,1] and lerps the whole RGBA toward
            // (0.5, 0.5, 1.0, 1.0).
            float t = Mathf.Clamp(_energizerTimer, 0.0f, 1.0f);
            r = Mathf.Lerp(r, 0.5f, t);
            g = Mathf.Lerp(g, 0.5f, t);
            b = Mathf.Lerp(b, 1.0f, t);
            a = Mathf.Lerp(a, 1.0f, t);
        }
        if (e.LifecycleStage < 0.0f)
        {
            a = Mathf.Max(0.0f, a + e.LifecycleStage * 0.1f);
        }
        // The hit-flash is now applied ADDITIVELY in TintedSpriteShader (via the
        // custom-data w channel) — a multiply tint toward white did nothing.
        return new Color(r, g, b, a);
    }

    /// <summary>Add a flat round shadow blob on the plane at <paramref name="arena"/>,
    /// sized to the sprite's footprint.</summary>
    private void AddShadow(Vector3 arena, float meters)
    {
        if (_shadowCount >= ShadowCap)
        {
            return;
        }
        float s = meters * ShadowScale;
        Basis basis = FlatBasis.Scaled(new Vector3(s, s, s));
        Vector3 pos = arena + new Vector3(0.0f, ShadowLift, 0.0f);
        _shadows.SetInstanceTransform(_shadowCount++, new Transform3D(basis, pos));
    }

    /// <summary>Direction a sim heading points, in arena space: game direction
    /// (sin theta, -cos theta) with game +y mapped to arena +z.</summary>
    private static Vector3 ForwardFromHeading(float heading)
        => new Vector3(Mathf.Sin(heading), 0.0f, -Mathf.Cos(heading));

    /// <summary>Flat sprite basis (lies on the plane, normal up) whose local +Y
    /// points along <paramref name="forward"/>, scaled by <paramref name="meters"/>.
    /// Columns are local x (in-plane perpendicular), y (forward), z (up).</summary>
    private static Basis FlatFacingBasis(Vector3 forward, float meters)
    {
        var b = new Basis(
            new Vector3(-forward.Z, 0.0f, forward.X),
            forward,
            new Vector3(0.0f, 1.0f, 0.0f));
        return b.Scaled(new Vector3(meters, meters, meters));
    }

    /// <summary>Flat quad basis lying on the plane, elongated <paramref name="length"/>
    /// along <paramref name="forward"/> (travel) and <paramref name="width"/> across —
    /// a projectile streak.</summary>
    private static Basis StreakBasis(Vector3 forward, float length, float width)
        => new Basis(
            new Vector3(-forward.Z, 0.0f, forward.X) * width,
            forward * length,
            new Vector3(0.0f, 1.0f, 0.0f));

    /// <summary>Debug: a magenta needle from <paramref name="arena"/> along the
    /// raw heading direction, so we can confirm sprite facing against it.</summary>
    private void AddNeedle(Vector3 arena, float heading)
    {
        if (_needles == null || _needleCount >= NeedleCap)
        {
            return;
        }
        const float len = 0.03f;
        const float wid = 0.004f;
        Vector3 forward = ForwardFromHeading(heading);
        var basis = new Basis(
            new Vector3(-forward.Z, 0.0f, forward.X) * wid,
            forward * len,
            new Vector3(0.0f, 1.0f, 0.0f) * 0.001f);
        Vector3 pos = arena + forward * (len * 0.5f) + new Vector3(0.0f, 0.03f, 0.0f);
        _needles.SetInstanceTransform(_needleCount++, new Transform3D(basis, pos));
    }

    private static float LerpAngle(float from, float to, float t)
    {
        float diff = Mathf.Wrap(to - from, -Mathf.Pi, Mathf.Pi);
        return from + diff * t;
    }

    // ---- Manifest ----

    private sealed class SpriteDesc
    {
        public string sheet { get; set; } = "";
        public int grid { get; set; } = 1;
        public int frame { get; set; }
        public float[] pivot { get; set; } = { 0.5f, 0.5f };

        [JsonPropertyName("offset_deg")]
        public float offsetDeg { get; set; }

        public int priority { get; set; } = 8;

        // Animation layout (creatures): base frame + long-strip mirror fold,
        // consumed by CreatureAnim.SelectFrame with the snapshot's anim_phase.
        [JsonPropertyName("base_frame")]
        public int baseFrame { get; set; }

        public bool mirror { get; set; }
    }

    private sealed class EffectUv
    {
        public float[] uv_off { get; set; } = { 0.0f, 0.0f };
        public float uv_scale { get; set; } = 1.0f;
    }

    private sealed class CorpseDesc
    {
        public string sheet { get; set; } = "";
        public int grid { get; set; } = 4;
        public Dictionary<string, int> frames { get; set; } = new();
        public int priority { get; set; } = -2;
    }

    private sealed class TerrainDesc
    {
        public Dictionary<string, string> slots { get; set; } = new();
    }

    private sealed class BonusDesc
    {
        public string sheet { get; set; } = "";
        public int grid { get; set; } = 4;
        public Dictionary<string, int> icons { get; set; } = new();
        public int priority { get; set; } = 25;
    }

    private sealed class SpriteManifest
    {
        public Dictionary<string, SpriteDesc>? creatures { get; set; }
        public SpriteDesc? player { get; set; }
        public Dictionary<string, EffectUv>? effects { get; set; }
        public string? effects_sheet { get; set; }
        public CorpseDesc? corpses { get; set; }
        public TerrainDesc? terrain { get; set; }
        public BonusDesc? bonuses { get; set; }
    }

    private static SpriteManifest? LoadManifest()
    {
        string path = SpriteDir + "sprite_manifest.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.PushWarning($"CrimsonVR: {path} missing; rendering colored quads. Run tools/bake_assets.py.");
            return null;
        }
        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        string json = file.GetAsText();
        try
        {
            return JsonSerializer.Deserialize<SpriteManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException e)
        {
            GD.PushError($"CrimsonVR: bad sprite manifest: {e.Message}");
            return null;
        }
    }
}
