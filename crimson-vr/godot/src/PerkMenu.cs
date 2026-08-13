using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 2: the level-up perk pick as poke buttons (PLAN M4 UI model). When
/// the sim reports perks pending (snapshot header perk_pending_count /
/// perk_choices[]), the candidate perks float above the arena as cards the player
/// pokes to select and inspect, then confirms with a separate button below the
/// row. The confirmed choice feeds back as CrimsonHostInput.perk_choice_index
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
    private readonly int[] _cardPerk = new int[7];       // perk id per visible card
    private VrButton _confirm = null!;
    private int _count;
    private float _cardW;
    private float _cardGap;
    private float _arenaSide;
    private readonly Dictionary<int, string> _perkNames = new();
    private readonly Dictionary<int, string> _perkDescs = new();
    private int _focused = -1;
    private static readonly Color CardIdle = new(0.22f, 0.24f, 0.30f);
    private static readonly Color CardFocused = new(0.42f, 0.50f, 0.68f);

    // Description popup above the card row. It stays pinned after a card poke so
    // the player can review the selection before committing with the separate
    // confirmation button below the cards.
    private Node3D _descPanel = null!;
    private Label3D _descLabel = null!;

    /// <summary>Chosen choice-index (0..count-1) after explicit confirmation;
    /// Main reads it into the next tick's perk_choice_index and clears it.
    /// -1 = nothing.</summary>
    public int Chosen = -1;

    private bool _opened; // cards revealed (via the level-up button)
    private bool _layoutPreview;

    // Cross-fade: each new candidate set (first open, or the next accumulated pick)
    // eases in over FadeMs so the swap reads clearly instead of popping.
    private const ulong FadeMs = 220;
    private ulong _fadeStartMs;
    private int _setSig = int.MinValue; // signature of the visible set, to detect a swap

    /// <summary>True while a perk pick is available (Main pauses the sim + shows the
    /// level-up button). The cards themselves only appear once <see cref="Open"/>.</summary>
    public bool Pending { get; private set; }

    /// <summary>True while the cards are shown and pollable (Pending AND opened).</summary>
    public bool Active => _layoutPreview || (Pending && _opened);

    /// <summary>Reveal the cards (from the level-up button).</summary>
    public void Open()
    {
        _opened = true;
        ClearFocus();
    }

    /// <summary>Show a representative, inert three-card offer while UI Edit is
    /// open. The perk menu is deliberately not an editable target: it is the
    /// clearance envelope the movable Pause/Level Up controls must respect.</summary>
    public void SetLayoutPreview(bool visible)
    {
        _layoutPreview = visible;
        ClearFocus();
        if (!visible)
        {
            Visible = Pending && _opened;
            if (!Visible)
            {
                for (int i = 0; i < _cards.Length; i++)
                {
                    _cards[i].Visible = false;
                    _cards[i].ResetPress();
                }
                _confirm.Visible = false;
                _confirm.ResetPress();
            }
            return;
        }

        // Maximum live offer: base 5, Perk Expert 6, Perk Master 7. Use seven
        // realistic labels so Edit mode exposes the true worst-case width.
        string[] names =
        {
            "Fastloader", "Regeneration", "Long Distance Runner", "Fastshot",
            "Sharpshooter", "Bonus Economist", "Perk Master",
        };
        _count = names.Length;
        float total = _count * _cardW + (_count - 1) * _cardGap;
        float x0 = -total * 0.5f + _cardW * 0.5f;
        for (int i = 0; i < _cards.Length; i++)
        {
            bool shown = i < _count;
            _cards[i].Visible = shown;
            if (!shown)
            {
                continue;
            }
            float x = x0 + i * (_cardW + _cardGap);
            _cards[i].Position = new Vector3(x, 0.0f, 0.0f);
            _cards[i].SetText(names[i]);
            _cards[i].SetFade(1.0f);
        }
        // Preview the selected state so UI Edit includes the true description +
        // confirmation envelope without suggesting Confirm exists before selection.
        _focused = 0;
        _cards[0].SetColor(CardFocused);
        _descLabel.Text = "Selected perk details\n\nConfirm below to choose this perk.";
        _descPanel.Visible = true;
        _confirm.Visible = true;
        _confirm.SetFade(1.0f);
        Visible = true;
    }

    /// <summary>Force the whole perk pick hidden + reset. Needed when quitting a
    /// mission with a pick open: the sim tick that would clear it via Update() stops
    /// running once the main menu owns the screen, so the cards would otherwise
    /// linger over the menu.</summary>
    public void ForceHide()
    {
        Pending = false;
        _opened = false;
        Chosen = -1;
        _count = 0;
        _setSig = int.MinValue;
        _focused = -1;
        Visible = false;
        _descPanel.Visible = false;
        for (int i = 0; i < _cards.Length; i++)
        {
            _cards[i].Visible = false;
            _cards[i].ResetPress();
        }
        _confirm.Visible = false;
        _confirm.ResetPress();
    }

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
        for (int i = 0; i < _cards.Length; i++)
        {
            var card = new VrButton();
            AddChild(card);
            card.Build(_cardW, cardH, string.Empty, CardIdle);
            // Portrait cards: shrink + word-wrap the perk name to fit the card width
            // (the default height-based sizing made long names overflow neighbours).
            card.ConfigureLabel(arenaSideMeters * 0.00035f, _cardW * 0.85f);
            int idx = i;
            card.OnPress += () => PressCard(idx);
            card.Visible = false;
            // Alpha pipeline up-front: SetFade's lazy transparency switch caused
            // a first-fade pipeline-compile stall on Quest that ate the ease.
            card.PrewarmFade();
            _cards[i] = card;

        }

        // Commitment is spatially separate from the changing card row. After a
        // choice is confirmed, the player's hand remains below the next offer
        // instead of overlapping a freshly spawned card.
        float confirmW = arenaSideMeters * 0.72f;
        _confirm = new VrButton();
        AddChild(_confirm);
        _confirm.Build(confirmW, arenaSideMeters * 0.13f, "Confirm Perk Selection",
            new Color(0.30f, 0.46f, 0.34f));
        _confirm.ConfigureLabel(arenaSideMeters * 0.00035f, confirmW * 0.90f);
        _confirm.Position = new Vector3(0.0f, -(cardH * 0.5f + arenaSideMeters * 0.13f), 0.0f);
        _confirm.OnPress += ConfirmSelection;
        _confirm.Visible = false;
        _confirm.PrewarmFade();

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
                // UI Edit deliberately keeps real moving sprites active. Own the
                // reading surface above every world layer so they cannot puncture
                // the selected-perk copy.
                RenderPriority = ClassicPanel.BackdropRenderPriority,
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
            RenderPriority = ClassicPanel.TextRenderPriority,
        };
        _descPanel.AddChild(_descLabel);
    }

    /// <summary>Refresh from the latest snapshot: show/lay out the candidate cards
    /// when perks are pending, else hide. Call each tick with the snapshot.</summary>
    public void Update(in SnapshotView snap)
    {
        int count = Mathf.Min((int)snap.Header.PerkChoiceCount, _cards.Length);
        // Pending tracks the PICK being owed, not the cards existing. Those are
        // now two different moments: the offer is rolled when the menu opens,
        // because that roll draws from the sim rng and has to happen somewhere a
        // replay can reproduce. Requiring count > 0 here deadlocked it — Active
        // gates perk_menu_active, which is what triggers the roll, so the cards
        // could never arrive and the Level Up! button would never stick.
        Pending = snap.Header.PerkPendingCount > 0;
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
            _confirm.Visible = false;
            _confirm.ResetPress();
            _descPanel.Visible = false;
            _focused = -1;
            _count = 0;
            _setSig = int.MinValue; // next open counts as a fresh set
            return;
        }

        // Detect a new candidate set (first open or the next accumulated pick) and
        // (re)start the fade-in.
        int sig = count;
        for (int i = 0; i < count; i++)
        {
            sig = sig * 131 + PerkChoice(snap.Header, i);
        }
        if (sig != _setSig)
        {
            _setSig = sig;
            ClearFocus();
            _fadeStartMs = Time.GetTicksMsec();
            // Start the new set invisible RIGHT NOW: PollPoke applies the ease
            // later in the frame, so without this the swapped-in cards rendered
            // one full-alpha frame first (visible flash before the fade).
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i].SetFade(0.0f);
            }
            _confirm.SetFade(0.0f);
        }

        float total = count * _cardW + (count - 1) * _cardGap;
        float x0 = -total * 0.5f + _cardW * 0.5f;
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
            }
            else
            {
                _cards[i].Visible = false;
                _cards[i].ResetPress();
            }
        }
        _count = count;
        // Runtime invariant: an unselected offer never exposes the commit action.
        // In particular, a freshly swapped-in set starts with ClearFocus above,
        // so the hand that confirmed the prior pick has nothing to press here.
        _confirm.Visible = _focused >= 0;
    }

    /// <summary>Feed the current controller-tip world positions to the visible
    /// cards each rendered frame.</summary>
    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active || _layoutPreview)
        {
            return;
        }
        // Ease the current set in. Applied AFTER PollPoke so VrButton's per-frame
        // recolor (which resets alpha to opaque) doesn't clobber the fade.
        ulong dt = Time.GetTicksMsec() - _fadeStartMs;
        float fade = dt >= FadeMs ? 1.0f : Mathf.Clamp((float)dt / FadeMs, 0.0f, 1.0f);
        for (int i = 0; i < _count; i++)
        {
            _cards[i].PollPoke(probes);
            _cards[i].SetFade(fade);
        }
        if (_confirm.Visible)
        {
            _confirm.PollPoke(probes);
            _confirm.SetFade(fade);
        }
    }

    private void PressCard(int index)
    {
        FocusCard(index);
    }

    private void ConfirmSelection()
    {
        if (_focused >= 0 && _focused < _count)
        {
            Chosen = _focused;
            // Remove the commit target immediately; do not wait for the sim tick
            // that consumes this pick and supplies the next offer.
            ClearFocus();
        }
    }

    private void FocusCard(int index)
    {
        if (index < 0 || index >= _count)
        {
            return;
        }
        _focused = index;
        for (int i = 0; i < _count; i++)
        {
            _cards[i].SetColor(i == index ? CardFocused : CardIdle);
        }
        string desc = _perkDescs.TryGetValue(_cardPerk[index], out string? d) ? d : "(no description)";
        _descLabel.Text = desc + "\n\nConfirm below to choose this perk.";
        _descPanel.Visible = true;
        _confirm.Visible = true;
        // The button has just appeared, so require the hand to be observed clear
        // before it can commit even if tracking jumps across the two controls.
        _confirm.ResetPress();
    }

    private void ClearFocus()
    {
        _focused = -1;
        if (_descPanel != null)
        {
            _descPanel.Visible = false;
        }
        if (_confirm != null)
        {
            _confirm.Visible = false;
            _confirm.ResetPress();
        }
        for (int i = 0; i < _cards.Length; i++)
        {
            if (_cards[i] != null)
            {
                _cards[i].SetColor(CardIdle);
            }
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
        string path = AssetStore.SpritePath("sprite_manifest.json");
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
