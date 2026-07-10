using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The Play Game mode-select panel (base panels/play_game.py): poked open from
/// the main menu's PLAY GAME item. Mode buttons in the native order — Quests,
/// Rush, Survival — plus Back. Typo'Shooter stays hidden (VR input design
/// pending) and Tutorial/Network are out of the VR scope; player count is
/// fixed at 1. Shares the main menu's anchor transform so all menu planes
/// coincide (no cross-plane poke carry).
/// </summary>
public sealed partial class PlayGameMenu : Node3D
{
    private VrButton _quests = null!;
    private VrButton _rush = null!;
    private VrButton _survival = null!;
    private VrButton _back = null!;

    public bool IsOpen { get; private set; }

    public event Action? OnQuests;
    public event Action? OnRush;
    public event Action? OnSurvival;
    public event Action? OnBack;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        // Shared menu anchor (keep in sync with MainMenu/Options — see the
        // one-plane note there).
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        // Classic panel backdrop + the PLAY GAME itemTexts title art.
        ClassicPanel.Build(this, s * 1.0f, s * 1.05f, z: -0.012f);
        ClassicTitle.BuildRow(this, s * 0.5f, ClassicTitle.RowPlayGame, y: s * 0.42f);

        // Native _mode_entries order: Quests, Rush, Survival.
        float w = s * 0.55f;
        float h = s * 0.13f;
        float pitch = h + s * 0.04f;
        float y = s * 0.24f;
        _quests = MakeButton("Quests", w, h, y, () => OnQuests?.Invoke()); y -= pitch;
        _rush = MakeButton("Rush", w, h, y, () => OnRush?.Invoke()); y -= pitch;
        _survival = MakeButton("Survival", w, h, y, () => OnSurvival?.Invoke()); y -= pitch;
        _back = MakeButton("Back", w * 0.55f, h, y - s * 0.03f, () => OnBack?.Invoke());

        Visible = false;
    }

    private VrButton MakeButton(string text, float w, float h, float y, Action onPress)
    {
        var b = new VrButton();
        AddChild(b);
        b.BuildClassic(w, h, text);
        b.Position = new Vector3(0.0f, y, 0.0f);
        b.OnPress += onPress;
        return b;
    }

    public void Open()
    {
        IsOpen = true;
        Visible = true;
        _quests.ResetPress();
        _rush.ResetPress();
        _survival.ResetPress();
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
        _quests.PollPoke(probes);
        _rush.PollPoke(probes);
        _survival.PollPoke(probes);
        _back.PollPoke(probes);
    }
}
