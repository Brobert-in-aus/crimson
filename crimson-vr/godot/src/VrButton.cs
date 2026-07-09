using System;
using Godot;

namespace CrimsonVR;

/// <summary>One controller's per-frame menu probe: whether it's tracking, its
/// poke-tip world position, and whether its grip is squeezed (for grab-drag).
/// Passed as a fixed [left, right] span so grabs keep a stable hand identity.</summary>
public readonly struct HandProbe
{
    public readonly bool Valid;
    public readonly Vector3 Tip;
    public readonly bool Grip;

    public HandProbe(bool valid, Vector3 tip, bool grip)
    {
        Valid = valid;
        Tip = tip;
        Grip = grip;
    }
}

/// <summary>
/// A diegetic physical push-button for VR menus (PLAN M4 UI model): a button that
/// sits PROUD of its panel along local +Z and is POKED with a controller tip —
/// it depresses to follow the tip and fires <see cref="OnPress"/> on the
/// press-down edge, showing a pressed colour while held in. No laser pointer.
///
/// Local frame: the panel lies in local XY, the front face normal is local +Z
/// (toward the player). Callers place/orient the button node; poke points are
/// supplied in world space each frame via <see cref="PollPoke"/>.
///
/// Built from code as a child of a menu root so it inherits the menu's
/// world placement. Dimensions are metres; tuned first-pass, refine in-headset.
/// </summary>
public sealed partial class VrButton : Node3D
{
    private MeshInstance3D _face = null!;
    private StandardMaterial3D _mat = null!;
    private Label3D? _label;

    private float _halfW;
    private float _halfH;
    private float _proud;         // rest depth of the button front (local +Z)
    private float _pressDepth;    // press fires once the front passes this local Z
    private bool _pressed;

    private Color _baseColor;
    private Color _pressedColor;

    /// <summary>Fired once when the button is pushed past the press depth.</summary>
    public event Action? OnPress;

    /// <summary>Index/payload the owner can read in the OnPress handler (e.g. the
    /// perk choice index or a menu action id). Purely for the caller's use.</summary>
    public int Payload;

    public void Build(float width, float height, string? text, Color color, float proud = 0.012f)
    {
        _halfW = width * 0.5f;
        _halfH = height * 0.5f;
        _proud = proud;
        _pressDepth = proud * 0.1f; // fires once pushed ~90% of the way in
        _baseColor = color;
        _pressedColor = color.Lerp(Colors.White, 0.55f);

        // Socket/backing at the panel surface (local Z = 0) so the button reads as
        // recessed into a frame.
        var socket = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(width * 1.08f, height * 1.08f, 0.004f) },
            Position = new Vector3(0.0f, 0.0f, -0.002f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.08f, 0.08f, 0.10f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(socket);

        // The proud button face, moved along local Z by the poke.
        _mat = new StandardMaterial3D
        {
            AlbedoColor = _baseColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _face = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(width, height, 0.006f) },
            Position = new Vector3(0.0f, 0.0f, _proud),
            MaterialOverride = _mat,
        };
        AddChild(_face);

        // Always create the label (even if empty) so SetText works later -
        // checklist rows are built empty then filled, and without this they had
        // no label node at all (blank buttons).
        _label = new Label3D
        {
            Text = text ?? string.Empty,
            FontSize = 96,
            PixelSize = height / 220.0f, // scale text to the button
            Modulate = new Color(0.95f, 0.95f, 0.97f),
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, 0.0f, _proud + 0.004f),
            NoDepthTest = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
        };
        _face.AddChild(_label);
    }

    public void SetText(string text)
    {
        if (_label != null)
        {
            _label.Text = text;
        }
    }

    /// <summary>Recolor the button (e.g. a checklist item changing pass/fail).</summary>
    public void SetColor(Color color)
    {
        _baseColor = color;
        _pressedColor = color.Lerp(Colors.White, 0.55f);
        if (_mat != null && !_pressed)
        {
            _mat.AlbedoColor = _baseColor;
        }
    }

    /// <summary>Update the depress state from the controller probes. Call every
    /// rendered frame while the button is visible.</summary>
    // The hand-marker sphere (this radius) is the collider: its leading surface,
    // PokeRadius ahead of the tracked centre, pushes the button face in. So the
    // visible sphere and the collision line up.
    private const float PokeRadius = 0.02f;

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        bool inside = false;
        float frontZ = _proud; // least-pressed default
        foreach (HandProbe h in probes)
        {
            if (!h.Valid)
            {
                continue;
            }
            Vector3 local = ToLocal(h.Tip);
            float surface = local.Z - PokeRadius; // sphere front toward the panel
            if (Mathf.Abs(local.X) <= _halfW + PokeRadius && Mathf.Abs(local.Y) <= _halfH + PokeRadius && surface < _proud)
            {
                inside = true;
                frontZ = Mathf.Min(frontZ, surface);
            }
        }

        float z = inside ? Mathf.Clamp(frontZ, 0.0f, _proud) : _proud;
        _face.Position = new Vector3(0.0f, 0.0f, z);

        bool nowPressed = inside && z <= _pressDepth;
        _mat.AlbedoColor = nowPressed ? _pressedColor : _baseColor;
        if (nowPressed && !_pressed)
        {
            OnPress?.Invoke();
        }
        _pressed = nowPressed;
    }

    /// <summary>Reset to the unpressed rest state (e.g. when the menu hides, so a
    /// held poke doesn't re-fire when it reappears).</summary>
    public void ResetPress()
    {
        _pressed = false;
        if (_face != null)
        {
            _face.Position = new Vector3(0.0f, 0.0f, _proud);
            _mat.AlbedoColor = _baseColor;
        }
    }
}
