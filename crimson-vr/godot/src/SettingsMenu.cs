using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 4: the settings menu (MVP). A panel above the arena, opened from the
/// pause menu's Settings button, holding the non-deferred settings: a hand-swap
/// toggle (which hand moves) and a dead-zone grab-drag slider, plus Back. Arena
/// scale/height + seated calibration are their own later slices (PLAN §5, M4).
///
/// A child of ArenaRoot; hidden until opened. Reflects the current values and
/// raises events Main applies live. Layout first-pass; tune in-headset.
/// </summary>
public sealed partial class SettingsMenu : Node3D
{
    // Dead zone shown as segmented pips like the Options sliders: value 0-10 maps
    // to 0-40 game units (4 per pip).
    private const int DeadZoneStep = 4;

    private VrButton _handSwap = null!;
    private VrSegmentedSlider _deadZone = null!;
    private Label3D _deadZoneLabel = null!;
    private VrButton _back = null!;

    private VrButton _debug = null!;
    private bool _swapState;
    private bool _debugState;

    public event Action? OnBack;
    public event Action<bool>? OnHandSwapChanged;
    public event Action<float>? OnDeadZoneChanged;
    public event Action<bool>? OnDebugChanged;

    public void Build(float arenaSideMeters, bool handSwap, float deadZone, bool debug, Texture2D? rectOn, Texture2D? rectOff)
    {
        float s = arenaSideMeters;
        _swapState = handSwap;
        _debugState = debug;

        // Shared menu anchor (see MainMenu): all menus coplanar + pushed back.
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        float bw = s * 0.7f;
        float bh = s * 0.1f;
        float pitch = s * 0.16f; // > bh, so rows never overlap
        float y = s * 0.42f;     // top-down cursor

        AddTitleBacking(y, "VR Settings", 120.0f, s / 1000.0f);
        var title = new Label3D
        {
            Text = "VR Settings",
            FontSize = 120,
            PixelSize = s / 1000.0f,
            Modulate = new Color(0.45f, 0.72f, 1.0f), // blue neon, like the OG headings
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.002f),
            NoDepthTest = true,
        };
        AddChild(title);
        y -= pitch;

        // Hand-swap toggle.
        _handSwap = new VrButton();
        AddChild(_handSwap);
        _handSwap.Build(bw, bh, HandSwapText(), new Color(0.5f, 0.6f, 0.85f), plate: true);
        _handSwap.Position = new Vector3(0.0f, y, 0.0f);
        _handSwap.OnPress += ToggleHandSwap;
        y -= pitch;

        // Dead-zone: a value label above its slider. Sit the label lower (more air
        // below the Movement toggle above) and pull the next row up a touch (less
        // gap to the Debug toggle below).
        float dzTitleY = y + s * 0.04f;
        AddTitleBacking(dzTitleY, DeadZoneText(deadZone), 90.0f, s / 1200.0f);
        _deadZoneLabel = new Label3D
        {
            Text = DeadZoneText(deadZone),
            FontSize = 90,
            PixelSize = s / 1200.0f,
            Modulate = new Color(0.9f, 0.92f, 0.98f),
            OutlineSize = 20,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, dzTitleY, 0.002f),
            NoDepthTest = true,
        };
        AddChild(_deadZoneLabel);

        _deadZone = new VrSegmentedSlider();
        AddChild(_deadZone);
        int dzValue = Mathf.Clamp(Mathf.RoundToInt(deadZone / DeadZoneStep), 0, 10);
        _deadZone.Build(s * 0.03f, 0, 10, dzValue, rectOn, rectOff);
        _deadZone.Position = new Vector3(0.0f, y - s * 0.03f, 0.0f);
        _deadZone.OnValueChanged += v =>
        {
            float units = v * DeadZoneStep;
            _deadZoneLabel.Text = DeadZoneText(units);
            OnDeadZoneChanged?.Invoke(units);
        };
        y -= s * 0.13f; // tighter step to the Debug toggle

        // Debug-overlay toggle (poke-tip markers + creature facing needle).
        _debug = new VrButton();
        AddChild(_debug);
        _debug.Build(bw, bh, DebugText(), new Color(0.55f, 0.55f, 0.7f), plate: true);
        _debug.Position = new Vector3(0.0f, y, 0.0f);
        _debug.OnPress += ToggleDebug;
        y -= pitch;

        // Back to the pause panel.
        _back = new VrButton();
        AddChild(_back);
        _back.Build(bw * 0.5f, bh, "Back", new Color(0.6f, 0.6f, 0.66f), plate: true);
        _back.Position = new Vector3(0.0f, y, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        // Re-arm + start the settle window on show AND hide, so a lingering finger
        // where a button appears can't instant-fire.
        _handSwap.ResetPress();
        _debug.ResetPress();
        _back.ResetPress();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        _handSwap.PollPoke(probes);
        _debug.PollPoke(probes);
        _back.PollPoke(probes);
        _deadZone.PollPoke(probes);
    }

    private void ToggleHandSwap()
    {
        _swapState = !_swapState;
        _handSwap.SetText(HandSwapText());
        OnHandSwapChanged?.Invoke(_swapState);
    }

    private void ToggleDebug()
    {
        _debugState = !_debugState;
        _debug.SetText(DebugText());
        OnDebugChanged?.Invoke(_debugState);
    }

    /// <summary>A dark translucent backing strip behind a title, sized from the
    /// text so the title always fits (see VrOptionsMenu).</summary>
    private void AddTitleBacking(float y, string text, float fontSize, float pixelSize)
    {
        float glyph = fontSize * pixelSize;
        float width = text.Length * glyph * 0.62f + glyph;
        float height = glyph * 1.5f;
        AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(width, height) },
            Position = new Vector3(0.0f, y, 0.0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.04f, 0.05f, 0.08f, 0.72f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 25,
            },
        });
    }

    private string HandSwapText() => _swapState ? "Movement: Right hand" : "Movement: Left hand";

    private string DebugText() => _debugState ? "Debug overlays: ON" : "Debug overlays: off";

    private static string DeadZoneText(float v) => $"Dead zone: {v:0} units";
}
