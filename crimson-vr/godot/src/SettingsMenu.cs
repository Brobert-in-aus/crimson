using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 4: the settings menu (MVP). A panel above the arena, opened from the
/// pause menu's Settings button, holding the non-deferred settings: a hand-swap
/// toggle (which hand moves) and a dead-zone grab-drag slider, plus Back. Arena
/// scale/height + seated calibration are their own later slices (PLAN Â§5, M4).
///
/// A child of ArenaRoot; hidden until opened. Reflects the current values and
/// raises events Main applies live. Layout first-pass; tune in-headset.
/// </summary>
public sealed partial class SettingsMenu : Node3D
{
    // Dead zone shown as segmented pips like the Options sliders: value 0-10 maps
    // to 0-40 game units (4 per pip).
    private const int DeadZoneStep = 4;

    // Render scale slider: value 0-10 maps to 0.6..1.6x supersampling.
    private const float RenderScaleBase = 0.6f;
    private const float RenderScaleStep = 0.1f;

    private VrButton _handSwap = null!;
    private VrSegmentedSlider _deadZone = null!;
    private Label3D _deadZoneLabel = null!;
    private VrSegmentedSlider _renderScale = null!;
    private Label3D _renderScaleLabel = null!;
    private VrButton _controlMode = null!;
    private VrButton _uiEdit = null!;
    private VrButton _aa = null!;
    private VrButton _mixedReality = null!;
    private VrButton _controllerModels = null!;
    private VrButton _pokeMarkers = null!;
    private VrButton _debug = null!;
    private VrButton _back = null!;
    private VrButton _pageButton = null!;
    private Label3D _title = null!;
    private int _page;
    private float _arenaSide;

    private bool _swapState;
    private bool _debugState;
    private bool _pokeMarkersState;
    private bool _mixedRealityState;
    private bool _mixedRealitySupported;
    private bool _controllerModelsState;
    private int _msaaState;
    private ControlMode _controlModeState;

    public event Action? OnBack;
    public event Action<bool>? OnHandSwapChanged;
    public event Action<float>? OnDeadZoneChanged;
    public event Action<bool>? OnDebugChanged;
    public event Action<bool>? OnPokeMarkersChanged;
    public event Action<ControlMode>? OnControlModeChanged;
    public event Action? OnArenaLayout;
    public event Action<float>? OnRenderScaleChanged;
    public event Action<int>? OnMsaaChanged;
    public event Action<bool>? OnMixedRealityChanged;
    public event Action<bool>? OnControllerModelsChanged;

    public void Build(float arenaSideMeters, bool handSwap, float deadZone, bool debug, bool pokeMarkers,
        bool controllerModels,
        ControlMode controlMode, float renderScale, int msaa, bool mixedReality,
        bool mixedRealitySupported, Texture2D? rectOn, Texture2D? rectOff)
    {
        float s = arenaSideMeters;
        _arenaSide = s;
        _swapState = handSwap;
        _debugState = debug;
        _pokeMarkersState = pokeMarkers;
        _controllerModelsState = controllerModels;
        _mixedRealityState = mixedReality && mixedRealitySupported;
        _mixedRealitySupported = mixedRealitySupported;
        _msaaState = msaa;
        _controlModeState = controlMode;

        // Shared menu anchor (see MainMenu): all menus coplanar + pushed back.
        Position = new Vector3(0.0f, s * 0.9f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        float bw = s * 0.7f;
        float bh = s * 0.09f;
        float pitch = s * 0.13f;  // button-row step
        float sPitch = s * 0.18f; // into/between slider rows (label sits above the pips)
        float y = s * 0.5f;       // top-down cursor

        AddTitleBacking(y, "VR Settings", 120.0f, s / 1000.0f);
        _title = new Label3D
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
        AddChild(_title);
        y -= pitch;

        // Control mode: the most consequential setting on this panel, so it
        // leads. Tabletop = hands reach into the arena (the board must stay in
        // reach); Cabinet = hands work a control rectangle and the board is a
        // screen up in front. Switching reloads that mode's board placement.
        _controlMode = new VrButton();
        AddChild(_controlMode);
        _controlMode.Build(bw, bh, ControlModeText(), new Color(0.45f, 0.72f, 1.0f), plate: true);
        _controlMode.Position = new Vector3(0.0f, y, 0.0f);
        _controlMode.OnPress += ToggleControlMode;
        y -= pitch;

        // Arena & Layout: its own screen, holding the board placement sliders
        // and putting the widgets into edit mode while it is open. Reset lives
        // there too, next to the thing it undoes. Two rows of this panel became
        // one, which also stops it growing further.
        _uiEdit = new VrButton();
        AddChild(_uiEdit);
        _uiEdit.Build(bw, bh, "Arena & Layout", new Color(0.9f, 0.7f, 0.3f), plate: true);
        _uiEdit.Position = new Vector3(0.0f, y, 0.0f);
        _uiEdit.OnPress += () => OnArenaLayout?.Invoke();
        y -= pitch;

        // Hand-swap toggle.
        _handSwap = new VrButton();
        AddChild(_handSwap);
        _handSwap.Build(bw, bh, HandSwapText(), new Color(0.5f, 0.6f, 0.85f), plate: true);
        _handSwap.Position = new Vector3(0.0f, y, 0.0f);
        _handSwap.OnPress += ToggleHandSwap;
        y -= sPitch; // extra clearance so the dead-zone label doesn't touch this button

        // Dead-zone slider (label above its pips).
        _deadZoneLabel = MakeSliderLabel(s, DeadZoneText(deadZone), y);
        _deadZone = new VrSegmentedSlider();
        AddChild(_deadZone);
        _deadZone.Build(s * 0.03f, 0, 10, Mathf.Clamp(Mathf.RoundToInt(deadZone / DeadZoneStep), 0, 10), rectOn, rectOff);
        _deadZone.Position = new Vector3(0.0f, y - s * 0.03f, 0.0f);
        _deadZone.OnValueChanged += v =>
        {
            float units = v * DeadZoneStep;
            _deadZoneLabel.Text = DeadZoneText(units);
            OnDeadZoneChanged?.Invoke(units);
        };
        y -= sPitch;

        // Render-scale slider (supersampling).
        _renderScaleLabel = MakeSliderLabel(s, RenderScaleText(renderScale), y);
        _renderScale = new VrSegmentedSlider();
        AddChild(_renderScale);
        _renderScale.Build(s * 0.03f, 0, 10, RenderScaleToValue(renderScale), rectOn, rectOff);
        _renderScale.Position = new Vector3(0.0f, y - s * 0.03f, 0.0f);
        _renderScale.OnValueChanged += v =>
        {
            float sc = RenderScaleBase + v * RenderScaleStep;
            _renderScaleLabel.Text = RenderScaleText(sc);
            OnRenderScaleChanged?.Invoke(sc);
        };
        y -= sPitch;

        // Anti-aliasing cycle (Off / 2x / 4x MSAA).
        _aa = new VrButton();
        AddChild(_aa);
        _aa.Build(bw, bh, AaText(), new Color(0.5f, 0.6f, 0.85f), plate: true);
        _aa.Position = new Vector3(0.0f, y, 0.0f);
        _aa.OnPress += CycleAa;
        y -= pitch;

        // Passthrough is packaged as an optional Quest capability. The button is
        // present on every headset so the setting layout stays stable, but it is
        // inert and explicitly labelled when the runtime lacks alpha blending.
        _mixedReality = new VrButton();
        AddChild(_mixedReality);
        _mixedReality.Build(bw, bh, MixedRealityText(),
            _mixedRealitySupported ? new Color(0.45f, 0.72f, 1.0f) : new Color(0.38f, 0.38f, 0.42f),
            plate: true);
        _mixedReality.Position = new Vector3(0.0f, y, 0.0f);
        _mixedReality.OnPress += ToggleMixedReality;
        y -= pitch;

        // Runtime-supplied controller geometry is a display preference rather
        // than an input requirement. Unsupported runtimes simply supply no
        // model; the setting stays reversible and poke markers still work.
        _controllerModels = new VrButton();
        AddChild(_controllerModels);
        _controllerModels.Build(bw, bh, ControllerModelsText(), new Color(0.5f, 0.6f, 0.85f), plate: true);
        _controllerModels.Position = new Vector3(0.0f, y, 0.0f);
        _controllerModels.OnPress += ToggleControllerModels;
        y -= pitch;

        // Poke-tip markers, standalone. Sits above Debug because it is a normal
        // comfort/visibility option now, not a dev switch: the tips hover over
        // the control rectangle rather than the playfield, so leaving them on
        // costs the player nothing.
        _pokeMarkers = new VrButton();
        AddChild(_pokeMarkers);
        _pokeMarkers.Build(bw, bh, PokeMarkersText(), new Color(0.5f, 0.6f, 0.85f), plate: true);
        _pokeMarkers.Position = new Vector3(0.0f, y, 0.0f);
        _pokeMarkers.OnPress += TogglePokeMarkers;
        y -= pitch;

        // Debug-overlay toggle (validation checklist, status line, debug FX menu,
        // and the poke markers regardless of their own toggle).
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

        _pageButton = new VrButton();
        AddChild(_pageButton);
        _pageButton.Build(bw * 0.5f, bh, "Next", new Color(0.45f, 0.62f, 0.82f), plate: true);
        _pageButton.OnPress += () => SetPage(1 - _page);

        ApplyPageLayout();

        Visible = false;
    }

    /// <summary>A value label sitting above a slider's pips (with its dark backing).</summary>
    private Label3D MakeSliderLabel(float s, string text, float rowY)
    {
        float ty = rowY + s * 0.06f;
        var label = new Label3D
        {
            Text = text,
            FontSize = 90,
            PixelSize = s / 1200.0f,
            Modulate = new Color(0.9f, 0.92f, 0.98f),
            OutlineSize = 20,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, ty, 0.002f),
            NoDepthTest = true,
        };
        AddChild(label);
        return label;
    }

    private static int RenderScaleToValue(float scale) =>
        Mathf.Clamp(Mathf.RoundToInt((scale - RenderScaleBase) / RenderScaleStep), 0, 10);

    private static string RenderScaleText(float scale) => $"Render scale: {scale:0.0}x";

    private string AaText() => _msaaState <= 0 ? "Anti-aliasing: Off" : $"Anti-aliasing: {_msaaState}x";

    private void CycleAa()
    {
        _msaaState = _msaaState switch { 0 => 2, 2 => 4, _ => 0 };
        _aa.SetText(AaText());
        OnMsaaChanged?.Invoke(_msaaState);
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        if (visible)
        {
            SetPage(0);
        }
        // Re-arm + start the settle window on show AND hide, so a lingering finger
        // where a button appears can't instant-fire.
        _handSwap.ResetPress();
        _controlMode.ResetPress();
        _uiEdit.ResetPress();
        _debug.ResetPress();
        _pokeMarkers.ResetPress();
        _back.ResetPress();
        _aa.ResetPress();
        _mixedReality.ResetPress();
        _controllerModels.ResetPress();
        _deadZone.ResetPress();
        _renderScale.ResetPress();
        _pageButton.ResetPress();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        if (_page == 0)
        {
            _handSwap.PollPoke(probes);
            _controlMode.PollPoke(probes);
            _uiEdit.PollPoke(probes);
            _pokeMarkers.PollPoke(probes);
            _deadZone.PollPoke(probes);
        }
        else
        {
            _debug.PollPoke(probes);
            _aa.PollPoke(probes);
            _controllerModels.PollPoke(probes);
            if (_mixedRealitySupported)
            {
                _mixedReality.PollPoke(probes);
            }
            _renderScale.PollPoke(probes);
        }
        _back.PollPoke(probes);
        _pageButton.PollPoke(probes);
    }

    private void SetPage(int page)
    {
        _page = Mathf.Clamp(page, 0, 1);
        ApplyPageLayout();
        _pageButton.ResetPress();
    }

    private void ApplyPageLayout()
    {
        float s = _arenaSide;
        bool controls = _page == 0;
        _title.Text = controls ? "VR Controls" : "VR Display";

        _controlMode.Visible = controls;
        _uiEdit.Visible = controls;
        _handSwap.Visible = controls;
        _deadZone.Visible = controls;
        _deadZoneLabel.Visible = controls;
        _pokeMarkers.Visible = controls;

        _renderScale.Visible = !controls;
        _renderScaleLabel.Visible = !controls;
        _aa.Visible = !controls;
        _mixedReality.Visible = !controls;
        _controllerModels.Visible = !controls;
        _debug.Visible = !controls;

        if (controls)
        {
            _controlMode.Position = new Vector3(0.0f, s * 0.32f, 0.0f);
            _uiEdit.Position = new Vector3(0.0f, s * 0.19f, 0.0f);
            _handSwap.Position = new Vector3(0.0f, s * 0.06f, 0.0f);
            _deadZoneLabel.Position = new Vector3(0.0f, -s * 0.08f, 0.002f);
            _deadZone.Position = new Vector3(0.0f, -s * 0.14f, 0.0f);
            _pokeMarkers.Position = new Vector3(0.0f, -s * 0.28f, 0.0f);
        }
        else
        {
            _renderScaleLabel.Position = new Vector3(0.0f, s * 0.32f, 0.002f);
            _renderScale.Position = new Vector3(0.0f, s * 0.26f, 0.0f);
            _aa.Position = new Vector3(0.0f, s * 0.10f, 0.0f);
            _mixedReality.Position = new Vector3(0.0f, -s * 0.04f, 0.0f);
            _controllerModels.Position = new Vector3(0.0f, -s * 0.18f, 0.0f);
            _debug.Position = new Vector3(0.0f, -s * 0.32f, 0.0f);
        }

        _pageButton.SetText(controls ? "Display >" : "< Controls");
        _pageButton.Position = new Vector3(-s * 0.19f, -s * 0.43f, 0.0f);
        _back.Position = new Vector3(s * 0.19f, -s * 0.43f, 0.0f);
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

    private string MixedRealityText() => !_mixedRealitySupported
        ? "Mixed reality: unavailable"
        : _mixedRealityState ? "Mixed reality: On" : "Mixed reality: Off";

    private void ToggleMixedReality()
    {
        if (!_mixedRealitySupported)
        {
            return;
        }
        _mixedRealityState = !_mixedRealityState;
        _mixedReality.SetText(MixedRealityText());
        OnMixedRealityChanged?.Invoke(_mixedRealityState);
    }

    private string ControllerModelsText() => _controllerModelsState
        ? "Controller models: On"
        : "Controller models: Off";

    private void ToggleControllerModels()
    {
        _controllerModelsState = !_controllerModelsState;
        _controllerModels.SetText(ControllerModelsText());
        OnControllerModelsChanged?.Invoke(_controllerModelsState);
    }

    private string ControlModeText() =>
        _controlModeState == ControlMode.Cabinet ? "Control: Cabinet" : "Control: Tabletop";

    private void ToggleControlMode()
    {
        _controlModeState = _controlModeState == ControlMode.Cabinet
            ? ControlMode.Tabletop
            : ControlMode.Cabinet;
        _controlMode.SetText(ControlModeText());
        OnControlModeChanged?.Invoke(_controlModeState);
    }

    private string PokeMarkersText() => _pokeMarkersState ? "Poke markers: On" : "Poke markers: Off";

    private void TogglePokeMarkers()
    {
        _pokeMarkersState = !_pokeMarkersState;
        _pokeMarkers.SetText(PokeMarkersText());
        OnPokeMarkersChanged?.Invoke(_pokeMarkersState);
    }

    /// <summary>A dark translucent backing strip behind a title, sized from the
    /// text so the title always fits (see VrOptionsMenu).</summary>
    private void AddTitleBacking(float y, string text, float fontSize, float pixelSize)
    {
        float glyph = fontSize * pixelSize;
        float width = text.Length * glyph * 0.62f + glyph;
        float height = glyph * 1.2f; // hug the text; don't spill onto neighbours
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
                RenderPriority = 59,
            },
        });
    }

    private string HandSwapText() => _swapState ? "Movement: Right hand" : "Movement: Left hand";

    private string DebugText() => _debugState ? "Debug overlays: ON" : "Debug overlays: off";

    private static string DeadZoneText(float v) => $"Dead zone: {v:0} units";
}
