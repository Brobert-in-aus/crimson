using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Quest selection (base quest_views/quests_menu.py): 5 stage tabs across the
/// top, the selected stage's 10 quests below in two columns of five. Quests up
/// to the persisted unlock index are playable; later ones show dimmed with a
/// lock marker (native gating: global_index &lt;= quest_unlock_index). Titles
/// come from the baked manifest's quests table. Hardcore toggle deferred.
/// </summary>
public sealed partial class QuestSelectMenu : Node3D
{
    public readonly record struct QuestInfo(int Key, int Stage, int Index, string Title, long TimeLimitMs);

    private readonly List<QuestInfo> _quests = new();
    private readonly VrButton[] _stageButtons = new VrButton[5];
    private readonly VrButton[] _rows = new VrButton[10];
    private readonly bool[] _rowLocked = new bool[10];
    private VrButton _back = null!;
    private int _stage = 1;
    private int _unlockIndex;
    private float _side;

    public bool IsOpen { get; private set; }

    /// <summary>Poked a playable quest: (quest_level_key, title).</summary>
    public event Action<int, string>? OnStart;
    public event Action? OnBack;

    public void Build(float arenaSideMeters)
    {
        _side = arenaSideMeters;
        float s = arenaSideMeters;
        LoadQuestTable();

        // Shared menu anchor (one plane for all menus).
        Position = new Vector3(0.0f, s * 0.85f, s * 0.25f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        // Classic backdrop + the ui_textQuest title banner.
        ClassicPanel.Build(this, s * 1.5f, s * 1.15f, z: -0.012f);
        ClassicTitle.BuildBanner(this, "ui_textQuest.png", s * 0.55f, y: s * 0.47f);

        // Stage tabs: the native ui_num1..5 stage-icon numerals (selected icon
        // full-scale/bright, others dimmed at 0.8 — quests_menu.py).
        float tabW = s * 0.13f;
        float tabPitch = tabW + s * 0.045f;
        float tabX = -tabPitch * 2.0f;
        for (int i = 0; i < 5; i++)
        {
            int stage = i + 1;
            var b = new VrButton();
            AddChild(b);
            string iconPath = $"res://assets/sprites/ui_num{stage}.png";
            if (ResourceLoader.Exists(iconPath)
                && ResourceLoader.Load<Texture2D>(iconPath) is Texture2D icon)
            {
                b.BuildIcon(tabW, icon);
            }
            else
            {
                b.Build(tabW, tabW, stage.ToString(), new Color(0.4f, 0.42f, 0.5f));
            }
            b.Position = new Vector3(tabX + i * tabPitch, s * 0.32f, 0.0f);
            b.OnPress += () => SelectStage(stage);
            _stageButtons[i] = b;
        }

        // Quest rows: two columns x five (10 per stage), compact.
        float rowW = s * 0.62f;
        float rowH = s * 0.105f;
        float rowPitch = rowH + s * 0.022f;
        float colX = rowW * 0.5f + s * 0.03f;
        float topY = s * 0.16f;
        for (int i = 0; i < 10; i++)
        {
            var b = new VrButton();
            AddChild(b);
            b.BuildClassic(rowW, rowH, string.Empty);
            int col = i / 5;
            int row = i % 5;
            b.Position = new Vector3(col == 0 ? -colX : colX, topY - row * rowPitch, 0.0f);
            int idx = i;
            b.OnPress += () => PressRow(idx);
            _rows[i] = b;
        }

        _back = new VrButton();
        AddChild(_back);
        _back.BuildClassic(s * 0.3f, s * 0.11f, "Back");
        _back.Position = new Vector3(0.0f, topY - 5.0f * rowPitch - s * 0.02f, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    public void Open(int unlockIndex)
    {
        _unlockIndex = unlockIndex;
        IsOpen = true;
        Visible = true;
        // Open on the stage containing the first locked quest (the frontier),
        // like returning players expect; clamp when everything is unlocked.
        int frontier = Mathf.Clamp(unlockIndex, 0, 49);
        _stage = frontier / 10 + 1;
        RefreshStage();
        foreach (VrButton b in _rows)
        {
            b.ResetPress();
        }
        foreach (VrButton b in _stageButtons)
        {
            b.ResetPress();
        }
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
        foreach (VrButton b in _stageButtons)
        {
            b.PollPoke(probes);
        }
        foreach (VrButton b in _rows)
        {
            b.PollPoke(probes);
        }
        _back.PollPoke(probes);
    }

    private void SelectStage(int stage)
    {
        _stage = stage;
        RefreshStage();
    }

    private void PressRow(int idx)
    {
        if (_rowLocked[idx])
        {
            return;
        }
        QuestInfo? q = QuestAt(_stage, idx + 1);
        if (q is { } quest)
        {
            OnStart?.Invoke(quest.Key, $"{quest.Stage}.{quest.Index} {quest.Title}");
        }
    }

    /// <summary>Quest time limit in ms for a quest_level_key (0 if unknown),
    /// for the HUD timer.</summary>
    public long TimeLimitFor(int key)
    {
        foreach (QuestInfo q in _quests)
        {
            if (q.Key == key)
            {
                return q.TimeLimitMs;
            }
        }
        return 0;
    }

    /// <summary>Display title ("2.3 Quest Name") for a quest_level_key, for
    /// panels that need it outside the menu (e.g. Next Quest chaining).</summary>
    public string TitleFor(int key)
    {
        foreach (QuestInfo q in _quests)
        {
            if (q.Key == key)
            {
                return $"{q.Stage}.{q.Index} {q.Title}";
            }
        }
        return $"{key / 100}.{key % 100}";
    }

    private QuestInfo? QuestAt(int stage, int index)
    {
        foreach (QuestInfo q in _quests)
        {
            if (q.Stage == stage && q.Index == index)
            {
                return q;
            }
        }
        return null;
    }

    private void RefreshStage()
    {
        for (int i = 0; i < 5; i++)
        {
            // Native: the selected stage icon draws full-scale and bright; the
            // rest at 0.8 scale, dimmed (quests_menu.py stage icon pass).
            bool selected = i + 1 == _stage;
            _stageButtons[i].SetColor(selected ? Colors.White : new Color(0.55f, 0.55f, 0.6f));
            _stageButtons[i].Scale = Vector3.One * (selected ? 1.0f : 0.8f);
        }
        for (int i = 0; i < 10; i++)
        {
            QuestInfo? q = QuestAt(_stage, i + 1);
            if (q is not { } quest)
            {
                _rows[i].Visible = false;
                _rowLocked[i] = true;
                continue;
            }
            _rows[i].Visible = true;
            // Native gating: quests with global_index <= unlock index are
            // playable (index == unlock is the frontier quest).
            int globalIndex = (quest.Stage - 1) * 10 + (quest.Index - 1);
            bool locked = globalIndex > _unlockIndex;
            _rowLocked[i] = locked;
            _rows[i].SetText(locked
                ? $"{quest.Stage}.{quest.Index}  - locked -"
                : $"{quest.Stage}.{quest.Index}  {quest.Title}");
            // Classic skin: the colour tints the plate art.
            _rows[i].SetColor(locked ? new Color(0.45f, 0.45f, 0.5f) : Colors.White);
        }
    }

    private void LoadQuestTable()
    {
        _quests.Clear();
        string path = "res://assets/sprites/sprite_manifest.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            return;
        }
        using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        try
        {
            using var doc = JsonDocument.Parse(f.GetAsText());
            if (doc.RootElement.TryGetProperty("quests", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement q in arr.EnumerateArray())
                {
                    _quests.Add(new QuestInfo(
                        q.GetProperty("key").GetInt32(),
                        q.GetProperty("stage").GetInt32(),
                        q.GetProperty("index").GetInt32(),
                        q.GetProperty("title").GetString() ?? string.Empty,
                        q.TryGetProperty("time_limit_ms", out JsonElement tl) ? tl.GetInt64() : 0));
                }
            }
        }
        catch (JsonException e)
        {
            GD.PushWarning($"CrimsonVR: bad quests table in manifest: {e.Message}");
        }
    }
}
