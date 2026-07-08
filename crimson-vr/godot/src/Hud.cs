using Godot;

namespace CrimsonVR;

/// <summary>
/// M3 slice 4: in-world HUD. A small panel anchored to the arena's near edge
/// (PLAN §6) showing the player's health, ammo/reload, and level — all read
/// straight from the per-tick <see cref="Sim.TickResult"/> and
/// <see cref="Sim.PlayerSnap"/>, so no sim/ABI change is needed.
///
/// Built entirely from code (a Label3D plus a few unshaded quad bars) as a child
/// of ArenaRoot, so it inherits the arena's placement/scale/yaw and sits at the
/// tabletop's near edge facing the seated player. Sizes are in arena-local
/// meters derived from the arena side length. Placement/tilt are first-pass and
/// meant to be tuned in-headset like the rest of the VR presentation.
/// </summary>
public sealed partial class Hud : Node3D
{
    private Label3D _text = null!;
    private MeshInstance3D _hpFill = null!;
    private MeshInstance3D _ammoFill = null!;
    private StandardMaterial3D _ammoMat = null!;

    private float _barWidth;

    // HP bar assumes a 100 base max (perks can exceed it); the fill clamps and
    // the numeric readout stays exact, so the bar is a glanceable approximation.
    private const float AssumedMaxHealth = 100.0f;

    private static readonly Color HpColor = new(0.85f, 0.25f, 0.2f);
    private static readonly Color AmmoColor = new(0.35f, 0.7f, 0.95f);
    private static readonly Color ReloadColor = new(0.95f, 0.8f, 0.3f);
    private static readonly Color BarBg = new(0.1f, 0.1f, 0.12f);

    public void Build(float arenaSideMeters)
    {
        float half = arenaSideMeters * 0.5f;
        _barWidth = arenaSideMeters * 0.8f;
        float barHeight = arenaSideMeters * 0.05f;

        // Anchor just outside the near edge (game +y -> arena +z), lifted a touch
        // and tilted up toward a seated player looking down at the table.
        Position = new Vector3(0.0f, 0.02f, half + arenaSideMeters * 0.14f);
        RotationDegrees = new Vector3(-55.0f, 0.0f, 0.0f);

        _text = new Label3D
        {
            Position = new Vector3(0.0f, arenaSideMeters * 0.12f, 0.0f),
            FontSize = 40,
            PixelSize = arenaSideMeters * 0.00035f,
            Modulate = new Color(0.95f, 0.95f, 0.95f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            NoDepthTest = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AddChild(_text);

        _hpFill = MakeBar(_barWidth, barHeight, arenaSideMeters * 0.04f, HpColor, out _);
        _ammoFill = MakeBar(_barWidth, barHeight, -arenaSideMeters * 0.02f, AmmoColor, out _ammoMat);
    }

    /// <summary>Create a bar (bg quad + fill quad) at a local Y offset. Returns
    /// the fill node; the fill grows from the left as its X scale shrinks.</summary>
    private MeshInstance3D MakeBar(float width, float height, float y, Color fill, out StandardMaterial3D fillMat)
    {
        var bg = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(width, height) },
            Position = new Vector3(0.0f, y, -0.001f),
            MaterialOverride = FlatMat(BarBg),
        };
        AddChild(bg);

        fillMat = FlatMat(fill);
        var fillNode = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(width, height) },
            Position = new Vector3(0.0f, y, 0.0f),
            MaterialOverride = fillMat,
        };
        AddChild(fillNode);
        return fillNode;
    }

    private static StandardMaterial3D FlatMat(Color c) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = true,
    };

    public void Update(in Sim.TickResult result, in Sim.PlayerSnap player)
    {
        float hp = player.Health;
        SetBar(_hpFill, Mathf.Clamp(hp / AssumedMaxHealth, 0.0f, 1.0f));

        bool reloading = player.ReloadActive != 0;
        float ammoFrac;
        string ammoText;
        if (reloading)
        {
            float t = player.ReloadTimerMax > 0.0f ? player.ReloadTimer / player.ReloadTimerMax : 0.0f;
            ammoFrac = Mathf.Clamp(t, 0.0f, 1.0f);
            ammoText = "RELOAD";
            _ammoMat.AlbedoColor = ReloadColor;
        }
        else
        {
            int clip = Mathf.Max(player.ClipSize, 1);
            ammoFrac = Mathf.Clamp(player.Ammo / clip, 0.0f, 1.0f);
            ammoText = $"AMMO {Mathf.FloorToInt(player.Ammo)}/{player.ClipSize}";
            _ammoMat.AlbedoColor = AmmoColor;
        }
        SetBar(_ammoFill, ammoFrac);

        _text.Text =
            $"HP {Mathf.CeilToInt(hp)}    LV {result.PlayerLevel}    FOES {result.CreatureActiveCount}\n{ammoText}";
    }

    /// <summary>Grow a fill bar from its left edge by scaling X and shifting so
    /// the left edge stays put.</summary>
    private void SetBar(MeshInstance3D fill, float frac)
    {
        frac = Mathf.Max(frac, 0.0001f);
        fill.Scale = new Vector3(frac, 1.0f, 1.0f);
        fill.Position = new Vector3(-_barWidth * 0.5f * (1.0f - frac), fill.Position.Y, fill.Position.Z);
    }
}
