using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The Statistics screen (base panels/stats.py, VR-sized subset): lifetime
/// per-mode aggregates (runs, playtime, kills, accuracy, best score) plus the
/// local top-10 high-score tables for Survival and Rush — the high-scores
/// browser folded in. Opened from the main menu's STATISTICS item. Classic
/// panel/backdrop/small-font theming; shares the menu anchor plane.
/// </summary>
public sealed partial class StatsMenu : Node3D
{
    private UserSettings _settings = null!;
    private SmallFontLabel? _statsText;
    private SmallFontLabel? _survivalScores;
    private SmallFontLabel? _rushScores;
    private Label3D? _fallbackText;
    private VrButton _back = null!;

    public bool IsOpen { get; private set; }

    public event Action? OnBack;

    public void Build(float arenaSideMeters, UserSettings settings)
    {
        _settings = settings;
        float s = arenaSideMeters;
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        ClassicPanel.Build(this, s * 1.5f, s * 1.15f, z: -0.012f);
        ClassicTitle.BuildRow(this, s * 0.5f, ClassicTitle.RowStatistics, y: s * 0.47f);

        SmallFont? font = SmallFont.Shared();
        if (font != null)
        {
            float px = s / 620.0f; // ~16px glyphs sized for the panel
            _statsText = MakeText(font, px, new Vector3(0.0f, s * 0.24f, 0.0f), center: true);
            _survivalScores = MakeText(font, px, new Vector3(-s * 0.36f, -s * 0.13f, 0.0f), center: true);
            _rushScores = MakeText(font, px, new Vector3(s * 0.36f, -s * 0.13f, 0.0f), center: true);
        }
        else
        {
            _fallbackText = new Label3D
            {
                FontSize = 80,
                PixelSize = s / 1300.0f,
                Modulate = new Color(0.9f, 0.9f, 0.95f),
                OutlineSize = 18,
                OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
                Position = new Vector3(0.0f, s * 0.05f, 0.0f),
            };
            AddChild(_fallbackText);
        }

        _back = new VrButton();
        AddChild(_back);
        _back.BuildClassic(s * 0.3f, s * 0.11f, "Back");
        _back.Position = new Vector3(0.0f, -s * 0.48f, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    private SmallFontLabel MakeText(SmallFont font, float pixelSize, Vector3 pos, bool center)
    {
        var t = new SmallFontLabel();
        t.Build(font, pixelSize, new Color(1.0f, 1.0f, 1.0f, 0.85f), center);
        t.Position = pos;
        AddChild(t);
        return t;
    }

    public void Open()
    {
        IsOpen = true;
        Visible = true;
        Refresh();
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
        _back.PollPoke(probes);
    }

    private void Refresh()
    {
        string stats = BuildStatsBlock();
        if (_statsText != null)
        {
            _statsText.SetText(stats);
            _survivalScores!.SetText(BuildScoreTable("High Scores - Survival", _settings.Highscores));
            _rushScores!.SetText(BuildScoreTable("High Scores - Rush", _settings.RushHighscores));
        }
        else if (_fallbackText != null)
        {
            _fallbackText.Text = stats;
        }
    }

    private string BuildStatsBlock()
    {
        var sb = new StringBuilder();
        AppendMode(sb, "Survival", 1);
        AppendMode(sb, "Rush", 2);
        AppendMode(sb, "Quests", 3);
        return sb.ToString();
    }

    private void AppendMode(StringBuilder sb, string name, int mode)
    {
        ModeStats s = _settings.StatsFor(mode);
        long mins = s.PlayMs / 60000;
        float acc = s.Shots > 0 ? 100.0f * s.Hits / s.Shots : 0.0f;
        sb.Append(name).Append(":  ")
          .Append(s.Runs).Append(" games   ")
          .Append(mins / 60).Append('h').Append((mins % 60).ToString("D2")).Append("m   ")
          .Append(s.Kills).Append(" kills   ")
          .Append(acc.ToString("F0")).Append("% acc");
        if (mode != 3)
        {
            sb.Append("   best ").Append(s.BestScore);
        }
        sb.Append('\n');
    }

    private static string BuildScoreTable(string title, List<HighscoreEntry> list)
    {
        var sb = new StringBuilder();
        sb.Append(title).Append('\n');
        if (list.Count == 0)
        {
            sb.Append("- no scores yet -");
            return sb.ToString();
        }
        for (int i = 0; i < list.Count && i < 10; i++)
        {
            sb.Append(i + 1).Append(". ").Append(list[i].Name).Append("  ").Append(list[i].Score);
            if (i < list.Count - 1 && i < 9)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
