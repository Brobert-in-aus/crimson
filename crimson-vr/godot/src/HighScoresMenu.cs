using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace CrimsonVR;

/// <summary>Local counterpart of the base game's high-score browser. Online
/// scores are deliberately absent: the original service no longer exists.</summary>
public sealed partial class HighScoresMenu : Node3D
{
    private static readonly (int Id, string Name)[] Modes =
        { (3, "Quests"), (2, "Rush"), (1, "Survival"), (4, "Typ'o") };
    private static readonly string[] Dates = { "All time", "Month", "Week", "Day" };
    private UserSettings _settings = null!;
    private SmallFontLabel? _rows;
    private Label3D? _fallback;
    private VrButton _mode = null!, _date = null!, _playerFilter = null!, _name = null!;
    private VrButton _questPrev = null!, _questNext = null!, _pagePrev = null!, _pageNext = null!, _back = null!;
    private int _modeIndex, _dateIndex, _playerCount = 1, _nameIndex, _questKey = 101, _page;
    private string[] _names = { "All names" };
    public bool IsOpen { get; private set; }
    public event Action? OnBack;

    public void Build(float s, UserSettings settings)
    {
        _settings = settings;
        Position = new Vector3(0, s * .85f, s * .25f);
        RotationDegrees = new Vector3(-12, 180, 0);
        ClassicPanel.Build(this, s * 1.65f, s * 1.25f, -.012f);
        if (SmallFont.Shared() is { } font)
        {
            _rows = new SmallFontLabel(); AddChild(_rows);
            _rows.Build(font, s / 650f, new Color(.95f, .95f, 1), center: false);
            _rows.Position = new Vector3(-s * .68f, s * .16f, 0);
        }
        else
        {
            _fallback = new Label3D { FontSize = 58, PixelSize = s / 1250f,
                Position = new Vector3(0, s * .1f, 0), HorizontalAlignment = HorizontalAlignment.Center };
            AddChild(_fallback);
        }
        _mode = Button(s, -.57f, .47f, "Mode", () => { _modeIndex = (_modeIndex + 1) % Modes.Length; ResetPage(); });
        _date = Button(s, -.19f, .47f, "Date", () => { _dateIndex = (_dateIndex + 1) % Dates.Length; ResetPage(); });
        _playerFilter = Button(s, .19f, .47f, "Players", () => { _playerCount = _playerCount % 4 + 1; ResetPage(); });
        _name = Button(s, .57f, .47f, "Name", () => { _nameIndex = (_nameIndex + 1) % _names.Length; ResetPage(); });
        _questPrev = Button(s, -.57f, -.39f, "< Quest", () => { _questKey = PrevQuest(_questKey); ResetPage(); });
        _questNext = Button(s, -.19f, -.39f, "Quest >", () => { _questKey = NextQuest(_questKey); ResetPage(); });
        _pagePrev = Button(s, .19f, -.39f, "< Page", () => { if (_page > 0) _page--; Refresh(); });
        _pageNext = Button(s, .57f, -.39f, "Page >", () => { _page++; Refresh(); });
        _back = Button(s, 0, -.54f, "Back", () => OnBack?.Invoke());
        Visible = false;
    }

    private VrButton Button(float s, float x, float y, string text, Action press)
    {
        var b = new VrButton(); AddChild(b); b.BuildClassic(s * .31f, s * .1f, text);
        b.Position = new Vector3(x * s, y * s, 0); b.OnPress += press; return b;
    }

    public void Open(int gameMode = 0, int playerCount = 0)
    {
        IsOpen = Visible = true; _page = 0;
        if (gameMode != 0)
        {
            int found = Array.FindIndex(Modes, mode => mode.Id == gameMode);
            if (found >= 0) _modeIndex = found;
        }
        if (playerCount is >= 1 and <= 4) _playerCount = playerCount;
        _names = new[] { "All names" }.Concat(AllScores().Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)).ToArray();
        _nameIndex = Math.Min(_nameIndex, _names.Length - 1); ResetButtons(); Refresh();
    }
    public void Close() { IsOpen = Visible = false; }
    public void PollPoke(ReadOnlySpan<HandProbe> p)
    {
        if (!IsOpen) return;
        foreach (VrButton b in new[] { _mode, _date, _playerFilter, _name, _questPrev, _questNext, _pagePrev, _pageNext, _back })
            if (b.Visible) b.PollPoke(p);
    }
    private void ResetPage() { _page = 0; Refresh(); }
    private void ResetButtons() { foreach (VrButton b in new[] { _mode, _date, _playerFilter, _name, _questPrev, _questNext, _pagePrev, _pageNext, _back }) b.ResetPress(); }

    private void Refresh()
    {
        int mode = Modes[_modeIndex].Id;
        long cutoff = _dateIndex switch { 1 => DateTimeOffset.UtcNow.AddMonths(-1).ToUnixTimeSeconds(), 2 => DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds(), 3 => DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), _ => 0 };
        List<HighscoreEntry> list = _settings.HighscoresFor(mode).Where(e => e.PlayerCount == _playerCount)
            .Where(e => cutoff == 0 || e.RecordedAtUnix >= cutoff)
            .Where(e => _nameIndex == 0 || string.Equals(e.Name, _names[_nameIndex], StringComparison.OrdinalIgnoreCase))
            .Where(e => mode != 3 || e.QuestKey == _questKey).ToList();
        int pages = Math.Max(1, (list.Count + 9) / 10); _page = Math.Clamp(_page, 0, pages - 1);
        _mode.SetText("Mode: " + Modes[_modeIndex].Name); _date.SetText(Dates[_dateIndex]);
        _playerFilter.SetText($"Players: {_playerCount}"); _name.SetText(_names[_nameIndex]);
        bool quests = mode == 3; _questPrev.Visible = _questNext.Visible = quests;
        _pagePrev.Visible = _page > 0; _pageNext.Visible = _page + 1 < pages;
        var sb = new StringBuilder();
        sb.Append("HIGH SCORES - ").Append(Modes[_modeIndex].Name.ToUpperInvariant());
        if (quests) sb.Append("  ").Append(_questKey / 100).Append('.').Append(_questKey % 100);
        sb.Append("     PAGE ").Append(_page + 1).Append('/').Append(pages).Append("\n\n");
        if (list.Count == 0) sb.Append("No local scores match these filters.");
        for (int i = _page * 10; i < list.Count && i < (_page + 1) * 10; i++)
        {
            HighscoreEntry e = list[i]; float accuracy = e.Shots > 0 ? e.Hits * 100f / e.Shots : 0;
            string score = mode is 2 or 3 ? $"{e.Score / 1000f:F2}s" : e.Score.ToString();
            sb.Append(i + 1).Append(". ").Append(e.Name.PadRight(12)).Append(score.PadLeft(10))
              .Append("   ").Append(e.Kills).Append(" kills  ").Append(accuracy.ToString("F0")).Append("%\n");
        }
        _rows?.SetText(sb.ToString()); if (_fallback != null) _fallback.Text = sb.ToString();
    }
    private IEnumerable<HighscoreEntry> AllScores() => _settings.Highscores.Concat(_settings.RushHighscores).Concat(_settings.QuestHighscores).Concat(_settings.TypoHighscores);
    private static int PrevQuest(int key) => key % 100 > 1 ? key - 1 : key / 100 > 1 ? (key / 100 - 1) * 100 + 10 : 510;
    private static int NextQuest(int key) => key % 100 < 10 ? key + 1 : key / 100 < 5 ? (key / 100 + 1) * 100 + 1 : 101;
}
