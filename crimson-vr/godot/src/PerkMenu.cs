using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 2: the level-up perk pick as poke buttons (PLAN M4 UI model). When
/// the sim reports perks pending (snapshot header perk_pending_count /
/// perk_choices[]), the candidate perks float above the arena as cards the player
/// pokes to choose. The choice feeds back as CrimsonHostInput.perk_choice_index
/// (Main drives the pause via perk_menu_active).
///
/// A child of ArenaRoot, so the cards sit above the tabletop in arena-local space
/// and inherit its placement/scale/yaw. Card labels come from the baked
/// perk-id -> name table (sprite manifest). Layout/lean are first-pass; tune
/// in-headset.
/// </summary>
public sealed partial class PerkMenu : Node3D
{
    private readonly VrButton[] _cards = new VrButton[7];
    private int _count;
    private float _cardW;
    private float _cardGap;
    private readonly Dictionary<int, string> _perkNames = new();

    /// <summary>Chosen choice-index (0..count-1) when a card is poked; Main reads
    /// it into the next tick's perk_choice_index and clears it. -1 = nothing.</summary>
    public int Chosen = -1;

    private bool _opened; // cards revealed (via the level-up button)

    /// <summary>True while a perk pick is available (Main pauses the sim + shows the
    /// level-up button). The cards themselves only appear once <see cref="Open"/>.</summary>
    public bool Pending { get; private set; }

    /// <summary>True while the cards are shown and pollable (Pending AND opened).</summary>
    public bool Active => Pending && _opened;

    /// <summary>Reveal the cards (from the level-up button).</summary>
    public void Open() => _opened = true;

    public void Build(float arenaSideMeters)
    {
        LoadPerkNames();
        // Float above the arena centre facing the player. RecenterArena yaws the
        // arena so local +z is the far edge, so a 180 deg yaw faces the near
        // (player) side; a small back-lean tips the tops away from the player.
        Position = new Vector3(0.0f, arenaSideMeters * 0.8f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        _cardW = arenaSideMeters * 0.26f;
        _cardGap = arenaSideMeters * 0.05f;
        float cardH = arenaSideMeters * 0.34f;
        for (int i = 0; i < _cards.Length; i++)
        {
            var card = new VrButton();
            AddChild(card);
            card.Build(_cardW, cardH, string.Empty, new Color(0.9f, 0.82f, 0.4f));
            // Portrait cards: shrink + word-wrap the perk name to fit the card width
            // (the default height-based sizing made long names overflow neighbours).
            card.ConfigureLabel(arenaSideMeters * 0.00035f, _cardW * 0.85f);
            int idx = i;
            card.OnPress += () => Chosen = idx;
            card.Visible = false;
            _cards[i] = card;
        }
        Visible = false;
    }

    /// <summary>Refresh from the latest snapshot: show/lay out the candidate cards
    /// when perks are pending, else hide. Call each tick with the snapshot.</summary>
    public void Update(in SnapshotView snap)
    {
        int count = Mathf.Min((int)snap.Header.PerkChoiceCount, _cards.Length);
        Pending = snap.Header.PerkPendingCount > 0 && count > 0;
        if (!Pending)
        {
            _opened = false; // pick consumed -> reset for the next level-up
        }
        Visible = Active;
        if (!Active)
        {
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i].Visible = false;
                _cards[i].ResetPress();
            }
            _count = 0;
            return;
        }

        float total = count * _cardW + (count - 1) * _cardGap;
        float x0 = -total * 0.5f + _cardW * 0.5f;
        for (int i = 0; i < _cards.Length; i++)
        {
            if (i < count)
            {
                _cards[i].Visible = true;
                _cards[i].Position = new Vector3(x0 + i * (_cardW + _cardGap), 0.0f, 0.0f);
                int pid = PerkChoice(snap.Header, i);
                _cards[i].SetText(_perkNames.TryGetValue(pid, out string? n) ? n : $"Perk {pid}");
            }
            else
            {
                _cards[i].Visible = false;
                _cards[i].ResetPress();
            }
        }
        _count = count;
    }

    /// <summary>Feed the current controller-tip world positions to the visible
    /// cards each rendered frame.</summary>
    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active)
        {
            return;
        }
        for (int i = 0; i < _count; i++)
        {
            _cards[i].PollPoke(probes);
        }
    }

    private static int PerkChoice(Sim.SnapshotHeader h, int i) => i switch
    {
        0 => h.PerkChoice0,
        1 => h.PerkChoice1,
        2 => h.PerkChoice2,
        3 => h.PerkChoice3,
        4 => h.PerkChoice4,
        5 => h.PerkChoice5,
        6 => h.PerkChoice6,
        _ => -1,
    };

    private void LoadPerkNames()
    {
        string path = "res://assets/sprites/sprite_manifest.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            return;
        }
        using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        try
        {
            using var doc = JsonDocument.Parse(f.GetAsText());
            if (doc.RootElement.TryGetProperty("perks", out JsonElement perks) && perks.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in perks.EnumerateObject())
                {
                    if (int.TryParse(p.Name, out int id) && p.Value.ValueKind == JsonValueKind.String)
                    {
                        _perkNames[id] = p.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException e)
        {
            GD.PushWarning($"CrimsonVR: bad perk names in manifest: {e.Message}");
        }
    }
}
