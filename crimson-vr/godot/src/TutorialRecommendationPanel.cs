using System;
using Godot;

namespace CrimsonVR;

/// <summary>One-time recommendation shown on the player's first Play Game action.</summary>
public sealed partial class TutorialRecommendationPanel : Node3D
{
    private VrButton _tutorial = null!;
    private VrButton _skip = null!;

    public bool Active { get; private set; }
    public event Action? OnTutorial;
    public event Action? OnSkip;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        _arenaSide = s;
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        ClassicPanel.Build(this, s * 1.18f, s * 0.78f, z: -0.012f);

        AddChild(new Label3D
        {
            Text = "Play the Tutorial first?",
            FontSize = 86,
            PixelSize = s / 1250.0f,
            Position = new Vector3(0.0f, s * 0.26f, 0.002f),
            Width = (s * 1.0f) / (s / 1250.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(0.9f, 0.85f, 0.4f),
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });

        AddChild(new Label3D
        {
            Text = "Recommended for your first match. It teaches movement, aiming, power-ups and perk selection.",
            FontSize = 58,
            PixelSize = s / 1250.0f,
            Position = new Vector3(0.0f, s * 0.06f, 0.002f),
            Width = (s * 0.98f) / (s / 1250.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.92f, 0.94f, 1.0f),
            OutlineSize = 16,
            OutlineModulate = Colors.Black,
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });

        float bw = s * 0.4f;
        float bh = s * 0.15f;
        float gap = s * 0.06f;
        _tutorial = MakeButton("Tutorial", -(bw * 0.5f + gap * 0.5f), bw, bh);
        _tutorial.SetColor(new Color(0.92f, 0.78f, 0.34f));
        _tutorial.OnPress += () => OnTutorial?.Invoke();
        _skip = MakeButton("Skip", bw * 0.5f + gap * 0.5f, bw, bh);
        _skip.SetColor(new Color(0.72f, 0.72f, 0.76f));
        _skip.OnPress += () => OnSkip?.Invoke();
        Visible = false;
    }

    private VrButton MakeButton(string text, float x, float width, float height)
    {
        var button = new VrButton();
        AddChild(button);
        button.BuildClassic(width, height, text);
        button.Position = new Vector3(x, -0.22f * _arenaSide, 0.0f);
        return button;
    }

    private float _arenaSide;

    public void Open()
    {
        Active = true;
        Visible = true;
        _tutorial.ResetPress();
        _skip.ResetPress();
    }

    public void Dismiss()
    {
        Active = false;
        Visible = false;
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active) return;
        _tutorial.PollPoke(probes);
        _skip.PollPoke(probes);
    }
}
