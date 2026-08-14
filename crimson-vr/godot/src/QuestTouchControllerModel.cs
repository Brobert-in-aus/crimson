using Godot;

namespace CrimsonVR;

/// <summary>
/// A lightweight Touch-style controller built from separate moving parts.
/// Meta's XR_FB_render_model is display-only, so this model deliberately uses
/// the live OpenXR actions to show trigger, grip, stick and button travel.
/// </summary>
public sealed partial class QuestTouchControllerModel : Node3D
{
    private XRController3D? _input;
    private bool _left;
    private Node3D _trigger = null!;
    private Node3D _grip = null!;
    private Node3D _stick = null!;
    private Node3D _primary = null!;
    private Node3D _secondary = null!;
    private Node3D? _menu;
    private Transform3D _triggerRest;
    private Transform3D _gripRest;
    private Transform3D _stickRest;
    private Transform3D _primaryRest;
    private Transform3D _secondaryRest;
    private Transform3D _menuRest;

    public void Configure(XRController3D input, bool left)
    {
        _input = input;
        _left = left;
        BuildModel();
    }

    public override void _Process(double delta)
    {
        if (_input == null || _trigger == null)
        {
            return;
        }

        float trigger = Mathf.Clamp(_input.GetFloat("trigger"), 0.0f, 1.0f);
        float grip = Mathf.Clamp(_input.GetFloat("grip"), 0.0f, 1.0f);
        Vector2 stick = _input.GetVector2("primary");
        bool stickClick = _input.IsButtonPressed("primary_click");
        bool primary = _input.IsButtonPressed("ax_button");
        bool secondary = _input.IsButtonPressed("by_button");

        _trigger.Transform = _triggerRest * new Transform3D(
            Basis.FromEuler(new Vector3(Mathf.DegToRad(-16.0f * trigger), 0.0f, 0.0f)),
            Vector3.Down * (0.002f * trigger));
        _grip.Transform = _gripRest * new Transform3D(Basis.Identity,
            new Vector3(_left ? 0.0035f : -0.0035f, 0.0f, 0.0f) * grip);
        _stick.Transform = _stickRest * new Transform3D(
            Basis.FromEuler(new Vector3(Mathf.DegToRad(11.0f * stick.Y), 0.0f,
                Mathf.DegToRad(-11.0f * stick.X))),
            Vector3.Down * (stickClick ? 0.002f : 0.0f));
        _primary.Transform = _primaryRest * Press(primary);
        _secondary.Transform = _secondaryRest * Press(secondary);
        if (_menu != null)
        {
            bool pressed = _input.IsButtonPressed("menu_button") || _input.IsButtonPressed("recenter");
            _menu.Transform = _menuRest * Press(pressed);
        }
    }

    private void BuildModel()
    {
        float inner = _left ? 1.0f : -1.0f;
        Material body = Material(new Color(0.20f, 0.21f, 0.23f), 0.62f);
        Material trim = Material(new Color(0.055f, 0.06f, 0.07f), 0.78f);
        Material control = Material(new Color(0.10f, 0.11f, 0.13f), 0.48f);
        Material accent = Material(_left
            ? new Color(0.24f, 0.54f, 1.0f) : new Color(1.0f, 0.30f, 0.22f), 0.4f);

        AddMesh(new CapsuleMesh { Radius = 0.025f, Height = 0.105f }, body,
            new Vector3(0.0f, -0.040f, 0.014f), new Vector3(Mathf.DegToRad(-8.0f), 0.0f, 0.0f));
        MeshInstance3D housing = AddMesh(new SphereMesh { Radius = 0.04f, Height = 0.08f }, body,
            new Vector3(0.0f, 0.018f, -0.018f), Vector3.Zero);
        housing.Scale = new Vector3(1.0f, 0.62f, 1.18f);
        AddMesh(new BoxMesh { Size = new Vector3(0.064f, 0.008f, 0.070f) }, trim,
            new Vector3(0.0f, 0.039f, -0.020f), new Vector3(Mathf.DegToRad(-7.0f), 0.0f, 0.0f));

        _trigger = new Node3D { Position = new Vector3(inner * 0.018f, 0.017f, -0.060f) };
        AddChild(_trigger);
        AddMeshTo(_trigger, new BoxMesh { Size = new Vector3(0.020f, 0.013f, 0.030f) }, control,
            new Vector3(0.0f, 0.0f, -0.012f));

        _grip = new Node3D { Position = new Vector3(-inner * 0.024f, -0.036f, 0.006f) };
        AddChild(_grip);
        AddMeshTo(_grip, new BoxMesh { Size = new Vector3(0.008f, 0.042f, 0.024f) }, control, Vector3.Zero);

        _stick = new Node3D { Position = new Vector3(inner * 0.017f, 0.047f, -0.026f) };
        AddChild(_stick);
        AddMeshTo(_stick, new CylinderMesh { TopRadius = 0.010f, BottomRadius = 0.010f, Height = 0.008f }, control,
            Vector3.Zero);
        AddMeshTo(_stick, new CylinderMesh { TopRadius = 0.013f, BottomRadius = 0.011f, Height = 0.004f }, accent,
            Vector3.Up * 0.006f);

        _primary = MakeButton(new Vector3(-inner * 0.014f, 0.047f, -0.039f), control, accent);
        _secondary = MakeButton(new Vector3(-inner * 0.025f, 0.044f, -0.018f), control, accent);
        if (_left)
        {
            _menu = MakeButton(new Vector3(0.0f, 0.045f, 0.001f), control, trim, 0.006f);
        }

        // Thin accent seam makes the handedness and controller orientation
        // readable even against the dark VR background.
        AddMesh(new BoxMesh { Size = new Vector3(0.004f, 0.050f, 0.004f) }, accent,
            new Vector3(inner * 0.022f, -0.032f, 0.029f), Vector3.Zero);

        _triggerRest = _trigger.Transform;
        _gripRest = _grip.Transform;
        _stickRest = _stick.Transform;
        _primaryRest = _primary.Transform;
        _secondaryRest = _secondary.Transform;
        if (_menu != null) _menuRest = _menu.Transform;
    }

    private Node3D MakeButton(Vector3 position, Material baseMaterial, Material capMaterial, float radius = 0.008f)
    {
        var pivot = new Node3D { Position = position };
        AddChild(pivot);
        AddMeshTo(pivot, new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.006f },
            baseMaterial, Vector3.Zero);
        AddMeshTo(pivot, new CylinderMesh { TopRadius = radius * 0.72f, BottomRadius = radius * 0.72f, Height = 0.002f },
            capMaterial, Vector3.Up * 0.004f);
        return pivot;
    }

    private MeshInstance3D AddMesh(PrimitiveMesh mesh, Material material, Vector3 position, Vector3 rotation)
    {
        var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position, Rotation = rotation };
        AddChild(instance);
        return instance;
    }

    private static void AddMeshTo(Node3D parent, PrimitiveMesh mesh, Material material, Vector3 position)
    {
        parent.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position });
    }

    private static StandardMaterial3D Material(Color color, float roughness) => new()
    {
        AlbedoColor = color,
        Metallic = 0.15f,
        Roughness = roughness,
    };

    private static Transform3D Press(bool pressed) =>
        new(Basis.Identity, Vector3.Down * (pressed ? 0.0025f : 0.0f));
}
