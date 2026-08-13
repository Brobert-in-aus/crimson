using System;
using System.Collections.Generic;
using Godot;

namespace CrimsonVR;

/// <summary>
/// VR presentation for the native nine-stage tutorial. The simulation owns all
/// stage transitions and scripted spawns; this panel only translates its prompt
/// and hint indices into hand/controller language and provides the native Skip,
/// Play a game, and Repeat tutorial exits.
/// </summary>
public sealed partial class TutorialPanel : Node3D
{
    private static readonly string[] Prompts =
    {
        "Welcome! This tutorial teaches Crimsonland in VR.",
        "Move: hold a pinch or trigger with your movement hand and point where you want to go.",
        "Walk over the bonuses to pick them up.",
        "Keep moving, then pinch or hold trigger with your aim hand to shoot.",
        "Move your aim hand to point the spread ring at the monsters.",
        "Practice: clear each wave and collect its dropped power-up to continue.",
        "Perks: poke Level Up, select a perk card, then confirm your selection.",
        "Perks grant passive abilities that help you survive.",
        "Great! You are ready to play Crimsonland in VR.",
    };

    private static readonly string[] Hints =
    {
        "Speed increases movement for a limited time. Walk over it to continue.",
        "Weapon Power Up replaces your weapon. Walk over it to continue.",
        "Double Experience doubles XP. Walk over it to continue.",
        "Nuke damages every monster. Walk over it to continue.",
        "Reflex Boost slows the action. Walk over it to continue.",
        string.Empty,
        string.Empty,
    };

    private SmallFontLabel? _prompt;
    private SmallFontLabel? _hint;
    private Label3D? _fallback;
    private VrButton _skip = null!;
    private VrButton _play = null!;
    private VrButton _repeat = null!;
    private int _stage = -1;

    public bool Active { get; private set; }
    public event Action? OnSkip;
    public event Action? OnPlay;
    public event Action? OnRepeat;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        // Keep the tutorial on the close, reachable menu root, but use it as a
        // sidecar instead of a centred overlay. ArenaRoot remains aligned with
        // the hand/control surface in both Cabinet and Tabletop modes, while the
        // visible playfield can move and scale independently. Centring this panel
        // therefore put it directly in the sightline to either playfield.
        //
        // At x=1.35s the panel's inner edge is 0.675s from centre, beyond the
        // 0.5s tabletop arena edge with a clear gap. Cabinet's more distant board
        // projects narrower at this close UI depth, so it clears that mode too.
        Position = new Vector3(s * 1.35f, s * 0.82f, s * 0.27f);
        RotationDegrees = new Vector3(0.0f, 180.0f, 0.0f);
        ClassicPanel.Build(this, s * 1.35f, s * 0.72f, z: -0.012f);

        if (SmallFont.Shared() is { } font)
        {
            _prompt = MakeText(font, s / 550.0f, new Vector3(0.0f, s * 0.20f, 0.0f), Colors.White);
            _hint = MakeText(font, s / 590.0f, new Vector3(0.0f, s * 0.03f, 0.0f), new Color(0.65f, 0.82f, 1.0f));
        }
        else
        {
            _fallback = new Label3D
            {
                FontSize = 66,
                PixelSize = s / 1150.0f,
                Position = new Vector3(0.0f, s * 0.10f, 0.0f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            AddChild(_fallback);
        }

        _skip = MakeButton(s * 0.36f, s * 0.11f, new Vector3(0.0f, -s * 0.23f, 0.0f), "Skip Tutorial", () => OnSkip?.Invoke());
        _play = MakeButton(s * 0.42f, s * 0.11f, new Vector3(-s * 0.25f, -s * 0.23f, 0.0f), "Play a Game", () => OnPlay?.Invoke());
        _repeat = MakeButton(s * 0.42f, s * 0.11f, new Vector3(s * 0.25f, -s * 0.23f, 0.0f), "Repeat", () => OnRepeat?.Invoke());
        Visible = false;
    }

    private SmallFontLabel MakeText(SmallFont font, float px, Vector3 pos, Color color)
    {
        var label = new SmallFontLabel();
        label.Build(font, px, color, center: true);
        label.Position = pos;
        AddChild(label);
        return label;
    }

    private VrButton MakeButton(float w, float h, Vector3 pos, string text, Action action)
    {
        var button = new VrButton();
        AddChild(button);
        button.BuildClassic(w, h, text);
        button.Position = pos;
        button.OnPress += action;
        return button;
    }

    public void SetActive(bool active)
    {
        Active = active;
        Visible = active;
        _stage = -1;
        if (active)
        {
            _skip.ResetPress();
            _play.ResetPress();
            _repeat.ResetPress();
        }
    }

    public void Update(in Sim.TickResult result)
    {
        if (!Active)
        {
            return;
        }
        int stage = Mathf.Clamp(result.TutorialStageIndex, -1, Prompts.Length - 1);
        string prompt = stage >= 0 ? Prompts[stage] : string.Empty;
        string hint = result.TutorialHintIndex >= 0 && result.TutorialHintIndex < Hints.Length
            ? Hints[result.TutorialHintIndex]
            : string.Empty;
        float promptAlpha = Mathf.Clamp(result.TutorialPromptAlpha, 0.0f, 1.0f);
        float hintAlpha = Mathf.Clamp(result.TutorialHintAlpha, 0.0f, 1.0f);
        _prompt?.SetText(Wrap(prompt, 64));
        _hint?.SetText(Wrap(hint, 68));
        _prompt?.SetColor(new Color(1, 1, 1, promptAlpha));
        _hint?.SetColor(new Color(0.65f, 0.82f, 1.0f, hintAlpha));
        if (_fallback != null) _fallback.Text = prompt + (string.IsNullOrEmpty(hint) ? string.Empty : "\n\n" + hint);

        bool complete = stage == 8;
        _skip.Visible = !complete && stage >= 0;
        _play.Visible = complete;
        _repeat.Visible = complete;
        if (stage != _stage)
        {
            _stage = stage;
            _skip.ResetPress();
            _play.ResetPress();
            _repeat.ResetPress();
        }
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active) return;
        if (_skip.Visible) _skip.PollPoke(probes);
        if (_play.Visible) _play.PollPoke(probes);
        if (_repeat.Visible) _repeat.PollPoke(probes);
    }

    private static string Wrap(string text, int width)
    {
        if (text.Length <= width) return text;
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return string.Join('\n', lines);
    }
}
