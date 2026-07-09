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
    private VrButton _handSwap = null!;
    private VrSlider _deadZone = null!;
    private Label3D _deadZoneLabel = null!;
    private VrButton _back = null!;

    private bool _swapState;

    public event Action? OnBack;
    public event Action<bool>? OnHandSwapChanged;
    public event Action<float>? OnDeadZoneChanged;

    public void Build(float arenaSideMeters, bool handSwap, float deadZone)
    {
        float s = arenaSideMeters;
        _swapState = handSwap;

        Position = new Vector3(0.0f, s * 0.8f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        float bw = s * 0.62f;
        float bh = s * 0.14f;
        float rowGap = s * 0.06f;
        float top = s * 0.34f;

        var title = new Label3D
        {
            Text = "Settings",
            FontSize = 120,
            PixelSize = s / 260.0f,
            Modulate = new Color(0.9f, 0.9f, 0.95f),
            Position = new Vector3(0.0f, top + bh, 0.0f),
            NoDepthTest = true,
        };
        AddChild(title);

        // Hand-swap toggle.
        _handSwap = new VrButton();
        AddChild(_handSwap);
        _handSwap.Build(bw, bh, HandSwapText(), new Color(0.5f, 0.6f, 0.85f));
        _handSwap.Position = new Vector3(0.0f, top, 0.0f);
        _handSwap.OnPress += ToggleHandSwap;

        // Dead-zone slider with a live value label above it.
        _deadZoneLabel = new Label3D
        {
            Text = DeadZoneText(deadZone),
            FontSize = 90,
            PixelSize = s / 320.0f,
            Modulate = new Color(0.85f, 0.85f, 0.9f),
            Position = new Vector3(0.0f, top - (bh + rowGap) + bh * 0.7f, 0.0f),
            NoDepthTest = true,
        };
        AddChild(_deadZoneLabel);

        _deadZone = new VrSlider();
        AddChild(_deadZone);
        _deadZone.Build(bw, s * 0.03f, 5.0f, 40.0f, deadZone);
        _deadZone.Position = new Vector3(0.0f, top - (bh + rowGap) - bh * 0.2f, 0.0f);
        _deadZone.OnValueChanged += v =>
        {
            _deadZoneLabel.Text = DeadZoneText(v);
            OnDeadZoneChanged?.Invoke(v);
        };

        // Back to the pause panel.
        _back = new VrButton();
        AddChild(_back);
        _back.Build(bw * 0.5f, bh, "Back", new Color(0.6f, 0.6f, 0.66f));
        _back.Position = new Vector3(0.0f, top - 2.0f * (bh + rowGap), 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        if (!visible)
        {
            _handSwap.ResetPress();
            _back.ResetPress();
            _deadZone.ResetGrab();
        }
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        _handSwap.PollPoke(probes);
        _back.PollPoke(probes);
        _deadZone.PollGrab(probes);
    }

    private void ToggleHandSwap()
    {
        _swapState = !_swapState;
        _handSwap.SetText(HandSwapText());
        OnHandSwapChanged?.Invoke(_swapState);
    }

    private string HandSwapText() => _swapState ? "Movement: Right hand" : "Movement: Left hand";

    private static string DeadZoneText(float v) => $"Dead zone: {v:0} units";
}
