using System;
using Godot;

namespace CrimsonVR;

/// <summary>Asset-free Quest recovery screen built only from generated geometry.</summary>
public sealed partial class AssetBootstrapPanel : Node3D
{
    private Label3D _detail = null!;
    private VrButton _retry = null!;

    public event Action? OnRetry;

    public void Build(float referenceSide, string? error)
    {
        float s = referenceSide;
        Position = Vector3.Zero;
        RotationDegrees = new Vector3(-10.0f, 180.0f, 0.0f);

        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(s * 1.9f, s * 1.25f, 0.008f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.025f, 0.03f, 0.045f, 0.96f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
            Position = new Vector3(0.0f, 0.0f, 0.015f),
        });

        AddLabel("GAME ASSETS REQUIRED", s * 0.38f, 105,
            new Color(1.0f, 0.72f, 0.25f), s / 900.0f);
        AddLabel(
            "CrimsonVR does not include Crimsonland art or audio.\n\n"
            + "On your PC, open the Crimson checkout and run:\n"
            + "crimson-vr\\tools\\prepare_assets.ps1 -Quest\n\n"
            + "Use Crimsonland Classic from GOG Extras, not the 2014 HD remake.\n"
            + "The helper transfers your assets locally; nothing is uploaded.",
            s * 0.08f, 56, new Color(0.9f, 0.92f, 0.96f), s / 1050.0f);

        _detail = AddLabel(error ?? "After transfer, poke Retry Import.",
            -s * 0.28f, 50,
            error == null ? new Color(0.55f, 0.82f, 1.0f) : new Color(1.0f, 0.5f, 0.42f),
            s / 1100.0f);

        _retry = new VrButton();
        AddChild(_retry);
        _retry.Build(s * 0.72f, s * 0.17f, "Retry Import", new Color(0.25f, 0.62f, 0.42f));
        _retry.Position = new Vector3(0.0f, -s * 0.48f, -0.006f);
        _retry.OnPress += () => OnRetry?.Invoke();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes) => _retry.PollPoke(probes);

    public void SetDetail(string text, bool error)
    {
        _detail.Text = text;
        _detail.Modulate = error ? new Color(1.0f, 0.5f, 0.42f) : new Color(0.55f, 0.82f, 1.0f);
        _retry.ResetPress();
    }

    private Label3D AddLabel(string text, float y, int fontSize, Color color, float pixelSize)
    {
        var label = new Label3D
        {
            Text = text,
            FontSize = fontSize,
            PixelSize = pixelSize,
            Modulate = color,
            OutlineSize = 12,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f, 0.9f),
            Position = new Vector3(0.0f, y, -0.006f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            NoDepthTest = true,
        };
        AddChild(label);
        return label;
    }
}
