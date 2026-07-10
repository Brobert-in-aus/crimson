using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

public sealed class HighscoreEntry
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
}

/// <summary>Lifetime per-mode aggregates for the Statistics screen, updated at
/// each run's end (the base game keeps these in the status blob).</summary>
public sealed class ModeStats
{
    public int Runs { get; set; }
    public long PlayMs { get; set; }
    public int Kills { get; set; }
    public int Shots { get; set; }
    public int Hits { get; set; }
    public int BestScore { get; set; }
}

/// <summary>
/// M4 slice 9: persisted user settings + highscores, stored in a Godot ConfigFile
/// under user:// (writable on desktop and Quest). Covers the MVP settings
/// (hand-swap, dead-zone), the first-run-seen flag, and the local highscore list.
/// Arena scale/height + calibration are their own later slices and aren't stored
/// yet. Load once at startup; save on any change.
/// </summary>
public sealed class UserSettings
{
    private const string ConfigPath = "user://crimsonvr.cfg";

    public bool HandSwap;
    public float DeadZone = VrInput.DefaultDeadZoneGameUnits;
    public bool FirstRunDone;
    public bool Debug;

    // Original Options settings (mirrors the base game). Volumes 0-10, graphics
    // detail 1-5, info-texts toggle — same scales as the desktop Options screen.
    public int SfxVolume = 10;
    public int MusicVolume = 10;
    public int GraphicsDetail = 5;
    public bool UiInfoTexts = true;

    // VR render quality: OpenXR render-target multiplier (supersampling) + MSAA
    // level (0 = off, 2, 4). The flat-sprite scene is cheap, so default high.
    public float RenderScale = 1.4f;
    public int Msaa = 4;

    // Quest unlock progression (base game_status quest_unlock_index): the
    // 0-based global index of the FIRST still-locked quest. 0 = only 1.1
    // playable; completing quest N (== the index) advances it to N+1.
    public int QuestUnlockIndex;

    // Per-mode local highscore tables: Survival keeps the original list (and
    // its legacy cfg key); Rush gets its own. Quests have no score table.
    public readonly List<HighscoreEntry> Highscores = new();
    public readonly List<HighscoreEntry> RushHighscores = new();

    // Lifetime stats per game mode (keys: mode id as string).
    public readonly Dictionary<string, ModeStats> Stats = new();
    // In-headset validation checklist results, item id -> 0 untested / 1 pass / 2 fail.
    public readonly Dictionary<string, int> Checklist = new();

    public void Load()
    {
        var cf = new ConfigFile();
        if (cf.Load(ConfigPath) != Error.Ok)
        {
            return; // no file yet -> defaults
        }
        HandSwap = cf.GetValue("input", "hand_swap", HandSwap).AsBool();
        DeadZone = cf.GetValue("input", "dead_zone", DeadZone).AsSingle();
        FirstRunDone = cf.GetValue("game", "first_run_done", FirstRunDone).AsBool();
        Debug = cf.GetValue("dev", "debug", Debug).AsBool();
        SfxVolume = cf.GetValue("audio", "sfx_volume", SfxVolume).AsInt32();
        MusicVolume = cf.GetValue("audio", "music_volume", MusicVolume).AsInt32();
        GraphicsDetail = cf.GetValue("video", "graphics_detail", GraphicsDetail).AsInt32();
        UiInfoTexts = cf.GetValue("game", "ui_info_texts", UiInfoTexts).AsBool();
        RenderScale = cf.GetValue("video", "render_scale", RenderScale).AsSingle();
        Msaa = cf.GetValue("video", "msaa", Msaa).AsInt32();

        QuestUnlockIndex = cf.GetValue("game", "quest_unlock_index", QuestUnlockIndex).AsInt32();

        LoadHighscoreList(cf, "highscores", Highscores);
        LoadHighscoreList(cf, "highscores_rush", RushHighscores);

        Stats.Clear();
        string st = cf.GetValue("game", "stats", string.Empty).AsString();
        if (!string.IsNullOrEmpty(st))
        {
            try
            {
                Dictionary<string, ModeStats>? map = JsonSerializer.Deserialize<Dictionary<string, ModeStats>>(st);
                if (map != null)
                {
                    foreach (KeyValuePair<string, ModeStats> kv in map)
                    {
                        Stats[kv.Key] = kv.Value;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        Checklist.Clear();
        string ck = cf.GetValue("dev", "checklist", string.Empty).AsString();
        if (!string.IsNullOrEmpty(ck))
        {
            try
            {
                Dictionary<string, int>? map = JsonSerializer.Deserialize<Dictionary<string, int>>(ck);
                if (map != null)
                {
                    foreach (KeyValuePair<string, int> kv in map)
                    {
                        Checklist[kv.Key] = kv.Value;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    public void Save()
    {
        var cf = new ConfigFile();
        cf.SetValue("input", "hand_swap", HandSwap);
        cf.SetValue("input", "dead_zone", DeadZone);
        cf.SetValue("game", "first_run_done", FirstRunDone);
        cf.SetValue("game", "quest_unlock_index", QuestUnlockIndex);
        cf.SetValue("game", "highscores", JsonSerializer.Serialize(Highscores));
        cf.SetValue("game", "highscores_rush", JsonSerializer.Serialize(RushHighscores));
        cf.SetValue("game", "stats", JsonSerializer.Serialize(Stats));
        cf.SetValue("game", "ui_info_texts", UiInfoTexts);
        cf.SetValue("audio", "sfx_volume", SfxVolume);
        cf.SetValue("audio", "music_volume", MusicVolume);
        cf.SetValue("video", "graphics_detail", GraphicsDetail);
        cf.SetValue("video", "render_scale", RenderScale);
        cf.SetValue("video", "msaa", Msaa);
        cf.SetValue("dev", "debug", Debug);
        cf.SetValue("dev", "checklist", JsonSerializer.Serialize(Checklist));
        cf.Save(ConfigPath);
    }

    /// <summary>The highscore table for a game mode (Sim GameModeId values:
    /// 1 survival, 2 rush). Quests intentionally fall back to the survival
    /// table only so callers never get null; quest flows skip highscores.</summary>
    public List<HighscoreEntry> HighscoresFor(int gameMode)
        => gameMode == 2 ? RushHighscores : Highscores;

    /// <summary>Add a highscore to the mode's table, kept sorted high-to-low
    /// and capped at 10.</summary>
    public void AddHighscore(string name, int score, int gameMode = 1)
    {
        List<HighscoreEntry> list = HighscoresFor(gameMode);
        list.Add(new HighscoreEntry { Name = name, Score = score });
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (list.Count > 10)
        {
            list.RemoveRange(10, list.Count - 10);
        }
        Save();
    }

    /// <summary>The lifetime stats bucket for a game mode (created on demand).</summary>
    public ModeStats StatsFor(int gameMode)
    {
        string key = gameMode.ToString();
        if (!Stats.TryGetValue(key, out ModeStats? s))
        {
            s = new ModeStats();
            Stats[key] = s;
        }
        return s;
    }

    /// <summary>Fold one finished run into the mode's lifetime stats.</summary>
    public void RecordRun(int gameMode, long playMs, int kills, int shots, int hits, int score)
    {
        ModeStats s = StatsFor(gameMode);
        s.Runs++;
        s.PlayMs += playMs;
        s.Kills += kills;
        s.Shots += shots;
        s.Hits += hits;
        if (score > s.BestScore)
        {
            s.BestScore = score;
        }
        Save();
    }

    private static void LoadHighscoreList(ConfigFile cf, string key, List<HighscoreEntry> into)
    {
        into.Clear();
        string blob = cf.GetValue("game", key, string.Empty).AsString();
        if (string.IsNullOrEmpty(blob))
        {
            return;
        }
        try
        {
            List<HighscoreEntry>? list = JsonSerializer.Deserialize<List<HighscoreEntry>>(blob);
            if (list != null)
            {
                into.AddRange(list);
            }
        }
        catch (JsonException)
        {
            // Corrupt highscore blob -> start fresh, keep the scalar settings.
        }
    }
}
