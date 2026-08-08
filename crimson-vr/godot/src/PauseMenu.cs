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
    private Label3D _levelUpBadge = null!;
    private Node3D _edgeRoot = null!;

    /// <summary>The pause/level-up poke buttons, held in their own node so Main
    /// can mount them on the control rectangle (within reach) while the pause
    /// PANEL stays with the other menus. Reparent right after Build.</summary>
    public Node3D EdgeRoot => _edgeRoot;

    /// <summary>The two repositionable action buttons, exposed so Main can hang
    /// UiEditable handles on them. Size is needed for the corner footprint.</summary>
    public Node3D ToggleButton => _toggle;
    public Node3D LevelUpButton => _levelUp;
    public float ButtonWidth { get; private set; }
    public float ButtonHeight { get; private set; }

    /// <summary>Show/hide the menu AND its detached edge buttons together — once
    /// EdgeRoot is reparented it no longer inherits this node's visibility.</summary>
    public void SetMenuVisible(bool visible)
    {
        Visible = visible;
        _edgeRoot.Visible = visible;
    }
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

        // Pause + Level Up live on the LEFT VERTICAL FACE of an imaginary cube
        // whose bottom face is the VISIBLE floor square (the floor extends
        // FloorMarginScale past the playable zone — using the playable edge put
        // the buttons INSIDE the table, in-headset fail). Mounted on the
        // OUTSIDE: yaw -90 (like the checklist) points the fronts inward (-x)
        // so they're readable from the seat; the node sits one proud-depth
        // outside the edge plane so the face — even fully depressed — never
        // crosses into the cube. Stacked vertically near the player's end.
        //
        // These two are POKE targets, so they follow the CONTROL rectangle, not
        // the playfield: once the board scaled up and tilted away they would
        // have been metres out of arm's reach mounted to it. Main reparents
        // EdgeRoot onto the control rect after Build; the offsets below are
        // unchanged because the control rect is the same reference square the
        // old table was.
        float edge = s * 0.5f * Diorama.FloorMarginScale;
        float proud = s * 0.03f;
        _edgeRoot = new Node3D { Name = "PauseEdgeButtons" };
        AddChild(_edgeRoot);
        ButtonWidth = s * 0.18f;
        ButtonHeight = s * 0.12f;
        _toggle = new VrButton();
        _edgeRoot.AddChild(_toggle);
        _toggle.Build(s * 0.18f, s * 0.12f, "Pause", new Color(0.6f, 0.6f, 0.66f), proud: proud, plate: true);
        _toggle.Position = new Vector3(edge + proud, s * 0.16f, -s * 0.32f);
        _toggle.RotationDegrees = new Vector3(0.0f, -90.0f, 0.0f);
        _toggle.OnPress += TogglePause;

        // Level-up button above the pause toggle on the same face. Shown only
        // while a perk pick is pending; poking it opens the perk menu (rather
        // than the cards auto-appearing).
        _levelUp = new VrButton();
        _edgeRoot.AddChild(_levelUp);
        _levelUp.Build(s * 0.18f, s * 0.12f, "Level Up!", new Color(0.9f, 0.8f, 0.35f), proud: proud, plate: true);
        _levelUp.Position = new Vector3(edge + proud, s * 0.34f, -s * 0.32f);
        _levelUp.RotationDegrees = new Vector3(0.0f, -90.0f, 0.0f);
        _levelUp.OnPress += () => OnLevelUp?.Invoke();
        _levelUp.Visible = false;

        // Accumulated level-up counter ("xN" for N>1), beside the button along
        // the face (toward the arena centre = the viewer's right here) so it
        // never overlaps the label (the old in-plane offset drew the X2 across
        // the button text — in-headset fail).
        _levelUpBadge = new Label3D
        {
            Text = string.Empty,
            FontSize = 130,
            PixelSize = s / 650.0f,
            Modulate = new Color(1.0f, 0.9f, 0.3f),
            OutlineSize = 28,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            // 0.22s along the face: far enough that a wide "x12" never reaches
            // back over the button label (0.16s clipped the button edge).
            Position = new Vector3(edge + s * 0.01f, s * 0.34f, -s * 0.32f + s * 0.22f),
            RotationDegrees = new Vector3(0.0f, -90.0f, 0.0f),
            Visible = false,
        };
        _edgeRoot.AddChild(_levelUpBadge);

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

    /// <summary>Show/hide the level-up button (shown while a perk pick is pending)
    /// and its accumulated-count badge ("xN" for more than one pending pick).</summary>
    public void SetLevelUp(bool visible, int count)
    {
        if (_levelUp.Visible != visible)
        {
            _levelUp.Visible = visible;
            _levelUp.ResetPress();
        }
        _levelUpBadge.Visible = visible && count > 1;
        _levelUpBadge.Text = count > 1 ? $"x{count}" : string.Empty;
    }

    /// <summary>Show/hide the pause panel without changing the paused state — used
    /// to overlay the settings menu while staying paused.</summary>
    public void SetPanelVisible(bool visible) => _panel.Visible = visible;
}
