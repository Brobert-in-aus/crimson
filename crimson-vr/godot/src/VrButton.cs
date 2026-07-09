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
    // The button must be seen RELEASED once before it can fire again. Stops an
    // instant press when a panel is (re)shown with a fingertip already inside the
    // button volume (e.g. Back returns to a menu with the hand over Quit).
    private bool _armed;
    // Presses are also ignored for a short settle window after the button is shown,
    // so a finger arriving within a frame or two as the panel pops up can't fire.
    private ulong _readyAtMs;
    private const ulong ShowCooldownMs = 350;

    private Color _baseColor;
    private Color _pressedColor;

    /// <summary>Fired once when the button is pushed past the press depth.</summary>
    public event Action? OnPress;

    /// <summary>Global poke-click hook: fired (with this button's <see cref="ClickSound"/>)
    /// on every press-down edge, so a single wiring in Main plays the UI click cue for
    /// every diegetic button without per-button plumbing.</summary>
    public static Action<int>? OnAnyPress;

    /// <summary>Which UI cue this button plays on press (AudioBank.UiButton/UiType/
    /// UiEnter). Default is the menu button click; the keyboard sets its keys to Type.</summary>
    public int ClickSound;

    /// <summary>True while the button is currently held in (for press-and-hold, e.g.
    /// the perk '?' description popup).</summary>
    public bool IsPressed => _pressed;

    /// <summary>Index/payload the owner can read in the OnPress handler (e.g. the
    /// perk choice index or a menu action id). Purely for the caller's use.</summary>
    public int Payload;

    // Shared ui_menuItem neon-bar plate (the original menu-button art), lazily
    // loaded so plate-styled buttons match the main menu without per-call plumbing.
    private static Texture2D? _sharedPlate;
    private static bool _plateChecked;
    private static Texture2D? SharedPlate()
    {
        if (!_plateChecked)
        {
            _plateChecked = true;
            const string path = "res://assets/sprites/ui_menuItem.png";
            _sharedPlate = ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
        }
        return _sharedPlate;
    }

    private bool _plate;

    /// <param name="plate">Use the original ui_menuItem neon-bar plate as the button
    /// face (dark textured bar + centred label, matching the main menu), instead of
    /// the flat colour box. <paramref name="color"/> becomes a subtle accent tint.</param>
    public void Build(float width, float height, string? text, Color color, float proud = 0.02f, bool plate = false)
    {
        _halfW = width * 0.5f;
        _halfH = height * 0.5f;
        _proud = proud;
        _pressDepth = proud * 0.1f; // fires once pushed ~90% of the way in
        Texture2D? plateTex = plate ? SharedPlate() : null;
        _plate = plateTex != null;

        if (_plate)
        {
            // Plate style: the neon bar is its own dark background, so no socket.
            // Base tint slightly dims the plate; pressed brightens it.
            _baseColor = new Color(0.85f, 0.85f, 0.9f);
            _pressedColor = Colors.White;
        }
        else
        {
            _baseColor = color;
            _pressedColor = color.Lerp(Colors.White, 0.55f);

            // Socket/backing at the panel surface so the button reads as recessed.
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(width * 1.08f, height * 1.08f, 0.004f) },
                Position = new Vector3(0.0f, 0.0f, -0.002f),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.08f, 0.08f, 0.10f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
            });
        }

        // The proud button face, moved along local Z by the poke. A textured quad
        // for the plate style, else a flat colour box.
        _mat = new StandardMaterial3D
        {
            AlbedoTexture = plateTex,
            AlbedoColor = _baseColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = _plate ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _face = new MeshInstance3D
        {
            Mesh = _plate
                ? new QuadMesh { Size = new Vector2(width, height) }
                : new BoxMesh { Size = new Vector3(width, height, 0.006f) },
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
            // Child of _face, so a small +Z lift keeps it just in front of the face
            // surface (was _proud+0.004 which double-counted the face's own proud
            // offset, floating the text forward and shifting it under parallax).
            Position = new Vector3(0.0f, 0.0f, 0.004f),
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

    /// <summary>Override the auto-sized label with an explicit pixel size and a wrap
    /// width (metres), for buttons where the default height-based sizing is wrong —
    /// e.g. tall portrait perk cards, whose long names must shrink and word-wrap to
    /// fit the card rather than overflow into their neighbours.</summary>
    public void ConfigureLabel(float pixelSize, float wrapWidthMeters)
    {
        if (_label == null)
        {
            return;
        }
        _label.PixelSize = pixelSize;
        _label.Width = wrapWidthMeters / pixelSize; // Label3D.Width is in font pixels
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    /// <summary>Permanently switch the face material to alpha transparency so
    /// SetFade never has to flip the pipeline mid-fade. Call once after Build on
    /// buttons that will fade: the lazy switch inside SetFade caused a shader/
    /// pipeline compile stall on Quest the first time a fade ran, eating the
    /// whole 220 ms window (cards popped instead of easing in).</summary>
    public void PrewarmFade()
    {
        if (_mat != null)
        {
            _mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }
    }

    /// <summary>Fade the whole button in/out (0 = invisible, 1 = opaque) for the
    /// perk-pick cross-fade. Modulates the face + label alpha; enables alpha
    /// transparency so a flat-colour face can fade. 1.0 restores the normal look.</summary>
    public void SetFade(float alpha)
    {
        alpha = Mathf.Clamp(alpha, 0.0f, 1.0f);
        if (_mat != null)
        {
            if (alpha < 1.0f && _mat.Transparency == BaseMaterial3D.TransparencyEnum.Disabled)
            {
                _mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            }
            Color c = _mat.AlbedoColor;
            _mat.AlbedoColor = new Color(c.R, c.G, c.B, alpha);
        }
        if (_label != null)
        {
            Color m = _label.Modulate;
            _label.Modulate = new Color(m.R, m.G, m.B, alpha);
            Color o = _label.OutlineModulate;
            _label.OutlineModulate = new Color(o.R, o.G, o.B, alpha);
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
        bool inside = false;         // over the face AND pushed in past the rest depth
        bool overFootprint = false;  // fingertip within the button's X/Y area (any depth)
        float frontZ = _proud;       // least-pressed default
        float nearestSurface = float.MaxValue; // closest fingertip depth over the footprint
        foreach (HandProbe h in probes)
        {
            if (!h.Valid)
            {
                continue;
            }
            Vector3 local = ToLocal(h.Tip);
            if (Mathf.Abs(local.X) <= _halfW + PokeRadius && Mathf.Abs(local.Y) <= _halfH + PokeRadius)
            {
                overFootprint = true;
                float surface = local.Z - PokeRadius; // sphere front toward the panel
                nearestSurface = Mathf.Min(nearestSurface, surface);
                if (surface < _proud)
                {
                    inside = true;
                    frontZ = Mathf.Min(frontZ, surface);
                }
            }
        }

        float z = inside ? Mathf.Clamp(frontZ, 0.0f, _proud) : _proud;
        _face.Position = new Vector3(0.0f, 0.0f, z);

        bool nowPressed = inside && z <= _pressDepth;
        _mat.AlbedoColor = nowPressed ? _pressedColor : _baseColor;
        // Re-arm only once the fingertip has fully LEFT the button's dead-zone
        // volume: laterally out of the footprint, OR pulled back past a clearance
        // of 2x the button depth in front of the rest face. So a hand resting/
        // hovering just above a button (e.g. after pressing Back, or when a menu
        // reopens under it) must be deliberately moved clear before it can fire.
        bool inDeadZone = overFootprint && nearestSurface <= _proud + DeadZoneClearance;
        if (!inDeadZone)
        {
            _armed = true;
        }
        else if (nowPressed && !_pressed && _armed && Time.GetTicksMsec() >= _readyAtMs)
        {
            OnAnyPress?.Invoke(ClickSound);
            OnPress?.Invoke();
        }
        _pressed = nowPressed;
    }

    private float DeadZoneClearance => _proud * 2.0f; // 2x button depth of clearance in front

    /// <summary>Reset to the unpressed rest state (e.g. when the menu hides, so a
    /// held poke doesn't re-fire when it reappears).</summary>
    public void ResetPress()
    {
        _pressed = false;
        _armed = false; // require a release before the next press can fire
        _readyAtMs = Time.GetTicksMsec() + ShowCooldownMs;
        if (_face != null)
        {
            _face.Position = new Vector3(0.0f, 0.0f, _proud);
            _mat.AlbedoColor = _baseColor;
        }
    }
}
