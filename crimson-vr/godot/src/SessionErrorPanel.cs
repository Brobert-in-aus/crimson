using System;
using Godot;

namespace CrimsonVR;

/// <summary>Recoverable in-headset error state for an offline run that could
/// not be created. Keeps the requested mode available for Retry and always
/// provides a stable route back to the main menu.</summary>
public sealed partial class SessionErrorPanel : Node3D
{
    private Label3D _detail = null!;
    private VrButton _retry = null!;
    private VrButton _mainMenu = null!;

    public bool Active { get; private set; }
    public event Action? OnRetry;
    public event Action? OnMainMenu;

    public void Build(float referenceSide)
    {
        float s = referenceSide;
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        ClassicPanel.Build(this, s * 1.35f, s * 0.92f, z: -0.012f);

        AddChild(new Label3D
        {
            Text = "Run could not start",
            FontSize = 96,
            PixelSize = s / 1200.0f,
            Modulate = new Color(1.0f, 0.62f, 0.48f),
            OutlineSize = 22,
            OutlineModulate = Colors.Black,
            Position = new Vector3(0.0f, s * 0.30f, 0.002f),
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });

        _detail = new Label3D
        {
            FontSize = 58,
            PixelSize = s / 1300.0f,
            Modulate = new Color(0.92f, 0.93f, 0.98f),
            OutlineSize = 18,
            OutlineModulate = Colors.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = (s * 1.12f) / (s / 1300.0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector3(0.0f, s * 0.08f, 0.002f),
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        };
        AddChild(_detail);

        float w = s * 0.46f;
        float h = s * 0.14f;
        _retry = new VrButton();
        AddChild(_retry);
        _retry.BuildClassic(w, h, "Retry");
        _retry.Position = new Vector3(-s * 0.28f, -s * 0.25f, 0.0f);
        _retry.OnPress += () => OnRetry?.Invoke();

        _mainMenu = new VrButton();
        AddChild(_mainMenu);
        _mainMenu.BuildClassic(w, h, "Main Menu");
        _mainMenu.Position = new Vector3(s * 0.28f, -s * 0.25f, 0.0f);
        _mainMenu.OnPress += () => OnMainMenu?.Invoke();
        Visible = false;
    }

    public void Show(string message)
    {
        Active = true;
        Visible = true;
        _detail.Text = "Your mode selection was preserved. Retry safely, or return to the menu.\n\n"
            + Friendly(message);
        _retry.ResetPress();
        _mainMenu.ResetPress();
    }

    public void Dismiss()
    {
        Active = false;
        Visible = false;
        _retry.ResetPress();
        _mainMenu.ResetPress();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active) return;
        _retry.PollPoke(probes);
        _mainMenu.PollPoke(probes);
    }

    private static string Friendly(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The game engine did not provide an error message.";
        string oneLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 150 ? oneLine : oneLine[..147] + "...";
    }
}
