using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Quest 5.10 "Show End Note" finale (base quest_views/end_note.py, native
/// game_update_victory_screen @ 0x00406350): a classic panel with the victory
/// text — "Congratulations!" after a casual clear, "Incredible!" after a
/// hardcore clear — and the mode shortcuts Survival / Rush / Typ'o'Shooter /
/// Main Menu. Typo stays disabled until the VR input design exists.
/// Uses the shared menu anchor plane.
/// </summary>
public sealed partial class EndNotePanel : Node3D
{
    private readonly System.Collections.Generic.List<SmallFontLabel> _lines = new();
    private Label3D? _fallbackBody;
    private VrButton _survival = null!;
    private VrButton _rush = null!;
    private VrButton _typo = null!;
    private VrButton _mainMenu = null!;
    private float _side;

    public bool Active { get; private set; }

    public event Action? OnSurvival;
    public event Action? OnRush;
    public event Action? OnMainMenu;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        _side = s;
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        ClassicPanel.Build(this, s * 1.4f, s * 1.2f, z: -0.012f);

        float w = s * 0.55f;
        float h = s * 0.12f;
        float pitch = h + s * 0.03f;
        float y0 = -s * 0.04f;
        _survival = MakeButton(w, h, y0, "Survival", () => OnSurvival?.Invoke());
        _rush = MakeButton(w, h, y0 - pitch, "Rush", () => OnRush?.Invoke());
        // Inert until a VR typing input design exists: dimmed, no press handler,
        // never polled.
        _typo = MakeButton(w, h, y0 - pitch * 2.0f, "Typ'o'Shooter", null);
        _typo.SetColor(new Color(0.45f, 0.45f, 0.5f));
        _mainMenu = MakeButton(w, h, y0 - pitch * 3.0f, "Main Menu", () => OnMainMenu?.Invoke());

        Visible = false;
    }

    private VrButton MakeButton(float w, float h, float y, string label, Action? onPress)
    {
        var b = new VrButton();
        AddChild(b);
        b.BuildClassic(w, h, label);
        b.Position = new Vector3(0.0f, y, 0.0f);
        if (onPress != null)
        {
            b.OnPress += onPress;
        }
        return b;
    }

    /// <summary>Show the end note. Text per end_note.py (the corrected
    /// non-preserve-bugs line variant).</summary>
    public void Show(bool hardcore)
    {
        Active = true;
        Visible = true;
        foreach (SmallFontLabel l in _lines)
        {
            l.QueueFree();
        }
        _lines.Clear();
        _fallbackBody?.QueueFree();
        _fallbackBody = null;

        string header = hardcore ? "Incredible!" : "Congratulations!";
        string[] body = hardcore
            ? new[]
            {
                "You've done the thing we all thought was",
                "virtually impossible. To reward your",
                "efforts a new weapon has been unlocked ",
                "for you: Splitter Gun.",
            }
            : new[]
            {
                "You've completed all the levels, but the battle",
                "isn't over yet! With all of the unlocked perks",
                "and weapons your Survival is just a bit easier.",
                "You can also replay the quests in Hardcore.",
                "As an additional reward for your victorious",
                "playing, a completely new and different game",
                "mode is unlocked for you: Typ'o'Shooter.",
            };
        const string signOff = "Good luck with your battles, trooper!";

        float s = _side;
        SmallFont? font = SmallFont.Shared();
        if (font != null)
        {
            float lineStep = s * 0.052f;
            float y = s * 0.47f;
            AddLine(font, header, s / 460.0f, new Color(1.0f, 1.0f, 1.0f, 0.8f), y);
            y -= lineStep * 1.6f;
            foreach (string line in body)
            {
                AddLine(font, line, s / 560.0f, new Color(1.0f, 1.0f, 1.0f, 0.5f), y);
                y -= lineStep;
            }
            y -= lineStep * 0.5f;
            AddLine(font, signOff, s / 560.0f, new Color(1.0f, 1.0f, 1.0f, 0.5f), y);
        }
        else
        {
            _fallbackBody = new Label3D
            {
                Text = header + "\n\n" + string.Join('\n', body) + "\n\n" + signOff,
                FontSize = 64,
                PixelSize = s / 1100.0f,
                Position = new Vector3(0.0f, s * 0.28f, 0.0f),
                RenderPriority = 63,
            };
            AddChild(_fallbackBody);
        }

        _survival.ResetPress();
        _rush.ResetPress();
        _mainMenu.ResetPress();
    }

    private void AddLine(SmallFont font, string text, float pixelSize, Color color, float y)
    {
        var l = new SmallFontLabel();
        l.Build(font, pixelSize, color, center: true);
        l.Position = new Vector3(0.0f, y, 0.0f);
        AddChild(l);
        l.SetText(text);
        _lines.Add(l);
    }

    public void Dismiss()
    {
        Active = false;
        Visible = false;
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active)
        {
            return;
        }
        _survival.PollPoke(probes);
        _rush.PollPoke(probes);
        _mainMenu.PollPoke(probes);
    }
}
