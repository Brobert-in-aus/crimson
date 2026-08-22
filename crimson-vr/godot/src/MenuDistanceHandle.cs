using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The one layout-edit affordance for the shared spatial-menu plane. It can only
/// move toward or away from the player: lateral position, height and angle are
/// intentionally fixed so menu, perk and results screens remain aligned.
/// </summary>
public sealed partial class MenuDistanceHandle : Node3D
{
    private const float HandleRadius = 0.028f;
    private const float GrabRadius = 0.065f;
    // Match PerkMenu's card-row anchor. The preview toggle sits below the row,
    // leaving this point centred among the cards and above the toggle.
    private const float PerkRowHeightFactor = 0.8f;

    private float _referenceSide;
    private bool _held;
    private int _heldProbe = -1;
    private Vector3 _grabTip;
    private float _grabDistance;

    public event Action<float>? OnDistanceChanged;

    public void Build(float referenceSideMeters, float additionalDistanceMeters)
    {
        _referenceSide = referenceSideMeters;
        Position = SpatialMenuPlacement.PlayerFacing(referenceSideMeters,
            heightFactor: PerkRowHeightFactor, additionalDistanceMeters: additionalDistanceMeters);
        // Player-facing spatial menus use this same orientation. Without it the
        // label's back faced the seat, making the text read reversed in-headset.
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = HandleRadius, Height = HandleRadius * 2.0f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.28f, 0.82f, 1.0f, 0.95f),
                EmissionEnabled = true,
                Emission = new Color(0.08f, 0.42f, 0.72f),
                EmissionEnergyMultiplier = 1.5f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true,
            },
        });

        AddChild(new Label3D
        {
            Text = "Menu distance\nGrip with controller/hand; move toward or away",
            FontSize = 48,
            PixelSize = referenceSideMeters / 1200.0f,
            Width = (referenceSideMeters * 0.9f) / (referenceSideMeters / 1200.0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector3(0.0f, -referenceSideMeters * 0.12f, 0.0f),
            Modulate = new Color(0.72f, 0.9f, 1.0f),
            OutlineSize = 18,
            OutlineModulate = Colors.Black,
            NoDepthTest = true,
            RenderPriority = ClassicPanel.TextRenderPriority,
        });
        Visible = false;
    }

    public void SetShown(bool shown, float additionalDistanceMeters)
    {
        Visible = shown;
        SetDistance(additionalDistanceMeters);
        if (!shown)
        {
            _held = false;
            _heldProbe = -1;
        }
    }

    public void SetDistance(float additionalDistanceMeters)
    {
        float distance = SpatialMenuPlacement.ClampAdditionalDistance(additionalDistanceMeters);
        Position = SpatialMenuPlacement.PlayerFacing(_referenceSide,
            heightFactor: PerkRowHeightFactor, additionalDistanceMeters: distance);
    }

    /// <summary>Returns true while the centred point is held.</summary>
    public bool PollGrab(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return false;
        }

        if (_held)
        {
            if (_heldProbe >= probes.Length || !probes[_heldProbe].Valid || !probes[_heldProbe].Grip)
            {
                _held = false;
                _heldProbe = -1;
                return false;
            }

            Vector3 deltaWorld = probes[_heldProbe].Tip - _grabTip;
            var parent = (Node3D)GetParent();
            Vector3 deltaLocal = parent.GlobalBasis.Inverse() * deltaWorld;
            float distance = SpatialMenuPlacement.AdjustAdditionalDistance(_grabDistance, deltaLocal);
            SetDistance(distance);
            OnDistanceChanged?.Invoke(distance);
            return true;
        }

        for (int i = 0; i < probes.Length; i++)
        {
            if (!probes[i].Valid || !probes[i].Grip || probes[i].Tip.DistanceTo(GlobalPosition) > GrabRadius)
            {
                continue;
            }
            _held = true;
            _heldProbe = i;
            _grabTip = probes[i].Tip;
            _grabDistance = Position.Z - _referenceSide * 0.25f;
            return true;
        }
        return false;
    }
}
