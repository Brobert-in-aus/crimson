using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The game-over / results screen (screens/results/game_over.py) as a diegetic
/// VR panel: the Reaper banner over a score card — Score, Rank, Game time with
/// the analog ui_clockTable/ui_clockPointer gauge (pointer = 6 deg per second,
/// like the base game), most-used weapon icon + name (ABI v10), Frags and
/// Hit % — plus Play Again / Main Menu poke buttons.
///
/// Flow (driven by Main, matching the base game's two-phase panel): death →
/// short pacing delay → the panel appears immediately. If the score ranks, the
/// panel starts in the NAME-ENTRY phase — raised and pushed back so the virtual
/// keyboard sits in front of it (the base game's "State your name, trooper!"
/// input lives on the same panel) with the buttons hidden; Enter drops it to
/// the standard spot and reveals the buttons. Unranked deaths skip straight to
/// the buttons phase. The base game's High-scores button is omitted until the
/// high-scores browser screen exists.
///
/// A child of ArenaRoot; inherits arena placement/scale/yaw.
/// </summary>
public sealed partial class GameOverPanel : Node3D
{
    private const int WeaponGrid = 8;    // ui_wicons is 8x8; icon spans 2 cells
    private const int TableMax = 10;     // UserSettings keeps a local top-10

    public bool Active { get; private set; }
    public event Action? OnPlayAgain;
    public event Action? OnMainMenu;

    private bool _buttonsShown;
    private readonly List<VrButton> _buttons = new();
    private readonly Dictionary<int, (string Name, int IconIndex)> _weapons = new();

    private float _side;
    private Label3D _score = null!;
    private Label3D _rank = null!;
    private Label3D _time = null!;
    private Label3D _weaponName = null!;
    private Label3D _frags = null!;
    private Label3D _hitRatio = null!;
    private Label3D _tooLow = null!;
    private MeshInstance3D? _weaponIcon;
    private StandardMaterial3D? _weaponIconMat;
    private MeshInstance3D? _clockPointer;

    public void Build(float arenaSideMeters)
    {
        float s = _side = arenaSideMeters;
        LoadWeaponTable();

        // Same slot as the virtual keyboard: above the arena, yawed to face the
        // player (arena local +z = far edge), slight back-lean.
        Position = new Vector3(0.0f, s * 0.85f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        // Backing panel. Coplanar overlay quads must be transparent with distinct
        // RenderPriority for draw order to work (HUD lesson); the backing also
        // sits slightly behind on z. DEPTH-TESTED (noDepthTest: false): with
        // NoDepthTest the backing drew over the opaque VrButton faces sitting in
        // front of it, dimming Play Again / Main Menu behind the 93%-alpha dark
        // quad (in-headset fail). Depth-testing lets the buttons punch through
        // while the panel's own labels still order above it by priority.
        AddQuad(null, 0.0f, 0.0f, s * 1.35f, s * 1.05f, new Color(0.05f, 0.055f, 0.085f, 0.93f), priority: 60, z: -0.006f, noDepthTest: false);

        // Reaper banner (256x64 art). Well Done is the quest-victory variant;
        // survival death is always the Reaper.
        AddQuad(Load("ui_textReaper"), 0.0f, s * 0.40f, s * 0.62f, s * 0.155f, new Color(1, 1, 1, 0.95f), priority: 62);

        // Score column (left) — value + rank, like the base card's first column.
        // Line spacing: Label3D glyphs are ~FontSize*PixelSize tall (110*s/1000
        // = 0.11s for the score), so adjacent rows need >= that between centres
        // — the first pass overlapped descenders (Frags g touched the Hit %).
        _score = AddLabel("Score: 0", -s * 0.36f, s * 0.25f, s / 1000.0f, new Color(0.9f, 0.9f, 1.0f));
        _rank = AddLabel("Rank: -", -s * 0.36f, s * 0.12f, s / 1200.0f, new Color(0.9f, 0.9f, 0.92f));
        _tooLow = AddLabel($"Score too low for top{TableMax}.", 0.0f, -s * 0.24f, s / 1400.0f, new Color(0.78f, 0.78f, 0.78f));

        // Game time (right): analog gauge + mm:ss. Pointer rotates 6 deg per
        // elapsed second like the base clock (viewer-clockwise = -Z here).
        float clock = s * 0.16f;
        AddQuad(Load("ui_clockTable"), s * 0.22f, s * 0.185f, clock, clock, new Color(1, 1, 1, 0.9f), priority: 62);
        _clockPointer = AddQuad(Load("ui_clockPointer"), s * 0.22f, s * 0.185f, clock, clock, new Color(1, 1, 1, 0.95f), priority: 63, z: 0.001f);
        _time = AddLabel("0:00", s * 0.44f, s * 0.185f, s / 1100.0f, new Color(0.9f, 0.9f, 0.92f));

        // Weapon row: most-used icon (2 wicons cells, 2:1) + name, frags, hit %.
        _weaponIcon = AddQuad(Load("ui_wicons"), -s * 0.36f, -s * 0.02f, s * 0.30f, s * 0.15f, new Color(1, 1, 1, 0.9f), priority: 62, out _weaponIconMat);
        _weaponName = AddLabel("", -s * 0.36f, -s * 0.15f, s / 1400.0f, new Color(0.8f, 0.8f, 0.82f));
        _frags = AddLabel("Frags: 0", s * 0.28f, s * 0.005f, s / 1150.0f, new Color(0.9f, 0.9f, 0.92f));
        _hitRatio = AddLabel("Hit %: 0%", s * 0.28f, -s * 0.125f, s / 1150.0f, new Color(0.9f, 0.9f, 0.92f));

        // Buttons: Play Again / Main Menu (High scores needs the browser screen).
        AddButton("Play Again", -s * 0.26f, -s * 0.34f, () => OnPlayAgain?.Invoke(), new Color(0.35f, 0.55f, 0.4f));
        AddButton("Main Menu", s * 0.26f, -s * 0.34f, () => OnMainMenu?.Invoke(), new Color(0.45f, 0.45f, 0.55f));

        Visible = false;
    }

    /// <summary>Show the panel BESIDE the keyboard with buttons hidden (base
    /// phase 0). Stacking above forced the player to crane up (in-headset
    /// FAIL), and pushing it behind interleaved with the keys while the panel
    /// text was no-depth-test. Now: same comfortable height as the keyboard,
    /// off to the player's right (arena -x, mirroring the checklist on the
    /// left), angled toward the seat — and all panel elements depth-test, so
    /// any overlap resolves like real geometry.</summary>
    public void ShowForNameEntry(in Sim.TickResult result, int rank)
    {
        Show(result, rank);
        _buttonsShown = false;
        foreach (VrButton b in _buttons)
        {
            b.Visible = false;
        }
        Scale = Vector3.One * 0.85f;
        Position = new Vector3(-_side * 1.05f, _side * 0.85f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 135.0f, 0.0f);
    }

    /// <summary>Name entry done: drop to the standard spot and reveal the
    /// Play Again / Main Menu buttons (base phase 1).</summary>
    public void ShowButtons()
    {
        Scale = Vector3.One;
        Position = new Vector3(0.0f, _side * 0.85f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        _buttonsShown = true;
        foreach (VrButton b in _buttons)
        {
            b.Visible = true;
            b.ResetPress();
        }
    }

    /// <summary>Fill the card from the final tick stats and show the panel in
    /// the buttons phase. <paramref name="rank"/> is the 0-based insertion index
    /// into the local highscore table (TableMax = didn't rank).</summary>
    public void Show(in Sim.TickResult result, int rank)
    {
        _score.Text = $"Score: {result.PlayerExperience}";
        bool ranked = rank < TableMax;
        _rank.Text = ranked ? $"Rank: {Ordinal(rank + 1)}" : "Rank: -";
        _tooLow.Visible = !ranked;

        long ms = result.ElapsedMsSim;
        long seconds = Math.Max(0, ms / 1000);
        _time.Text = $"{seconds / 60}:{seconds % 60:00}";
        if (_clockPointer != null)
        {
            // Viewer-facing side of the yawed panel: clockwise = negative local Z.
            _clockPointer.RotationDegrees = new Vector3(0.0f, 0.0f, -6.0f * seconds);
        }

        int weaponId = result.MostUsedWeaponId;
        if (_weapons.TryGetValue(weaponId, out (string Name, int IconIndex) w))
        {
            _weaponName.Text = w.Name;
            SetWeaponIcon(w.IconIndex);
        }
        else
        {
            _weaponName.Text = $"Weapon {weaponId}";
            SetWeaponIcon(-1);
        }

        _frags.Text = $"Frags: {result.CreatureKillCount}";
        int fired = Math.Max(0, result.ShotsFired);
        int hit = Math.Max(0, result.ShotsHit);
        int ratio = fired > 0 ? hit * 100 / fired : 0;
        _hitRatio.Text = $"Hit %: {ratio}%";

        Scale = Vector3.One;
        Position = new Vector3(0.0f, _side * 0.85f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        _buttonsShown = true;
        foreach (VrButton b in _buttons)
        {
            b.Visible = true;
            b.ResetPress();
        }
        Active = true;
        Visible = true;
    }

    public void Dismiss()
    {
        Active = false;
        Visible = false;
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        // Name-entry phase has no pokeable elements (VrButton has no visibility
        // guard of its own, so don't poll hidden buttons).
        if (!Active || !_buttonsShown)
        {
            return;
        }
        foreach (VrButton b in _buttons)
        {
            b.PollPoke(probes);
        }
    }

    private void SetWeaponIcon(int iconIndex)
    {
        if (_weaponIcon == null || _weaponIconMat == null)
        {
            return;
        }
        bool ok = iconIndex >= 0 && iconIndex <= 31;
        _weaponIcon.Visible = ok;
        if (ok)
        {
            int frame = iconIndex * 2;
            int col = frame % WeaponGrid;
            int row = frame / WeaponGrid;
            _weaponIconMat.Uv1Scale = new Vector3(2.0f / WeaponGrid, 1.0f / WeaponGrid, 1.0f);
            _weaponIconMat.Uv1Offset = new Vector3(col / (float)WeaponGrid, row / (float)WeaponGrid, 0.0f);
        }
    }

    private static string Ordinal(int n)
    {
        int mod100 = n % 100;
        string suffix = (mod100 is 11 or 12 or 13) ? "th" : (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return $"{n}{suffix}";
    }

    private static Texture2D? Load(string name)
    {
        return AssetStore.LoadTexture(AssetStore.SpritePath($"{name}.png"));
    }

    // Depth-tested by default: overlap with the keyboard/world must resolve
    // like real geometry (a no-depth-test score card drew through the keys).
    private MeshInstance3D AddQuad(Texture2D? tex, float x, float y, float w, float h, Color tint, int priority, float z = 0.0f, bool noDepthTest = false)
        => AddQuad(tex, x, y, w, h, tint, priority, out _, z, noDepthTest);

    private MeshInstance3D AddQuad(Texture2D? tex, float x, float y, float w, float h, Color tint, int priority, out StandardMaterial3D mat, float z = 0.0f, bool noDepthTest = false)
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
            NoDepthTest = noDepthTest,
            RenderPriority = priority,
        };
        var node = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(w, h) },
            Position = new Vector3(x, y, z),
            MaterialOverride = mat,
        };
        AddChild(node);
        return node;
    }

    private Label3D AddLabel(string text, float x, float y, float pixelSize, Color color)
    {
        var l = new Label3D
        {
            Text = text,
            FontSize = 110,
            PixelSize = pixelSize,
            Modulate = color,
            // 4mm in front of the backing so depth testing (no NoDepthTest —
            // overlap with the keyboard must occlude correctly) keeps the text
            // cleanly above its own panel.
            Position = new Vector3(x, y, 0.004f),
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderPriority = 64,
        };
        AddChild(l);
        return l;
    }

    private void AddButton(string text, float x, float y, Action onPress, Color color)
    {
        _ = color; // classic plate skin: colour no longer differentiates buttons
        var b = new VrButton();
        AddChild(b);
        b.BuildClassic(_side * 0.44f, _side * 0.13f, text);
        b.Position = new Vector3(x, y, 0.004f);
        b.OnPress += onPress;
        _buttons.Add(b);
    }

    private void LoadWeaponTable()
    {
        string path = AssetStore.SpritePath("sprite_manifest.json");
        if (!Godot.FileAccess.FileExists(path))
        {
            return;
        }
        using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        try
        {
            using var doc = JsonDocument.Parse(f.GetAsText());
            if (doc.RootElement.TryGetProperty("weapons", out JsonElement obj) && obj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in obj.EnumerateObject())
                {
                    if (int.TryParse(p.Name, out int id)
                        && p.Value.TryGetProperty("name", out JsonElement name)
                        && p.Value.TryGetProperty("icon_index", out JsonElement icon))
                    {
                        _weapons[id] = (name.GetString() ?? $"Weapon {id}", icon.GetInt32());
                    }
                }
            }
        }
        catch (JsonException e)
        {
            GD.PushWarning($"CrimsonVR: bad weapons table in manifest: {e.Message}");
        }
    }
}
