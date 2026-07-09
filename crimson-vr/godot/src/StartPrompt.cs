using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 5: the first-run prompt. On launch the arena shows at its default
/// size with a poke prompt: Accept (play at the default) or Calibrate (fit the
/// arena to your seated reach). The sim is held until the player accepts. The
/// seated reach calibration itself is a later slice (PLAN §5), so Calibrate
/// currently raises <see cref="OnCalibrate"/> and proceeds with the default.
///
/// A child of ArenaRoot, hidden once dismissed. Layout first-pass; tune in-headset.
/// </summary>
public sealed partial class StartPrompt : Node3D
{
    private VrButton _accept = null!;
    private VrButton _calibrate = null!;

    /// <summary>True until the player dismisses the prompt (Main holds the sim).</summary>
    public bool Pending { get; private set; } = true;

    public event Action? OnAccept;
    public event Action? OnCalibrate;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        Position = new Vector3(0.0f, s * 0.8f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        var title = new Label3D
        {
            Text = "Crimsonland VR",
            FontSize = 130,
            PixelSize = s / 240.0f,
            Modulate = new Color(0.9f, 0.85f, 0.4f),
            Position = new Vector3(0.0f, s * 0.42f, 0.0f),
            NoDepthTest = true,
        };
        AddChild(title);

        float bw = s * 0.4f;
        float bh = s * 0.16f;
        float gap = s * 0.06f;

        _accept = new VrButton();
        AddChild(_accept);
        _accept.Build(bw, bh, "Accept", new Color(0.4f, 0.8f, 0.45f));
        _accept.Position = new Vector3(-(bw * 0.5f + gap * 0.5f), 0.0f, 0.0f);
        _accept.OnPress += () => Dismiss(calibrate: false);

        _calibrate = new VrButton();
        AddChild(_calibrate);
        _calibrate.Build(bw, bh, "Calibrate", new Color(0.5f, 0.6f, 0.85f));
        _calibrate.Position = new Vector3(bw * 0.5f + gap * 0.5f, 0.0f, 0.0f);
        _calibrate.OnPress += () => Dismiss(calibrate: true);
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Pending)
        {
            return;
        }
        _accept.PollPoke(probes);
        _calibrate.PollPoke(probes);
    }

    /// <summary>Dismiss without prompting (returning player who has already done
    /// the first run); fires no events.</summary>
    public void Skip()
    {
        Pending = false;
        Visible = false;
    }

    private void Dismiss(bool calibrate)
    {
        if (!Pending)
        {
            return;
        }
        Pending = false;
        Visible = false;
        if (calibrate)
        {
            OnCalibrate?.Invoke();
        }
        OnAccept?.Invoke();
    }
}
