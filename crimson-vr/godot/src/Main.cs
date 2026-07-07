using Godot;

namespace CrimsonVR;

/// <summary>
/// M0 spike scene: OpenXR bootstrap, a tabletop arena plane, and the
/// controller vertical-projection reticles (PLAN.md §4). Everything is built
/// in code; scenes/main.tscn is just this script on a root node.
/// </summary>
public partial class Main : Node3D
{
    private const float ArenaSideMeters = 1.0f;
    private const float ArenaHeightMeters = 0.75f;
    private const float GameWorldSize = 1024.0f;

    private XROrigin3D _origin = null!;
    private XRController3D _leftHand = null!;
    private XRController3D _rightHand = null!;
    private Node3D _arenaRoot = null!;
    private Node3D _leftReticle = null!;
    private Node3D _rightReticle = null!;
    private Node3D _leftGuide = null!;
    private Node3D _rightGuide = null!;
    private Label3D _status = null!;

    private bool _xrActive;

    public override void _Ready()
    {
        InitializeXr();
        BuildEnvironment();
        BuildRig();
        BuildArena();
        BuildReticles();
        BuildStatusLabel();
        _status.Text = _xrActive
            ? $"XR active | sim abi v{TryQueryAbiVersion()}"
            : "XR NOT ACTIVE (flat window fallback)";
    }

    private void InitializeXr()
    {
        var xr = XRServer.FindInterface("OpenXR");
        if (xr != null && (xr.IsInitialized() || xr.Initialize()))
        {
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            _xrActive = true;
            GD.Print("CrimsonVR: OpenXR initialized");
        }
        else
        {
            GD.PushWarning("CrimsonVR: OpenXR unavailable; running flat");
        }
    }

    private void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.05f, 0.06f, 0.08f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.4f, 0.4f, 0.45f),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var light = new DirectionalLight3D { LightEnergy = 1.2f };
        light.RotationDegrees = new Vector3(-55.0f, 30.0f, 0.0f);
        AddChild(light);
    }

    private void BuildRig()
    {
        _origin = new XROrigin3D();
        AddChild(_origin);
        _origin.AddChild(new XRCamera3D { Position = new Vector3(0, 1.7f, 0) });

        _leftHand = new XRController3D { Tracker = "left_hand", Pose = "grip" };
        _rightHand = new XRController3D { Tracker = "right_hand", Pose = "grip" };
        _origin.AddChild(_leftHand);
        _origin.AddChild(_rightHand);

        _leftHand.AddChild(MakeHandMarker(new Color(0.2f, 0.5f, 1.0f)));
        _rightHand.AddChild(MakeHandMarker(new Color(1.0f, 0.3f, 0.25f)));
    }

    private static MeshInstance3D MakeHandMarker(Color color)
        => new()
        {
            Mesh = new SphereMesh { Radius = 0.02f, Height = 0.04f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
        };

    private void BuildArena()
    {
        _arenaRoot = new Node3D { Position = new Vector3(0.0f, ArenaHeightMeters, -0.6f) };
        AddChild(_arenaRoot);

        var surface = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(ArenaSideMeters, ArenaSideMeters) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.22f, 0.12f),
                Roughness = 1.0f,
            },
        };
        _arenaRoot.AddChild(surface);

        // Rim so the playfield edge reads clearly in-headset.
        var rim = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(ArenaSideMeters + 0.04f, 0.02f, ArenaSideMeters + 0.04f) },
            Position = new Vector3(0, -0.011f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.18f) },
        };
        _arenaRoot.AddChild(rim);

        // Placeholder "player" marker at arena center (game 512,512).
        var player = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.012f, Height = 0.05f },
            Position = Mapper.GameToArenaLocal(new Vector2(512, 512), ArenaSideMeters, GameWorldSize)
                       + new Vector3(0, 0.025f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.9f, 0.9f) },
        };
        _arenaRoot.AddChild(player);
    }

    private void BuildReticles()
    {
        _leftReticle = MakeReticle(new Color(0.2f, 0.5f, 1.0f));
        _rightReticle = MakeReticle(new Color(1.0f, 0.3f, 0.25f));
        _leftGuide = MakeGuide(new Color(0.2f, 0.5f, 1.0f, 0.35f));
        _rightGuide = MakeGuide(new Color(1.0f, 0.3f, 0.25f, 0.35f));
        AddChild(_leftReticle);
        AddChild(_rightReticle);
        AddChild(_leftGuide);
        AddChild(_rightGuide);
    }

    private static Node3D MakeReticle(Color color)
    {
        var reticle = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.018f, OuterRadius = 0.028f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        return reticle;
    }

    private static Node3D MakeGuide(Color color)
        => new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0015f, BottomRadius = 0.0015f, Height = 1.0f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };

    private void BuildStatusLabel()
    {
        _status = new Label3D
        {
            Position = new Vector3(0, ArenaHeightMeters + 0.35f, -0.6f),
            FontSize = 48,
            PixelSize = 0.001f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        AddChild(_status);
    }

    public override void _Process(double delta)
    {
        UpdateHand(_leftHand, _leftReticle, _leftGuide, isMoveHand: true);
        UpdateHand(_rightHand, _rightReticle, _rightGuide, isMoveHand: false);
    }

    private void UpdateHand(XRController3D hand, Node3D reticle, Node3D guide, bool isMoveHand)
    {
        bool tracking = hand.GetHasTrackingData();
        reticle.Visible = tracking;
        guide.Visible = tracking;
        if (!tracking)
        {
            return;
        }

        float planeY = _arenaRoot.GlobalPosition.Y;
        Vector3 handPos = hand.GlobalPosition;
        Vector3 hit = Mapper.ProjectVertically(handPos, planeY);

        // Clamp the reticle to the arena footprint; dim it when outside.
        Vector3 arenaLocal = _arenaRoot.ToLocal(hit);
        bool over = Mapper.IsOverArena(arenaLocal, ArenaSideMeters);
        Vector2 game = Mapper.ArenaLocalToGame(arenaLocal, ArenaSideMeters, GameWorldSize);
        Vector3 clampedLocal = Mapper.GameToArenaLocal(game, ArenaSideMeters, GameWorldSize);
        Vector3 clampedWorld = _arenaRoot.ToGlobal(clampedLocal);

        reticle.GlobalPosition = clampedWorld + new Vector3(0, 0.002f, 0);
        var mesh = (MeshInstance3D)reticle;
        var material = (StandardMaterial3D)mesh.MaterialOverride;
        float triggerValue = hand.GetFloat("trigger");
        Color baseColor = isMoveHand ? new Color(0.2f, 0.5f, 1.0f) : new Color(1.0f, 0.3f, 0.25f);
        material.AlbedoColor = over
            ? baseColor.Lerp(Colors.White, triggerValue)
            : baseColor * new Color(1, 1, 1, 0.35f);

        // Vertical guide line from the controller down to the plane point.
        float guideHeight = Mathf.Max(0.02f, handPos.Y - planeY);
        guide.GlobalPosition = new Vector3(handPos.X, planeY + guideHeight * 0.5f, handPos.Z);
        ((MeshInstance3D)guide).Scale = new Vector3(1, guideHeight, 1);
    }

    private static uint TryQueryAbiVersion()
    {
        try
        {
            return Sim.AbiVersion();
        }
        catch (System.Exception)
        {
            return 0; // native library not present; fine for the M0 spike
        }
    }
}
