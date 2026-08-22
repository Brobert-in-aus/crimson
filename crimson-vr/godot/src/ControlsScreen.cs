using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Controls screen (the base Options screen's Controls button, reimagined for
/// VR): a read-only reference card for the control mapping — there are no
/// keybinds to edit, the controllers/hands ARE the bindings. Roles honour the
/// hand-swap setting live. Opened from Options, Back returns there.
/// </summary>
public sealed partial class ControlsScreen : Node3D
{
    private static readonly Color TitleColor = new(0.45f, 0.72f, 1.0f);

    private readonly System.Collections.Generic.List<SmallFontLabel> _lines = new();
    private Label3D? _fallback;
    private VrButton _back = null!;
    private float _side;
    private bool _handSwap;

    public event Action? OnBack;

    public void Build(float arenaSideMeters, bool handSwap, ControlMode mode = ControlMode.Cabinet)
    {
        float s = arenaSideMeters;
        _side = s;
        _handSwap = handSwap;
        _mode = mode;
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        float y = s * 0.34f;
        AddChild(new Label3D
        {
            Text = "Controls",
            FontSize = 110,
            PixelSize = s / 1100.0f,
            Modulate = TitleColor,
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.002f),
        });

        _back = new VrButton();
        AddChild(_back);
        _back.Build(s * 0.26f, s * 0.075f, "Back", new Color(0.6f, 0.6f, 0.66f), plate: true);
        _back.Position = new Vector3(0.0f, -s * 0.42f, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        RebuildLines();
        Visible = false;
    }

    /// <summary>Refresh the hand labels after a hand-swap change.</summary>
    public void SetHandSwap(bool handSwap)
    {
        if (handSwap == _handSwap)
        {
            return;
        }
        _handSwap = handSwap;
        RebuildLines();
    }

    /// <summary>Refresh after a control-mode change. The two modes are steered
    /// completely differently — over the arena itself, or over a separate pad —
    /// so a card that described only one of them would be wrong half the time,
    /// and wrong about the first thing a player needs to know.</summary>
    public void SetControlMode(ControlMode mode)
    {
        if (mode == _mode)
        {
            return;
        }
        _mode = mode;
        RebuildLines();
    }

    private ControlMode _mode = ControlMode.Cabinet;

    private void RebuildLines()
    {
        foreach (SmallFontLabel l in _lines)
        {
            l.QueueFree();
        }
        _lines.Clear();
        _fallback?.QueueFree();
        _fallback = null;

        string moveControl = _handSwap ? "Right controller/hand" : "Left controller/hand";
        string aimControl = _handSwap ? "Left controller/hand" : "Right controller/hand";
        bool cabinet = _mode == ControlMode.Cabinet;
        // The surface the hands work over is the whole difference between the
        // modes, and it is not guessable from looking at the scene — in Cabinet
        // the thing you touch and the thing you watch are in two places.
        string surface = cabinet ? "the control pad in front of you" : "the arena";
        string[] lines =
        {
            cabinet ? "Mode: Cabinet  -  controllers/hands on the pad, board up ahead"
                    : "Mode: Tabletop  -  controllers/hands reach into the arena",
            "",
            $"{moveControl}  -  trigger / pinch over {surface} to move",
            $"{aimControl}  -  aim: the spread ring is the crosshair",
            "Trigger / pinch (aim controller/hand)  -  fire",
            "Reload / swap  -  aim-controller grip / hand fist; empty clips auto-reload",
            "Buttons  -  poke with a controller top or fingertip",
            cabinet ? "Pause  -  the flat button beside the pad"
                    : "Pause  -  the flat button beside the arena",
            "Recenter  -  hold the controller's menu/recenter control",
            "            or choose Recenter View from Pause",
            "",
            "Mode, arena size and layout live in VR Settings.",
        };

        float s = _side;
        SmallFont? font = SmallFont.Shared();
        if (font != null)
        {
            // Tightened to fit ten lines above the Back button at -0.42s: at the
            // old 0.22/0.075 the mode header and trailing hint pushed the last
            // two lines straight through it.
            float y = s * 0.25f;
            float step = s * 0.057f;
            foreach (string line in lines)
            {
                var l = new SmallFontLabel();
                l.Build(font, s / 520.0f, new Color(1.0f, 1.0f, 1.0f, 0.75f), center: true);
                l.Position = new Vector3(0.0f, y, 0.0f);
                AddChild(l);
                l.SetText(line);
                _lines.Add(l);
                y -= step;
            }
        }
        else
        {
            _fallback = new Label3D
            {
                Text = string.Join('\n', lines),
                FontSize = 56,
                PixelSize = s / 1100.0f,
                Position = new Vector3(0.0f, s * 0.05f, 0.0f),
            };
            AddChild(_fallback);
        }
    }

    public void SetShown(bool shown)
    {
        Visible = shown;
        if (shown)
        {
            _back.ResetPress();
        }
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        _back.PollPoke(probes);
    }
}
