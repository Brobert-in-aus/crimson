using Godot;

namespace CrimsonVR;

/// <summary>
/// Programmer-art renderer for M2: draws the sim snapshot as flat colored quads
/// on the arena plane via one MultiMeshInstance3D per entity layer (PLAN.md §6
/// instancing). Real sprites/atlases/2.5D tilt come in M3.
///
/// Sim ticks at 60 Hz; the headset renders faster, so each tick's entity state
/// is copied into per-layer prev/curr buffers and <see cref="Interpolate"/>
/// lerps between them at render rate. Snapshot arrays carry no stable ids, so
/// interpolation matches by dense index — correct except on the exact frame an
/// entity spawns/dies, where a token may pop (acceptable for the skeleton).
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

    // Capacities comfortably above the runtime pool sizes (see host_abi
    // snapshotMaxSize); excess entities in a tick are dropped rather than grow.
    private const int PlayerCap = 4;
    private const int CreatureCap = 2048;
    private const int ProjectileCap = 8192;
    private const int SecondaryCap = 2048;
    private const int BonusCap = 256;

    private float _arenaSideMeters;
    private float _worldSize;

    private Layer _players = null!;
    private Layer _creatures = null!;
    private Layer _projectiles = null!;
    private Layer _secondaries = null!;
    private Layer _bonuses = null!;

    // Base flat orientation: rotate the +Z-facing QuadMesh to face up (+Y) so it
    // lies on the arena plane. Yaw is applied on top per entity heading.
    private static readonly Basis FlatBasis = Basis.FromEuler(new Vector3(-Mathf.Pi / 2.0f, 0.0f, 0.0f));

    public void Configure(float arenaSideMeters, float worldSize)
    {
        _arenaSideMeters = arenaSideMeters;
        _worldSize = worldSize;

        _players = BuildLayer(PlayerCap, new Color(0.95f, 0.95f, 0.95f), lift: 0.010f, sizeScale: 2.2f);
        _creatures = BuildLayer(CreatureCap, new Color(0.85f, 0.2f, 0.2f), lift: 0.008f, sizeScale: 2.0f);
        _projectiles = BuildLayer(ProjectileCap, new Color(1.0f, 0.9f, 0.3f), lift: 0.006f, sizeScale: 6.0f);
        _secondaries = BuildLayer(SecondaryCap, new Color(1.0f, 0.55f, 0.15f), lift: 0.006f, sizeScale: 8.0f);
        _bonuses = BuildLayer(BonusCap, new Color(0.3f, 0.85f, 0.95f), lift: 0.006f, sizeScale: 14.0f);
    }

    private Layer BuildLayer(int capacity, Color color, float lift, float sizeScale)
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new QuadMesh { Size = new Vector2(1.0f, 1.0f) },
            InstanceCount = capacity,
            VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D
        {
            Multimesh = mesh,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
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
            _players.Add(new Vector2(p.X, p.Y), p.Heading, p.Size);
        }

        _creatures.BeginPush();
        foreach (Sim.CreatureSnap c in view.Creatures)
        {
            _creatures.Add(new Vector2(c.X, c.Y), c.Heading, c.Size);
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
        InterpolateLayer(_players, frac);
        InterpolateLayer(_creatures, frac);
        InterpolateLayer(_projectiles, frac);
        InterpolateLayer(_secondaries, frac);
        InterpolateLayer(_bonuses, frac);
    }

    private void InterpolateLayer(Layer layer, float frac)
    {
        float k = _arenaSideMeters / _worldSize;
        // Snapshot arrays are dense with no stable ids, so index i maps to the
        // same entity across ticks ONLY while the active set is unchanged (equal
        // counts). On a spawn/death frame the dense order shifts, so
        // interpolating by index would streak unrelated tokens across the arena
        // for a frame -- draw the fresh positions directly on those frames. (A
        // proper fix is id-based matching, an M3 concern once sprites need
        // persistent per-entity state.)
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

            Vector3 pos = Mapper.GameToArenaLocal(game, _arenaSideMeters, _worldSize)
                          + new Vector3(0.0f, layer.Lift, 0.0f);
            float meters = Mathf.Max(sizeGame * k * layer.SizeScale, 0.002f);
            Basis basis = (new Basis(Vector3.Up, angle) * FlatBasis).Scaled(new Vector3(meters, meters, meters));
            layer.Mesh.SetInstanceTransform(i, new Transform3D(basis, pos));
        }
        layer.Mesh.VisibleInstanceCount = layer.CurrCount;
    }

    private static float LerpAngle(float from, float to, float t)
    {
        float diff = Mathf.Wrap(to - from, -Mathf.Pi, Mathf.Pi);
        return from + diff * t;
    }
}
