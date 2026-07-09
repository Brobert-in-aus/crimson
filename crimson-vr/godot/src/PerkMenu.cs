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
    private readonly VrButton[] _help = new VrButton[7]; // "?" press-and-hold per card
    private readonly int[] _cardPerk = new int[7];       // perk id per visible card
    private int _count;
    private float _cardW;
    private float _cardGap;
    private float _arenaSide;
    private readonly Dictionary<int, string> _perkNames = new();
    private readonly Dictionary<int, string> _perkDescs = new();

    // Description popup above the card row, shown while a "?" is held.
    private Node3D _descPanel = null!;
    private Label3D _descLabel = null!;

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
        _arenaSide = arenaSideMeters;
        // Float above the arena centre facing the player. RecenterArena yaws the
        // arena so local +z is the far edge, so a 180 deg yaw faces the near
        // (player) side; a small back-lean tips the tops away from the player.
        Position = new Vector3(0.0f, arenaSideMeters * 0.8f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);
        _cardW = arenaSideMeters * 0.26f;
        _cardGap = arenaSideMeters * 0.05f;
        float cardH = arenaSideMeters * 0.34f;
        // Neutral dark card (the original perk cards aren't bright yellow).
        var cardColor = new Color(0.22f, 0.24f, 0.30f);
        for (int i = 0; i < _cards.Length; i++)
        {
            var card = new VrButton();
            AddChild(card);
            card.Build(_cardW, cardH, string.Empty, cardColor);
            // Portrait cards: shrink + word-wrap the perk name to fit the card width
            // (the default height-based sizing made long names overflow neighbours).
            card.ConfigureLabel(arenaSideMeters * 0.00035f, _cardW * 0.85f);
            int idx = i;
            card.OnPress += () => Chosen = idx;
            card.Visible = false;
            _cards[i] = card;

            // "?" button below each card: press-and-hold to show the description.
            var help = new VrButton();
            AddChild(help);
            help.Build(_cardW * 0.5f, arenaSideMeters * 0.11f, "?", new Color(0.35f, 0.4f, 0.55f));
            help.Visible = false;
            _help[i] = help;
        }

        BuildDescPanel(arenaSideMeters);
        Visible = false;
    }

    private void BuildDescPanel(float s)
    {
        _descPanel = new Node3D { Visible = false };
        AddChild(_descPanel);
        float w = s * 1.3f;
        float h = s * 0.4f;
        _descPanel.Position = new Vector3(0.0f, s * 0.42f, 0.0f); // above the card row
        _descPanel.AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(w, h) },
            Position = new Vector3(0.0f, 0.0f, -0.006f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.04f, 0.05f, 0.08f, 0.9f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        });
        _descLabel = new Label3D
        {
            Text = string.Empty,
            FontSize = 72,
            PixelSize = s / 1500.0f,
            Modulate = new Color(0.92f, 0.92f, 0.96f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = (w * 0.92f) / (s / 1500.0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            NoDepthTest = true,
        };
        _descPanel.AddChild(_descLabel);
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
                _help[i].Visible = false;
                _help[i].ResetPress();
            }
            _descPanel.Visible = false;
            _count = 0;
            return;
        }

        float total = count * _cardW + (count - 1) * _cardGap;
        float x0 = -total * 0.5f + _cardW * 0.5f;
        float cardH = _arenaSide * 0.34f;
        for (int i = 0; i < _cards.Length; i++)
        {
            if (i < count)
            {
                float cx = x0 + i * (_cardW + _cardGap);
                _cards[i].Visible = true;
                _cards[i].Position = new Vector3(cx, 0.0f, 0.0f);
                int pid = PerkChoice(snap.Header, i);
                _cardPerk[i] = pid;
                _cards[i].SetText(_perkNames.TryGetValue(pid, out string? n) ? n : $"Perk {pid}");
                // "?" sits below its card, with a clear gap from the card button.
                _help[i].Visible = true;
                _help[i].Position = new Vector3(cx, -(cardH * 0.5f + _arenaSide * 0.12f), 0.0f);
            }
            else
            {
                _cards[i].Visible = false;
                _cards[i].ResetPress();
                _help[i].Visible = false;
                _help[i].ResetPress();
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
        int held = -1;
        for (int i = 0; i < _count; i++)
        {
            _cards[i].PollPoke(probes);
            _help[i].PollPoke(probes);
            if (_help[i].IsPressed)
            {
                held = i;
            }
        }
        // Press-and-hold a "?" to show that perk's description above the row.
        if (held >= 0)
        {
            _descLabel.Text = _perkDescs.TryGetValue(_cardPerk[held], out string? d) ? d : "(no description)";
            _descPanel.Visible = true;
        }
        else
        {
            _descPanel.Visible = false;
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
            LoadStringMap(doc.RootElement, "perks", _perkNames);
            LoadStringMap(doc.RootElement, "perk_descriptions", _perkDescs);
        }
        catch (JsonException e)
        {
            GD.PushWarning($"CrimsonVR: bad perk data in manifest: {e.Message}");
        }
    }

    private static void LoadStringMap(JsonElement root, string key, Dictionary<int, string> into)
    {
        if (root.TryGetProperty(key, out JsonElement obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (int.TryParse(p.Name, out int id) && p.Value.ValueKind == JsonValueKind.String)
                {
                    into[id] = p.Value.GetString() ?? string.Empty;
                }
            }
        }
    }
}
