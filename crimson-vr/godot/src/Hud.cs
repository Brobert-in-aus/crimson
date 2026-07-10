using Godot;

namespace CrimsonVR;

/// <summary>
/// In-world HUD rebuilt from the game's own HUD art (ui_gameTop / ui_lifeHeart /
/// ui_indLife / ui_wicons / ui_ind* / ui_indPanel), replacing the earlier custom
/// Label3D + colour bars. The native HUD (ui/hud.py, Survival flags) is a
/// 512-wide screen-corner overlay; here the same elements are laid out in their
/// native relative coordinates and mapped into an arena-local panel at the near
/// edge, so it reads as the real Crimsonland HUD floating over the tabletop.
///
/// Data comes straight from the per-tick <see cref="Sim.TickResult"/> and
/// <see cref="Sim.PlayerSnap"/> (health/ammo/level/xp + the ABI v8 weapon icon
/// index and ammo class), so no sim change is needed. Placement/tilt are
/// first-pass — tune in-headset like the rest of the VR presentation.
/// </summary>
public sealed partial class Hud : Node3D
{
    // Native HUD coordinates (ui/hud.py). Elements are positioned by their native
    // top-left + size. Horizontally the bbox is centred on the panel; vertically the
    // HUD's TOP edge (native y = 0) is anchored at the panel origin (NativeCenterY =
    // 0) so the whole HUD hangs DOWN from the arena's near edge rather than
    // extending up over the play surface.
    private const float NativeCenterX = 222.0f; // bbox x in [-68, 512]
    private const float NativeCenterY = 0.0f;   // top edge at the origin -> hangs down
    private const float NativeSpan = 580.0f;    // bbox width (-68..512)

    // Ammo bars enlarged for VR readability (native was 6-wide / 6-step / 16-tall).
    // Native rule (hud.py:46-47, 498-500): up to 30 bars are drawn; a clip
    // BIGGER than 30 collapses to 20 bars (+ the "+ N" overflow text).
    private const int AmmoBarLimit = 30;       // HUD_AMMO_BAR_LIMIT
    private const int AmmoBarClamp = 20;       // HUD_AMMO_BAR_CLAMP
    private const float AmmoBarStep = 11.0f;
    private const float AmmoBarW = 10.0f;
    private const float AmmoBarH = 30.0f;
    private const float AmmoBaseX = 318.0f;
    private const float AmmoBaseY = 17.0f;
    private const int WeaponGrid = 8;          // ui_wicons is 8x8

    // Health bar stretched much taller than the native 9px sliver so it reads in
    // VR (doubled again after in-headset feedback; fills the 64-tall top bar).
    private const float HealthBarX = 64.0f;
    private const float HealthBarY = 4.0f;
    private const float HealthBarH = 56.0f;
    private const float HeartBase = 40.0f; // heart quad base size (native ~32)

    // Bottom of the laid-out native content (XP panel 60..113): used to anchor
    // the panel's BOTTOM edge on the arena plane.
    private const float NativeBottomY = 113.0f;

    private float _u;      // metres per native HUD unit
    private float _side;

    private Texture2D? _wicons;
    private Texture2D? _indLife;
    private Texture2D?[] _ammoTex = System.Array.Empty<Texture2D?>();

    private MeshInstance3D? _heart;
    private StandardMaterial3D? _healthFillMat;
    private MeshInstance3D? _healthFill;
    private MeshInstance3D? _weaponIcon;
    private StandardMaterial3D? _weaponMat;
    private int _weaponIconShown = -2;
    private readonly MeshInstance3D[] _ammoBars = new MeshInstance3D[AmmoBarLimit];
    private readonly StandardMaterial3D[] _ammoMats = new StandardMaterial3D[AmmoBarLimit];
    private Label3D _ammoExtra = null!; // "+ N" overflow text (clip > bar count)
    private Label3D _xpValue = null!;
    private Label3D _lvlValue = null!;
    private MeshInstance3D? _xpFill;
    private int _xpSmoothed; // HudState.smooth_xp roll-up (displayed XP eases to target)
    private float _fade = 1.0f;
    private bool _fadeApplied;

    private const float HealthBarW = 120.0f; // native health bar width
    private const float XpProgressW = 54.0f;

    private static Texture2D? Load(string name)
    {
        string path = $"res://assets/sprites/{name}.png";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    public void Build(float arenaSideMeters)
    {
        _side = arenaSideMeters;
        float half = arenaSideMeters * 0.5f;
        // The HUD top bar spans ~1.15x the arena width; scale native units to fit.
        _u = arenaSideMeters * 1.15f / NativeSpan;

        // The HUD stands VERTICALLY at the FAR edge of the visible floor square
        // (in-headset decision: a scoreboard across the table beats a panel at
        // the near edge), its BOTTOM edge aligned with the floor's far edge.
        // Content is laid out hanging DOWN from the node origin (native y=0 at
        // the origin), so lifting the origin by the content height puts the
        // bottom exactly on the plane. Arena local +z = far; the 180 yaw turns
        // the art back toward the player. Quads stay depth-tested.
        Position = new Vector3(0.0f, NativeBottomY * _u, half * Diorama.FloorMarginScale);
        RotationDegrees = new Vector3(0.0f, 180.0f, 0.0f);

        _wicons = Load("ui_wicons");
        _indLife = Load("ui_indLife");
        _ammoTex = new[] { Load("ui_indBullet"), Load("ui_indFire"), Load("ui_indRocket"), Load("ui_indElectric") };

        // Top bar backing.
        TexQuad(Load("ui_gameTop"), 0.0f, 0.0f, 512.0f, 64.0f, new Color(1, 1, 1, 0.7f), priority: 40);

        // Survival XP panel (ind_panel), behind its text.
        TexQuad(Load("ui_indPanel"), -68.0f, 60.0f, 182.0f, 53.0f, new Color(1, 1, 1, 0.9f), priority: 41);

        // Pulsing heart (updated each frame), vertically centred in the bar.
        _heart = TexQuad(Load("ui_lifeHeart"), 27.0f - HeartBase * 0.5f, 32.0f - HeartBase * 0.5f, HeartBase, HeartBase, new Color(1, 1, 1, 0.8f), priority: 43);

        // Health bar (tall): dim full background + bright left-aligned fill (both ind_life).
        TexQuad(_indLife, HealthBarX, HealthBarY, HealthBarW, HealthBarH, new Color(1, 1, 1, 0.5f), priority: 42);
        _healthFill = TexQuad(_indLife, HealthBarX, HealthBarY, HealthBarW, HealthBarH, new Color(1, 1, 1, 0.8f), priority: 43, out _healthFillMat);

        // Weapon icon (wicons sub-cell, set per weapon in Update).
        _weaponIcon = TexQuad(_wicons, 206.0f, 6.0f, 96.0f, 48.0f, new Color(1, 1, 1, 0.8f), priority: 43, out _weaponMat);

        // Ammo bars (per-shot, enlarged), textured by ammo class in Update.
        for (int i = 0; i < AmmoBarLimit; i++)
        {
            _ammoBars[i] = TexQuad(null, AmmoBaseX + i * AmmoBarStep, AmmoBaseY, AmmoBarW, AmmoBarH, new Color(1, 1, 1, 0.8f), priority: 43, out _ammoMats[i]);
            _ammoBars[i].Visible = false;
        }
        // "+ N" overflow text when the clip exceeds the drawn bars (hud.py:521-527).
        _ammoExtra = MakeLabel(AmmoBaseX, AmmoBaseY + 10.0f, HorizontalAlignment.Left);
        _ammoExtra.Visible = false;

        // XP progress fill (left-aligned, updated in Update).
        _xpFill = ColorQuad(26.0f, 91.0f, XpProgressW, 4.0f, new Color(0.1f, 0.3f, 0.6f, 1.0f), priority: 43);

        // XP + level text on the panel.
        _xpValue = MakeLabel(26.0f, 74.0f, HorizontalAlignment.Left);
        _lvlValue = MakeLabel(85.0f, 79.0f, HorizontalAlignment.Left);
        MakeStaticLabel(4.0f, 78.0f, "Xp");
    }

    public void Update(in Sim.TickResult result, in Sim.PlayerSnap player)
    {
        // Pulsing heart: ((sin(t*speed)^4)*4 + 14) radius, faster when hurt.
        float t = result.ElapsedMsSim / 1000.0f;
        float speed = player.Health < 30.0f ? 5.0f : 2.0f;
        float sp = Mathf.Sin(t * speed);
        float pulse = (Mathf.Pow(sp, 4.0f) * 4.0f + 14.0f);
        if (_heart != null)
        {
            float d = pulse * 2.0f; // native diameter (units); quad base is HeartBase
            _heart.Scale = new Vector3(d / HeartBase, d / HeartBase, 1.0f);
        }

        // Health fill (left ratio of the bar, left-aligned).
        float hr = Mathf.Clamp(player.Health / 100.0f, 0.0f, 1.0f);
        if (_healthFill != null && _healthFillMat != null)
        {
            _healthFill.Visible = hr > 0.001f;
            _healthFill.Scale = new Vector3(Mathf.Max(hr, 0.001f), 1.0f, 1.0f);
            // Re-anchor the (centre-based) quad so its left edge stays at the bar left.
            float cx = HealthBarX + HealthBarW * 0.5f * hr;
            _healthFill.Position = new Vector3(LocalX(cx), LocalY(HealthBarY + HealthBarH * 0.5f), _healthFill.Position.Z);
            _healthFillMat.Uv1Scale = new Vector3(hr, 1.0f, 1.0f);
        }

        // Weapon icon: wicons cell = icon_index * 2 (2 cells wide).
        if (_weaponIcon != null && _weaponMat != null)
        {
            int icon = player.WeaponIconIndex;
            _weaponIcon.Visible = icon >= 0;
            if (icon >= 0 && icon != _weaponIconShown)
            {
                _weaponIconShown = icon;
                int frame = icon * 2;
                int col = frame % WeaponGrid;
                int row = frame / WeaponGrid;
                _weaponMat.Uv1Scale = new Vector3(2.0f / WeaponGrid, 1.0f / WeaponGrid, 1.0f);
                _weaponMat.Uv1Offset = new Vector3(col / (float)WeaponGrid, row / (float)WeaponGrid, 0.0f);
            }
        }

        // Ammo bars: bright for loaded shots, dim for the empty remainder.
        int cls = player.WeaponAmmoClass;
        Texture2D? ammoTex = cls switch
        {
            1 => _ammoTex.Length > 1 ? _ammoTex[1] : null,
            2 => _ammoTex.Length > 2 ? _ammoTex[2] : null,
            0 => _ammoTex.Length > 0 ? _ammoTex[0] : null,
            _ => _ammoTex.Length > 3 ? _ammoTex[3] : null,
        };
        // Native bar-count rule: clip_size bars up to 30; a clip over 30
        // collapses to 20 bars, and loaded shots beyond the drawn bars show as
        // "+ N" text after the row (hud.py:498-500, 521-527).
        int bars = Mathf.Max(player.ClipSize, 0);
        if (bars > AmmoBarLimit)
        {
            bars = AmmoBarClamp;
        }
        int loaded = Mathf.Max(Mathf.FloorToInt(player.Ammo), 0);
        bool showAmmo = ammoTex != null && player.WeaponIconIndex >= 0;
        for (int i = 0; i < AmmoBarLimit; i++)
        {
            bool on = showAmmo && i < bars;
            _ammoBars[i].Visible = on;
            if (on)
            {
                _ammoMats[i].AlbedoTexture = ammoTex;
                _ammoMats[i].AlbedoColor = new Color(1, 1, 1, i < loaded ? 0.8f : 0.8f * 0.3f);
            }
        }
        bool overflow = showAmmo && loaded > bars;
        _ammoExtra.Visible = overflow;
        if (overflow)
        {
            _ammoExtra.Text = $"+ {loaded - bars}";
            _ammoExtra.Position = new Vector3(LocalX(AmmoBaseX + bars * AmmoBarStep + 8.0f), LocalY(AmmoBaseY + 10.0f), 0.001f);
        }

        // XP panel text + intra-level progress bar. Displayed XP rolls toward
        // the target like HudState.smooth_xp (hud.py:95-120) instead of snapping.
        int xp = SmoothXp(player.Experience);
        _xpValue.Text = $"{xp}";
        _lvlValue.Text = $"{player.Level}";
        if (_xpFill != null)
        {
            float ratio = SurvivalProgress(xp, player.Level);
            _xpFill.Visible = ratio > 0.001f;
            _xpFill.Scale = new Vector3(Mathf.Max(ratio, 0.001f), 1.0f, 1.0f);
            float cx = 26.0f + XpProgressW * 0.5f * ratio;
            _xpFill.Position = new Vector3(LocalX(cx), LocalY(91.0f + 2.0f), _xpFill.Position.Z);
        }
    }

    // Port of HudState.smooth_xp: step = max(1, dt_ms/2) per frame, scaled up
    // by diff/100 when more than 1000 behind; approaches from either side.
    // Called once per 60 Hz sim tick (dt_ms ~= 16).
    private int SmoothXp(int target)
    {
        if (target <= 0)
        {
            _xpSmoothed = 0;
            return 0;
        }
        int smoothed = _xpSmoothed;
        if (smoothed == target)
        {
            return smoothed;
        }
        int step = Mathf.Max(1, 16 / 2);
        int diff = Mathf.Abs(smoothed - target);
        if (diff > 1000)
        {
            step *= diff / 100;
        }
        smoothed = smoothed < target
            ? Mathf.Min(smoothed + step, target)
            : Mathf.Max(smoothed - step, target);
        _xpSmoothed = smoothed;
        return smoothed;
    }

    /// <summary>Whole-HUD fade (0 = hidden, 1 = opaque): the base game eases the
    /// HUD out/in over the perk-menu transition (survival_mode.py:486) instead
    /// of hard-toggling. Applied via GeometryInstance3D.Transparency so every
    /// quad/label fades without touching its material alphas.</summary>
    public void SetFade(float fade)
    {
        fade = Mathf.Clamp(fade, 0.0f, 1.0f);
        if (fade == _fade && _fadeApplied)
        {
            return;
        }
        _fade = fade;
        _fadeApplied = true;
        foreach (Node child in GetChildren())
        {
            if (child is GeometryInstance3D g)
            {
                g.Transparency = 1.0f - fade;
            }
        }
    }

    // survival_level_threshold(level) = 1000 + level^1.8 * 1000 (gameplay.py).
    private static float SurvivalThreshold(int level)
    {
        level = Mathf.Max(1, level);
        return 1000.0f + Mathf.Pow(level, 1.8f) * 1000.0f;
    }

    private static float SurvivalProgress(int xp, int level)
    {
        level = Mathf.Max(1, level);
        float prev = level <= 1 ? 0.0f : SurvivalThreshold(level - 1);
        float next = SurvivalThreshold(level);
        return next <= prev ? 0.0f : Mathf.Clamp((xp - prev) / (next - prev), 0.0f, 1.0f);
    }

    // ---- native-coordinate -> arena-local helpers ----

    private float LocalX(float nx) => (nx - NativeCenterX) * _u;
    private float LocalY(float ny) => (NativeCenterY - ny) * _u; // native Y down -> local up

    /// <summary>Place a textured quad at native top-left (x,y) with native size (w,h).
    /// Null texture -> untextured (recoloured per frame, e.g. ammo bars).</summary>
    private MeshInstance3D TexQuad(Texture2D? tex, float x, float y, float w, float h, Color tint, int priority)
        => TexQuad(tex, x, y, w, h, tint, priority, out _);

    private MeshInstance3D TexQuad(Texture2D? tex, float x, float y, float w, float h, Color tint, int priority, out StandardMaterial3D mat)
    {
        mat = new StandardMaterial3D
        {
            AlbedoTexture = tex,
            AlbedoColor = tint,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            // Depth-TESTED (no NoDepthTest): the HUD hangs below/outside the
            // table edge, and the opaque tabletop must occlude it at shallow
            // angles — with NoDepthTest it drew over the arena (in-headset
            // fail). Coplanar HUD quads still order among themselves by
            // RenderPriority (all transparent, depth write off).
            RenderPriority = priority,
        };
        var node = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(w * _u, h * _u) },
            Position = new Vector3(LocalX(x + w * 0.5f), LocalY(y + h * 0.5f), 0.0f),
            MaterialOverride = mat,
        };
        AddChild(node);
        return node;
    }

    private MeshInstance3D ColorQuad(float x, float y, float w, float h, Color color, int priority)
    {
        var node = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(w * _u, h * _u) },
            Position = new Vector3(LocalX(x + w * 0.5f), LocalY(y + h * 0.5f), 0.0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                // Depth-tested like TexQuad: the tabletop occludes the HUD.
                RenderPriority = priority,
            },
        };
        AddChild(node);
        return node;
    }

    private Label3D MakeLabel(float nx, float ny, HorizontalAlignment align)
    {
        var l = new Label3D
        {
            Text = string.Empty,
            FontSize = 48,
            PixelSize = _u * 0.32f, // ~15 native units tall
            Modulate = new Color(0.9f, 0.9f, 0.9f),
            HorizontalAlignment = align,
            // Depth-tested like the quads: the tabletop occludes the HUD.
            Position = new Vector3(LocalX(nx), LocalY(ny), 0.001f),
            RenderPriority = 44,
        };
        AddChild(l);
        return l;
    }

    private void MakeStaticLabel(float nx, float ny, string text)
    {
        Label3D l = MakeLabel(nx, ny, HorizontalAlignment.Left);
        l.Text = text;
    }
}
