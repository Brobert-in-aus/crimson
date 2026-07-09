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

        public void Add(Vector2 game, float angle, float sizeGame, float animPhase = 0.0f, uint flags = 0, int typeId = 0, float maxHp = 0.0f, float lifecycleStage = 16.0f, Color? baseColor = null, float hitFlash = 0.0f, int frame = 0)
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
            };
        }
    }

    private const int PlayerCap = 4;
    private const int CreatureCapPerType = 1024;
    // Below this projectile speed (game units/step) a bullet is treated as
    // stopped/lodged and dropped to the ground (a moving bullet's base speed is
    // ~1.5); tune against the ABI v6 velocity.
    private const float StoppedProjectileSpeed = 0.4f;
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
    public void SetGraphicsDetail(int level)
    {
        if (_shadowNode != null)
        {
            _shadowNode.Visible = level >= 3;
        }
        if (_particleNode != null)
        {
            _particleNode.Visible = level >= 2;
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
    private float _worldSize;
    private float _energizerTimer; // global energizer bonus timer (snapshot header)

    private Layer _players = null!;
    private readonly Dictionary<int, Layer> _creatureLayers = new();
    private Layer _creatureFallback = null!;
    private Layer _projectiles = null!;
    private Layer _secondaries = null!;
    private Layer _bonuses = null!;
    private MultiMesh? _needles;
    private int _needleCount;
    private MultiMesh _shadows = null!;
    private int _shadowCount;

    // Effects (slice 6a): additive glow bursts for muzzle flashes (player
    // MuzzleFlashAlpha) and explosions (secondary detonation). Captured at tick
    // time in PushSnapshot, drawn each frame in Interpolate. True sim particle
    // pools (blood/gibs) need an ABI stream — slice 6b.
    private struct Muzzle { public Vector2 Game; public float Heading; public float Alpha; public float SizeGame; }
    private struct Explosion { public Vector2 Game; public float Scale; public float T; }
    private readonly Muzzle[] _muzzles = new Muzzle[PlayerCap];
    private int _muzzleCount;
    private readonly Explosion[] _explosionsCap = new Explosion[512];
    private int _explosionCapCount;
    private MultiMesh _fx = null!;
    private int _fxCount;
    private const int FxCap = 2048;

    // Sprite-effect pool (slice 6b): blood/gibs/explosions/casings from the ABI
    // particle stream, drawn from particles.png. effect_id -> precomputed UV cell
    // (uv offset + scale) from the bake; per-instance UV + color via the shader.
    private MultiMesh? _particles;
    private readonly Dictionary<int, Vector3> _effectUv = new(); // effect_id -> (offX, offY, scale)

    // Freeze overlay (draw_freeze_overlay): while the global freeze bonus is
    // active (ABI v5 header), each creature wears a FREEZE_SHATTER frame from
    // particles.png. One shared mesh, drawn over the creatures at tick rate.
    private MultiMesh? _freezeMesh;
    private float _freezeTimer;
    private const int FreezeShatterEffectId = 0x0E; // EffectId.FREEZE_SHATTER
    private const int FreezeCap = 1024;

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
    private const float CorpseLift = 0.0018f; // above blood, below shadows
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
    private const float FloorMarginScale = 1.3f; // floor size vs playfield side
    private const float FloorY = -0.001f;        // just below the decal plane
    private const float FloorTile = 4.0f;        // ground texture repeats across the floor

    // Bonus icons (bonuses.png, 4x4 grid): bonus_id -> icon frame. Rendered on
    // the _bonuses layer via per-instance UV (Layer.UvIndexed). Icon-only for now
    // (the bubble container + pulse/rotate are refinements).
    private readonly Dictionary<int, int> _bonusIcons = new();
    private int _bonusGrid = 4;

    // Per-type projectile glow tint (known_proj_rgb, projectile_render_registry.py).
    // Colors only (not asset-derived); default is the tan bullet glow.
    private static readonly Color ProjTintDefault = new(240f / 255f, 220f / 255f, 160f / 255f);
    private static readonly Dictionary<int, Color> ProjTints = new()
    {
        // ProjectileTemplateId -> rgb (KNOWN_PROJ_RGB_BY_TYPE_ID).
        { 21, new Color(120f / 255f, 200f / 255f, 1.0f) },   // ION_RIFLE (blue)
        { 22, new Color(120f / 255f, 200f / 255f, 1.0f) },   // ION_MINIGUN
        { 23, new Color(120f / 255f, 200f / 255f, 1.0f) },   // ION_CANNON
        { 45, new Color(1.0f, 170f / 255f, 90f / 255f) },    // FIRE_BULLETS (orange)
        { 24, new Color(160f / 255f, 1.0f, 170f / 255f) },   // SHRINKIFIER (green)
        { 25, new Color(240f / 255f, 120f / 255f, 1.0f) },   // BLADE_GUN (magenta)
    };

    private static Color ProjTint(int typeId)
        => ProjTints.TryGetValue(typeId, out Color c) ? c : ProjTintDefault;

    private static readonly Basis FlatBasis = Basis.FromEuler(new Vector3(-Mathf.Pi / 2.0f, 0.0f, 0.0f));

    // Fixed back-tilt applied to sprite bases (about arena X so the lean is the
    // same for every sprite regardless of heading). Negative angle leans the top
    // toward -z (the player). Zero when SpriteTiltDegrees is 0.
    private static readonly Basis SpriteTilt =
        new Basis(Vector3.Right, -Mathf.DegToRad(SpriteTiltDegrees));
    private static readonly float SpriteTiltSin = Mathf.Sin(Mathf.DegToRad(SpriteTiltDegrees));

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
        _players = manifest?.player is { } pd
            ? BuildSpriteLayer(PlayerCap, pd, new Color(0.95f, 0.95f, 0.95f), lift: 0.012f, sizeScale: 1.0f)
            : BuildColorLayer(PlayerCap, new Color(0.95f, 0.95f, 0.95f), lift: 0.012f, sizeScale: 1.0f, renderPriority: 15);

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
                    _creatureLayers[typeId] = layer;
                }
            }
        }
        // Fallback for unmapped creature types (e.g. bosses) so nothing vanishes.
        _creatureFallback = BuildColorLayer(CreatureCapPerType, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 1.0f, renderPriority: 8);
        _creatureFallback.ClampRefSize = true;

        // Native pass order (top of the stack): player < projectiles/effects < bonuses/UI.
        // Projectiles/secondaries: additive per-type-tinted glow streaks. Lifted to
        // the PLAYER's plane (0.012, above the creature plane at 0.008) so bullets
        // emerge from the shooter, not from the lower creature plane (in-headset
        // finding: the player reads on a higher plane, which is wanted).
        _projectiles = BuildStreakLayer(ProjectileCap, lift: 0.012f, sizeScale: 5.0f, renderPriority: 20);
        _secondaries = BuildStreakLayer(SecondaryCap, lift: 0.012f, sizeScale: 7.0f, renderPriority: 20);
        _bonuses = BuildBonusLayer(manifest);

        BuildFloor(manifest);
        BuildShadows();
        BuildFx();
        BuildParticles(manifest);
        BuildDecals(manifest);
        BuildCorpses(manifest);

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
    private Shader? _particleShader;
    private Shader ParticleShader => _particleShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never;
            uniform sampler2D sheet : source_color, filter_linear;
            varying vec4 inst;
            varying vec4 col;
            void vertex() { inst = INSTANCE_CUSTOM; col = COLOR; }
            void fragment() {
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                ALBEDO = c.rgb * col.rgb;
                ALPHA = c.a * col.a;
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

        // Freeze-shatter overlay shares particles.png (same UV table); RenderPriority
        // 24 sits just over the particle layer.
        var freezeMat = new ShaderMaterial { Shader = ParticleShader, RenderPriority = 24 };
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
        float k = _arenaSideMeters / _worldSize;
        int n = 0;
        int idx = 0;
        foreach (Sim.CreatureSnap c in view.Creatures)
        {
            if (n >= FreezeCap)
            {
                break;
            }
            float size = Mathf.Max(c.Size * k, 0.001f);
            float rot = idx * 0.01f + c.Heading; // matches draw_freeze_overlay
            Vector3 pos = Mapper.GameToArenaLocal(new Vector2(c.X, c.Y), _arenaSideMeters, _worldSize)
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
        float k = _arenaSideMeters / _worldSize;
        int n = 0;
        foreach (Sim.ParticleSnap p in view.Particles)
        {
            // Match draw_effect_pool's gate (alpha pass) and skip unknown effects.
            if ((p.Flags & EffectDrawFlag) == 0 || !_effectUv.TryGetValue(p.EffectId, out Vector3 uv))
            {
                continue;
            }
            if (n >= ParticleCap)
            {
                break;
            }
            float w = Mathf.Max(p.HalfWidth * 2.0f * p.Scale * k, 0.001f);
            float h = Mathf.Max(p.HalfHeight * 2.0f * p.Scale * k, 0.001f);
            Vector3 pos = Mapper.GameToArenaLocal(new Vector2(p.X, p.Y), _arenaSideMeters, _worldSize)
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
            _particles.SetInstanceTransform(n, new Transform3D(basis, pos));
            _particles.SetInstanceColor(n, new Color(p.R, p.G, p.B, p.A));
            _particles.SetInstanceCustomData(n, new Color(uv.X, uv.Y, uv.Z, 0.0f));
            n++;
        }
        _particles.VisibleInstanceCount = n;
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
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
        float s = _arenaSideMeters * FloorMarginScale;
        _floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(s, s) },
            MaterialOverride = _floorMaterial,
            Position = new Vector3(0.0f, FloorY, 0.0f),
        };
        AddChild(_floor);
    }

    /// <summary>The base terrain-slot texture applied to the arena floor (null
    /// until <see cref="ApplyTerrainInfo"/> succeeds), so the surrounding world
    /// floor can share the same ground.</summary>
    public Texture2D? FloorTexture => _floorMaterial?.AlbedoTexture as Texture2D;

    /// <summary>Texture the floor from the session's base terrain slot (ABI
    /// terrain-info). Called once the session exists; leaves the grey fallback if
    /// the terrain sheet isn't baked.</summary>
    public void ApplyTerrainInfo(Sim.TerrainInfo info)
    {
        if (_floorMaterial == null || !_terrainSlots.TryGetValue(info.Slot0, out string? file))
        {
            return;
        }
        string path = SpriteDir + file;
        if (ResourceLoader.Exists(path) && ResourceLoader.Load<Texture2D>(path) is Texture2D tex)
        {
            _floorMaterial.AlbedoTexture = tex;
            // Drop the dark neutral-grey fallback tint so the grass/dirt shows at
            // full colour (AlbedoColor multiplies the texture).
            _floorMaterial.AlbedoColor = Colors.White;
        }
        else
        {
            GD.PushWarning($"CrimsonVR: terrain sheet missing ({path}); grey floor");
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
                ALBEDO = c.rgb * col.rgb;
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
                int frame = _corpseFrames.TryGetValue(c.CreatureTypeId, out int f) ? f : (c.CreatureTypeId & 0xF);
                frame &= _corpseGrid * _corpseGrid - 1;
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
        _muzzleCount = 0;
        foreach (Sim.PlayerSnap p in view.Players)
        {
            // Player torso (trooper.png frame 16) is aimed, so rotate by aim.
            _players.Add(new Vector2(p.X, p.Y), p.AimHeading, p.Size);
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
                baseColor: new Color(c.R, c.G, c.B, c.A), hitFlash: c.HitFlashTimer);
        }

        _projectiles.BeginPush();
        foreach (Sim.ProjectileSnap pr in view.Projectiles)
        {
            // Carry the projectile speed (ABI v6) in AnimPhase — unused for streak
            // layers — so InterpolateLayer can drop a stopped/lodged bullet to the
            // ground with the corpse instead of leaving it floating at the projectile
            // plane.
            float speed = Mathf.Sqrt(pr.Vx * pr.Vx + pr.Vy * pr.Vy);
            _projectiles.Add(new Vector2(pr.X, pr.Y), pr.Angle, 1.0f, animPhase: speed, typeId: pr.TypeId);
        }

        _secondaries.BeginPush();
        _explosionCapCount = 0;
        foreach (Sim.SecondarySnap s in view.Secondaries)
        {
            _secondaries.Add(new Vector2(s.X, s.Y), s.Angle, 1.0f, typeId: s.TypeId);
            // Detonating secondaries (rockets/grenades) burst into an explosion.
            if (s.DetonationT > 0.0f && _explosionCapCount < _explosionsCap.Length)
            {
                _explosionsCap[_explosionCapCount++] = new Explosion
                {
                    Game = new Vector2(s.X, s.Y),
                    Scale = s.DetonationScale,
                    T = s.DetonationT,
                };
            }
        }

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
        RenderParticles(view);
        RenderFreezeOverlay(view);
    }

    /// <summary>Write interpolated instance transforms for the current frame.
    /// <paramref name="frac"/> is 0..1 between the last two ticks.</summary>
    public void Interpolate(float frac)
    {
        frac = Mathf.Clamp(frac, 0.0f, 1.0f);
        _needleCount = 0;
        _shadowCount = 0;
        InterpolateLayer(_players, frac, sprite: true, castShadow: true);
        foreach (Layer layer in _creatureLayers.Values)
        {
            InterpolateLayer(layer, frac, sprite: true, castShadow: true);
        }
        InterpolateLayer(_creatureFallback, frac, sprite: false, castShadow: true);
        InterpolateLayer(_projectiles, frac, sprite: false);
        InterpolateLayer(_secondaries, frac, sprite: false);
        InterpolateLayer(_bonuses, frac, sprite: false);
        _shadows.VisibleInstanceCount = _shadowCount;
        EmitFx();
        if (_needles != null)
        {
            _needles.VisibleInstanceCount = _needleCount;
        }
    }

    /// <summary>Draw the captured muzzle-flash and explosion bursts into the
    /// shared additive fx mesh (positions are tick-captured; these are transient
    /// so they aren't interpolated).</summary>
    private void EmitFx()
    {
        float k = _arenaSideMeters / _worldSize;
        _fxCount = 0;
        for (int i = 0; i < _muzzleCount; i++)
        {
            Muzzle m = _muzzles[i];
            // Just ahead of the player along aim; sized by flash strength.
            Vector3 dir = ForwardFromHeading(m.Heading);
            Vector3 arena = Mapper.GameToArenaLocal(m.Game, _arenaSideMeters, _worldSize)
                + dir * (m.SizeGame * k * 1.6f) + new Vector3(0.0f, 0.012f, 0.0f);
            float s = Mathf.Max(m.SizeGame * k * 2.2f * m.Alpha, 0.002f);
            var color = new Color(1.0f, 0.85f, 0.5f, m.Alpha);
            AddFx(arena, s, color);
        }
        for (int i = 0; i < _explosionCapCount; i++)
        {
            Explosion e = _explosionsCap[i];
            Vector3 arena = Mapper.GameToArenaLocal(e.Game, _arenaSideMeters, _worldSize)
                + new Vector3(0.0f, 0.012f, 0.0f);
            float s = Mathf.Max(e.Scale * k * 2.0f, 0.004f);
            // Fade out over the detonation's life (T rising 0->1).
            float a = Mathf.Clamp(1.0f - e.T, 0.0f, 1.0f);
            var color = new Color(1.0f, 0.6f, 0.25f, a);
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
        float k = _arenaSideMeters / _worldSize;
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
            if (interp)
            {
                Ent prev = layer.Prev[i];
                game = prev.Game.Lerp(cur.Game, frac);
                angle = LerpAngle(prev.Angle, cur.Angle, frac);
                sizeGame = Mathf.Lerp(prev.SizeGame, cur.SizeGame, frac);
            }

            Vector3 arena = Mapper.GameToArenaLocal(game, _arenaSideMeters, _worldSize);
            // Flat on the plane at a constant per-layer lift; layering is by draw
            // order (RenderPriority), not physical height.
            Vector3 pos = arena + new Vector3(0.0f, layer.Lift, 0.0f);
            // Faithful creature world size: 64 * clamp(size/64, 0.25, 2.0) game
            // units (creature_render_type). Others scale linearly from size.
            float sizeUnits = layer.ClampRefSize
                ? 64.0f * Mathf.Clamp(sizeGame / 64.0f, 0.25f, 2.0f)
                : sizeGame;
            float meters = Mathf.Max(sizeUnits * k * layer.SizeScale, 0.002f);

            if (castShadow)
            {
                AddShadow(arena, meters);
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
            else if (layer.Streak)
            {
                // Additive glow elongated along travel (flat on the plane), tinted
                // per projectile type. Orientation via the creature heading
                // convention (validate in-headset; flip if streaks read sideways).
                basis = StreakBasis(ForwardFromHeading(angle), length: meters * 2.6f, width: meters);
                layer.Mesh.SetInstanceColor(i, ProjTint(cur.TypeId));
            }
            else if (layer.UvIndexed)
            {
                // Textured icons (bonuses): FlatQuadBasis reads upright (texture
                // top -> +z far edge) from the player's downward view. The plain
                // FlatBasis below maps texture-top to the near edge -> UPSIDE DOWN,
                // so it's only for solid-colour fallbacks. See the FLAT-SPRITE
                // ORIENTATION note on FlatQuadBasis.
                basis = FlatQuadBasis(angle, meters, meters);
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
            // A stopped/lodged bullet (speed ~0, carried in AnimPhase for streak
            // layers) drops to the ground too, so it settles onto the corpse rather
            // than floating at the projectile plane after its target dies.
            if (layer.Streak && cur.AnimPhase < StoppedProjectileSpeed)
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
                layer.Mesh.SetInstanceCustomData(i, new Color(
                    (frame % grid) * inv,
                    (frame / grid) * inv,
                    inv,
                    0.0f));
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
                layer.Mesh.SetInstanceColor(i, CreatureTint(cur));
            }

            if (sprite && _debug && _needles != null)
            {
                AddNeedle(arena, angle);
            }
        }
        layer.Mesh.VisibleInstanceCount = layer.CurrCount;
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
        if (e.HitFlash > 0.0f)
        {
            // White flash on hit (timer starts at 0.2): brighten toward white by
            // the remaining fraction. First-pass approximation of the original's
            // additive flash (no Python render reference) — tune in-headset.
            float f = Mathf.Clamp(e.HitFlash / 0.2f, 0.0f, 1.0f);
            r = Mathf.Lerp(r, 1.0f, f);
            g = Mathf.Lerp(g, 1.0f, f);
            b = Mathf.Lerp(b, 1.0f, f);
        }
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
