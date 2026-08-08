using System;
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
    // Two-row layout (VR glanceability): row 1 = full-width HP, row 2 = weapon
    // icon + full-width ammo. The full-width ammo row fits the native 30/20
    // limit/clamp again (96 + 30*11 + 10 <= 508).
    private const int AmmoBarLimit = 30;       // HUD_AMMO_BAR_LIMIT
    private const int AmmoBarClamp = 20;       // HUD_AMMO_BAR_CLAMP
    // Ammo owns the WHOLE top bar now. Health vacating it left a 512-wide,
    // 64-tall slab of backing art empty, and the ammo row had been dropped onto
    // the XP panel's line — where the ind_panel art is only 182 wide, so the
    // bars ran off their own background into open space. Back on the top bar,
    // spread across its full width and grown to fill its height.
    private const float AmmoBarStep = 13.0f;
    private const float AmmoBarW = 11.0f;
    private const float AmmoBarH = 34.0f;
    private const float AmmoBaseX = 96.0f;
    // Row geometry. XP occupies 0..53, the ammo bar 49..113; NativeBottomY (113)
    // is the panel's bottom edge, which rests on the arena's far edge.
    private const float XpRowTop = 0.0f;
    private const float XpPanelW = 240.0f;
    private const float AmmoRowTop = 49.0f;
    private const float AmmoBaseY = AmmoRowTop + 15.0f; // centres 34 in the 64-tall bar
    private const float AmmoExtraMaxX = 440.0f; // "+ N" start, kept on the backing
    private const int WeaponGrid = 8;          // ui_wicons is 8x8

    // Health bar stretched much taller than the native 9px sliver so it reads in
    // VR (doubled again after in-headset feedback; fills the 64-tall top bar).
    private const float HealthBarX = 36.0f;
    private const float HealthBarY = 2.0f;   // hugs the bar top: breathing room over the ammo row
    private const float HealthBarH = 22.0f;
    private const float HeartBase = 40.0f; // heart quad base size (native ~32)
    /// <summary>Roll applied to the heart quad alone, to stand it upright again
    /// after the health group is laid into the board plane. Negative = clockwise
    /// seen from the front.</summary>
    private const float HeartSpinDegrees = -90.0f;

    // Bottom of the laid-out native content (XP panel 60..113): used to anchor
    // the panel's BOTTOM edge on the arena plane.
    private const float NativeBottomY = 113.0f;

    /// <summary>Panel origin height as a multiple of the content height. 1.0
    /// sits the bottom edge exactly on the plane; above that is clearance.
    /// Interim: parked flat on the arena's far edge so the panel stops
    /// obstructing the features being tested, pending the proper cabinet
    /// re-layout (health vertical on the left edge, ammo along the top).</summary>
    private const float HudFloatFactor = 1.0f;

    private float _u;      // metres per native HUD unit
    private float _side;

    private Texture2D? _wicons;
    private Texture2D? _indLife;
    private Texture2D?[] _ammoTex = System.Array.Empty<Texture2D?>();

    private Node3D _healthRoot = null!;
    private Node3D _xpRoot = null!;

    /// <summary>The health readout (track, fill, heart) in its own node so Main
    /// can detach it: in Cabinet mode it lies FLAT in the board plane along the
    /// left edge, which the rest of the HUD cannot do because it hangs off the
    /// pivot that keeps the panel standing upright. Quads inside keep the panel's
    /// native coordinates, so the fill logic is unaffected by the move.</summary>
    public Node3D HealthRoot => _healthRoot;

    /// <summary>The XP readout (panel art, progress fill, value, level) in its
    /// own node, for the same reason as <see cref="HealthRoot"/> — Cabinet lays
    /// it flat along the board edge OPPOSITE health, so the two run down either
    /// side of the playfield instead of stacking in one panel above it.</summary>
    public Node3D XpRoot => _xpRoot;

    // Where each group's content sits relative to its own origin, in metres.
    // Both groups keep PANEL coordinates wherever they are placed, and neither
    // is centred on its origin there — health spans native x 36..482 while XP
    // spans -68..172. Placing them by origin alone would hang them off their
    // board edges by different amounts and in different directions, so Cabinet
    // subtracts these to centre the content itself.
    public Vector3 HealthContentCentreLocal
        => new(LocalX(HealthBarX + HealthBarW * 0.5f), LocalY(HealthBarY + HealthBarH * 0.5f), 0.0f);

    public Vector3 XpContentCentreLocal
        => new(LocalX(-68.0f + XpPanelW * 0.5f), LocalY(XpRowTop + 53.0f * 0.5f), 0.0f);

    // Draw-order bands: the world runs -8..27 (border, decals, then sprites from
    // 6), the HUD panel 40..44, menus 58..67.
    private const int HealthPanelTrackPriority = 42;
    private const int HealthPanelFacePriority = 43;
    // On the board, health is a FLOOR MARKING and belongs in the world band
    // below the sprites. At panel priorities it drew after every creature,
    // projectile and blood splat, so a bar lying in the playfield painted over
    // the entire game — the panel numbers were right only while it floated
    // above the arena and had nothing in front of it.
    private const int HealthBoardTrackPriority = 3;
    private const int HealthBoardFacePriority = 4;
    // XP sits one band above health so its text stays legible over its own
    // backing art, and still below the sprites (6+) like any floor marking.
    private const int XpPanelBackPriority = 41;
    private const int XpPanelFacePriority = 43;
    private const int XpPanelTextPriority = 44;
    private const int XpBoardBackPriority = 3;
    private const int XpBoardFacePriority = 4;
    private const int XpBoardTextPriority = 5;

    /// <summary>Tell the HUD which layout it is in.
    ///
    /// Two things depend on it. Page content is turned upright to compensate for
    /// a group being laid into the board plane, so applying that spin
    /// unconditionally would leave it on its side in Tabletop where the groups
    /// are never rotated. And a group's draw order has to move from the panel
    /// band to the world band, since on the board it has creatures in front of
    /// it.</summary>
    public void SetCabinetLayout(bool cabinet)
    {
        if (_heart != null)
        {
            _heart.RotationDegrees = new Vector3(0.0f, 0.0f, cabinet ? HeartSpinDegrees : 0.0f);
        }

        int track = cabinet ? HealthBoardTrackPriority : HealthPanelTrackPriority;
        int face = cabinet ? HealthBoardFacePriority : HealthPanelFacePriority;
        if (_healthTrackMat != null) _healthTrackMat.RenderPriority = track;
        if (_healthFillMat != null) _healthFillMat.RenderPriority = face;
        if (_heartMat != null) _heartMat.RenderPriority = face;

        // The XP group gets the same treatment. Its LABELS take the spin the
        // heart does: the group rotation runs the page's +x along the board's
        // near->far axis, which would leave the readings running away from the
        // player. Rolling them back by the same -90 puts the text across the
        // board with its tops toward the far edge — read from the seat, not
        // from the side.
        float spin = cabinet ? HeartSpinDegrees : 0.0f;
        foreach (Label3D l in _xpLabels)
        {
            l.RotationDegrees = new Vector3(0.0f, 0.0f, spin);
            l.RenderPriority = cabinet ? XpBoardTextPriority : XpPanelTextPriority;
        }
        if (_xpPanelMat != null) _xpPanelMat.RenderPriority = cabinet ? XpBoardBackPriority : XpPanelBackPriority;
        if (_xpFillMat != null) _xpFillMat.RenderPriority = cabinet ? XpBoardFacePriority : XpPanelFacePriority;
    }

    private MeshInstance3D? _heart;
    private StandardMaterial3D? _heartMat;
    private StandardMaterial3D? _healthTrackMat;
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
    private StandardMaterial3D? _xpPanelMat;
    private StandardMaterial3D? _xpFillMat;
    private readonly System.Collections.Generic.List<Label3D> _xpLabels = new();
    private int _xpSmoothed; // HudState.smooth_xp roll-up (displayed XP eases to target)
    private float _fade = 1.0f;
    private bool _fadeApplied;

    private const float HealthBarW = 446.0f; // full top-bar width (36..482; the box art's body ends ~486)
    // Intra-level progress bar, inside the XP panel. Build and per-frame
    // re-anchor both read these, so the bar cannot end up drawn on one row and
    // moved to another (it used to: built on the XP row, updated onto the ammo).
    private const float XpProgressX = 26.0f;
    private const float XpProgressW = 96.0f; // widened with the panel
    private const float XpProgressY = XpRowTop + 31.0f;
    private const float XpProgressH = 5.0f;

    private static Texture2D? Load(string name)
    {
        string path = $"res://assets/sprites/{name}.png";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    public void Build(float arenaSideMeters)
    {
        _side = arenaSideMeters;
        // The HUD top bar spans ~1.15x the arena width; scale native units to fit.
        _u = arenaSideMeters * 1.15f / NativeSpan;

        // The HUD stands VERTICALLY at the FAR edge of the visible floor square
        // (in-headset decision: a scoreboard across the table beats a panel at
        // the near edge), floating ONE panel-height above the plane (bottom
        // edge where the top used to be — sitting on the floor read too low).
        // Content is laid out hanging DOWN from the node origin (native y=0 at
        // the origin), so the origin sits at twice the content height. Arena
        // local +z = far; the 180 yaw turns the art back toward the player.
        // Quads stay depth-tested.
        //
        // The far-edge offset itself now lives on Main's HudPivot, which sits at
        // that anchor and counter-rotates the playfield tilt so this panel stays
        // world-vertical instead of leaning back with the floor. Only the float
        // above the plane is positioned here.
        // Content hangs DOWN from the origin by NativeBottomY, so the origin
        // height IS the float: at the old 2.0 the panel's bottom edge sat a
        // whole panel-height clear of the plane, which the 3x cabinet board
        // multiplied into roughly a third of a metre of empty air — the panel
        // read as unmoored from the arena rather than standing on its far edge.
        // 1.12 leaves a slim visible gap instead of a gulf.
        Position = new Vector3(0.0f, HudFloatFactor * NativeBottomY * _u, 0.0f);
        RotationDegrees = new Vector3(0.0f, 180.0f, 0.0f);

        _wicons = Load("ui_wicons");
        _indLife = Load("ui_indLife");
        _ammoTex = new[] { Load("ui_indBullet"), Load("ui_indFire"), Load("ui_indRocket"), Load("ui_indElectric") };

        // Rows are SWAPPED relative to the flat game: the wide bar carrying ammo
        // is the LOWEST row, so it sits nearest the board's far edge, with XP
        // above it. Native y grows downward and the panel's bottom edge rests on
        // the arena, so the largest y is the closest thing to the play surface —
        // and ammo is what gets glanced at mid-fight, where a shorter eye
        // movement off the action is worth more than it is for a score readout.
        TexQuad(Load("ui_gameTop"), 0.0f, AmmoRowTop, 512.0f, 64.0f, new Color(1, 1, 1, 0.7f), priority: 40);

        // Survival XP panel (ind_panel), above the ammo bar. Widened from the
        // native 182: health leaving the top bar freed the room, and the value
        // and level readings were crowded into each other's space.
        //
        // In its own group for the same reason health is: Cabinet lays it flat
        // along the board edge opposite health, so the two readings the player
        // actually tracks mid-fight sit either side of the playfield rather than
        // stacked above it.
        _xpRoot = new Node3D { Name = "HudXp" };
        AddChild(_xpRoot);
        TexQuad(Load("ui_indPanel"), -68.0f, XpRowTop, XpPanelW, 53.0f, new Color(1, 1, 1, 0.9f), priority: XpPanelBackPriority, out _xpPanelMat, _xpRoot);

        // Health group. Kept in its own node so Cabinet mode can lift it out of
        // this panel and lay it flat along the board's left edge while the rest
        // of the HUD stays standing at the far edge (see HealthRoot).
        _healthRoot = new Node3D { Name = "HudHealth" };
        AddChild(_healthRoot);

        // Pulsing heart (updated each frame), at the health bar's base. Turned
        // back upright INSIDE the group: laying the group into the board plane
        // to stand the bar vertical takes the heart with it, and a heart is the
        // one element here that has an obvious right way up. Rotating the quad
        // rather than the group leaves the bar's orientation alone.
        _heart = TexQuad(Load("ui_lifeHeart"), 18.0f - HeartBase * 0.5f, 17.0f - HeartBase * 0.5f, HeartBase, HeartBase, new Color(1, 1, 1, 0.8f), priority: 43, out _heartMat, _healthRoot);

        // ROW 1 — full-width health bar. VR glanceability: a near-opaque
        // DARKENED track under an overbright fill, so the missing section
        // reads at a glance (the flat game's subtle dim didn't survive VR).
        TexQuad(_indLife, HealthBarX, HealthBarY, HealthBarW, HealthBarH, new Color(0.30f, 0.30f, 0.30f, 0.95f), priority: 42, out _healthTrackMat, _healthRoot);
        _healthFill = TexQuad(_indLife, HealthBarX, HealthBarY, HealthBarW, HealthBarH, new Color(1.35f, 1.35f, 1.35f, 1.0f), priority: 43, out _healthFillMat, _healthRoot);

        // Weapon icon at the left of the ammo row, matching its height.
        _weaponIcon = TexQuad(_wicons, 36.0f, AmmoBaseY, 52.0f, AmmoBarH, new Color(1, 1, 1, 0.9f), priority: 43, out _weaponMat);

        // Ammo bars (per-shot, enlarged), textured by ammo class in Update.
        for (int i = 0; i < AmmoBarLimit; i++)
        {
            _ammoBars[i] = TexQuad(null, AmmoBaseX + i * AmmoBarStep, AmmoBaseY, AmmoBarW, AmmoBarH, new Color(1, 1, 1, 0.8f), priority: 43, out _ammoMats[i]);
            _ammoBars[i].Visible = false;
        }
        // "+ N" overflow text when the clip exceeds the drawn bars (hud.py:521-527).
        _ammoExtra = MakeLabel(AmmoBaseX, AmmoBaseY + 10.0f, HorizontalAlignment.Left);
        _ammoExtra.Visible = false;

        // XP progress fill (left-aligned, updated in Update), on the upper row.
        _xpFill = ColorQuad(XpProgressX, XpProgressY, XpProgressW, XpProgressH, new Color(0.1f, 0.3f, 0.6f, 1.0f), XpPanelFacePriority, out _xpFillMat, _xpRoot);

        // XP + level text, spread across the widened panel. Level sits well
        // clear of the value now: at the native spacing a five-figure XP ran
        // straight into it.
        _xpValue = MakeLabel(26.0f, XpRowTop + 14.0f, HorizontalAlignment.Left, _xpRoot);
        // x138, not further: the panel's right edge is at -68 + 240 = 172, and a
        // two-digit level left-aligned much past this starts to overhang it.
        _lvlValue = MakeLabel(138.0f, XpRowTop + 19.0f, HorizontalAlignment.Left, _xpRoot);
        _xpLabels.Add(_xpValue);
        _xpLabels.Add(_lvlValue);
        _xpLabels.Add(MakeStaticLabel(4.0f, XpRowTop + 18.0f, "Xp", _xpRoot));

        // Quest timer (quest mode only): elapsed / time limit. Sits on the upper
        // row clear of the widened XP panel; the sim enforces the timeline, this
        // is only the readout.
        _questTimer = MakeLabel(340.0f, XpRowTop + 20.0f, HorizontalAlignment.Center);
        _questTimer.Visible = false;

        BuildBonusRows();
        BuildWeaponPopup();
        LoadWeaponNames();
    }

    // ---- Bonus-HUD slots (ui/hud.py drawBonusHud; ABI v19 timers) ----
    // Native: ui_indPanel rows sliding in at the screen's left edge, one per
    // active bonus — icon + name + a timer bar at 0.05 width-per-second. VR
    // adaptation: the rows stack UPWARD above the scoreboard's left side
    // (negative native y) and fade instead of sliding. Slot rows follow the
    // flat registry semantics: a bonus keeps its row while active, expired
    // rows fade out, and only the tail compacts (rows never shuffle).
    private const int BonusRowCap = 8;
    private const float BonusRowX = -68.0f;
    private const float BonusRowPitch = 52.0f;
    private const float BonusRowY0 = -63.0f; // first row's top, above the bar

    private sealed class BonusRow
    {
        public MeshInstance3D Panel = null!;
        public StandardMaterial3D PanelMat = null!;
        public MeshInstance3D Icon = null!;
        public StandardMaterial3D IconMat = null!;
        public Label3D Name = null!;
        public MeshInstance3D Bar = null!;
        public int BonusKey = -1; // index into the spec table; -1 = free
        public float Timer;
        public float Fade;
        public int IconShown = -1;
    }

    private readonly BonusRow[] _bonusRows = new BonusRow[BonusRowCap];
    private Texture2D? _bonusSheet;
    private const int BonusGrid = 4; // bonuses.png is 4x4

    // Fixed spec order (collectHudBonusSpecs): key -> (icon id, display name).
    private static readonly (int Icon, string Name)[] BonusSpecs =
    {
        (7, "Weapon Power Up"),
        (5, "Reflex Boost"),
        (10, "Energizer"),
        (4, "Double Experience"),
        (8, "Freeze"),
        (11, "Fire Bullets"),
        (6, "Shield"),
        (9, "Speed"),
    };

    private void BuildBonusRows()
    {
        _bonusSheet = Load("bonuses");
        for (int i = 0; i < BonusRowCap; i++)
        {
            float y = BonusRowY0 - i * BonusRowPitch; // rows rise above the bar
            var row = new BonusRow();
            row.Panel = TexQuad(Load("ui_indPanel"), BonusRowX, y - 11.0f, 182.0f, 53.0f, new Color(1, 1, 1, 0.7f), priority: 41, out row.PanelMat);
            row.Icon = TexQuad(_bonusSheet, BonusRowX + 15.0f - 16.0f, y + 16.0f - 16.0f, 32.0f, 32.0f, Colors.White, priority: 42, out row.IconMat);
            row.Name = MakeLabel(BonusRowX + 36.0f, y + 9.0f, HorizontalAlignment.Left);
            row.Bar = ColorQuad(BonusRowX + 36.0f, y + 21.0f, 100.0f, 6.0f, new Color(26 / 255f, 77 / 255f, 153 / 255f, 0.7f), priority: 42);
            SetRowVisible(row, false);
            _bonusRows[i] = row;
        }
    }

    private static void SetRowVisible(BonusRow row, bool visible)
    {
        row.Panel.Visible = visible;
        row.Icon.Visible = visible;
        row.Name.Visible = visible;
        row.Bar.Visible = visible;
    }

    private void UpdateBonusRows(in Sim.SnapshotHeader header, in Sim.PlayerSnap player)
    {
        Span<float> timers = stackalloc float[BonusSpecs.Length];
        timers[0] = header.WeaponPowerUpTimer;
        timers[1] = header.ReflexBoostTimer;
        timers[2] = header.EnergizerTimer;
        timers[3] = header.DoubleExperienceTimer;
        timers[4] = header.FreezeTimer;
        timers[5] = player.FireBulletsTimer;
        timers[6] = player.ShieldTimer;
        timers[7] = player.SpeedBonusTimer;

        // Register active bonuses: keep an existing row, else take the first
        // free one (bonus_hud.register).
        for (int key = 0; key < timers.Length; key++)
        {
            if (timers[key] <= 0.0f)
            {
                continue;
            }
            int found = -1;
            int free = -1;
            for (int i = 0; i < BonusRowCap; i++)
            {
                if (_bonusRows[i].BonusKey == key)
                {
                    found = i;
                    break;
                }
                if (_bonusRows[i].BonusKey < 0 && free < 0)
                {
                    free = i;
                }
            }
            int idx = found >= 0 ? found : free;
            if (idx < 0)
            {
                continue;
            }
            _bonusRows[idx].BonusKey = key;
            _bonusRows[idx].Timer = timers[key];
        }

        const float dt = 1.0f / 60.0f; // called once per sim tick
        for (int i = 0; i < BonusRowCap; i++)
        {
            BonusRow row = _bonusRows[i];
            if (row.BonusKey < 0)
            {
                SetRowVisible(row, false);
                continue;
            }
            float timer = timers[row.BonusKey];
            row.Timer = timer;
            row.Fade = Mathf.MoveToward(row.Fade, timer > 0.0f ? 1.0f : 0.0f, dt * 4.0f);
            if (row.Fade <= 0.001f && timer <= 0.0f)
            {
                // Tail compaction only, like the flat registry: a middle row
                // stays reserved (invisible) while later rows are active.
                bool laterActive = false;
                for (int j = i + 1; j < BonusRowCap; j++)
                {
                    if (_bonusRows[j].BonusKey >= 0)
                    {
                        laterActive = true;
                        break;
                    }
                }
                if (!laterActive)
                {
                    row.BonusKey = -1;
                    row.IconShown = -1;
                }
                SetRowVisible(row, false);
                continue;
            }
            SetRowVisible(row, true);
            (int iconId, string name) = BonusSpecs[row.BonusKey];
            if (row.IconShown != iconId)
            {
                row.IconShown = iconId;
                row.IconMat.Uv1Scale = new Vector3(1.0f / BonusGrid, 1.0f / BonusGrid, 1.0f);
                row.IconMat.Uv1Offset = new Vector3(iconId % BonusGrid / (float)BonusGrid, iconId / BonusGrid / (float)BonusGrid, 0.0f);
                row.Name.Text = name;
            }
            float a = row.Fade;
            row.PanelMat.AlbedoColor = new Color(1, 1, 1, 0.7f * a);
            row.IconMat.AlbedoColor = new Color(1, 1, 1, a);
            row.Name.Modulate = new Color(0.9f, 0.9f, 0.9f, a);
            // Native bar: width = 100 * timer * 0.05 (a 20s bonus fills it).
            float ratio = Mathf.Clamp(row.Timer * 0.05f, 0.0f, 1.0f);
            row.Bar.Visible = ratio > 0.001f;
            row.Bar.Scale = new Vector3(Mathf.Max(ratio, 0.001f), 1.0f, 1.0f);
            float rowY = BonusRowY0 - i * BonusRowPitch;
            float cx = BonusRowX + 36.0f + 100.0f * 0.5f * ratio;
            row.Bar.Position = new Vector3(LocalX(cx), LocalY(rowY + 21.0f + 3.0f), row.Bar.Position.Z);
            if (row.Bar.MaterialOverride is StandardMaterial3D barMat)
            {
                barMat.AlbedoColor = new Color(26 / 255f, 77 / 255f, 153 / 255f, 0.7f * a);
            }
        }
    }

    // ---- Weapon-pickup name popup (drawWeaponAuxHud; player.aux_timer) ----
    // Native: an ind_panel with the weapon icon + name at the bonus stack's
    // foot, fading in over aux_timer [2,1] and out over [1,0]. VR: a fixed
    // panel above the scoreboard's right side (the bonus rows own the left).
    private MeshInstance3D? _auxPanel;
    private StandardMaterial3D? _auxPanelMat;
    private MeshInstance3D? _auxIcon;
    private StandardMaterial3D? _auxIconMat;
    private Label3D _auxName = null!;
    private int _auxIconShown = -2;
    private readonly System.Collections.Generic.Dictionary<int, string> _weaponNames = new();

    private void BuildWeaponPopup()
    {
        const float x = 330.0f;
        const float y = BonusRowY0;
        _auxPanel = TexQuad(Load("ui_indPanel"), x, y - 11.0f, 182.0f, 53.0f, new Color(1, 1, 1, 0.8f), priority: 41, out _auxPanelMat);
        _auxIcon = TexQuad(_wicons, x + 8.0f, y + 3.0f, 52.0f, 26.0f, Colors.White, priority: 42, out _auxIconMat);
        _auxName = MakeLabel(x + 66.0f, y + 12.0f, HorizontalAlignment.Left);
        _auxPanel.Visible = false;
        _auxIcon.Visible = false;
        _auxName.Visible = false;
    }

    private void LoadWeaponNames()
    {
        string path = "res://assets/sprites/sprite_manifest.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            return;
        }
        using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(f.GetAsText());
            if (doc.RootElement.TryGetProperty("weapons", out System.Text.Json.JsonElement weapons)
                && weapons.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (System.Text.Json.JsonProperty w in weapons.EnumerateObject())
                {
                    if (int.TryParse(w.Name, out int id)
                        && w.Value.TryGetProperty("name", out System.Text.Json.JsonElement n))
                    {
                        _weaponNames[id] = n.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException e)
        {
            GD.PushWarning($"CrimsonVR: bad weapons table in manifest: {e.Message}");
        }
    }

    private void UpdateWeaponPopup(in Sim.PlayerSnap player)
    {
        if (_auxPanel == null || _auxIcon == null)
        {
            return;
        }
        // Native fade: aux_timer counts 2 -> 0; alpha ramps in over [2,1] and
        // out over [1,0] (drawWeaponAuxHud).
        float aux = player.AuxTimer;
        float fade = Mathf.Clamp(aux > 1.0f ? 2.0f - aux : aux, 0.0f, 1.0f);
        bool visible = aux > 0.0f && fade > 0.001f && player.WeaponIconIndex >= 0;
        _auxPanel.Visible = visible;
        _auxIcon.Visible = visible;
        _auxName.Visible = visible;
        if (!visible)
        {
            return;
        }
        if (player.WeaponIconIndex != _auxIconShown && _auxIconMat != null)
        {
            _auxIconShown = player.WeaponIconIndex;
            int frame = player.WeaponIconIndex * 2;
            _auxIconMat.Uv1Scale = new Vector3(2.0f / WeaponGrid, 1.0f / WeaponGrid, 1.0f);
            _auxIconMat.Uv1Offset = new Vector3(frame % WeaponGrid / (float)WeaponGrid, frame / WeaponGrid / (float)WeaponGrid, 0.0f);
            _auxName.Text = _weaponNames.TryGetValue(player.WeaponId, out string? name) ? name : $"Weapon {player.WeaponId}";
        }
        if (_auxPanelMat != null)
        {
            _auxPanelMat.AlbedoColor = new Color(1, 1, 1, 0.8f * fade);
        }
        if (_auxIconMat != null)
        {
            _auxIconMat.AlbedoColor = new Color(1, 1, 1, fade);
        }
        _auxName.Modulate = new Color(0.9f, 0.9f, 0.9f, fade);
    }

    private Label3D _questTimer = null!;
    private long _questLimitMs;

    /// <summary>Enable the quest timer with the level's time limit (0 = hide;
    /// call per run start).</summary>
    public void SetQuestTimeLimit(long limitMs)
    {
        _questLimitMs = limitMs;
        _questTimer.Visible = limitMs > 0;
    }

    public void Update(in Sim.TickResult result, in Sim.PlayerSnap player, in Sim.SnapshotHeader header)
    {
        UpdateBonusRows(header, player);
        UpdateWeaponPopup(player);
        if (_questLimitMs > 0 && _questTimer.Visible)
        {
            long elapsed = result.ElapsedMsSim;
            long secs = elapsed / 1000;
            long limitSecs = _questLimitMs / 1000;
            _questTimer.Text = $"{secs / 60:D2}:{secs % 60:D2} / {limitSecs / 60:D2}:{limitSecs % 60:D2}";
            // Running out: tint toward red over the last 30s.
            float leftMs = Mathf.Max(0.0f, _questLimitMs - elapsed);
            float warn = 1.0f - Mathf.Clamp(leftMs / 30000.0f, 0.0f, 1.0f);
            _questTimer.Modulate = new Color(1.0f, 1.0f - warn * 0.6f, 1.0f - warn * 0.6f);
        }

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
            // Clamped inside the bar's backing. The label trails the last drawn
            // bar, so widening the bar pitch pushes it right: at 30 bars and the
            // current step it starts near x494 and the text itself would then
            // run off the 512-wide backing into open space.
            float extraX = Mathf.Min(AmmoBaseX + bars * AmmoBarStep + 8.0f, AmmoExtraMaxX);
            _ammoExtra.Position = new Vector3(LocalX(extraX), LocalY(AmmoBaseY + 10.0f), 0.001f);
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
            float cx = XpProgressX + XpProgressW * 0.5f * ratio;
            // Was a hardcoded 93, left behind when XP and ammo swapped rows: the
            // bar was built on the XP row and then jumped onto the ammo bars on
            // its first update. Re-anchor from the same constants it is built
            // with so the two cannot drift apart again.
            _xpFill.Position = new Vector3(LocalX(cx), LocalY(XpProgressY + XpProgressH * 0.5f), _xpFill.Position.Z);
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
        // RECURSIVE, and applied to the detached groups explicitly: this used to
        // walk only direct children, which silently stopped covering the health
        // readout the moment it was grouped into its own node — and covers it
        // not at all once Cabinet mode reparents that node onto the board. A
        // health bar that stayed opaque through the perk-menu fade would be the
        // only thing left lit on the table. XP is now in the same position, on
        // the opposite edge, so it needs the same treatment.
        ApplyFade(this, 1.0f - fade);
        foreach (Node3D group in new[] { _healthRoot, _xpRoot })
        {
            if (group.GetParent() != this)
            {
                ApplyFade(group, 1.0f - fade);
            }
        }
    }

    private static void ApplyFade(Node root, float transparency)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is GeometryInstance3D g)
            {
                g.Transparency = transparency;
            }
            ApplyFade(child, transparency);
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
        => TexQuad(tex, x, y, w, h, tint, priority, out mat, null);

    /// <param name="host">Group to add the quad to; null means the panel itself.
    /// Groups keep the SAME native-coordinate maths, so a quad reads identically
    /// whether it sits in the panel or in a group that has been detached and
    /// re-placed — which is what lets the health bar move into the board plane
    /// without touching the fill logic.</param>
    private MeshInstance3D TexQuad(Texture2D? tex, float x, float y, float w, float h, Color tint, int priority, out StandardMaterial3D mat, Node3D? host)
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
        (host ?? this).AddChild(node);
        return node;
    }

    private MeshInstance3D ColorQuad(float x, float y, float w, float h, Color color, int priority)
        => ColorQuad(x, y, w, h, color, priority, out _, null);

    /// <param name="host">Group to add the quad to; null means the panel itself.
    /// Same contract as the TexQuad overload — native coordinates are unchanged
    /// by the move.</param>
    private MeshInstance3D ColorQuad(
        float x, float y, float w, float h, Color color, int priority,
        out StandardMaterial3D mat, Node3D? host)
    {
        mat = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            // Depth-tested like TexQuad: the tabletop occludes the HUD.
            RenderPriority = priority,
        };
        var node = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(w * _u, h * _u) },
            Position = new Vector3(LocalX(x + w * 0.5f), LocalY(y + h * 0.5f), 0.0f),
            MaterialOverride = mat,
        };
        (host ?? this).AddChild(node);
        return node;
    }

    private Label3D MakeLabel(float nx, float ny, HorizontalAlignment align, Node3D? host = null)
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
        (host ?? this).AddChild(l);
        return l;
    }

    private Label3D MakeStaticLabel(float nx, float ny, string text, Node3D? host = null)
    {
        Label3D l = MakeLabel(nx, ny, HorizontalAlignment.Left, host);
        l.Text = text;
        return l;
    }
}
