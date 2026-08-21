using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The Play Game mode-select panel (base panels/play_game.py): poked open from
/// the main menu's PLAY GAME item. Mode buttons in the native order — Quests,
/// Rush, Survival, Typ-o-Shooter, Tutorial — plus Back. Offline player count
/// stays fixed at 1; Multiplayer opens a
/// separate direct-LAN submenu. Shares the main menu's anchor transform so all menu planes
/// coincide (no cross-plane poke carry).
/// </summary>
public sealed partial class PlayGameMenu : Node3D
{
    private VrButton _quests = null!;
    private VrButton _rush = null!;
    private VrButton _survival = null!;
    private VrButton _typo = null!;
    private VrButton _tutorial = null!;
    private VrButton _multiplayer = null!;
    private VrButton _back = null!;
    private float _topY;
    private float _pitch;
    private bool _tutorialRecommended;

    public bool IsOpen { get; private set; }

    public event Action? OnQuests;
    public event Action? OnRush;
    public event Action? OnSurvival;
    public event Action? OnTypo;
    public event Action? OnTutorial;
    public event Action? OnMultiplayer;
    public event Action? OnBack;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        // Shared menu anchor (keep in sync with MainMenu/Options — see the
        // one-plane note there).
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        // Classic panel backdrop + the PLAY GAME itemTexts title art.
        ClassicPanel.Build(this, s * 1.0f, s * 1.34f, z: -0.012f);
        ClassicTitle.BuildRow(this, s * 0.5f, ClassicTitle.RowPlayGame, y: s * 0.49f);

        // Native _mode_entries order: Quests, Rush, Survival.
        float w = s * 0.55f;
        float h = s * 0.115f;
        _pitch = h + s * 0.03f;
        _topY = s * 0.32f;
        float y = _topY;
        _quests = MakeButton("Quests", w, h, y, () => OnQuests?.Invoke()); y -= _pitch;
        _rush = MakeButton("Rush", w, h, y, () => OnRush?.Invoke()); y -= _pitch;
        _survival = MakeButton("Survival", w, h, y, () => OnSurvival?.Invoke()); y -= _pitch;
        _typo = MakeButton("Typ'o'Shooter", w, h, y, () => OnTypo?.Invoke()); y -= _pitch;
        _tutorial = MakeButton("Tutorial", w, h, y, () => OnTutorial?.Invoke()); y -= _pitch;
        _multiplayer = MakeButton("Multiplayer", w, h, y, () => OnMultiplayer?.Invoke()); y -= _pitch;
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

    public void Open(bool? tutorialRecommended = null)
    {
        if (tutorialRecommended.HasValue)
        {
            SetTutorialRecommended(tutorialRecommended.Value);
        }
        IsOpen = true;
        Visible = true;
        _quests.ResetPress();
        _rush.ResetPress();
        _survival.ResetPress();
        _typo.ResetPress();
        _tutorial.ResetPress();
        _multiplayer.ResetPress();
        _back.ResetPress();
    }

    public void SetTutorialRecommended(bool recommended)
    {
        _tutorialRecommended = recommended;
        _tutorial.SetText(recommended ? "Tutorial - Start Here" : "Tutorial");
        VrButton[] order = recommended
            ? new[] { _tutorial, _quests, _rush, _survival, _typo, _multiplayer }
            : new[] { _quests, _rush, _survival, _typo, _tutorial, _multiplayer };
        for (int i = 0; i < order.Length; i++)
        {
            order[i].Position = new Vector3(0.0f, _topY - i * _pitch, 0.0f);
        }
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
        _typo.PollPoke(probes);
        _tutorial.PollPoke(probes);
        _multiplayer.PollPoke(probes);
        _back.PollPoke(probes);
    }
}
