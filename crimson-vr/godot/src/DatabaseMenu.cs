using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The Unlocked Weapons / Perks Database screens (base panels/databases_*.py),
/// opened from the Statistics screen like the original. One instance serves
/// both databases: the flat game's tall left list panel + short right detail
/// panel become two classic panels side by side, the mouse-hover detail becomes
/// poke-to-select (rows are VrButtons), and the scrollwheel becomes page
/// up/down poke arrows (stepping VISIBLE_ROWS-1 like the flat PageUp/PageDown).
///
/// Unlocked sets are recomputed exactly like the flat game: the baked quest
/// unlock table + the persisted unlock indices rebuild the availability lists
/// (perks/availability.py, weapon_runtime/availability.py), the persisted
/// per-weapon usage counts (ABI v16 save-status parity) add every weapon the
/// player has ever picked up, and availability is evaluated under the
/// last-selected game mode like the flat config.gameplay.mode.
/// </summary>
public sealed partial class DatabaseMenu : Node3D
{
    public enum Db
    {
        Weapons,
        Perks,
    }

    private const int VisibleRows = 8;

    private readonly record struct WeaponInfo(
        string Name, int IconIndex, int ClipSize, float ReloadTime, int Rpm, int AmmoClass);

    // Baked tables (sprite_manifest.json).
    private readonly Dictionary<int, WeaponInfo> _weapons = new();
    private readonly Dictionary<int, string> _perkNames = new();
    private readonly Dictionary<int, string> _perkDescs = new();
    private readonly Dictionary<int, int> _perkPrereqs = new();
    private readonly List<int> _questUnlockWeapons = new(); // global-index order
    private readonly List<int> _questUnlockPerks = new();

    private readonly List<int> _ids = new(); // current database, sorted by id
    private readonly VrButton[] _rows = new VrButton[VisibleRows];
    private readonly int[] _rowIds = new int[VisibleRows];
    private VrButton _scrollUp = null!;
    private VrButton _scrollDown = null!;
    private VrButton _back = null!;

    private SmallFontLabel? _title;
    private SmallFontLabel? _countLine;
    private SmallFontLabel? _scrollLine;
    private SmallFontLabel? _detailNo;
    private SmallFontLabel? _detailName;
    private SmallFontLabel? _detailPrereq;
    private SmallFontLabel? _detailBody;
    private MeshInstance3D? _weaponIcon;
    private StandardMaterial3D? _weaponIconMat;

    private Db _db = Db.Weapons;
    private int _scroll;
    private int _selectedId = -1;
    private float _side;
    private float _px; // metres per small-font pixel

    public bool IsOpen { get; private set; }

    public event Action? OnBack;

    public void Build(float arenaSideMeters)
    {
        _side = arenaSideMeters;
        float s = arenaSideMeters;
        LoadTables();

        // Shared menu anchor (one plane for all menus).
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        // Flat layout: tall left list panel + short right detail panel
        // (databases_base.py LEFT/RIGHT_PANEL_*). Two classic panels here.
        var left = new Node3D { Position = new Vector3(-s * 0.40f, 0.0f, 0.0f) };
        AddChild(left);
        ClassicPanel.Build(left, s * 0.70f, s * 1.15f, z: -0.012f);
        var right = new Node3D { Position = new Vector3(s * 0.40f, s * 0.06f, 0.0f) };
        AddChild(right);
        ClassicPanel.Build(right, s * 0.70f, s * 0.85f, z: -0.012f);

        SmallFont? font = SmallFont.Shared();
        _px = s / 620.0f; // same panel text sizing as the stats screen
        if (font != null)
        {
            _title = MakeText(left, font, new Vector3(0.0f, s * 0.50f, 0.0f), new Color(1.0f, 1.0f, 1.0f, 1.0f));
            _countLine = MakeText(left, font, new Vector3(0.0f, s * 0.43f, 0.0f), new Color(1.0f, 1.0f, 1.0f, 0.7f));
            _scrollLine = MakeText(left, font, new Vector3(s * 0.24f, -s * 0.47f, 0.0f), new Color(1.0f, 1.0f, 1.0f, 0.7f));

            _detailNo = MakeText(right, font, new Vector3(s * 0.20f, s * 0.34f, 0.0f), new Color(1.0f, 1.0f, 1.0f, 0.4f));
            _detailName = MakeText(right, font, new Vector3(0.0f, s * 0.26f, 0.0f), new Color(1.0f, 1.0f, 1.0f, 1.0f));
            // Flat prereq line colour: (255,204,204) at 0.8 alpha.
            _detailPrereq = MakeText(right, font, new Vector3(0.0f, s * 0.17f, 0.0f), new Color(1.0f, 0.8f, 0.8f, 0.8f));
            _detailBody = MakeText(right, font, new Vector3(0.0f, -s * 0.05f, 0.0f), new Color(1.0f, 1.0f, 1.0f, 0.7f));
        }

        // Weapon icon: ui_wicons 8x8 grid, frame = icon_index*2 spanning two
        // cells (2:1 aspect) — same layout the HUD/score card use.
        if (ResourceLoader.Exists("res://assets/sprites/ui_wicons.png")
            && ResourceLoader.Load<Texture2D>("res://assets/sprites/ui_wicons.png") is Texture2D wicons)
        {
            _weaponIconMat = new StandardMaterial3D
            {
                AlbedoTexture = wicons,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                Uv1Scale = new Vector3(2.0f / 8.0f, 1.0f / 8.0f, 1.0f),
                RenderPriority = 63, // panel content art: over backdrop 58, under labels
            };
            _weaponIcon = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(s * 0.30f, s * 0.15f) },
                Position = new Vector3(0.0f, s * 0.03f, 0.0f),
                MaterialOverride = _weaponIconMat,
                Visible = false,
            };
            right.AddChild(_weaponIcon);
        }

        // List rows down the left panel; page arrows in a column beside them.
        float rowW = s * 0.52f;
        float rowH = s * 0.078f;
        float rowPitch = rowH + s * 0.017f;
        float topY = s * 0.33f;
        for (int i = 0; i < VisibleRows; i++)
        {
            var b = new VrButton();
            left.AddChild(b);
            b.BuildClassic(rowW, rowH, string.Empty);
            b.Position = new Vector3(-s * 0.055f, topY - i * rowPitch, 0.0f);
            int idx = i;
            b.OnPress += () => PressRow(idx);
            _rows[i] = b;
        }
        float arrowS = s * 0.10f;
        _scrollUp = new VrButton();
        left.AddChild(_scrollUp);
        _scrollUp.BuildClassic(arrowS, arrowS, "^");
        _scrollUp.Position = new Vector3(s * 0.27f, topY, 0.0f);
        _scrollUp.OnPress += () => ScrollBy(-(VisibleRows - 1));
        _scrollDown = new VrButton();
        left.AddChild(_scrollDown);
        _scrollDown.BuildClassic(arrowS, arrowS, "v");
        _scrollDown.Position = new Vector3(s * 0.27f, topY - (VisibleRows - 1) * rowPitch, 0.0f);
        _scrollDown.OnPress += () => ScrollBy(VisibleRows - 1);

        _back = new VrButton();
        AddChild(_back);
        _back.BuildClassic(s * 0.3f, s * 0.11f, "Back");
        _back.Position = new Vector3(0.0f, -s * 0.52f, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    private SmallFontLabel MakeText(Node3D parent, SmallFont font, Vector3 pos, Color color)
    {
        var t = new SmallFontLabel();
        t.Build(font, _px, color, center: true);
        t.Position = pos;
        parent.AddChild(t);
        return t;
    }

    public void Open(Db db, UserSettings settings)
    {
        _db = db;
        _ids.Clear();
        _ids.AddRange(db == Db.Weapons ? BuildWeaponIds(settings) : BuildPerkIds(settings.QuestUnlockIndex));
        _scroll = 0;
        _selectedId = _ids.Count > 0 ? _ids[0] : -1;
        IsOpen = true;
        Visible = true;
        RefreshList();
        RefreshDetail();
        foreach (VrButton b in _rows)
        {
            b.ResetPress();
        }
        _scrollUp.ResetPress();
        _scrollDown.ResetPress();
        _back.ResetPress();
    }

    public void Close()
    {
        IsOpen = false;
        Visible = false;
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!IsOpen)
        {
            return;
        }
        foreach (VrButton b in _rows)
        {
            if (b.Visible)
            {
                b.PollPoke(probes);
            }
        }
        if (_scrollUp.Visible)
        {
            _scrollUp.PollPoke(probes);
        }
        if (_scrollDown.Visible)
        {
            _scrollDown.PollPoke(probes);
        }
        _back.PollPoke(probes);
    }

    private void PressRow(int row)
    {
        int id = _rowIds[row];
        if (id <= 0)
        {
            return;
        }
        _selectedId = id;
        RefreshList();
        RefreshDetail();
    }

    private void ScrollBy(int delta)
    {
        int maxScroll = Math.Max(0, _ids.Count - VisibleRows);
        int next = Math.Clamp(_scroll + delta, 0, maxScroll);
        if (next == _scroll)
        {
            return;
        }
        _scroll = next;
        RefreshList();
    }

    private void RefreshList()
    {
        bool weapons = _db == Db.Weapons;
        _title?.SetText(weapons ? "Unlocked Weapons Database" : "Unlocked Perks Database");
        int count = _ids.Count;
        string noun = weapons ? (count == 1 ? "weapon" : "weapons") : (count == 1 ? "perk" : "perks");
        _countLine?.SetText($"{count} {noun} in database");

        int maxScroll = Math.Max(0, count - VisibleRows);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        for (int i = 0; i < VisibleRows; i++)
        {
            int listIndex = _scroll + i;
            if (listIndex >= count)
            {
                _rows[i].Visible = false;
                _rowIds[i] = 0;
                continue;
            }
            int id = _ids[listIndex];
            _rowIds[i] = id;
            _rows[i].Visible = true;
            _rows[i].SetText(NameFor(id));
            // Flat rows: selected bright, the rest dimmed (the classic skin
            // tints the plate art).
            _rows[i].SetColor(id == _selectedId ? Colors.White : new Color(0.62f, 0.62f, 0.68f));
        }

        bool scrollable = count > VisibleRows;
        _scrollUp.Visible = scrollable;
        _scrollDown.Visible = scrollable;
        _scrollLine?.SetText(scrollable
            ? $"{_scroll + 1}-{Math.Min(count, _scroll + VisibleRows)} of {count}"
            : string.Empty);
    }

    private void RefreshDetail()
    {
        if (_selectedId <= 0)
        {
            _detailNo?.SetText(string.Empty);
            _detailName?.SetText(string.Empty);
            _detailPrereq?.SetText(string.Empty);
            _detailBody?.SetText(string.Empty);
            if (_weaponIcon != null)
            {
                _weaponIcon.Visible = false;
            }
            return;
        }
        int id = _selectedId;
        _detailNo?.SetText($"{(_db == Db.Weapons ? "weapon" : "perk")} #{id}");
        _detailName?.SetText(NameFor(id));
        if (_db == Db.Weapons)
        {
            _detailPrereq?.SetText(string.Empty);
            WeaponInfo w = _weapons.TryGetValue(id, out WeaponInfo info) ? info : default;
            // databases_weapons.py: continuous-fire weapons (ammo_class 1) have
            // no meaningful rpm.
            string rate = w.AmmoClass == 1 ? "Fire rate: n/a" : $"Fire rate: {w.Rpm} rpm";
            _detailBody?.SetText(
                $"{rate}\nReload time: {w.ReloadTime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} secs\nClip size: {w.ClipSize}");
            if (_weaponIcon != null && _weaponIconMat != null)
            {
                int frame = w.IconIndex * 2;
                _weaponIconMat.Uv1Offset = new Vector3(frame % 8 / 8.0f, frame / 8 / 8.0f, 0.0f);
                _weaponIcon.Visible = true;
            }
        }
        else
        {
            if (_weaponIcon != null)
            {
                _weaponIcon.Visible = false;
            }
            _detailPrereq?.SetText(
                _perkPrereqs.TryGetValue(id, out int prereq) ? $"Requires: {NameFor(prereq)}" : string.Empty);
            string desc = _perkDescs.TryGetValue(id, out string? d) ? d : string.Empty;
            // Flat wrap width: 256 font px (databases_perks.py _DESC_WRAP_WIDTH_PX).
            _detailBody?.SetText(WrapSmallText(desc, 256.0f));
        }
    }

    private string NameFor(int id) => _db == Db.Weapons
        ? (_weapons.TryGetValue(id, out WeaponInfo w) ? w.Name : $"weapon_{id}")
        : (_perkNames.TryGetValue(id, out string? n) ? n : $"perk_{id}");

    /// <summary>Native small-text wrapper (databases_perks.py
    /// _wrap_small_text_native): walk characters spending width, on overflow
    /// back up to the last space and break there.</summary>
    private static string WrapSmallText(string text, float maxWidthPx)
    {
        SmallFont? font = SmallFont.Shared();
        if (font == null || text.Length == 0)
        {
            return text;
        }
        var wrapped = new StringBuilder(text);
        float remaining = maxWidthPx;
        int i = 0;
        while (i < wrapped.Length)
        {
            char ch = wrapped[i];
            if (ch == '\r')
            {
                i++;
                continue;
            }
            if (ch == '\n')
            {
                remaining = maxWidthPx;
                i++;
                continue;
            }
            remaining -= font.MeasureWidth(ch.ToString());
            if (remaining < 0.0f)
            {
                int j = i;
                while (j > 0 && wrapped[j] != ' ' && wrapped[j] != '\n')
                {
                    j--;
                }
                if (wrapped[j] == ' ')
                {
                    wrapped[j] = '\n';
                    i = j;
                }
                remaining = maxWidthPx;
            }
            i++;
        }
        return wrapped.ToString();
    }

    // ---- unlocked-set rules (perks/availability.py, weapon_runtime/availability.py) ----

    private IEnumerable<int> BuildWeaponIds(UserSettings settings)
    {
        // build_weapon_availability: pistol + quest unlock rewards up to the
        // frontier index + the survival trio when the (persisted) mode is
        // survival + the Splitter Gun once the FULL (hardcore) unlock index
        // reaches 40.
        var ids = new SortedSet<int> { 1 };
        for (int i = 0; i < settings.QuestUnlockIndex && i < _questUnlockWeapons.Count; i++)
        {
            if (_questUnlockWeapons[i] > 0)
            {
                ids.Add(_questUnlockWeapons[i]);
            }
        }
        if (settings.LastGameMode == 1)
        {
            ids.Add(2); // assault rifle
            ids.Add(3); // shotgun
            ids.Add(5); // submachine gun
        }
        if (settings.QuestUnlockIndexFull >= 40)
        {
            ids.Add(29); // splitter gun (native gate: quest_unlock_index_full >= 0x28)
        }
        // databases_weapons.py additionally includes every weapon with a
        // lifetime usage count (i.e. ever picked up).
        for (int id = 1; id < settings.WeaponUsageCounts.Length; id++)
        {
            if (settings.WeaponUsageCounts[id] != 0)
            {
                ids.Add(id);
            }
        }
        ids.RemoveWhere(id => !_weapons.ContainsKey(id));
        return ids;
    }

    private IEnumerable<int> BuildPerkIds(int unlockIndex)
    {
        var ids = new SortedSet<int>();
        for (int id = 1; id <= 27; id++) // base range: 1..BONUS_MAGNET
        {
            ids.Add(id);
        }
        // _PERK_ALWAYS_AVAILABLE: Man Bomb / Fire Cough / Living Fortress /
        // Tough Reloader.
        ids.Add(53);
        ids.Add(54);
        ids.Add(55);
        ids.Add(56);
        for (int i = 0; i < unlockIndex && i < _questUnlockPerks.Count; i++)
        {
            if (_questUnlockPerks[i] > 0)
            {
                ids.Add(_questUnlockPerks[i]);
            }
        }
        ids.RemoveWhere(id => !_perkNames.ContainsKey(id));
        return ids;
    }

    private void LoadTables()
    {
        const string path = "res://assets/sprites/sprite_manifest.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            return;
        }
        using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        try
        {
            using var doc = JsonDocument.Parse(f.GetAsText());
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("weapons", out JsonElement weapons) && weapons.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in weapons.EnumerateObject())
                {
                    _weapons[int.Parse(p.Name)] = new WeaponInfo(
                        p.Value.GetProperty("name").GetString() ?? string.Empty,
                        p.Value.GetProperty("icon_index").GetInt32(),
                        p.Value.TryGetProperty("clip_size", out JsonElement cs) ? cs.GetInt32() : 0,
                        p.Value.TryGetProperty("reload_time", out JsonElement rt) ? rt.GetSingle() : 0.0f,
                        p.Value.TryGetProperty("rpm", out JsonElement rpm) ? rpm.GetInt32() : 0,
                        p.Value.TryGetProperty("ammo_class", out JsonElement ac) ? ac.GetInt32() : 0);
                }
            }
            if (root.TryGetProperty("perks", out JsonElement perks) && perks.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in perks.EnumerateObject())
                {
                    _perkNames[int.Parse(p.Name)] = p.Value.GetString() ?? string.Empty;
                }
            }
            if (root.TryGetProperty("perk_descriptions", out JsonElement descs) && descs.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in descs.EnumerateObject())
                {
                    _perkDescs[int.Parse(p.Name)] = p.Value.GetString() ?? string.Empty;
                }
            }
            if (root.TryGetProperty("perk_prereqs", out JsonElement prereqs) && prereqs.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in prereqs.EnumerateObject())
                {
                    _perkPrereqs[int.Parse(p.Name)] = p.Value.GetInt32();
                }
            }
            if (root.TryGetProperty("quests", out JsonElement quests) && quests.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement q in quests.EnumerateArray())
                {
                    _questUnlockWeapons.Add(
                        q.TryGetProperty("unlock_weapon_id", out JsonElement uw) ? uw.GetInt32() : 0);
                    _questUnlockPerks.Add(
                        q.TryGetProperty("unlock_perk_id", out JsonElement up) ? up.GetInt32() : 0);
                }
            }
        }
        catch (JsonException e)
        {
            GD.PushWarning($"CrimsonVR: bad database tables in manifest: {e.Message}");
        }
    }
}
