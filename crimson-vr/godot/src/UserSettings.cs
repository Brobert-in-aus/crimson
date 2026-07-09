using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

public sealed class HighscoreEntry
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
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
    public readonly List<HighscoreEntry> Highscores = new();
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

        Highscores.Clear();
        string hs = cf.GetValue("game", "highscores", string.Empty).AsString();
        if (!string.IsNullOrEmpty(hs))
        {
            try
            {
                List<HighscoreEntry>? list = JsonSerializer.Deserialize<List<HighscoreEntry>>(hs);
                if (list != null)
                {
                    Highscores.AddRange(list);
                }
            }
            catch (JsonException)
            {
                // Corrupt highscore blob -> start fresh, keep the scalar settings.
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
        cf.SetValue("game", "highscores", JsonSerializer.Serialize(Highscores));
        cf.SetValue("dev", "debug", Debug);
        cf.SetValue("dev", "checklist", JsonSerializer.Serialize(Checklist));
        cf.Save(ConfigPath);
    }

    /// <summary>Add a highscore, keep the list sorted high-to-low and capped.</summary>
    public void AddHighscore(string name, int score)
    {
        Highscores.Add(new HighscoreEntry { Name = name, Score = score });
        Highscores.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (Highscores.Count > 10)
        {
            Highscores.RemoveRange(10, Highscores.Count - 10);
        }
        Save();
    }
}
