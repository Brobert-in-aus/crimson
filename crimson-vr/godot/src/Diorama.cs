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
/// grounds each creature/player on the plane.
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
        public float AnimPhase; // creatures only; drives per-instance frame
        public uint Flags;      // creatures only; anim strip/mirror/shock bits
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

        public void Add(Vector2 game, float angle, float sizeGame, float animPhase = 0.0f, uint flags = 0)
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
            };
        }
    }

    private const int PlayerCap = 4;
    private const int CreatureCapPerType = 1024;
    private const int ProjectileCap = 8192;
    private const int SecondaryCap = 2048;
    private const int BonusCap = 256;

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
    private static readonly bool DebugFacing = false;
    private const int NeedleCap = 8192;

    // 2.5D presentation (PLAN §6). Creature/player sprites lie flat on the plane
    // (top-down art), tilted back toward the seated player so they read as
    // "standing" from a low viewing angle without breaking when walking around.
    // The tilt is a fixed arena-frame lean (heading-independent normal), toward
    // the player's near edge (-z; see RecenterArena). 0 = flat. Tune in-headset.
    private const float SpriteTiltDegrees = 22.0f;

    // Drop shadows: a soft dark blob on the plane under each creature/player to
    // ground them. One shared MultiMesh (circular, so no heading needed).
    private const int ShadowCap = 8192;
    private const float ShadowScale = 1.15f; // shadow diameter vs sprite footprint
    private const float ShadowLift = 0.0015f; // just above terrain to avoid z-fight

    private float _arenaSideMeters;
    private float _worldSize;

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
        _players = manifest?.player is { } pd
            ? BuildSpriteLayer(PlayerCap, pd, new Color(0.95f, 0.95f, 0.95f), lift: 0.012f, sizeScale: 2.6f)
            : BuildColorLayer(PlayerCap, new Color(0.95f, 0.95f, 0.95f), lift: 0.012f, sizeScale: 2.2f, renderPriority: 15);

        // One creature layer per type so each can bind its own sheet texture.
        if (manifest?.creatures is { } creatures)
        {
            foreach (KeyValuePair<string, SpriteDesc> kv in creatures)
            {
                if (int.TryParse(kv.Key, out int typeId))
                {
                    _creatureLayers[typeId] =
                        BuildAnimatedSpriteLayer(CreatureCapPerType, kv.Value, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 2.4f);
                }
            }
        }
        // Fallback for unmapped creature types (e.g. bosses) so nothing vanishes.
        _creatureFallback = BuildColorLayer(CreatureCapPerType, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 2.0f, renderPriority: 8);

        // Native pass order (top of the stack): player < projectiles/effects < bonuses/UI.
        _projectiles = BuildColorLayer(ProjectileCap, new Color(1.0f, 0.9f, 0.3f), lift: 0.006f, sizeScale: 6.0f, renderPriority: 20);
        _secondaries = BuildColorLayer(SecondaryCap, new Color(1.0f, 0.55f, 0.15f), lift: 0.006f, sizeScale: 8.0f, renderPriority: 20);
        _bonuses = BuildColorLayer(BonusCap, new Color(0.3f, 0.85f, 0.95f), lift: 0.006f, sizeScale: 14.0f, renderPriority: 25);

        BuildShadows();

        if (DebugFacing)
        {
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

        var material = new ShaderMaterial { Shader = SpriteShader, RenderPriority = desc.priority };
        material.SetShaderParameter("sheet", tex);
        Layer layer = BuildLayer(capacity, material, lift, sizeScale, useCustomData: true);
        layer.HeadingOffset = Mathf.DegToRad(desc.offsetDeg);
        layer.Animated = true;
        layer.Grid = Mathf.Max(desc.grid, 1);
        layer.BaseFrame = desc.baseFrame;
        layer.MirrorLong = desc.mirror;
        return layer;
    }

    private Layer BuildLayer(int capacity, Material material, float lift, float sizeScale, bool useCustomData = false)
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = useCustomData,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = capacity,
            VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D { Multimesh = mesh, MaterialOverride = material };
        AddChild(node);
        return new Layer(capacity) { Mesh = mesh, Lift = lift, SizeScale = sizeScale };
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
        AddChild(new MultiMeshInstance3D { Multimesh = _shadows, MaterialOverride = material });
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
        foreach (Sim.PlayerSnap p in view.Players)
        {
            // Player torso (trooper.png frame 16) is aimed, so rotate by aim.
            _players.Add(new Vector2(p.X, p.Y), p.AimHeading, p.Size);
        }

        foreach (Layer layer in _creatureLayers.Values)
        {
            layer.BeginPush();
        }
        _creatureFallback.BeginPush();
        foreach (Sim.CreatureSnap c in view.Creatures)
        {
            Layer target = _creatureLayers.TryGetValue(c.TypeId, out Layer? l) ? l : _creatureFallback;
            target.Add(new Vector2(c.X, c.Y), c.Heading, c.Size, c.AnimPhase, c.Flags);
        }

        _projectiles.BeginPush();
        foreach (Sim.ProjectileSnap pr in view.Projectiles)
        {
            _projectiles.Add(new Vector2(pr.X, pr.Y), pr.Angle, 1.0f);
        }

        _secondaries.BeginPush();
        foreach (Sim.SecondarySnap s in view.Secondaries)
        {
            _secondaries.Add(new Vector2(s.X, s.Y), s.Angle, 1.0f);
        }

        _bonuses.BeginPush();
        foreach (Sim.BonusSnap b in view.Bonuses)
        {
            _bonuses.Add(new Vector2(b.X, b.Y), 0.0f, 1.0f);
        }
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
        if (_needles != null)
        {
            _needles.VisibleInstanceCount = _needleCount;
        }
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
            float meters = Mathf.Max(sizeGame * k * layer.SizeScale, 0.002f);

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
            else
            {
                basis = (new Basis(Vector3.Up, angle) * FlatBasis).Scaled(new Vector3(meters, meters, meters));
            }
            layer.Mesh.SetInstanceTransform(i, new Transform3D(basis, pos));

            if (layer.Animated)
            {
                // Frame from the current tick's phase (not interpolated: the
                // phase wraps, so lerping across the seam would glitch; 60 Hz is
                // already smooth). Custom data = (uvOffX, uvOffY, uvScale, 0).
                int grid = layer.Grid;
                int frame = CreatureAnim.SelectFrame(cur.AnimPhase, layer.BaseFrame, layer.MirrorLong, cur.Flags);
                int cells = grid * grid;
                frame = frame < 0 ? 0 : (frame >= cells ? cells - 1 : frame);
                float inv = 1.0f / grid;
                layer.Mesh.SetInstanceCustomData(i, new Color(
                    (frame % grid) * inv,
                    (frame / grid) * inv,
                    inv,
                    0.0f));
            }

            if (sprite && _needles != null)
            {
                AddNeedle(arena, angle);
            }
        }
        layer.Mesh.VisibleInstanceCount = layer.CurrCount;
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

    private sealed class SpriteManifest
    {
        public Dictionary<string, SpriteDesc>? creatures { get; set; }
        public SpriteDesc? player { get; set; }
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
