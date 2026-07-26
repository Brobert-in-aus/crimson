using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Controls screen (the base Options screen's Controls button, reimagined for
/// VR): a read-only reference card for the control mapping — there are no
/// keybinds to edit, the hands ARE the bindings. Hand roles honour the
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

    public void Build(float arenaSideMeters, bool handSwap)
    {
        float s = arenaSideMeters;
        _side = s;
        _handSwap = handSwap;
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
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

    private void RebuildLines()
    {
        foreach (SmallFontLabel l in _lines)
        {
            l.QueueFree();
        }
        _lines.Clear();
        _fallback?.QueueFree();
        _fallback = null;

        string moveHand = _handSwap ? "Right hand" : "Left hand";
        string aimHand = _handSwap ? "Left hand" : "Right hand";
        string[] lines =
        {
            $"{moveHand}  -  move: the trooper chases its ring reticle",
            $"{aimHand}  -  aim: the spread ring is the crosshair",
            "Trigger (aim hand)  -  fire",
            "Reload  -  automatic when the clip runs dry",
            "Buttons  -  poke them with either hand sphere",
            "Pause  -  the flat button beside the arena",
            "",
            "Hand swap and stick dead zone live in VR Settings.",
        };

        float s = _side;
        SmallFont? font = SmallFont.Shared();
        if (font != null)
        {
            float y = s * 0.22f;
            float step = s * 0.075f;
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
