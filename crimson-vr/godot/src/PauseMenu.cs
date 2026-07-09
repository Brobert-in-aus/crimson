using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 3: pause. A small pause toggle sits FLAT (parallel to the arena) off
/// to one side, pressable at any time during play (PLAN M4 UI model). Poking it
/// pauses the sim (Main simply stops ticking) and raises a panel ABOVE the arena
/// with Resume / Settings / Quit poke buttons; poking Resume (or the toggle again)
/// unpauses.
///
/// A child of ArenaRoot, so everything is arena-local and inherits placement.
/// Settings is wired to <see cref="OnSettings"/> for the later settings slice;
/// until then Main leaves it unhooked (poking it is a no-op). Layout is
/// first-pass; tune in-headset.
/// </summary>
public sealed partial class PauseMenu : Node3D
{
    private VrButton _toggle = null!;
    private VrButton _levelUp = null!;
    private Node3D _panel = null!;
    private VrButton _resume = null!;
    private VrButton _settings = null!;
    private VrButton _quit = null!;

    public bool IsPaused { get; private set; }

    public event Action? OnQuit;
    public event Action? OnSettings;
    public event Action? OnLevelUp;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;

        // Pause toggle: flat on the arena plane (face up: local +Z -> world +Y via
        // a -90 deg X rotation), off to the +x side and near the player edge, just
        // outside the playfield so it never collides with the move/aim reticles.
        _toggle = new VrButton();
        AddChild(_toggle);
        _toggle.Build(s * 0.18f, s * 0.12f, "Pause", new Color(0.6f, 0.6f, 0.66f), proud: s * 0.03f, plate: true);
        // Clear of the terrain floor (which extends ~0.65*s past centre); sit it
        // out to the +x side so it never overlaps the play area.
        _toggle.Position = new Vector3(s * 0.9f, 0.02f, -s * 0.35f);
        // Lie flat facing up. The extra 180 about local Z spins the label in-plane
        // so its top points away from the player (upright when looking down), not
        // toward them (which read upside down).
        _toggle.RotationDegrees = new Vector3(-90.0f, 0.0f, 180.0f);
        _toggle.OnPress += TogglePause;

        // Level-up button: flat like the pause toggle, just inboard of it (toward
        // the far edge). Shown only while a perk pick is pending; poking it opens
        // the perk menu (rather than the cards auto-appearing).
        _levelUp = new VrButton();
        AddChild(_levelUp);
        _levelUp.Build(s * 0.18f, s * 0.12f, "Level Up!", new Color(0.9f, 0.8f, 0.35f), proud: s * 0.03f, plate: true);
        _levelUp.Position = new Vector3(s * 0.9f, 0.02f, -s * 0.12f);
        _levelUp.RotationDegrees = new Vector3(-90.0f, 0.0f, 180.0f);
        _levelUp.OnPress += () => OnLevelUp?.Invoke();
        _levelUp.Visible = false;

        // Pause panel above the arena, facing the player (same anchor style as the
        // perk menu): Resume / Settings / Quit stacked vertically.
        _panel = new Node3D
        {
            // Shared menu anchor (see MainMenu): all menus coplanar + pushed back.
            Position = new Vector3(0.0f, s * 0.85f, s * 0.25f),
            RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f),
            Visible = false,
        };
        AddChild(_panel);

        float bw = s * 0.5f;
        float bh = s * 0.16f;
        float gap = s * 0.05f;
        _resume = MakeButton(_panel, "Resume", new Color(0.4f, 0.8f, 0.45f), bw, bh, 0, gap);
        _settings = MakeButton(_panel, "Settings", new Color(0.5f, 0.6f, 0.85f), bw, bh, 1, gap);
        _quit = MakeButton(_panel, "Quit", new Color(0.85f, 0.35f, 0.3f), bw, bh, 2, gap);
        _resume.OnPress += () => SetPaused(false);
        _settings.OnPress += () => OnSettings?.Invoke();
        _quit.OnPress += () => OnQuit?.Invoke();
    }

    private static VrButton MakeButton(Node3D parent, string text, Color color, float w, float h, int row, float gap)
    {
        var b = new VrButton();
        parent.AddChild(b);
        b.Build(w, h, text, color, plate: true);
        // Row 0 at the top, stacking downward.
        b.Position = new Vector3(0.0f, (1 - row) * (h + gap), 0.0f);
        return b;
    }

    /// <summary>Force the unpaused state (e.g. when quitting to the main menu).</summary>
    public void ForceResume() => SetPaused(false);

    private void TogglePause() => SetPaused(!IsPaused);

    private void SetPaused(bool paused)
    {
        IsPaused = paused;
        _panel.Visible = paused;
        if (!paused)
        {
            _resume.ResetPress();
            _settings.ResetPress();
            _quit.ResetPress();
        }
    }

    /// <summary>Feed controller probes each rendered frame. The toggle is always
    /// live; the panel buttons only while paused and the panel is showing (the
    /// settings overlay hides it).</summary>
    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        _toggle.PollPoke(probes);
        if (_levelUp.Visible)
        {
            _levelUp.PollPoke(probes);
        }
        if (IsPaused && _panel.Visible)
        {
            _resume.PollPoke(probes);
            _settings.PollPoke(probes);
            _quit.PollPoke(probes);
        }
    }

    /// <summary>Show/hide the level-up button (shown while a perk pick is pending).</summary>
    public void SetLevelUpVisible(bool visible)
    {
        if (_levelUp.Visible != visible)
        {
            _levelUp.Visible = visible;
            _levelUp.ResetPress();
        }
    }

    /// <summary>Show/hide the pause panel without changing the paused state — used
    /// to overlay the settings menu while staying paused.</summary>
    public void SetPanelVisible(bool visible) => _panel.Visible = visible;
}
