using System;
using System.Collections.Generic;
using Godot;

namespace CrimsonVR;

/// <summary>
/// In-headset validation checklist: a floating, paged panel of poke items for the
/// things built headless-only (M3 visuals + M4 menus) that need eyes. Poking an
/// item cycles it untested -> pass -> fail; results persist (UserSettings) so a
/// test session survives relaunch. Opened from the pause menu.
///
/// A child of ArenaRoot, hidden until opened. Reuses VrButton (recoloured per
/// state). Layout is first-pass; tune in-headset.
/// </summary>
public sealed partial class ValidationChecklist : Node3D
{
    // (id, label). Ids are stable keys for persistence. Items already persisted
    // as PASS are filtered out when the panel is built, so a headset keeps only
    // failures and work that genuinely still needs testing after an update.
    private static readonly (string Id, string Label)[] Items =
    {
        // Fresh 2026-08-22 batch: only questions that require a worn headset,
        // tracked hands/controllers, stereo depth, or a second network peer.
        ("ux0822-freshboot", "Fresh boot: recovery then first-run appear correctly"),
        ("ux0822-menudistance", "Menus + results/perks sit at a comfortable depth"),
        ("ux0822-menureach", "Seated poke reaches every menu row comfortably"),
        ("ux0822-menulegibility", "Menu text is crisp and readable in both eyes"),
        ("ux0822-quitsafe", "Quit confirmations require a clear vertical move"),
        ("ux0822-tutorialentry", "Play recommends Tutorial without blocking modes"),
        ("ux0822-tutorialfull", "Tutorial clears the arena and reaches completion"),
        ("ux0822-pausetrack", "Pause and Level Up are easy to find and reach"),
        ("ux0822-perks", "Perk inspect/confirm flow prevents accidental picks"),
        ("ux0822-vrsettings", "Both VR Settings pages fit and labels are clear"),
        ("ux0822-layoutcab", "Cabinet edit defaults, rotation and preview work"),
        ("ux0822-layouttable", "Tabletop is flat; size and height controls work"),
        ("ux0822-controllers", "Controller poses, buttons and both sticks animate"),
        ("ux0822-hands", "Optical glove joints follow tracked hands one-to-one"),
        ("ux0822-rayneed", "Direct poke remains comfortable without a ray option"),
        ("ux0822-lan", "PC/Quest host + join, slots, effects and results work"),
    };

    private const int PerPage = 4;

    private static readonly Color[] StateColors =
    {
        new(0.45f, 0.45f, 0.5f),  // 0 untested
        new(0.3f, 0.7f, 0.35f),   // 1 pass
        new(0.8f, 0.35f, 0.3f),   // 2 fail
    };
    private static readonly string[] StatePrefix = { "[ ] ", "[PASS] ", "[FAIL] " };
    private static readonly string[] StateName = { "untested", "PASS", "FAIL" };

    private readonly VrButton[] _rows = new VrButton[PerPage];
    private VrButton _prev = null!;
    private VrButton _next = null!;
    private VrButton _close = null!;
    private Label3D _pageLabel = null!;
    private int _page;

    private Dictionary<string, int> _results = new();
    private (string Id, string Label)[] _activeItems = Array.Empty<(string, string)>();

    /// <summary>Raised when an item's state changes (id, newState) so the owner
    /// can persist it.</summary>
    public event Action<string, int>? OnItemChanged;

    public void Build(float arenaSideMeters, Dictionary<string, int> results)
    {
        _results = results;
        _activeItems = Array.FindAll(Items, item =>
            !_results.TryGetValue(item.Id, out int state) || state != 1);
        float s = arenaSideMeters;
        // Stand off to the player's RIGHT (arena-local +x), turned 90 deg so it
        // faces the player when they look right — always visible, out of the way of
        // the play area, with room for the full list. (Tune yaw sign in-headset if
        // it ends up on the wrong side.)
        // Raised to make room for the doubled rows (the stack reaches further down).
        Position = new Vector3(s * 1.25f, s * 0.9f, 0.0f);
        RotationDegrees = new Vector3(0.0f, -90.0f, 0.0f);

        var title = new Label3D
        {
            Text = "Validation Checklist",
            FontSize = 120,
            PixelSize = s / 1100.0f,
            Modulate = new Color(0.9f, 0.9f, 0.95f),
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, s * 0.42f, 0.0f),
            NoDepthTest = true,
        };
        AddChild(title);

        // Rows doubled in height + a wider gap (in-headset: adjacent rows got
        // poked together — the 2cm poke sphere spans a thin gap), with fewer
        // rows per page to keep the stack above the arena plane.
        float rw = s * 0.78f;
        float rh = s * 0.18f;
        float gap = s * 0.03f;
        float top = s * 0.32f;

        for (int i = 0; i < PerPage; i++)
        {
            var b = new VrButton();
            AddChild(b);
            b.Build(rw, rh, string.Empty, StateColors[0]);
            b.Position = new Vector3(0.0f, top - i * (rh + gap), 0.0f);
            int row = i;
            b.OnPress += () => CycleItem(row);
            _rows[i] = b;
        }

        float ctrlY = top - PerPage * (rh + gap) - gap;
        float bw = s * 0.22f;
        _prev = MakeCtrl("< Prev", bw, rh, -s * 0.28f, ctrlY, () => ChangePage(-1));
        _pageLabel = new Label3D
        {
            Text = string.Empty,
            FontSize = 90,
            PixelSize = s / 1300.0f,
            Modulate = new Color(0.85f, 0.85f, 0.9f),
            OutlineSize = 20,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, ctrlY, 0.0f),
            NoDepthTest = true,
        };
        AddChild(_pageLabel);
        _next = MakeCtrl("Next >", bw, rh, s * 0.28f, ctrlY, () => ChangePage(1));
        // Always-visible panel: the old Close is now a Log button (dumps the full
        // results to logcat on demand).
        _close = MakeCtrl("Log", bw, rh, 0.0f, ctrlY - (rh + gap), LogResults, new Color(0.6f, 0.6f, 0.66f));

        Visible = false;
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        if (visible)
        {
            RefreshPage();
        }
        else
        {
            foreach (VrButton r in _rows)
            {
                r.ResetPress();
            }
            _prev.ResetPress();
            _next.ResetPress();
            _close.ResetPress();
        }
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        foreach (VrButton r in _rows)
        {
            r.PollPoke(probes);
        }
        _prev.PollPoke(probes);
        _next.PollPoke(probes);
        _close.PollPoke(probes);
    }

    private VrButton MakeCtrl(string text, float w, float h, float x, float y, Action onPress, Color? color = null)
    {
        var b = new VrButton();
        AddChild(b);
        b.Build(w, h, text, color ?? new Color(0.5f, 0.55f, 0.66f));
        b.Position = new Vector3(x, y, 0.0f);
        b.OnPress += onPress;
        return b;
    }

    private int PageCount => Math.Max(1, (_activeItems.Length + PerPage - 1) / PerPage);

    private void ChangePage(int delta)
    {
        _page = Mathf.PosMod(_page + delta, PageCount);
        RefreshPage();
    }

    private void RefreshPage()
    {
        _pageLabel.Text = $"{_page + 1} / {PageCount}";
        for (int i = 0; i < PerPage; i++)
        {
            int idx = _page * PerPage + i;
            if (idx < _activeItems.Length)
            {
                _rows[i].Visible = true;
                ApplyRow(i, idx);
            }
            else
            {
                _rows[i].Visible = false;
            }
        }
    }

    private void ApplyRow(int row, int itemIndex)
    {
        (string id, string label) = _activeItems[itemIndex];
        int state = _results.TryGetValue(id, out int v) ? Mathf.Clamp(v, 0, 2) : 0;
        _rows[row].SetText(StatePrefix[state] + label);
        _rows[row].SetColor(StateColors[state]);
    }

    private void CycleItem(int row)
    {
        int idx = _page * PerPage + row;
        if (idx >= _activeItems.Length)
        {
            return;
        }
        string id = _activeItems[idx].Id;
        int state = (_results.TryGetValue(id, out int v) ? v : 0) + 1;
        if (state > 2)
        {
            state = 0;
        }
        _results[id] = state;
        ApplyRow(row, idx);
        // Log every change so it's retrievable off-device (adb logcat | grep CVRCHECK).
        GD.Print($"CVRCHECK item {id} -> {StateName[state]}");
        OnItemChanged?.Invoke(id, state);
    }

    /// <summary>Print the full pass/fail summary to the log (Android logcat) so
    /// results can be pulled off the Quest with `adb logcat | grep CVRCHECK`.</summary>
    public void LogResults()
    {
        int pass = 0;
        int fail = 0;
        int untested = 0;
        GD.Print("CVRCHECK ===== checklist results =====");
        foreach ((string id, string label) in Items)
        {
            int state = _results.TryGetValue(id, out int v) ? Mathf.Clamp(v, 0, 2) : 0;
            if (state == 1)
            {
                pass++;
            }
            else if (state == 2)
            {
                fail++;
            }
            else
            {
                untested++;
            }
            GD.Print($"CVRCHECK [{StateName[state]}] {id} - {label}");
        }
        GD.Print($"CVRCHECK ===== pass={pass} fail={fail} untested={untested} / {Items.Length} =====");
    }
}
