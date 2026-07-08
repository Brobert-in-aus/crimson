using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Diorama renderer. M3 slice 1: creatures and the player are drawn as real
/// Crimsonland sprites (static frame from their 8x8 sheets, PLAN §6), one
/// MultiMeshInstance3D per creature type so each can carry its own sheet
/// texture; projectiles/secondaries/bonuses remain colored quads for now.
/// Orientation comes from each entity's heading (a yaw applied per instance),
/// so static frames still face the right way — animation (anim_phase -> frame)
/// is slice 2 on this same per-instance path.
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

        public void Add(Vector2 game, float angle, float sizeGame)
        {
            if (CurrCount >= Curr.Length)
            {
                return;
            }
            Curr[CurrCount++] = new Ent { Game = game, Angle = angle, SizeGame = sizeGame };
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

    private static readonly Basis FlatBasis = Basis.FromEuler(new Vector3(-Mathf.Pi / 2.0f, 0.0f, 0.0f));

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
                        BuildSpriteLayer(CreatureCapPerType, kv.Value, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 2.4f);
                }
            }
        }
        // Fallback for unmapped creature types (e.g. bosses) so nothing vanishes.
        _creatureFallback = BuildColorLayer(CreatureCapPerType, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 2.0f, renderPriority: 8);

        // Native pass order (top of the stack): player < projectiles/effects < bonuses/UI.
        _projectiles = BuildColorLayer(ProjectileCap, new Color(1.0f, 0.9f, 0.3f), lift: 0.006f, sizeScale: 6.0f, renderPriority: 20);
        _secondaries = BuildColorLayer(SecondaryCap, new Color(1.0f, 0.55f, 0.15f), lift: 0.006f, sizeScale: 8.0f, renderPriority: 20);
        _bonuses = BuildColorLayer(BonusCap, new Color(0.3f, 0.85f, 0.95f), lift: 0.006f, sizeScale: 14.0f, renderPriority: 25);

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

    private Layer BuildLayer(int capacity, Material material, float lift, float sizeScale)
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = capacity,
            VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D { Multimesh = mesh, MaterialOverride = material };
        AddChild(node);
        return new Layer(capacity) { Mesh = mesh, Lift = lift, SizeScale = sizeScale };
    }

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
            target.Add(new Vector2(c.X, c.Y), c.Heading, c.Size);
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
        InterpolateLayer(_players, frac, sprite: true);
        foreach (Layer layer in _creatureLayers.Values)
        {
            InterpolateLayer(layer, frac, sprite: true);
        }
        InterpolateLayer(_creatureFallback, frac, sprite: false);
        InterpolateLayer(_projectiles, frac, sprite: false);
        InterpolateLayer(_secondaries, frac, sprite: false);
        InterpolateLayer(_bonuses, frac, sprite: false);
        if (_needles != null)
        {
            _needles.VisibleInstanceCount = _needleCount;
        }
    }

    private void InterpolateLayer(Layer layer, float frac, bool sprite)
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
            Basis basis;
            if (sprite)
            {
                // Face the sim direction directly (no RotY handedness flip),
                // plus the sheet's art-facing correction.
                basis = FlatFacingBasis(ForwardFromHeading(angle + layer.HeadingOffset), meters);
            }
            else
            {
                basis = (new Basis(Vector3.Up, angle) * FlatBasis).Scaled(new Vector3(meters, meters, meters));
            }
            layer.Mesh.SetInstanceTransform(i, new Transform3D(basis, pos));

            if (sprite && _needles != null)
            {
                AddNeedle(arena, angle);
            }
        }
        layer.Mesh.VisibleInstanceCount = layer.CurrCount;
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
