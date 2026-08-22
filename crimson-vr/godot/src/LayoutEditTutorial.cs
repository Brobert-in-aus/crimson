using System;
using Godot;

namespace CrimsonVR;

/// <summary>One-time, task-led introduction shown before layout handles activate.</summary>
public sealed partial class LayoutEditTutorial : Node3D
{
    private VrButton _continue = null!;

    public bool Active { get; private set; }
    public event Action? OnContinue;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        ClassicPanel.Build(this, s * 1.28f, s * 1.08f, z: -0.012f);

        AddChild(new Label3D
        {
            Text = "Edit Layout",
            FontSize = 92,
            PixelSize = s / 1250.0f,
            Position = new Vector3(0.0f, s * 0.42f, 0.002f),
            Width = (s * 1.08f) / (s / 1250.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(0.45f, 0.8f, 1.0f),
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });

        AddChild(new Label3D
        {
            Text = "Movable objects\n\n"
                + "Pause — pauses or resumes a match.\n"
                + "Level Up — opens perk selection.\n"
                + "Control pad (Cabinet) — sets your controller/hand travel area.\n"
                + "Menu distance — moves menus, perks and results nearer or farther.\n\n"
                + "Grip a gold corner with a controller/hand to move an object.\n"
                + "Use two corners to resize or rotate it. The blue centre point only changes menu distance.",
            FontSize = 53,
            PixelSize = s / 1250.0f,
            Position = new Vector3(0.0f, s * 0.08f, 0.002f),
            Width = (s * 1.08f) / (s / 1250.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.92f, 0.94f, 1.0f),
            OutlineSize = 16,
            OutlineModulate = Colors.Black,
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });

        _continue = new VrButton();
        AddChild(_continue);
        _continue.BuildClassic(s * 0.48f, s * 0.13f, "Start Editing");
        _continue.Position = new Vector3(0.0f, -s * 0.39f, 0.0f);
        _continue.OnPress += () => OnContinue?.Invoke();
        Visible = false;
    }

    public void Open()
    {
        Active = true;
        Visible = true;
        _continue.ResetPress();
    }

    public void Dismiss()
    {
        Active = false;
        Visible = false;
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (Active)
        {
            _continue.PollPoke(probes);
        }
    }
}
