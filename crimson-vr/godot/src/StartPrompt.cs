using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// First-run interaction guide. It teaches the one action a new player must know
/// before the main menu can make sense (direct-touch poke), names the recenter
/// shortcut, and offers a direct route to the reach/layout controls. Returning
/// players skip it through UserSettings.FirstRunDone.
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
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        ClassicPanel.Build(this, s * 1.25f, s * 0.92f, z: -0.012f);

        var title = new Label3D
        {
            Text = "Welcome to Crimsonland VR",
            FontSize = 92,
            PixelSize = s / 1250.0f,
            Modulate = new Color(0.9f, 0.85f, 0.4f),
            Position = new Vector3(0.0f, s * 0.34f, 0.002f),
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = (s * 1.08f) / (s / 1250.0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        };
        AddChild(title);

        AddChild(new Label3D
        {
            Text = "Touch buttons with a fingertip or controller top.\n\n"
                + "Hold Menu / A to recenter the arena.\n"
                + "You can also use Recenter View in Pause.\n\n"
                + "If controls are out of reach, choose Adjust Reach.",
            FontSize = 58,
            PixelSize = s / 1250.0f,
            Modulate = new Color(0.92f, 0.93f, 0.98f),
            OutlineSize = 18,
            OutlineModulate = Colors.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = (s * 1.04f) / (s / 1250.0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector3(0.0f, s * 0.09f, 0.002f),
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });

        float bw = s * 0.4f;
        float bh = s * 0.16f;
        float gap = s * 0.06f;

        _accept = new VrButton();
        AddChild(_accept);
        _accept.BuildClassic(bw, bh, "Continue");
        _accept.Position = new Vector3(-(bw * 0.5f + gap * 0.5f), -s * 0.22f, 0.0f);
        _accept.OnPress += () => Dismiss(calibrate: false);

        _calibrate = new VrButton();
        AddChild(_calibrate);
        _calibrate.BuildClassic(bw, bh, "Adjust Reach");
        _calibrate.Position = new Vector3(bw * 0.5f + gap * 0.5f, -s * 0.22f, 0.0f);
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
