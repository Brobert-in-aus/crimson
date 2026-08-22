using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// First-run interaction guide. It teaches the one action a new player must know
/// before the main menu can make sense (direct-touch poke), names the recenter
/// shortcut, and offers a direct route to the layout editor. Returning
/// players skip it through UserSettings.FirstRunDone.
///
/// A child of ArenaRoot, hidden once dismissed. Layout first-pass; tune in-headset.
/// </summary>
public sealed partial class StartPrompt : Node3D
{
    private VrButton _editLayout = null!;
    private VrButton _skip = null!;

    /// <summary>True until the player dismisses the prompt (Main holds the sim).</summary>
    public bool Pending { get; private set; } = true;

    public event Action? OnEditLayout;
    public event Action? OnSkip;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        Position = SpatialMenuPlacement.PlayerFacing(s);
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
            Text = "Touch buttons with a controller top or fingertip.\n\n"
                + "Hold right A / left Menu to recenter the arena.\n"
                + "You can also use Recenter View in Pause.\n\n"
                + "Choose Edit Layout to place the arena, action buttons and shared menu distance, or Skip to use the defaults.",
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

        _editLayout = new VrButton();
        AddChild(_editLayout);
        _editLayout.BuildClassic(bw, bh, "Edit Layout");
        _editLayout.SetColor(new Color(0.92f, 0.78f, 0.34f));
        _editLayout.Position = new Vector3(-(bw * 0.5f + gap * 0.5f), -s * 0.22f, 0.0f);
        _editLayout.OnPress += () => Dismiss(editLayout: true);

        _skip = new VrButton();
        AddChild(_skip);
        _skip.BuildClassic(bw, bh, "Skip");
        _skip.SetColor(new Color(0.72f, 0.72f, 0.76f));
        _skip.Position = new Vector3(bw * 0.5f + gap * 0.5f, -s * 0.22f, 0.0f);
        _skip.OnPress += () => Dismiss(editLayout: false);
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Pending)
        {
            return;
        }
        _editLayout.PollPoke(probes);
        _skip.PollPoke(probes);
    }

    /// <summary>Dismiss without prompting (returning player who has already done
    /// the first run); fires no events.</summary>
    public void Skip()
    {
        Pending = false;
        Visible = false;
    }

    private void Dismiss(bool editLayout)
    {
        if (!Pending)
        {
            return;
        }
        Pending = false;
        Visible = false;
        if (editLayout)
        {
            OnEditLayout?.Invoke();
        }
        else
        {
            OnSkip?.Invoke();
        }
    }
}
