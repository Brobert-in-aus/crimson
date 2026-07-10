using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Quest end panel (base quest_views quest_results / quest_failed): shown when
/// the quest timeline is cleared (completed) or the player dies (failed).
/// Completed: quest title, clear time, Next Quest / Quest Menu / Main Menu.
/// Failed: Retry / Quest Menu / Main Menu. No highscores in quest mode.
/// Uses the shared menu anchor plane.
/// </summary>
public sealed partial class QuestResultPanel : Node3D
{
    private Label3D _banner = null!;
    private Label3D _questTitle = null!;
    private Label3D _timeLabel = null!;
    private VrButton _primary = null!; // Next Quest (completed) / Retry (failed)
    private VrButton _questMenu = null!;
    private VrButton _mainMenu = null!;
    private bool _completed;

    public bool Active { get; private set; }

    /// <summary>Start the next quest (completed + a next quest exists).</summary>
    public event Action? OnNext;
    /// <summary>Retry the same quest (failed).</summary>
    public event Action? OnRetry;
    public event Action? OnQuestMenu;
    public event Action? OnMainMenu;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        _banner = MakeLabel(s, y: s * 0.40f, fontSize: 170, new Color(0.95f, 0.85f, 0.6f));
        _questTitle = MakeLabel(s, y: s * 0.26f, fontSize: 120, new Color(0.9f, 0.9f, 0.95f));
        _timeLabel = MakeLabel(s, y: s * 0.15f, fontSize: 100, new Color(0.8f, 0.8f, 0.88f));

        float w = s * 0.5f;
        float h = s * 0.13f;
        float pitch = h + s * 0.035f;
        float y0 = s * 0.0f;
        _primary = MakeButton(w, h, y0, () => { if (_completed) { OnNext?.Invoke(); } else { OnRetry?.Invoke(); } });
        _questMenu = MakeButton(w, h, y0 - pitch, () => OnQuestMenu?.Invoke());
        _mainMenu = MakeButton(w, h, y0 - pitch * 2.0f, () => OnMainMenu?.Invoke(), new Color(0.5f, 0.55f, 0.66f));
        _questMenu.SetText("Quest Menu");
        _mainMenu.SetText("Main Menu");

        Visible = false;
    }

    private Label3D MakeLabel(float s, float y, int fontSize, Color color)
    {
        var l = new Label3D
        {
            Text = string.Empty,
            FontSize = fontSize,
            PixelSize = s / 1100.0f,
            Modulate = color,
            OutlineSize = 26,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.0f),
        };
        AddChild(l);
        return l;
    }

    private VrButton MakeButton(float w, float h, float y, Action onPress, Color? color = null)
    {
        var b = new VrButton();
        AddChild(b);
        b.Build(w, h, string.Empty, color ?? new Color(0.55f, 0.3f, 0.28f));
        b.Position = new Vector3(0.0f, y, 0.0f);
        b.OnPress += onPress;
        return b;
    }

    /// <summary>Show the panel. elapsedMs = sim time at quest end; hasNext =
    /// a next quest exists (completed panels without one hide the button).</summary>
    public void Show(bool completed, string questTitle, long elapsedMs, bool hasNext)
    {
        _completed = completed;
        Active = true;
        Visible = true;
        _banner.Text = completed ? "Quest Completed!" : "Quest Failed";
        _banner.Modulate = completed ? new Color(0.7f, 0.95f, 0.6f) : new Color(0.95f, 0.5f, 0.4f);
        _questTitle.Text = questTitle;
        long totalSeconds = elapsedMs / 1000;
        _timeLabel.Text = $"Time  {totalSeconds / 60:D2}:{totalSeconds % 60:D2}";
        _primary.SetText(completed ? "Next Quest" : "Retry");
        _primary.Visible = !completed || hasNext;
        _primary.ResetPress();
        _questMenu.ResetPress();
        _mainMenu.ResetPress();
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
        if (_primary.Visible)
        {
            _primary.PollPoke(probes);
        }
        _questMenu.PollPoke(probes);
        _mainMenu.PollPoke(probes);
    }
}
