using System.Collections.Generic;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Faithful per-projectile-type renderers (port of render/projectile_draw/):
/// bullet trails + head sprites, plasma tail trains + head + aura, ion/fire
/// beam bodies + heads + chain arcs, pulse expansion, splitter/blade sprites,
/// the Plague Spreader darken pass, per-rocket glow styles, two-quad
/// detonations, and the Sharpshooter laser sight — replacing the old single
/// tinted streak per projectile. Data comes from ABI v12 (origin, speed_scale,
/// travel_budget, pool_index) + the creature/player snaps already in view.
///
/// Everything is written tick-rate into capped MultiMeshes (like the effect
/// pools); sizes are game units * k (meters per unit), flat on the plane at
/// the projectile lift. The reference draws in these blend modes: additive for
/// trails/plasma/beams/pulse/rocket-glow/detonations/laser, plain alpha for
/// splitter/blade/rocket bodies/known-frame fallback, and a SRC=ZERO /
/// INV_SRC_ALPHA darken for the Plague Spreader (reproduced exactly with
/// blend_mul + a pow-2.2 factor, which commutes through the linear pipeline).
/// </summary>
public sealed partial class Diorama
{
    // ---- projectile type ids (projectiles/types.py ProjectileTemplateId) ----
    private const int ProjPistol = 0x01;
    private const int ProjAssaultRifle = 0x02;
    private const int ProjSubmachineGun = 0x05;
    private const int ProjGaussGun = 0x06;
    private const int ProjPlasmaRifle = 0x09;
    private const int ProjPlasmaMinigun = 0x0B;
    private const int ProjPulseGun = 0x13;
    private const int ProjIonRifle = 0x15;
    private const int ProjIonMinigun = 0x16;
    private const int ProjIonCannon = 0x17;
    private const int ProjShrinkifier = 0x18;
    private const int ProjBladeGun = 0x19;
    private const int ProjSpiderPlasma = 0x1A;
    private const int ProjPlasmaCannon = 0x1C;
    private const int ProjSplitterGun = 0x1D;
    private const int ProjPlagueSpreader = 0x29;
    private const int ProjFireBullets = 0x2D;

    private const int SecRocket = 1;
    private const int SecHomingRocket = 2;
    private const int SecDetonation = 3;
    private const int SecRocketMinigun = 4;

    private const float ProjPlaneLift = 0.012f;

    // KNOWN_PROJ_FRAMES (world_defs.py): type_id -> (grid, frame) in projs.png.
    private static readonly Dictionary<int, (int Grid, int Frame)> KnownProjFrames = new()
    {
        [ProjPulseGun] = (2, 0),
        [ProjSplitterGun] = (4, 3),
        [ProjBladeGun] = (4, 6),
        [ProjIonMinigun] = (4, 2),
        [ProjIonCannon] = (4, 2),
        [ProjShrinkifier] = (4, 2),
        [ProjFireBullets] = (4, 2),
        [ProjIonRifle] = (4, 2),
    };

    // known_proj_rgb (projectile_render_registry.py) for the frame fallback.
    private static readonly Dictionary<int, Color> KnownProjRgb = new()
    {
        [ProjIonRifle] = new Color(120 / 255f, 200 / 255f, 1.0f),
        [ProjIonMinigun] = new Color(120 / 255f, 200 / 255f, 1.0f),
        [ProjIonCannon] = new Color(120 / 255f, 200 / 255f, 1.0f),
        [ProjFireBullets] = new Color(1.0f, 170 / 255f, 90 / 255f),
        [ProjShrinkifier] = new Color(160 / 255f, 1.0f, 170 / 255f),
        [ProjBladeGun] = new Color(240 / 255f, 120 / 255f, 1.0f),
    };
    private static readonly Color KnownProjRgbDefault = new(240 / 255f, 220 / 255f, 160 / 255f);

    // PlasmaProjectileRenderConfig (projectile_render_registry.py).
    private readonly struct PlasmaCfg
    {
        public readonly Color Rgb;
        public readonly float Spacing;
        public readonly int SegLimit;
        public readonly float TailSize;
        public readonly float HeadSize;
        public readonly float HeadAlphaMul;
        public readonly Color AuraRgb;
        public readonly float AuraSize;
        public readonly float AuraAlphaMul;

        public PlasmaCfg(Color rgb, float spacing, int segLimit, float tailSize, float headSize,
            float headAlphaMul, Color auraRgb, float auraSize, float auraAlphaMul)
        {
            Rgb = rgb;
            Spacing = spacing;
            SegLimit = segLimit;
            TailSize = tailSize;
            HeadSize = headSize;
            HeadAlphaMul = headAlphaMul;
            AuraRgb = auraRgb;
            AuraSize = auraSize;
            AuraAlphaMul = auraAlphaMul;
        }
    }

    private static readonly PlasmaCfg PlasmaDefault = new(
        Colors.White, 2.1f, 3, 12.0f, 16.0f, 0.45f, Colors.White, 120.0f, 0.15f);

    private static readonly Dictionary<int, PlasmaCfg> PlasmaCfgByType = new()
    {
        [ProjPlasmaRifle] = new PlasmaCfg(Colors.White, 2.5f, 8, 22.0f, 56.0f, 0.45f, Colors.White, 256.0f, 0.3f),
        [ProjPlasmaMinigun] = PlasmaDefault,
        [ProjPlasmaCannon] = new PlasmaCfg(Colors.White, 2.6f, 18, 44.0f, 84.0f, 0.45f, Colors.White, 256.0f, 0.4f),
        [ProjSpiderPlasma] = new PlasmaCfg(new Color(0.3f, 1.0f, 0.3f), 2.1f, 3, 12.0f, 16.0f, 0.45f, new Color(0.3f, 1.0f, 0.3f), 120.0f, 0.15f),
        [ProjShrinkifier] = new PlasmaCfg(new Color(0.3f, 0.3f, 1.0f), 2.1f, 3, 12.0f, 16.0f, 0.45f, new Color(0.3f, 0.3f, 1.0f), 120.0f, 0.15f),
    };

    private static float BeamEffectScale(int typeId) => typeId switch
    {
        ProjIonMinigun => 1.05f,
        ProjIonRifle => 2.2f,
        ProjIonCannon => 3.5f,
        _ => 0.8f,
    };

    private static bool IsPlasmaType(int t)
        => t is ProjPlasmaRifle or ProjPlasmaMinigun or ProjPlasmaCannon or ProjSpiderPlasma or ProjShrinkifier;

    private static bool IsIonType(int t) => t is ProjIonRifle or ProjIonMinigun or ProjIonCannon;

    private static bool IsBeamType(int t) => IsIonType(t) || t == ProjFireBullets;

    private static bool IsBulletTrailType(int t) => (t >= 0 && t < 8) || t == ProjSplitterGun;

    // SecondaryRocketStyle (secondary_rocket.py): base size, glow size/rgb/alpha.
    private static (float BaseSize, float GlowSize, Color GlowRgb, float GlowAlphaMul)? RocketStyle(int t) => t switch
    {
        SecRocket => (14.0f, 60.0f, Colors.White, 0.68f),
        SecHomingRocket => (10.0f, 40.0f, Colors.White, 0.58f),
        SecRocketMinigun => (8.0f, 30.0f, new Color(0.7f, 0.7f, 1.0f), 0.158f),
        _ => null,
    };

    // ---- meshes ----
    private const int TrailCap = 320;
    private const int BulletHeadCap = 320;
    private const int ProjGlowCap = 768;
    private const int ProjsAtlasAddCap = 1024;
    private const int ProjsAtlasAlphaCap = 320;
    private const int IonStripCap = 256;
    private const int PlagueCap = 320;
    private const int SpriteFxCap = 384; // sprite_effect_pool_size (0x180)
    private const int PlayerAuraCap = 4;

    private MultiMesh? _trailMesh;
    private MultiMesh? _bulletHeadMesh;
    private MultiMesh? _projGlowMesh;      // particles.png GLOW cell, additive
    private MultiMesh? _projsAtlasAddMesh; // projs.png per-instance cell, additive
    private MultiMesh? _projsAtlasAlphaMesh; // projs.png per-instance cell, alpha
    private MultiMesh? _ionStripMesh;
    private MultiMesh? _plagueMesh;
    // Sprite-effect pool (ABI v13): muzzle puffs / rocket exhaust / smoke, all
    // drawn as the EXPLOSION_PUFF cell, plain alpha, between secondaries (20)
    // and the effect pool (23) like the native pass order.
    private MultiMesh? _spriteFxMesh;
    // Radioactive aura: additive, priority 14 = under the player sprite (15).
    private MultiMesh? _playerAuraMesh;
    // particles.png GLOW cell (EffectId.GLOW = 0x0D = 13) from the manifest.
    // Every reference projectile draw (plasma/beam/rocket/detonation) samples
    // this soft round glow; id 12 next door is EXPLOSION_BURST — using it
    // stamps a translucent explosion copy under every glow.
    private Vector3 _glowUv;
    private const int ProjGlowEffectId = 13;
    // particles.png cells for the player passes + sprite-effect pool.
    private const int PlayerAuraEffectId = 16; // EffectId.AURA (trooper.py:107)
    private const int ShieldRingEffectId = 2;  // EffectId.SHIELD_RING (trooper.py:198)
    private const int SpriteFxEffectId = 17;   // EffectId.EXPLOSION_PUFF (draw_sprite_effect_pool)
    private Vector3 _spriteFxUv; // FULL cell — sprite effects skip the 2px clamp

    private int _trailN;
    private int _bulletHeadN;
    private int _projGlowN;
    private int _projsAddN;
    private int _projsAlphaN;
    private int _ionStripN;
    private int _plagueN;
    private int _spriteFxN;
    private int _playerAuraN;

    // The parity simulation stops updating a projectile once it is 64 game
    // units beyond the original screen. On a tabletop that offscreen band is
    // physically visible, so presentation continues the last measured velocity
    // until the authoritative projectile naturally despawns. This never feeds
    // back into simulation or replays.
    private sealed class ProjectileVisualTrack
    {
        public Sim.ProjectileSnap Snap;
        public Vector2 PreviousPosition;
        public Vector2 Velocity;
        public Vector2 GhostPosition;
        public float LastElapsedMs;
        public bool HasPrevious;
        public bool Ghosting;
    }

    private readonly System.Collections.Generic.Dictionary<int, ProjectileVisualTrack> _projectileVisualTracks = new();
    private readonly System.Collections.Generic.HashSet<int> _projectileVisualSeen = new();
    private readonly System.Collections.Generic.List<int> _projectileVisualRemove = new();
    private float _lastProjectileElapsedMs = -1.0f;

    // Trail shader: quad stretched tail->head (local +Y = head end). Instance
    // COLOR = head rgba; CUSTOM.x = tail alpha. Alpha ramps tail->head over the
    // quad and samples the bulletTrail gradient (native maps head at v=0.5,
    // tail at v=0). Additive premultiplied like the other effect shaders, with
    // the fitted pow-1.1 sRGB-space correction; fog_disabled is load-bearing.
    private Shader? _trailShader;
    private Shader TrailShader => _trailShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, blend_add, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 col;
            varying float tail_a;
            void vertex() { col = COLOR; tail_a = INSTANCE_CUSTOM.x; }
            void fragment() {
                float g = 1.0 - UV.y; // 1 at the head end (+Y), 0 at the tail
                vec4 c = texture(sheet, vec2(UV.x, (1.0 - UV.y) * 0.5));
                float a = mix(tail_a, col.a, g);
                vec3 s = c.rgb * col.rgb * (c.a * a);
                ALBEDO = pow(s, vec3(1.1));
                ALPHA = 1.0;
            }
            """,
    };

    private Shader? _trailShaderMr;
    private Shader TrailShaderMr => _trailShaderMr ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 col;
            varying float tail_a;
            void vertex() { col = COLOR; tail_a = INSTANCE_CUSTOM.x; }
            void fragment() {
                float g = 1.0 - UV.y;
                vec4 c = texture(sheet, vec2(UV.x, (1.0 - UV.y) * 0.5));
                float a = mix(tail_a, col.a, g);
                vec3 source = c.rgb * col.rgb * (c.a * a);
                vec3 contribution = clamp(pow(source, vec3(1.1)), vec3(0.0), vec3(1.0));
                float coverage = max(contribution.r, max(contribution.g, contribution.b));
                ALBEDO = coverage > 0.0001 ? contribution / coverage : vec3(0.0);
                ALPHA = coverage;
            }
            """,
    };

    // Ion chain strip: native samples a constant u=0.625 line of projs.png with
    // v spanning 0..0.25 ACROSS the strip; constant per-instance color.
    private Shader? _ionStripShader;
    private Shader IonStripShader => _ionStripShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, blend_add, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 col;
            void vertex() { col = COLOR; }
            void fragment() {
                vec4 c = texture(sheet, vec2(0.625, UV.x * 0.25));
                vec3 s = c.rgb * col.rgb * (c.a * col.a);
                ALBEDO = pow(s, vec3(1.1));
                ALPHA = 1.0;
            }
            """,
    };


    private Shader? _ionStripShaderMr;
    private Shader IonStripShaderMr => _ionStripShaderMr ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 col;
            void vertex() { col = COLOR; }
            void fragment() {
                vec4 c = texture(sheet, vec2(0.625, UV.x * 0.25));
                vec3 source = c.rgb * col.rgb * (c.a * col.a);
                vec3 contribution = clamp(pow(source, vec3(1.1)), vec3(0.0), vec3(1.0));
                float coverage = max(contribution.r, max(contribution.g, contribution.b));
                ALBEDO = coverage > 0.0001 ? contribution / coverage : vec3(0.0);
                ALPHA = coverage;
            }
            """,
    };

    // Plague Spreader darken: native blend is SRC=ZERO / DST=INV_SRC_ALPHA, i.e.
    // dst *= (1 - src_alpha). Godot blend_mul multiplies dst by ALBEDO in LINEAR
    // space; a constant factor commutes exactly through the sRGB transfer as
    // factor^2.2, so ALBEDO = pow(1 - a, 2.2) reproduces the native display
    // math EXACTLY (no dst-blind approximation needed for pure multiplies).
    // MR variant of the darken pass. blend_mul multiplies what is ALREADY in the
    // framebuffer, and over passthrough that is the transparent void rather than
    // the room — the compositor blends the app afterwards, so the multiply never
    // meets the real world and the quad resolves to a flat dark patch instead of
    // a darkening.
    //
    // Alpha blending reaches the room, and reproduces the SAME operation exactly.
    // Compositing black over dst gives dst*(1-ALPHA); the multiply path gives
    // dst*(1-a)^2.2 (the linear-space form of the native dst *= 1-src_alpha).
    // Setting ALPHA = 1 - (1-a)^2.2 makes the two algebraically identical, so
    // this is a change of blend mode, not of look.
    private Shader? _plagueShaderMr;
    private Shader PlagueShaderMr => _plagueShaderMr ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 inst;
            varying vec4 col;
            void vertex() { inst = INSTANCE_CUSTOM; col = COLOR; }
            void fragment() {
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                float a = clamp(c.a * col.a, 0.0, 1.0);
                ALBEDO = vec3(0.0);
                ALPHA = 1.0 - pow(1.0 - a, 2.2);
            }
            """,
    };

    private Shader? _plagueShader;
    private Shader PlagueShader => _plagueShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, blend_mul, fog_disabled;
            uniform sampler2D sheet : filter_linear;
            varying vec4 inst;
            varying vec4 col;
            void vertex() { inst = INSTANCE_CUSTOM; col = COLOR; }
            void fragment() {
                vec2 cell = UV * inst.z + inst.xy;
                vec4 c = texture(sheet, cell);
                float a = clamp(c.a * col.a, 0.0, 1.0);
                ALBEDO = vec3(pow(1.0 - a, 2.2));
                ALPHA = 1.0;
            }
            """,
    };

    private static Texture2D? LoadSprite(string name)
    {
        return AssetStore.LoadTexture(AssetStore.SpritePath($"{name}.png"));
    }

    private MultiMesh BuildProjMesh(Material material, int cap, bool customData = true)
        => BuildProjMesh(material, cap, customData, out _);

    private MultiMesh BuildProjMesh(
        Material material, int cap, bool customData, out MultiMeshInstance3D instance)
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = customData,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = cap,
            VisibleInstanceCount = 0,
            // Projectile instances deliberately travel well beyond the tabletop.
            // Keep the renderer's visibility bounds from becoming a second,
            // invisible diorama edge; the camera and natural lifetime remain the
            // only presentation limits.
            CustomAabb = new Aabb(
                new Vector3(-_arenaSideMeters * 64.0f, -2.0f, -_arenaSideMeters * 64.0f),
                new Vector3(_arenaSideMeters * 128.0f, 4.0f, _arenaSideMeters * 128.0f)),
        };
        instance = new MultiMeshInstance3D { Multimesh = mesh, MaterialOverride = material };
        AddChild(instance);
        return mesh;
    }

    private MultiMesh BuildMrSwappedProjMesh(Shader vrShader, Shader mrShader,
        Texture2D sheet, int priority, int cap, bool customData = true)
    {
        ShaderMaterial vr = ProjShaderMat(vrShader, sheet, priority);
        ShaderMaterial mr = ProjShaderMat(mrShader, sheet, priority);
        MultiMesh mesh = BuildProjMesh(vr, cap, customData, out MultiMeshInstance3D instance);
        TrackMrMaterialSwap(instance, vr, mr);
        return mesh;
    }

    private ShaderMaterial ProjShaderMat(Shader shader, Texture2D sheet, int priority)
    {
        var mat = new ShaderMaterial { Shader = shader, RenderPriority = priority };
        mat.SetShaderParameter("sheet", sheet);
        // Only the alpha-blended shader carries the dst-blind alpha curve that
        // has to be switched off over passthrough; the additive and multiply
        // passes have no such uniform.
        if (shader == ParticleShader)
        {
            TrackMrComposite(mat);
        }
        return mat;
    }

    /// <summary>Build the per-type projectile meshes. Missing art degrades that
    /// pass to nothing (the sim stays authoritative either way).</summary>
    private void BuildProjectileRenderers()
    {
        Texture2D? projs = LoadSprite("projs");
        Texture2D? trail = LoadSprite("bulletTrail");
        Texture2D? bullet = LoadSprite("bullet16");
        Texture2D? particles = LoadSprite("particles");

        if (_effectUv.TryGetValue(ProjGlowEffectId, out Vector3 g))
        {
            _glowUv = g;
        }

        if (trail != null)
        {
            _trailMesh = BuildMrSwappedProjMesh(TrailShader, TrailShaderMr, trail, priority: 18, TrailCap);
        }
        if (bullet != null)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = bullet,
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                RenderPriority = 19,
                DisableFog = true,
            };
            _bulletHeadMesh = BuildProjMesh(mat, BulletHeadCap, customData: false);
        }
        if (particles != null && _glowUv != Vector3.Zero)
        {
            _projGlowMesh = BuildMrSwappedProjMesh(ParticleShaderAdd, ParticleShaderAddMr,
                particles, priority: 20, ProjGlowCap);
        }
        if (particles != null && _effectUv.TryGetValue(SpriteFxEffectId, out Vector3 puff))
        {
            // The manifest rect carries the effect pool's (cell - 2px) clamp;
            // sprite effects sample the full cell, so widen by 2px in UV space.
            _spriteFxUv = new Vector3(puff.X, puff.Y, puff.Z + 2.0f / particles.GetWidth());
            _spriteFxMesh = BuildProjMesh(ProjShaderMat(ParticleShader, particles, priority: 22), SpriteFxCap);
        }
        if (particles != null && _effectUv.ContainsKey(PlayerAuraEffectId))
        {
            _playerAuraMesh = BuildMrSwappedProjMesh(ParticleShaderAdd, ParticleShaderAddMr,
                particles, priority: 14, PlayerAuraCap);
        }
        if (projs != null)
        {
            _projsAtlasAddMesh = BuildMrSwappedProjMesh(ParticleShaderAdd, ParticleShaderAddMr,
                projs, priority: 20, ProjsAtlasAddCap);
            _projsAtlasAlphaMesh = BuildProjMesh(ProjShaderMat(ParticleShader, projs, priority: 19), ProjsAtlasAlphaCap);
            _ionStripMesh = BuildMrSwappedProjMesh(IonStripShader, IonStripShaderMr,
                projs, priority: 20, IonStripCap, customData: false);
            // Two materials, swapped by MR state: the multiply pass cannot reach
            // the real world (see PlagueShaderMr).
            _plagueMatVr = ProjShaderMat(PlagueShader, projs, priority: 21);
            _plagueMatMr = ProjShaderMat(PlagueShaderMr, projs, priority: 21);
            _plagueMesh = BuildProjMesh(_plagueMatVr, PlagueCap, true, out _plagueInstance);
            ApplyPlagueBlend();
        }
    }

    // ---- emit helpers (game units -> arena meters, flat at the proj lift) ----

    private static Vector3 CellUv(int grid, int frame)
    {
        float inv = 1.0f / grid;
        return new Vector3(frame % grid * inv, frame / grid * inv, inv);
    }

    // The death cinematic still presents the arena through a fixed tabletop
    // window. These bounds are only used to clip that zoomed view; ordinary
    // projectile flight is intentionally unbounded.
    private float DrawBoundsMargin => _worldSize * (FloorMarginScale - 1.0f) * 0.5f;

    private bool OutsideDrawBounds(Vector2 game)
    {
        float m = DrawBoundsMargin;
        return game.X < -m || game.X > _worldSize + m || game.Y < -m || game.Y > _worldSize + m;
    }

    // One Liang-Barsky boundary: clip parameter range [t0, t1] against p*t <= q.
    private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
    {
        if (Mathf.Abs(p) < 1e-9f)
        {
            return q >= 0.0f;
        }
        float r = q / p;
        if (p < 0.0f)
        {
            if (r > t1)
            {
                return false;
            }
            if (r > t0)
            {
                t0 = r;
            }
        }
        else
        {
            if (r < t0)
            {
                return false;
            }
            if (r < t1)
            {
                t1 = r;
            }
        }
        return true;
    }

    /// <summary>Clamp a game-space segment to the draw bounds; false = fully outside.</summary>
    private bool ClampToDrawBounds(ref Vector2 a, ref Vector2 b)
    {
        float min = -DrawBoundsMargin;
        float max = _worldSize + DrawBoundsMargin;
        Vector2 d = b - a;
        float t0 = 0.0f;
        float t1 = 1.0f;
        if (!ClipEdge(-d.X, a.X - min, ref t0, ref t1)
            || !ClipEdge(d.X, max - a.X, ref t0, ref t1)
            || !ClipEdge(-d.Y, a.Y - min, ref t0, ref t1)
            || !ClipEdge(d.Y, max - a.Y, ref t0, ref t1))
        {
            return false;
        }
        Vector2 start = a + d * t0;
        b = a + d * t1;
        a = start;
        return true;
    }

    private void EmitAtlasSprite(MultiMesh? mesh, ref int n, int cap, Vector2 game, float sizeUnits,
        float rotation, Color color, Vector3 uv)
    {
        // Death-cinematic zoom: magnify into window space first — the
        // draw-bounds cull then doubles as the window-edge clip.
        game = ViewGame(game);
        // ONLY during that zoom. Outside it, projectiles are free to leave the
        // arena and keep going: the slab edge used to swallow rockets and gauss
        // rounds mid-flight, which read as them hitting an invisible wall. There
        // is no fogged ground plane to protect any more, and in MR a round
        // flying off into the room is the better picture.
        if (mesh == null || n >= cap || sizeUnits <= 1e-3f
            || (_viewZoom > 1.0f && OutsideDrawBounds(game)))
        {
            return;
        }
        float k = ViewK;
        Vector3 pos = Mapper.GameToArenaLocal(game, _arenaSideMeters, _worldSize)
            + new Vector3(0.0f, ProjPlaneLift, 0.0f);
        Basis basis = FlatFacingBasis(ForwardFromHeading(rotation), sizeUnits * k);
        mesh.SetInstanceTransform(n, new Transform3D(basis, pos));
        mesh.SetInstanceColor(n, color);
        mesh.SetInstanceCustomData(n, new Color(uv.X, uv.Y, uv.Z, 0.0f));
        n++;
    }

    private void EmitStretch(MultiMesh? mesh, ref int n, int cap, Vector2 startGame, Vector2 endGame,
        float halfWidthUnits, Color color, float customX = 0.0f, bool custom = true)
    {
        // Death-cinematic zoom: transform the endpoints into window space (the
        // segment length magnifies with them; only the width needs the factor)
        // and let the existing bounds clamp clip at the window edge.
        startGame = ViewGame(startGame);
        endGame = ViewGame(endGame);
        halfWidthUnits *= _viewZoom;
        if (mesh == null || n >= cap)
        {
            return;
        }
        // Clipped to the window only while the death cinematic is zoomed; see
        // EmitAtlasSprite. A trail that leaves the arena now draws its whole
        // length instead of being cut at the slab.
        if (_viewZoom > 1.0f && !ClampToDrawBounds(ref startGame, ref endGame))
        {
            return;
        }
        Vector2 seg = endGame - startGame;
        float len = seg.Length();
        if (len <= 1e-4f)
        {
            return;
        }
        float k = _arenaSideMeters / _worldSize;
        Vector2 mid = (startGame + endGame) * 0.5f;
        var forward = new Vector3(seg.X / len, 0.0f, seg.Y / len); // game xy -> arena xz
        Vector3 pos = Mapper.GameToArenaLocal(mid, _arenaSideMeters, _worldSize)
            + new Vector3(0.0f, ProjPlaneLift, 0.0f);
        Basis basis = StreakBasis(forward, length: len * k, width: halfWidthUnits * 2.0f * k);
        mesh.SetInstanceTransform(n, new Transform3D(basis, pos));
        mesh.SetInstanceColor(n, color);
        if (custom)
        {
            mesh.SetInstanceCustomData(n, new Color(customX, 0.0f, 0.0f, 0.0f));
        }
        n++;
    }

    // ---- the per-tick render ----

    /// <summary>Render all primaries/secondaries with their faithful per-type
    /// draw routines. Call once per pushed snapshot.</summary>
    private void RenderProjectiles(in SnapshotView view)
    {
        _trailN = 0;
        _bulletHeadN = 0;
        _projGlowN = 0;
        _projsAddN = 0;
        _projsAlphaN = 0;
        _ionStripN = 0;
        _plagueN = 0;
        _spriteFxN = 0;
        _playerAuraN = 0;

        float elapsedMs = view.Header.ElapsedMsSim;
        if (_lastProjectileElapsedMs >= 0.0f && elapsedMs < _lastProjectileElapsedMs)
        {
            _projectileVisualTracks.Clear();
        }
        _lastProjectileElapsedMs = elapsedMs;
        _projectileVisualSeen.Clear();
        bool ionMaster = false;
        if (view.Header.PlayerCount > 0)
        {
            Sim.PlayerSnap p0 = view.Players[0];
            ionMaster = (p0.PerkFlags & Sim.PlayerSnap.PerkFlagIonGunMaster) != 0;
            // Sharpshooter laser sight draws first in the projectile pass.
            bool laser = (p0.PerkFlags & Sim.PlayerSnap.PerkFlagSharpshooter) != 0 || DebugFx.LaserSight;
            if (laser && p0.Health > 0.0f)
            {
                var pp = new Vector2(p0.X, p0.Y);
                var dir = new Vector2(Mathf.Sin(p0.AimHeading), -Mathf.Cos(p0.AimHeading));
                // Head alpha 0.2 at the far end, tail alpha 0.5 at the start.
                EmitStretch(_trailMesh, ref _trailN, TrailCap, pp + dir * 15.0f, pp + dir * 512.0f,
                    halfWidthUnits: 1.0f, new Color(1.0f, 0.0f, 0.0f, 0.2f), customX: 0.5f);
            }
            RenderPlayerFx(p0, elapsedMs);
        }

        foreach (Sim.ProjectileSnap pr in view.Projectiles)
        {
            _projectileVisualSeen.Add(pr.PoolIndex);
            if (PrepareVisualProjectile(pr, elapsedMs, out Sim.ProjectileSnap visual))
            {
                DrawPrimary(visual, elapsedMs, ionMaster, view);
            }
        }
        PruneDepartedProjectiles();
        foreach (Sim.SecondarySnap s in view.Secondaries)
        {
            DrawSecondary(s);
        }
        // Sprite-effect pool: native gates it on fx_detail >= 2.
        if (_graphicsDetail >= 2)
        {
            foreach (Sim.SpriteEffectSnap fx in view.SpriteEffects)
            {
                EmitAtlasSprite(_spriteFxMesh, ref _spriteFxN, SpriteFxCap, new Vector2(fx.X, fx.Y),
                    fx.Scale, fx.Rotation, new Color(fx.R, fx.G, fx.B, fx.A), _spriteFxUv);
            }
        }

        Flush(_trailMesh, _trailN);
        Flush(_bulletHeadMesh, _bulletHeadN);
        Flush(_projGlowMesh, _projGlowN);
        Flush(_projsAtlasAddMesh, _projsAddN);
        Flush(_projsAtlasAlphaMesh, _projsAlphaN);
        Flush(_ionStripMesh, _ionStripN);
        Flush(_plagueMesh, _plagueN);
        Flush(_spriteFxMesh, _spriteFxN);
        Flush(_playerAuraMesh, _playerAuraN);
    }

    private bool PrepareVisualProjectile(in Sim.ProjectileSnap projectile,
        float elapsedMs, out Sim.ProjectileSnap visual)
    {
        visual = projectile;
        Vector2 position = new(projectile.X, projectile.Y);
        if (!_projectileVisualTracks.TryGetValue(projectile.PoolIndex, out ProjectileVisualTrack? track))
        {
            track = new ProjectileVisualTrack();
            _projectileVisualTracks[projectile.PoolIndex] = track;
        }

        // Pool slots are reused. Type + spawn origin identify a new occupant.
        bool sameOccupant = track.HasPrevious
            && track.Snap.TypeId == projectile.TypeId
            && Mathf.IsEqualApprox(track.Snap.OriginX, projectile.OriginX)
            && Mathf.IsEqualApprox(track.Snap.OriginY, projectile.OriginY)
            && elapsedMs >= track.LastElapsedMs;
        if (!sameOccupant)
        {
            track.HasPrevious = false;
            track.Ghosting = false;
            track.Velocity = Vector2.Zero;
        }

        float dt = track.HasPrevious
            ? Mathf.Clamp((elapsedMs - track.LastElapsedMs) * 0.001f, 0.0f, 0.1f)
            : 0.0f;
        if (track.Ghosting)
        {
            // Once the projectile crosses an arena edge, presentation owns its
            // position. Do not snap back to the simulation's offscreen linger
            // position or wait for its life timer to decay.
            track.GhostPosition += track.Velocity * dt;
            visual.X = track.GhostPosition.X;
            visual.Y = track.GhostPosition.Y;
            visual.LifeTimer = 0.4f;
        }
        else if (projectile.LifeTimer >= 0.4f)
        {
            Vector2 delta = position - track.PreviousPosition;
            if (track.HasPrevious && dt > 1e-5f && delta.LengthSquared() > 1e-6f)
            {
                track.Velocity = delta / dt;
            }
            track.PreviousPosition = position;
            track.HasPrevious = true;

            // Inclusive, direction-aware edge detection is deliberate. The sim
            // can stop a center exactly on the boundary, so a strict outside
            // test leaves the sprite looking pinned to an invisible wall.
            if (ReachedArenaExit(position, track.Velocity))
            {
                track.GhostPosition = position;
                track.Ghosting = true;
            }
        }
        else if (track.HasPrevious && ReachedArenaExit(position, track.Velocity))
        {
            // The stopping snapshot may be the first one whose center reaches
            // the boundary. Preserve the last live-flight velocity in that case.
            track.GhostPosition = position;
            track.Ghosting = true;
            visual.LifeTimer = 0.4f;
        }

        track.Snap = projectile;
        track.LastElapsedMs = elapsedMs;
        return true;
    }

    /// <summary>Discard visual extrapolation when the authoritative pool says
    /// the projectile has naturally despawned. Until then, PrepareVisualProjectile
    /// keeps it moving even after the parity simulation freezes its position.</summary>
    private void PruneDepartedProjectiles()
    {
        _projectileVisualRemove.Clear();
        foreach (System.Collections.Generic.KeyValuePair<int, ProjectileVisualTrack> pair in _projectileVisualTracks)
        {
            if (!_projectileVisualSeen.Contains(pair.Key))
            {
                _projectileVisualRemove.Add(pair.Key);
            }
        }
        foreach (int key in _projectileVisualRemove)
        {
            _projectileVisualTracks.Remove(key);
        }
    }

    private bool ReachedArenaExit(Vector2 game, Vector2 velocity)
        => velocity.LengthSquared() > 1e-4f
            && ((game.X <= 0.0f && velocity.X < 0.0f)
                || (game.X >= _worldSize && velocity.X > 0.0f)
                || (game.Y <= 0.0f && velocity.Y < 0.0f)
                || (game.Y >= _worldSize && velocity.Y > 0.0f));

    /// <summary>Player-anchored effect passes (trooper.py): the Radioactive
    /// green aura under the body and the counter-rotating shield-ring pair
    /// above it while the shield bonus runs.</summary>
    private void RenderPlayerFx(in Sim.PlayerSnap p, float elapsedMs)
    {
        float t = elapsedMs * 0.001f;
        var pos = new Vector2(p.X, p.Y);

        // trooper.py:107-132 — AURA cell, additive, 100u, pulsing alpha. Drawn
        // regardless of health (the native pass sits before the alive check).
        if (((p.PerkFlags & Sim.PlayerSnap.PerkFlagRadioactive) != 0 || DebugFx.RadioactiveAura)
            && _effectUv.TryGetValue(PlayerAuraEffectId, out Vector3 auraUv))
        {
            float auraAlpha = (Mathf.Sin(t) + 1.0f) * 0.1875f + 0.25f;
            EmitAtlasSprite(_playerAuraMesh, ref _playerAuraN, PlayerAuraCap, pos, 100.0f, 0.0f,
                new Color(77 / 255f, 153 / 255f, 77 / 255f, auraAlpha), auraUv);
        }

        // trooper.py:198-243 — SHIELD_RING pair, additive, centred 3u along the
        // aim heading; strength pulses and ramps out with the last second. The
        // debug toggle renders as if a fresh shield were up (no ramp).
        float shieldTimer = p.ShieldTimer;
        if (shieldTimer <= 1e-3f && DebugFx.ShieldRing)
        {
            shieldTimer = 5.0f;
        }
        if (shieldTimer > 1e-3f && p.Health > 0.0f
            && _effectUv.TryGetValue(ShieldRingEffectId, out Vector3 ringUv))
        {
            float strength = (Mathf.Sin(t) + 1.0f) * 0.25f + shieldTimer;
            if (shieldTimer < 1.0f)
            {
                strength *= shieldTimer;
            }
            strength = Mathf.Min(1.0f, strength);
            float offDir = p.AimHeading - Mathf.Pi * 0.5f;
            Vector2 centre = pos + new Vector2(Mathf.Cos(offDir), Mathf.Sin(offDir)) * 3.0f;
            var rgb = new Color(91 / 255f, 180 / 255f, 1.0f, 1.0f);
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, centre,
                (Mathf.Sin(t * 3.0f) + 17.5f) * 2.0f, t * 2.0f,
                new Color(rgb.R, rgb.G, rgb.B, strength * 0.4f), ringUv);
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, centre,
                (Mathf.Sin(t * 3.0f) * 4.0f + 24.0f) * 2.0f, t * -2.0f,
                new Color(rgb.R, rgb.G, rgb.B, strength * 0.3f), ringUv);
        }
    }

    private static void Flush(MultiMesh? mesh, int n)
    {
        if (mesh != null)
        {
            mesh.VisibleInstanceCount = n;
        }
    }

    private void DrawPrimary(in Sim.ProjectileSnap pr, float elapsedMs, bool ionMaster, in SnapshotView view)
    {
        int t = pr.TypeId;
        var pos = new Vector2(pr.X, pr.Y);
        var origin = new Vector2(pr.OriginX, pr.OriginY);
        float life = pr.LifeTimer;

        if (IsBulletTrailType(t))
        {
            DrawBulletTrail(pr, pos, origin, life);
            if (t == ProjSplitterGun)
            {
                // Splitter also draws its atlas sprite (falls through in the
                // native handler chain after the trail).
                DrawSplitterOrBlade(pr, pos, origin, life, elapsedMs);
            }
            return;
        }
        if (IsPlasmaType(t))
        {
            DrawPlasma(pr, pos, life);
            return;
        }
        if (IsBeamType(t))
        {
            DrawBeam(pr, pos, origin, life, ionMaster, view);
            return;
        }
        if (t == ProjPulseGun)
        {
            DrawPulse(pr, pos, origin, life);
            return;
        }
        if (t == ProjBladeGun)
        {
            DrawSplitterOrBlade(pr, pos, origin, life, elapsedMs);
            return;
        }
        if (t == ProjPlagueSpreader)
        {
            DrawPlagueSpreader(pr, pos, life, elapsedMs);
            return;
        }
        // Fallback: known frame, tinted, life-faded (draw_projectile tail).
        if (KnownProjFrames.TryGetValue(t, out (int Grid, int Frame) map))
        {
            float a = Mathf.Clamp(life / 0.4f, 0.0f, 1.0f);
            Color rgb = KnownProjRgb.TryGetValue(t, out Color c) ? c : KnownProjRgbDefault;
            // Native scale 0.6 of the cell: cell = 256/grid world units.
            float size = 256.0f / map.Grid * 0.6f;
            EmitAtlasSprite(_projsAtlasAlphaMesh, ref _projsAlphaN, ProjsAtlasAlphaCap, pos, size,
                pr.Angle, new Color(rgb.R, rgb.G, rgb.B, a), CellUv(map.Grid, map.Frame));
        }
    }

    private void DrawBulletTrail(in Sim.ProjectileSnap pr, Vector2 pos, Vector2 origin, float life)
    {
        int t = pr.TypeId;
        float alpha = Mathf.Clamp(life, 0.0f, 1.0f);
        if (alpha <= 1e-3f)
        {
            return;
        }
        float sideMul = t is ProjPistol or ProjAssaultRifle ? 1.2f : t == ProjGaussGun ? 1.1f : 0.7f;
        Color head = t == ProjGaussGun
            ? new Color(0.2f, 0.5f, 1.0f, alpha)
            : new Color(0.5f, 0.5f, 0.5f, alpha);
        EmitStretch(_trailMesh, ref _trailN, TrailCap, origin, pos, halfWidthUnits: 1.5f * sideMul, head, customX: 0.0f);

        if (life >= 0.39f)
        {
            float size = t == ProjAssaultRifle ? 6.0f : t == ProjSubmachineGun ? 8.0f : 4.0f;
            EmitBulletHead(pos, Mathf.Max(size, 2.0f), pr.Angle, new Color(220 / 255f, 220 / 255f, 220 / 255f, alpha));
        }
    }

    private void EmitBulletHead(Vector2 game, float sizeUnits, float rotation, Color color)
    {
        game = ViewGame(game);
        if (_bulletHeadMesh == null || _bulletHeadN >= BulletHeadCap
            || (_viewZoom > 1.0f && OutsideDrawBounds(game)))
        {
            return;
        }
        float k = ViewK;
        Vector3 pos = Mapper.GameToArenaLocal(game, _arenaSideMeters, _worldSize)
            + new Vector3(0.0f, ProjPlaneLift, 0.0f);
        Basis basis = FlatFacingBasis(ForwardFromHeading(rotation), sizeUnits * k);
        _bulletHeadMesh.SetInstanceTransform(_bulletHeadN, new Transform3D(basis, pos));
        _bulletHeadMesh.SetInstanceColor(_bulletHeadN, color);
        _bulletHeadN++;
    }

    private void DrawPlasma(in Sim.ProjectileSnap pr, Vector2 pos, float life)
    {
        PlasmaCfg cfg = PlasmaCfgByType.TryGetValue(pr.TypeId, out PlasmaCfg c) ? c : PlasmaDefault;
        if (life >= 0.4f)
        {
            int segCount = (int)pr.TravelBudget;
            if (segCount < 0)
            {
                segCount = 0;
            }
            segCount /= 5;
            if (segCount > cfg.SegLimit)
            {
                segCount = cfg.SegLimit;
            }
            // Stored angle is rotated +pi/2 vs travel; +pi walks BACK along it.
            float back = pr.Angle + Mathf.Pi;
            var step = new Vector2(Mathf.Sin(back), -Mathf.Cos(back)) * pr.SpeedScale * cfg.Spacing;

            var tail = new Color(cfg.Rgb.R, cfg.Rgb.G, cfg.Rgb.B, 0.4f);
            for (int i = 0; i < segCount; i++)
            {
                EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos + step * i, cfg.TailSize, 0.0f, tail, _glowUv);
            }
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos, cfg.HeadSize, 0.0f,
                new Color(cfg.Rgb.R, cfg.Rgb.G, cfg.Rgb.B, cfg.HeadAlphaMul), _glowUv);
            if (_graphicsDetail >= 2)
            {
                EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos, cfg.AuraSize, 0.0f,
                    new Color(cfg.AuraRgb.R, cfg.AuraRgb.G, cfg.AuraRgb.B, cfg.AuraAlphaMul), _glowUv);
            }
            return;
        }
        // Fade stage: a white pop shrinking with life (restores the visuals the
        // old blanket life<0.4 cull dropped).
        float fade = Mathf.Clamp(life * 2.5f, 0.0f, 1.0f);
        if (fade > 1e-3f)
        {
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos, 56.0f, 0.0f,
                new Color(1.0f, 1.0f, 1.0f, fade), _glowUv);
        }
    }

    private void DrawBeam(in Sim.ProjectileSnap pr, Vector2 pos, Vector2 origin, float life,
        bool ionMaster, in SnapshotView view)
    {
        int t = pr.TypeId;
        bool isFire = t == ProjFireBullets;
        Vector2 beam = pos - origin;
        float dist = beam.Length();
        if (dist <= 1e-6f)
        {
            return;
        }
        Vector2 dir = beam / dist;
        float effectScale = BeamEffectScale(t);
        float baseAlpha = life >= 0.4f ? 1.0f : Mathf.Clamp(life * 2.5f, 0.0f, 1.0f);
        if (baseAlpha <= 1e-3f)
        {
            return;
        }

        Color streak = isFire ? new Color(1.0f, 0.6f, 0.1f) : new Color(0.5f, 0.6f, 1.0f);
        Vector3 beamUv = CellUv(4, 2);
        float spriteSize = 64.0f * effectScale; // grid-4 cell = 64 units * effect scale

        // Body: stamped sprites over the last 256 units, alpha ramping 0 -> base.
        float start = dist > 256.0f ? dist - 256.0f : 0.0f;
        float span = dist - start;
        float step = Mathf.Min(effectScale * 3.1f, 9.0f);
        for (float s = start; s < dist; s += step)
        {
            float ta = span > 1e-6f ? (s - start) / span : 1.0f;
            float segAlpha = ta * baseAlpha;
            if (segAlpha > 1e-3f)
            {
                EmitAtlasSprite(_projsAtlasAddMesh, ref _projsAddN, ProjsAtlasAddCap, origin + dir * s,
                    spriteSize, 0.0f, new Color(streak.R, streak.G, streak.B, segAlpha), beamUv);
            }
        }

        if (life >= 0.4f)
        {
            EmitAtlasSprite(_projsAtlasAddMesh, ref _projsAddN, ProjsAtlasAddCap, pos, spriteSize,
                pr.Angle, new Color(1.0f, 1.0f, 0.7f, baseAlpha), beamUv);
            if (isFire)
            {
                // Fire Bullets extra particles.png glow overlay at the head.
                EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos, 64.0f, pr.Angle,
                    new Color(1.0f, 1.0f, 1.0f, 1.0f), _glowUv);
            }
            return;
        }

        // Fade stage: small blue core at the head.
        EmitAtlasSprite(_projsAtlasAddMesh, ref _projsAddN, ProjsAtlasAddCap, pos, 64.0f,
            pr.Angle, new Color(0.5f, 0.6f, 1.0f, baseAlpha), beamUv);

        if (IsIonType(t))
        {
            DrawIonChains(pos, effectScale, ionMaster ? 1.2f : 1.0f, baseAlpha, spriteSize, beamUv, view);
        }
    }

    private void DrawIonChains(Vector2 pos, float effectScale, float perkScale, float baseAlpha,
        float spriteSize, Vector3 beamUv, in SnapshotView view)
    {
        float radius = effectScale * perkScale * 40.0f;
        var tint = new Color(0.5f, 0.6f, 1.0f, baseAlpha);
        float innerHalf = 10.0f * effectScale;
        float outerHalf = 14.0f * effectScale;

        // Native scans the pool from index 1 (quirk); dense snapshot order is
        // pool order, so skip our first creature to match.
        bool first = true;
        foreach (Sim.CreatureSnap cSnap in view.Creatures)
        {
            if (first)
            {
                first = false;
                continue;
            }
            // Collidable lifecycle only (creature_lifecycle_is_collidable > 5).
            if (cSnap.LifecycleStage <= 5.0f)
            {
                continue;
            }
            var cp = new Vector2(cSnap.X, cSnap.Y);
            float d = pos.DistanceTo(cp);
            float threshold = cSnap.Size * 0.14285715f + 3.0f;
            if (d - radius >= threshold)
            {
                continue;
            }
            // Outer (softer) + inner strips, then an end glow on the target.
            EmitStretch(_ionStripMesh, ref _ionStripN, IonStripCap, pos, cp, outerHalf, tint, custom: false);
            EmitStretch(_ionStripMesh, ref _ionStripN, IonStripCap, pos, cp, innerHalf, tint, custom: false);
            EmitAtlasSprite(_projsAtlasAddMesh, ref _projsAddN, ProjsAtlasAddCap, cp, spriteSize, 0.0f, tint, beamUv);
        }
    }

    private void DrawPulse(in Sim.ProjectileSnap pr, Vector2 pos, Vector2 origin, float life)
    {
        Vector3 uv = CellUv(2, 0);
        if (life >= 0.4f)
        {
            float size = origin.DistanceTo(pos) * 0.16f;
            if (size > 1e-3f)
            {
                EmitAtlasSprite(_projsAtlasAddMesh, ref _projsAddN, ProjsAtlasAddCap, pos, size, pr.Angle,
                    new Color(0.1f, 0.6f, 0.2f, 0.7f), uv);
            }
            return;
        }
        float fade = Mathf.Clamp(life * 2.5f, 0.0f, 1.0f);
        if (fade > 1e-3f)
        {
            EmitAtlasSprite(_projsAtlasAddMesh, ref _projsAddN, ProjsAtlasAddCap, pos, 56.0f, pr.Angle,
                new Color(1.0f, 1.0f, 1.0f, fade), uv);
        }
    }

    private void DrawSplitterOrBlade(in Sim.ProjectileSnap pr, Vector2 pos, Vector2 origin, float life, float elapsedMs)
    {
        if (life < 0.4f || !KnownProjFrames.TryGetValue(pr.TypeId, out (int Grid, int Frame) map))
        {
            return;
        }
        float size = Mathf.Min(origin.DistanceTo(pos), 20.0f);
        if (size <= 1e-3f)
        {
            return;
        }
        float rotation = pr.Angle;
        var rgb = new Color(1.0f, 1.0f, 1.0f, 1.0f);
        if (pr.TypeId == ProjBladeGun)
        {
            rotation = pr.PoolIndex * 0.1f - elapsedMs * 0.1f;
            rgb = new Color(0.8f, 0.8f, 0.8f, 1.0f);
        }
        EmitAtlasSprite(_projsAtlasAlphaMesh, ref _projsAlphaN, ProjsAtlasAlphaCap, pos, size, rotation,
            rgb, CellUv(map.Grid, map.Frame));
    }

    private void DrawPlagueSpreader(in Sim.ProjectileSnap pr, Vector2 pos, float life, float elapsedMs)
    {
        Vector3 uv = CellUv(4, 2);
        var tint = new Color(1.0f, 1.0f, 1.0f, 1.0f);
        if (life >= 0.4f)
        {
            EmitAtlasSprite(_plagueMesh, ref _plagueN, PlagueCap, pos, 60.0f, 0.0f, tint, uv);

            float back = pr.Angle + Mathf.Pi;
            var offset = new Vector2(Mathf.Sin(back), -Mathf.Cos(back)) * 15.0f;
            EmitAtlasSprite(_plagueMesh, ref _plagueN, PlagueCap, pos + offset, 60.0f, 0.0f, tint, uv);

            float phase = pr.PoolIndex + elapsedMs * 0.01f;
            float cosP = Mathf.Cos(phase);
            float sinP = Mathf.Sin(phase);
            EmitAtlasSprite(_plagueMesh, ref _plagueN, PlagueCap,
                pos + new Vector2(cosP * cosP - 5.0f, sinP * 11.0f - 5.0f), 52.0f, 0.0f, tint, uv);

            float p120 = phase + 2.0943952f;
            float sin120 = Mathf.Sin(p120);
            EmitAtlasSprite(_plagueMesh, ref _plagueN, PlagueCap,
                pos + new Vector2(Mathf.Cos(p120) * 10.0f, Mathf.Sin(p120) * 10.0f), 62.0f, 0.0f, tint, uv);

            float p240 = phase + 4.1887903f;
            EmitAtlasSprite(_plagueMesh, ref _plagueN, PlagueCap,
                pos + new Vector2(Mathf.Cos(p240) * 10.0f, Mathf.Sin(p240) * sin120), 62.0f, 0.0f, tint, uv);
            return;
        }
        float fade = Mathf.Clamp(life * 2.5f, 0.0f, 1.0f);
        if (fade > 1e-3f)
        {
            EmitAtlasSprite(_plagueMesh, ref _plagueN, PlagueCap, pos, fade * 40.0f + 32.0f, 0.0f,
                new Color(1.0f, 1.0f, 1.0f, fade), uv);
        }
    }

    private void DrawSecondary(in Sim.SecondarySnap s)
    {
        var pos = new Vector2(s.X, s.Y);
        if (s.TypeId == SecDetonation)
        {
            float t = Mathf.Clamp(s.DetonationT, 0.0f, 1.0f);
            float fade = 1.0f - t;
            if (fade <= 1e-3f || s.DetonationScale <= 1e-6f)
            {
                return;
            }
            var tint = new Color(1.0f, 0.6f, 0.1f, fade);
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos, s.DetonationScale * t * 64.0f, 0.0f, tint, _glowUv);
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos, s.DetonationScale * t * 200.0f, 0.0f,
                new Color(1.0f, 0.6f, 0.1f, fade * 0.3f), _glowUv);
            return;
        }

        (float BaseSize, float GlowSize, Color GlowRgb, float GlowAlphaMul)? style = RocketStyle(s.TypeId);
        if (style is not { } st)
        {
            return;
        }
        // Glow pair trails a touch behind the rocket along its heading.
        var dir = new Vector2(Mathf.Sin(s.Angle), -Mathf.Cos(s.Angle));
        if (_graphicsDetail >= 2)
        {
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos - dir * 5.0f, 140.0f, 0.0f,
                new Color(1.0f, 1.0f, 1.0f, 0.48f), _glowUv);
            EmitAtlasSprite(_projGlowMesh, ref _projGlowN, ProjGlowCap, pos - dir * 9.0f, st.GlowSize, 0.0f,
                new Color(st.GlowRgb.R, st.GlowRgb.G, st.GlowRgb.B, st.GlowAlphaMul), _glowUv);
        }
        // Rocket body sprite (projs 4x4 frame 3), alpha pass.
        EmitAtlasSprite(_projsAtlasAlphaMesh, ref _projsAlphaN, ProjsAtlasAlphaCap, pos, st.BaseSize, s.Angle,
            new Color(0.8f, 0.8f, 0.8f, 0.9f), CellUv(4, 3));
    }
}
