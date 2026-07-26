using System;
using System.Collections.Generic;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M2 diorama scene: OpenXR bootstrap, a tabletop arena, the controller
/// vertical-projection reticles (PLAN.md §4), and the live simulation rendered
/// as a colored-quad diorama. The sim ticks at a fixed 60 Hz in
/// _PhysicsProcess; rendering interpolates between the last two snapshots at
/// headset refresh. Everything is built in code; scenes/main.tscn is just this
/// script on a root node.
/// </summary>
public partial class Main : Node3D
{
    // 0.4 m/side suits a seated player's reach (chest to fully-outstretched is
    // well under a meter). A proper seated reach-envelope calibration is a
    // future setup step (PLAN §5); for now this is the fixed default.
    private const float ArenaSideMeters = 0.4f;
    private const float ArenaHeightMeters = 0.75f;
    private const float GameWorldSize = 1024.0f;
    // Placement is defined by the near edge, not the centre: a seated player
    // wants the near edge just in front of them (~0.10 m) with the far edge at
    // arm's reach. Distance to centre = near-edge offset + half the side. (In-
    // headset finding, 2026-07: centre at 0.6 m put the far half out of reach.)
    private const float ArenaNearEdgeMeters = 0.10f;
    private const float ArenaDistanceMeters = ArenaNearEdgeMeters + ArenaSideMeters * 0.5f;
    // Vertical placement: the arena sits this far below the head pose, refreshed
    // on every recenter (so a standing player isn't left with the table far
    // below). MinVerticalDrop is the closest under the head it may sit. Both
    // become tunables in the M4 arena-customisation tool (PLAN §5).
    private const float VerticalDropMeters = 0.5f;
    private const float MinVerticalDropMeters = 0.35f;
    private const int SimTicksPerSecond = 60;

    private const float TriggerThreshold = 0.5f;
    private const float GripThreshold = 0.7f;

    // Standard 1024 world at 60 Hz (mirrors HostSessionConfig). With the Debug
    // setting on at boot, debug_fx_showcase is added: reload cycles the player
    // through the arsenal for a one-run weapon tour.
    //
    // The seed is randomized PER RUN like the base game (base_gameplay_mode
    // seeds each reset from the live app RNG state, so no two runs replay the
    // same stream). A fixed seed made every run's spawn order, drop rolls, and
    // perk coin flips (Fatal Lottery!) repeat across similar runs.
    private uint _runSeed = 1;

    private string SessionConfig =>
        $"{{\"seed\":{_runSeed}"
        + $",\"game_mode\":{_gameMode}"
        + (_gameMode == GameModeQuests ? $",\"quest_level_key\":{_questKey}" : string.Empty)
        + (_gameMode == GameModeQuests && _hardcore ? ",\"hardcore\":true" : string.Empty)
        + ",\"player_count\":1,\"world_size\":1024.0,\"tick_rate\":60"
        // The persisted quest-unlock progression also gates the survival/rush
        // weapon-drop pool, like the base game's status blob. The FULL index
        // (hardcore-only progression) gates the Splitter Gun; the usage counts
        // drive the native 50% used-weapon drop reroll.
        + $",\"status_quest_unlock_index\":{_settings.QuestUnlockIndex}"
        + $",\"status_quest_unlock_index_full\":{_settings.QuestUnlockIndexFull}"
        + $",\"status_weapon_usage_counts\":[{string.Join(',', _settings.WeaponUsageCounts)}]"
        + (_settings.Debug ? ",\"debug_fx_showcase\":true" : string.Empty) + "}";

    // Sim.GameModeId values (game_ids.zig).
    private const int GameModeSurvival = 1;
    private const int GameModeRush = 2;
    private const int GameModeQuests = 3;
    private int _gameMode = GameModeSurvival;
    private int _questKey = 101;      // quest_level_key = stage*100 + index
    private string _questTitle = string.Empty;
    private bool _questEndShown;      // quest end panel shown for this run
    private bool _hardcore;           // quest-select checkbox (quests only)

    private XROrigin3D _origin = null!;
    private XRCamera3D _camera = null!;
    private XRController3D _leftHand = null!;
    private XRController3D _rightHand = null!;
    private Node3D _arenaRoot = null!;
    private Node3D _leftReticle = null!;
    private Node3D _rightReticle = null!;
    private Node3D _leftGuide = null!;
    private Node3D _rightGuide = null!;
    private Label3D _status = null!;

    // Reticle textures projected onto the play plane: ui_aim = aim-hand crosshair,
    // ui_cursor = move-hand pointer (staged by bake_assets.py; null -> plain quad).
    private Texture2D? _aimTex;
    private Texture2D? _cursorTex;
    private const float ReticleSizeMeters = 0.02f; // ~creature-sized on the 0.4m arena

    private SimSession? _sim;
    private Diorama _diorama = null!;
    private AudioBank _audio = null!;
    private Hud _hud = null!;
    private Sim.PlayerSnap _lastPlayer;   // latest tick's player snap (aim overlays)
    private bool _hasPlayerSnap;
    private float _hudFade = 1.0f;        // eased HUD alpha (perk menu / death fade)
    private PerkMenu _perkMenu = null!;
    private int _perkChoice = -1; // pending poke choice for the next tick, -1 = none
    private int _prevPerkPending; // last tick's pending-pick count, for the level-up cue
    private PauseMenu _pauseMenu = null!;
    // Options screen (mirrors the base game) + the VR Settings submenu it opens.
    private VrOptionsMenu _optionsMenu = null!;
    private SettingsMenu _settingsMenu = null!; // VR Settings submenu (hand/dead-zone/debug)
    private bool _optionsOpen;
    private bool _vrSettingsOpen;
    private bool _optionsFromMenu; // options opened from the main menu (vs the pause menu)
    private float _deadZone = VrInput.DefaultDeadZoneGameUnits;
    private StartPrompt _startPrompt = null!;
    private VirtualKeyboard _keyboard = null!;
    private GameOverPanel _gameOverPanel = null!;
    private int _deathTicks;     // pacing counter: death -> results (base death-timer delay)
    // Death cinematic: the view zooms in on the corpse over roughly the death
    // animation's length (16/20 s = 48 ticks), holding until the run resets.
    // The zoom factor is computed at death so the corpse spans about a quarter
    // of the arena side regardless of the player's size stat.
    private Vector2 _deathZoomCenter;
    private float _deathZoomMax = 2.0f;
    private const float DeathZoomCorpseFraction = 0.25f;
    private const int DeathZoomTicks = 48;
    private int _deathRank = int.MaxValue; // 0-based insertion rank of the death score
    private int _deathScore;               // score pinned when the death screen opened

    // ~1.2 s at 60 Hz between death and the results flow, standing in for the
    // base game's death VO + death-timer delay before the panel slides in
    // (player_damage.py). Tune in-headset.
    private const int DeathPacingTicks = 72;
    private const int HighscoreTableMax = 10; // UserSettings.AddHighscore cap
    private readonly UserSettings _settings = new();
    private ValidationChecklist _checklist = null!;
    private DebugMenu _debugMenu = null!;
    private MainMenu _mainMenu = null!;
    private PlayGameMenu _playGameMenu = null!;
    private QuestSelectMenu _questSelect = null!;
    private QuestResultPanel _questPanel = null!;
    private EndNotePanel _endNote = null!;
    private StatsMenu _statsMenu = null!;
    private DatabaseMenu _databaseMenu = null!;

    /// <summary>The menu flow (main menu, or the options/VR-settings screens opened
    /// from it) owns the screen: the sim must not tick and gameplay input must not
    /// reach the game. Options opened from the pause menu is gated by IsPaused.</summary>
    private bool MenuOwnsScreen => _mainMenu.IsOpen || _playGameMenu.IsOpen || _questSelect.IsOpen
        || _statsMenu.IsOpen || _databaseMenu.IsOpen
        || (_optionsFromMenu && (_optionsOpen || _vrSettingsOpen));
    private bool _debug;
    private readonly MeshInstance3D[] _pokeMarkers = new MeshInstance3D[2];
    private Vector2 _playerGame = new(GameWorldSize * 0.5f, GameWorldSize * 0.5f);

    // Hand roles: default left = movement, right = aim/fire (PLAN §1); swap is a
    // settings toggle wired in M4. Index 0 = left, 1 = right.
    private bool _handSwap;
    private readonly bool[] _prevTrigger = new bool[2];
    private readonly bool[] _prevGrip = new bool[2];

    // Haptics (slice 7): fire pulse on the aim hand, a strong both-hand pulse when
    // hit, a tick on reload complete. Deltas tracked tick-to-tick.
    private float _prevHealth;
    private bool _prevReloadActive;

    private bool _xrActive;

    // Display mode (PLAN §6 / MR): Auto uses passthrough when the headset supports
    // it (Quest 3 etc. report ALPHA_BLEND as a supported environment blend mode),
    // else a skybox. The tabletop diorama is a natural MR fit — in passthrough it
    // sits in the player's real room. Forcing Skybox keeps the VR void; forcing
    // Passthrough warns and falls back if unsupported. (A settings toggle is M4.)
    public enum DisplayMode { Auto, Skybox, Passthrough }
    // Default to Skybox for now. Auto-passthrough on Quest 3 (ALPHA_BLEND +
    // transparent viewport) currently leaves the app compositing transparent —
    // the shell/room and system controllers stay visible and the scene never
    // presents, so it's opt-in only until the export-side passthrough feature is
    // verified end-to-end on-device. Skybox is the known-good opaque path.
    private DisplayMode _displayMode = DisplayMode.Skybox;
    private XRInterface? _xrInterface;
    private WorldEnvironment _worldEnv = null!;
    private bool _passthroughActive;

    // Skybox-mode grey fog. THEMING ONLY: depth-mode fog with an onset distance
    // so nothing near the player is tinted — the diorama and menus (< ~1.5 m)
    // stay fog-free, the ramp starts at FogStartMeters and reaches full grey by
    // FogEndMeters (hiding the world-floor edge at ~12 m). The old exponential
    // fog (density 0.35) was already ~30% opaque at 1 m, visibly greying the
    // tabletop and washing contrast out of the whole diorama.
    private static readonly Color FogGrey = new(0.55f, 0.55f, 0.58f);
    private const float FogStartMeters = 3.0f;
    // Full grey well BEFORE the 24m world floor's edge (~12m away): with the
    // end at 12 the edge itself was still faintly visible (in-headset fail).
    private const float FogEndMeters = 8.0f;

    private bool _recenterPending = true;
    private bool _prevRecenterHeld;
    private int _framesSinceStart;

    public override void _Ready()
    {
        Engine.PhysicsTicksPerSecond = SimTicksPerSecond;

        InitializeXr();
        BuildEnvironment();
        BuildRig();
        BuildArena();
        BuildReticles();
        BuildStatusLabel();
        StartSession();

        // Surface the REAL failure in-headset: the old fixed "native lib
        // missing" line hid version-guard and config errors behind one message.
        _status.Text = _sim != null
            ? $"CrimsonVR | sim abi v{TryQueryAbiVersion()}"
            : $"sim unavailable: {_simError ?? "native lib missing"}";
    }

    public override void _ExitTree()
    {
        // Menu Quit is not the only shutdown path: desktop window close and
        // platform lifecycle teardown also reach here. Persist the live status
        // before releasing the native session.
        CaptureWeaponUsage();
        _sim?.Dispose();
        _sim = null;
        VrButton.OnAnyPress = null;
    }

    private string? _simError; // SimSession ctor failure message (for the status line)

    private void StartSession()
    {
        // Persisted settings first, so the menus build with the saved values.
        _settings.Load();
        _handSwap = _settings.HandSwap;
        _deadZone = _settings.DeadZone;

        _diorama = new Diorama();
        _arenaRoot.AddChild(_diorama);
        _diorama.Configure(ArenaSideMeters, GameWorldSize);

        // Audio is anchored under ArenaRoot so its players sit at the tabletop
        // (positions are arena-local meters, like the diorama).
        _audio = new AudioBank();
        _arenaRoot.AddChild(_audio);
        _audio.Configure(ArenaSideMeters, GameWorldSize);
        // Every diegetic poke button plays a UI click cue (menu = click, keyboard
        // keys = type, Enter = type-enter), via the global VrButton press hook.
        VrButton.OnAnyPress = kind => _audio.PlayUi(kind);

        // HUD panel at the arena's near edge (health/ammo/level), also arena-local.
        _hud = new Hud();
        _arenaRoot.AddChild(_hud);
        _hud.Build(ArenaSideMeters);

        // Perk pick cards float above the arena (poke to choose), arena-local.
        _perkMenu = new PerkMenu();
        _arenaRoot.AddChild(_perkMenu);
        _perkMenu.Build(ArenaSideMeters);

        // Pause: an always-live flat toggle beside the arena; resume/settings/quit
        // above it while paused. Settings is unhooked until the settings slice.
        _pauseMenu = new PauseMenu();
        _arenaRoot.AddChild(_pauseMenu);
        _pauseMenu.Build(ArenaSideMeters);
        _pauseMenu.OnQuit += ReturnToMenu; // in-game Quit -> main menu (menu Quit exits the app)
        _pauseMenu.OnSettings += () => OpenOptions(fromMenu: false);
        // Level-up button (shown while a perk pick is pending) reveals the perk cards.
        _pauseMenu.OnLevelUp += () => _perkMenu.Open();

        // Options screen (mirrors the base game): audio + graphics-detail sliders,
        // UI-info-texts toggle, and a VR Settings submenu. Opened from the main
        // menu or the pause menu; values applied live + persisted.
        _optionsMenu = new VrOptionsMenu();
        _arenaRoot.AddChild(_optionsMenu);
        _optionsMenu.Build(
            ArenaSideMeters, _settings.SfxVolume, _settings.MusicVolume, _settings.GraphicsDetail, _settings.UiInfoTexts,
            LoadReticleTex("ui_menuPanel.png"), LoadReticleTex("ui_rectOn.png"), LoadReticleTex("ui_rectOff.png"),
            LoadReticleTex("ui_checkOn.png"), LoadReticleTex("ui_checkOff.png"));
        _optionsMenu.OnBack += CloseOptions;
        _optionsMenu.OnVrSettings += OpenVrSettings;
        _optionsMenu.OnSfxChanged += v => { _settings.SfxVolume = v; _audio.SetSfxVolume(v); _settings.Save(); };
        _optionsMenu.OnMusicChanged += v => { _settings.MusicVolume = v; _audio.SetMusicVolume(v); _settings.Save(); };
        _optionsMenu.OnDetailChanged += v => { _settings.GraphicsDetail = v; _diorama.SetGraphicsDetail(v); _settings.Save(); };
        _optionsMenu.OnInfoTextsChanged += v => { _settings.UiInfoTexts = v; _settings.Save(); };

        // VR Settings submenu (opened from Options): hand-swap + dead-zone + debug.
        _settingsMenu = new SettingsMenu();
        _arenaRoot.AddChild(_settingsMenu);
        _settingsMenu.Build(ArenaSideMeters, _handSwap, _deadZone, _settings.Debug,
            _settings.RenderScale, _settings.Msaa,
            LoadReticleTex("ui_rectOn.png"), LoadReticleTex("ui_rectOff.png"));
        _settingsMenu.OnBack += CloseVrSettings;
        _settingsMenu.OnHandSwapChanged += v => { _handSwap = v; _settings.HandSwap = v; _settings.Save(); };
        _settingsMenu.OnDeadZoneChanged += v => { _deadZone = v; _settings.DeadZone = v; _settings.Save(); };
        _settingsMenu.OnDebugChanged += SetDebug;
        _settingsMenu.OnRenderScaleChanged += v => { _settings.RenderScale = v; ApplyRenderQuality(); _settings.Save(); };
        _settingsMenu.OnMsaaChanged += v => { _settings.Msaa = v; ApplyRenderQuality(); _settings.Save(); };

        // Validation checklist: a standing panel 90 deg to the RIGHT of the arena,
        // always visible so it can be ticked off in any game state, results persisted.
        _checklist = new ValidationChecklist();
        _arenaRoot.AddChild(_checklist);
        _checklist.Build(ArenaSideMeters, _settings.Checklist);
        _checklist.OnItemChanged += (id, state) => { _settings.Checklist[id] = state; _settings.Save(); };
        _checklist.SetShown(true);

        // Debug FX menu: runtime force-toggles for the effect render passes,
        // mirrored on the player's left. Visible only while debug is on.
        _debugMenu = new DebugMenu();
        _arenaRoot.AddChild(_debugMenu);
        _debugMenu.Build(ArenaSideMeters);
        _debugMenu.SetShown(_settings.Debug);

        // Debug poke-tip markers (world-space); shown only in debug mode.
        for (int i = 0; i < _pokeMarkers.Length; i++)
        {
            _pokeMarkers[i] = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.008f, Height = 0.016f },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(1.0f, 0.9f, 0.2f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
                Visible = false,
            };
            AddChild(_pokeMarkers[i]);
        }
        SetDebug(_settings.Debug); // apply the saved debug state to the diorama

        // First-run prompt (accept/calibrate) is superseded by the main menu as the
        // boot screen — the menu is now the first thing the player sees, and seated
        // calibration is its own later slice (PLAN §5). Keep the node built for that
        // future integration but always skip it so it never gates the sim.
        _startPrompt = new StartPrompt();
        _arenaRoot.AddChild(_startPrompt);
        _startPrompt.Build(ArenaSideMeters);
        _startPrompt.OnCalibrate += () => GD.Print("CrimsonVR: seated calibration is a later slice; using default arena");
        _startPrompt.Skip();

        // Death flow: highscore name entry (virtual keyboard, when the score
        // ranks) then the game-over results panel.
        _keyboard = new VirtualKeyboard();
        _arenaRoot.AddChild(_keyboard);
        _keyboard.Build(ArenaSideMeters);
        _keyboard.OnSubmit += SubmitHighscoreName;

        _gameOverPanel = new GameOverPanel();
        _arenaRoot.AddChild(_gameOverPanel);
        _gameOverPanel.Build(ArenaSideMeters);
        _gameOverPanel.OnPlayAgain += PlayAgain;
        _gameOverPanel.OnMainMenu += () => { _gameOverPanel.Dismiss(); ReturnToMenu(); };

        // Main menu (boot screen): the original Crimsonland menu art, floating and
        // pokeable, over the terrain diorama. Holds the sim until PLAY is poked.
        _mainMenu = new MainMenu();
        _arenaRoot.AddChild(_mainMenu);
        _mainMenu.Build(
            ArenaSideMeters,
            LoadReticleTex("ui_signCrimson.png"),
            LoadReticleTex("ui_menuItem.png"),
            LoadReticleTex("ui_itemTexts.png"));
        _mainMenu.OnPlay += () => { _mainMenu.Close(); _playGameMenu.Open(); };
        _mainMenu.OnOptions += () => OpenOptions(fromMenu: true);
        _mainMenu.OnStatistics += () => { _mainMenu.Close(); _statsMenu.Open(); };
        _mainMenu.OnQuit += () => { CaptureWeaponUsage(); GetTree().Quit(); };

        // Statistics + high-scores browser (lifetime per-mode aggregates).
        _statsMenu = new StatsMenu();
        _arenaRoot.AddChild(_statsMenu);
        _statsMenu.Build(ArenaSideMeters, _settings);
        _statsMenu.OnBack += () => { _statsMenu.Close(); _mainMenu.Open(); };

        // Unlocked Weapons/Perks Databases, behind Statistics like the flat
        // game (panels/stats.py -> panels/databases_*.py).
        _databaseMenu = new DatabaseMenu();
        _arenaRoot.AddChild(_databaseMenu);
        _databaseMenu.Build(ArenaSideMeters);
        _statsMenu.OnWeapons += () =>
        {
            _statsMenu.Close();
            _databaseMenu.Open(DatabaseMenu.Db.Weapons, _settings);
        };
        _statsMenu.OnPerks += () =>
        {
            _statsMenu.Close();
            _databaseMenu.Open(DatabaseMenu.Db.Perks, _settings);
        };
        _databaseMenu.OnBack += () => { _databaseMenu.Close(); _statsMenu.Open(); };

        // Play Game mode select (base play_game.py): Quests / Rush / Survival.
        _playGameMenu = new PlayGameMenu();
        _arenaRoot.AddChild(_playGameMenu);
        _playGameMenu.Build(ArenaSideMeters);
        _playGameMenu.OnSurvival += () => StartRun(GameModeSurvival);
        _playGameMenu.OnRush += () => StartRun(GameModeRush);
        _playGameMenu.OnQuests += () => { _playGameMenu.Close(); _questSelect.Open(_settings.QuestUnlockIndex); };
        _playGameMenu.OnBack += () => { _playGameMenu.Close(); _mainMenu.Open(); };

        // Quest stage/level select, gated by the persisted unlock index.
        _questSelect = new QuestSelectMenu();
        _arenaRoot.AddChild(_questSelect);
        _questSelect.Build(ArenaSideMeters);
        _questSelect.OnStart += (key, title) =>
        {
            _questTitle = title;
            _hardcore = _questSelect.Hardcore;
            StartRun(GameModeQuests, key);
        };
        _questSelect.OnBack += () => { _questSelect.Close(); _playGameMenu.Open(); };

        // Quest end panel: completed (Next Quest) or failed (Retry).
        _questPanel = new QuestResultPanel();
        _arenaRoot.AddChild(_questPanel);
        _questPanel.Build(ArenaSideMeters);
        _questPanel.OnNext += StartNextQuest;
        _questPanel.OnRetry += () => StartRun(GameModeQuests, _questKey);
        _questPanel.OnQuestMenu += () =>
        {
            _questPanel.Dismiss();
            ReturnToMenu();
            _mainMenu.Close();
            _questSelect.Open(_settings.QuestUnlockIndex);
        };
        _questPanel.OnMainMenu += () => { _questPanel.Dismiss(); ReturnToMenu(); };

        // Quest 5.10 finale: Show End Note -> the victory text + mode shortcuts.
        _endNote = new EndNotePanel();
        _arenaRoot.AddChild(_endNote);
        _endNote.Build(ArenaSideMeters);
        _questPanel.OnEndNote += () =>
        {
            _questPanel.Dismiss();
            _audio.PlayUi(AudioBank.UiPanel);
            _endNote.Show(_hardcore);
        };
        _endNote.OnSurvival += () => { _endNote.Dismiss(); StartRun(GameModeSurvival); };
        _endNote.OnRush += () => { _endNote.Dismiss(); StartRun(GameModeRush); };
        _endNote.OnMainMenu += () => { _endNote.Dismiss(); ReturnToMenu(); };

        try
        {
            _runSeed = GD.Randi();
            _sim = new SimSession(SessionConfig);
            // Static terrain generation info (ABI v3): slots pick the ground atlas
            // sheets, seed drives the stamp layout. The terrain-base quad render is
            // a follow-up; log it so the plumbing is exercised meanwhile.
            Sim.TerrainInfo t = _sim.TerrainInfo();
            _diorama.ApplyTerrainInfo(t); // texture the arena floor from the base slot
            BuildWorldFloor(_diorama.FloorTexture); // extend that ground around the player
            GD.Print($"CrimsonVR: terrain slots=({t.Slot0},{t.Slot1},{t.Slot2}) seed={t.TerrainSeed} size={t.TerrainSize}");
        }
        catch (System.Exception e)
        {
            GD.PushError($"CrimsonVR: sim session create failed: {e.Message}");
            _sim = null;
            _simError = e.Message;
        }

        // Apply the persisted Options settings now the sim/diorama/audio exist.
        _audio.SetSfxVolume(_settings.SfxVolume);
        _audio.SetMusicVolume(_settings.MusicVolume);
        _diorama.SetGraphicsDetail(_settings.GraphicsDetail);
        ApplyRenderQuality();

        // Boot into the main menu: show it, hide the gameplay chrome until PLAY,
        // and play the menu theme (like the base game).
        _mainMenu.Open();
        SetGameplayVisible(false);
        _audio.PlayMusic("crimson_theme");
    }

    /// <summary>Show/hide the in-arena gameplay chrome (HUD, reticles, pause
    /// toggle) — hidden while the main menu owns the screen, shown during play.</summary>
    private void SetGameplayVisible(bool visible)
    {
        _hud.Visible = visible;
        _pauseMenu.Visible = visible;
        _leftReticle.Visible = visible;
        _rightReticle.Visible = visible;
        _leftGuide.Visible = visible;
        _rightGuide.Visible = visible;
    }

    /// <summary>Leave the menus and begin a run in the given mode (Quests also
    /// carry the level key). Recreates the sim session with the mode's config
    /// and re-applies terrain (quests have per-level terrain slots). The base
    /// game STOPS the menu theme entering a run; the in-game tune starts on the
    /// first creature hit (randomly gt1/gt2, sim trigger_game_tune).</summary>
    private void StartRun(int gameMode, int questKey = 0)
    {
        _gameMode = gameMode;
        // The base game persists the selected mode (config.gameplay.mode);
        // the weapons database evaluates availability under it.
        _settings.LastGameMode = gameMode;
        if (questKey > 0)
        {
            _questKey = questKey;
        }
        _mainMenu.Close();
        _playGameMenu.Close();
        _questSelect.Close();
        _questPanel.Dismiss();
        _questEndShown = false;
        RestartSession();
        _hud.SetQuestTimeLimit(gameMode == GameModeQuests ? _questSelect.TimeLimitFor(_questKey) : 0);
        SetGameplayVisible(true);
        _audio.StopMusic();
    }

    /// <summary>Recreate the sim with the CURRENT mode config and reset the
    /// per-run frontend state (terrain look, decals, player position).</summary>
    private void RestartSession()
    {
        if (_sim == null)
        {
            return;
        }
        // Harvest the outgoing session's weapon usage before it's destroyed —
        // native save-status parity: usage accrues on every pickup and
        // survives aborted runs too, not just completed ones.
        CaptureWeaponUsage();
        _runSeed = GD.Randi();
        _sim.Restart(SessionConfig);
        _diorama.ResetInterpolation();
        _diorama.ApplyTerrainInfo(_sim.TerrainInfo());
        _diorama.ResetTerrainFx();
        _diorama.ResetViewZoom(); // death cinematic ends with the run
        _playerGame = new Vector2(GameWorldSize * 0.5f, GameWorldSize * 0.5f);
    }

    /// <summary>Quest results "Next Quest": advance to the next global index.</summary>
    private void StartNextQuest()
    {
        _questPanel.Dismiss();
        int gi = QuestGlobalIndex(_questKey) + 1;
        if (gi > 49)
        {
            ReturnToMenu();
            return;
        }
        int key = (gi / 10 + 1) * 100 + (gi % 10 + 1);
        _questTitle = _questSelect.TitleFor(key);
        StartRun(GameModeQuests, key);
    }

    /// <summary>0-based global quest index from a quest_level_key.</summary>
    private static int QuestGlobalIndex(int key) => (key / 100 - 1) * 10 + (key % 100 - 1);

    /// <summary>Persist the live session's per-weapon usage counts (ABI v16
    /// query) into settings — the VR stand-in for the native save-status
    /// blob. Called before every session teardown/restart and on app exit.</summary>
    private void CaptureWeaponUsage()
    {
        uint[]? counts = _sim?.WeaponUsageCounts();
        if (counts != null)
        {
            _settings.WeaponUsageCounts = counts;
            _settings.Save();
        }
    }

    /// <summary>Fold the finished run into the mode's lifetime stats (called
    /// once per run, when the end panel first shows).</summary>
    private void RecordRunStats()
    {
        if (_sim == null)
        {
            return;
        }
        Sim.TickResult r = _sim.LastResult;
        _settings.RecordRun(_gameMode, r.ElapsedMsSim, r.CreatureKillCount, r.ShotsFired, r.ShotsHit, r.PlayerExperience);
    }

    /// <summary>Quit the current game back to the main menu: reset the sim to a
    /// fresh run, unpause, and show the menu (Play starts clean). The MAIN MENU's
    /// Quit exits the app; every in-game Quit routes here instead.</summary>
    private void ReturnToMenu()
    {
        _keyboard.Dismiss();
        _gameOverPanel.Dismiss();
        _questPanel.Dismiss();
        _perkMenu.ForceHide(); // don't leave perk cards floating over the main menu
        _pauseMenu.ForceResume();
        _questEndShown = false;
        RestartSession();
        _mainMenu.Open();
        SetGameplayVisible(false);
        _audio.PlayMusic("crimson_theme");
    }

    // ---- Options / VR Settings navigation ----
    // Options is the entry (from the main menu OR the pause menu); VR Settings is a
    // submenu of Options. From the main menu the sim is gated by MenuOwnsScreen;
    // from the pause menu it's gated by IsPaused. Backs unwind to the caller.

    private void OpenOptions(bool fromMenu)
    {
        _optionsFromMenu = fromMenu;
        _optionsOpen = true;
        if (fromMenu)
        {
            _mainMenu.Close();
        }
        else
        {
            _pauseMenu.SetPanelVisible(false);
        }
        _optionsMenu.SetShown(true);
    }

    private void CloseOptions()
    {
        _optionsOpen = false;
        _optionsMenu.SetShown(false);
        if (_optionsFromMenu)
        {
            _optionsFromMenu = false;
            _mainMenu.Open();
        }
        else if (_pauseMenu.IsPaused)
        {
            _pauseMenu.SetPanelVisible(true);
        }
    }

    private void OpenVrSettings()
    {
        _optionsOpen = false;
        _vrSettingsOpen = true;
        _optionsMenu.SetShown(false);
        _settingsMenu.SetShown(true);
    }

    private void CloseVrSettings()
    {
        _vrSettingsOpen = false;
        _settingsMenu.SetShown(false);
        _optionsOpen = true;
        _optionsMenu.SetShown(true);
    }

    private void InitializeXr()
    {
        var xr = XRServer.FindInterface("OpenXR");
        if (xr != null && (xr.IsInitialized() || xr.Initialize()))
        {
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            _xrActive = true;
            _xrInterface = xr; // BuildEnvironment applies passthrough once the env exists
            GD.Print("CrimsonVR: OpenXR initialized");
        }
        else
        {
            GD.PushWarning("CrimsonVR: OpenXR unavailable; running flat");
        }
    }

    /// <summary>Apply the persisted VR render-quality settings: MSAA level + the
    /// OpenXR render-target supersampling multiplier. The flat-sprite scene is cheap,
    /// so this is where the aliasing win comes from. Live-adjustable from VR Settings.</summary>
    private void ApplyRenderQuality()
    {
        GetViewport().Msaa3D = _settings.Msaa switch
        {
            >= 4 => Viewport.Msaa.Msaa4X,
            2 => Viewport.Msaa.Msaa2X,
            _ => Viewport.Msaa.Disabled,
        };
        if (_xrInterface is OpenXRInterface oxr)
        {
            oxr.RenderTargetSizeMultiplier = Mathf.Clamp(_settings.RenderScale, 0.6f, 1.6f);
        }
    }

    private void BuildEnvironment()
    {
        // VR skybox: a dim procedural gradient (dark room/void) rather than a flat
        // clear color, so looking around VR mode has a little depth without pulling
        // focus from the tabletop. Overridden to transparent when passthrough is on.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = MakeVrSky(),
            // Ambient stays a fixed color (not sky-sourced) so it survives the
            // passthrough switch, which clears the sky.
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.4f, 0.4f, 0.45f),
            AmbientLightEnergy = 1.0f,
            // Grey fog so the diorama reads as sitting in a contained foggy space
            // (and it masks the off-arena spawn margin, §6). DEPTH mode with an
            // onset: fog-free out to FogStartMeters (the diorama/menus are never
            // tinted), full grey by FogEndMeters. FogSkyAffect greys the
            // background sky to match. Disabled in passthrough
            // (TrySetupPassthrough) so the real room shows through.
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogLightColor = FogGrey,
            FogLightEnergy = 1.0f,
            FogDepthBegin = FogStartMeters,
            FogDepthEnd = FogEndMeters,
            FogDepthCurve = 1.0f,
            FogDensity = 1.0f, // depth mode: max opacity at FogDepthEnd
            FogSkyAffect = 1.0f,
            FogAerialPerspective = 0.0f,
        };
        _worldEnv = new WorldEnvironment { Environment = env };
        AddChild(_worldEnv);

        var light = new DirectionalLight3D { LightEnergy = 1.2f };
        light.RotationDegrees = new Vector3(-55.0f, 30.0f, 0.0f);
        AddChild(light);

        TrySetupPassthrough();
    }

    private static Sky MakeVrSky()
    {
        var mat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.02f, 0.03f, 0.06f),
            SkyHorizonColor = new Color(0.08f, 0.09f, 0.13f),
            GroundBottomColor = new Color(0.01f, 0.01f, 0.02f),
            GroundHorizonColor = new Color(0.06f, 0.07f, 0.10f),
            SunAngleMax = 1.0f,
            UseDebanding = true,
        };
        return new Sky { SkyMaterial = mat };
    }

    /// <summary>Enable MR passthrough when the headset supports it (or when forced).
    /// Composites the transparent app over the real world via the OpenXR ALPHA_BLEND
    /// environment blend mode, so the tabletop diorama sits in the player's room.
    /// No-ops (keeps the skybox) when XR is flat or passthrough is unsupported.
    /// NOTE: the Quest APK also needs the passthrough feature enabled in the export
    /// preset (meta plugin) for this to composite on-device.</summary>
    private void TrySetupPassthrough()
    {
        if (_displayMode == DisplayMode.Skybox || !_xrActive || _xrInterface == null)
        {
            return;
        }
        Godot.Collections.Array modes = _xrInterface.GetSupportedEnvironmentBlendModes();
        bool alphaBlend = false;
        foreach (Variant m in modes)
        {
            if (m.As<long>() == (long)XRInterface.EnvironmentBlendModeEnum.AlphaBlend)
            {
                alphaBlend = true;
                break;
            }
        }
        if (!alphaBlend)
        {
            if (_displayMode == DisplayMode.Passthrough)
            {
                GD.PushWarning("CrimsonVR: passthrough requested but ALPHA_BLEND unsupported; using skybox");
            }
            return; // Auto: no passthrough hardware -> keep the skybox
        }

        _xrInterface.EnvironmentBlendMode = XRInterface.EnvironmentBlendModeEnum.AlphaBlend;
        GetViewport().TransparentBg = true;
        Godot.Environment env = _worldEnv.Environment;
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.0f, 0.0f, 0.0f, 0.0f); // transparent -> passthrough shows through
        env.FogEnabled = false; // no grey fog in MR — show the real room
        _passthroughActive = true;
        GD.Print("CrimsonVR: MR passthrough enabled (ALPHA_BLEND)");
    }

    private void BuildRig()
    {
        _origin = new XROrigin3D();
        AddChild(_origin);
        _camera = new XRCamera3D { Position = new Vector3(0, 1.7f, 0) };
        _origin.AddChild(_camera);

        _leftHand = new XRController3D { Tracker = "left_hand", Pose = "grip" };
        _rightHand = new XRController3D { Tracker = "right_hand", Pose = "grip" };
        _origin.AddChild(_leftHand);
        _origin.AddChild(_rightHand);

        _handMarkers[0] = MakeHandMarker(new Color(0.2f, 0.5f, 1.0f));
        _handMarkers[1] = MakeHandMarker(new Color(1.0f, 0.3f, 0.25f));
        _leftHand.AddChild(_handMarkers[0]);
        _rightHand.AddChild(_handMarkers[1]);
    }

    // The physical poke spheres on the controllers. Shown only when a poke UI is up
    // (menus/perk pick/keyboard); hidden during combat, where they just clutter the
    // view over the aiming reticles/guides.
    private readonly MeshInstance3D[] _handMarkers = new MeshInstance3D[2];

    private void UpdateHandMarkers()
    {
        bool pokeUi = MenuOwnsScreen
            || _pauseMenu.IsPaused
            || _perkMenu.Active
            || _startPrompt.Pending
            || _questPanel.Active
            || _endNote.Active
            || (_sim != null && _sim.GameOver && (_keyboard.Active || _gameOverPanel.Active));
        if (_handMarkers[0] != null)
        {
            _handMarkers[0].Visible = pokeUi;
        }
        if (_handMarkers[1] != null)
        {
            _handMarkers[1].Visible = pokeUi;
        }
    }

    // Keep in sync with VrButton.PokeRadius — the visible sphere IS the poke
    // collider. 2/3 of the original 0.02 (in-headset: full size felt clumsy
    // for button pressing).
    private const float HandMarkerRadius = 0.0133f;

    private static MeshInstance3D MakeHandMarker(Color color)
        => new()
        {
            Mesh = new SphereMesh { Radius = HandMarkerRadius, Height = HandMarkerRadius * 2.0f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
        };

    private void BuildArena()
    {
        // Initial position is a placeholder; RecenterArena() repositions it in
        // front of the head once tracking is valid (see HandleRecenter).
        _arenaRoot = new Node3D { Position = new Vector3(0.0f, ArenaHeightMeters, -ArenaDistanceMeters) };
        AddChild(_arenaRoot);

        // The visible ground is the Diorama terrain floor (textured from the base
        // slot, extended past the playfield, greyed by fog). The old brown
        // placeholder plane + rim were removed: they sat on top of the floor
        // (hiding the terrain and z-fighting its edge). A larger world floor at
        // foot level (BuildWorldFloor) extends that same ground around the player.
    }

    private const float WorldFloorSize = 24.0f;   // metres; fog hides the edge
    private const float WorldFloorTiles = 24.0f;  // ~1 m per terrain tile
    private const float WorldFloorY = 0.0f;       // XR tracking floor (foot level)
    private MeshInstance3D? _worldFloor;

    /// <summary>Extend the arena's ground out around the player: a large plane at
    /// foot level textured with the same terrain sheet as the diorama floor. With
    /// the grey skybox fog it reads as the player standing in the same arena the
    /// diorama sits in front of. World-space (a child of Main, not the recentered
    /// ArenaRoot) so it stays put. Dimmed a touch so the closer diorama still
    /// reads as the focus. No-ops if the terrain texture isn't available.</summary>
    private void BuildWorldFloor(Texture2D? terrainTex)
    {
        if (_worldFloor != null)
        {
            return;
        }
        var mat = new StandardMaterial3D
        {
            AlbedoTexture = terrainTex,
            // Full-colour grass/dirt like the base game; only a hair dimmed so the
            // closer diorama still reads as the focus. Grey fog fades the distance.
            AlbedoColor = terrainTex != null ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.22f, 0.22f, 0.25f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, // crisp pixel terrain (match arena floor)
            Uv1Scale = new Vector3(WorldFloorTiles, WorldFloorTiles, 1.0f),
        };
        _worldFloor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(WorldFloorSize, WorldFloorSize) },
            MaterialOverride = mat,
            Position = new Vector3(0.0f, WorldFloorY, 0.0f),
        };
        AddChild(_worldFloor);
    }

    private void BuildReticles()
    {
        _aimTex = LoadReticleTex("ui_aim.png");
        _cursorTex = LoadReticleTex("ui_cursor.png");
        _leftReticle = MakeReticle(new Color(0.2f, 0.5f, 1.0f));
        _rightReticle = MakeReticle(new Color(1.0f, 0.3f, 0.25f));
        _leftGuide = MakeGuide(new Color(0.2f, 0.5f, 1.0f, 0.35f));
        _rightGuide = MakeGuide(new Color(1.0f, 0.3f, 0.25f, 0.35f));
        AddChild(_leftReticle);
        AddChild(_rightReticle);
        AddChild(_leftGuide);
        AddChild(_rightGuide);
        BuildAimOverlays();
    }

    // ---- Aim-hand overlays: reload clock gauge + spread ring ----
    // Base game (draw_aim_indicators): a 32-unit ui_clockTable/ui_clockPointer
    // gauge at the aim point while reloading (pointer = reload progress * 360),
    // and the aim-spread circle radius = max(6, dist(player, aim) *
    // spread_heat * 0.5). The circle is adapted as a flat RING around the aim
    // reticle per the VR disposition table (no filled world overlay).
    private Node3D _reloadGauge = null!;
    private MeshInstance3D _reloadPointer = null!;
    private MeshInstance3D _spreadRing = null!;
    private const float ReloadGaugeUnits = 40.0f; // native 32; slightly larger for VR

    private void BuildAimOverlays()
    {
        float k = ArenaSideMeters / GameWorldSize;
        float gauge = ReloadGaugeUnits * k;
        _reloadGauge = new Node3D { Visible = false };
        AddChild(_reloadGauge);
        _reloadGauge.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(gauge, gauge) },
            MaterialOverride = FlatOverlayMaterial(LoadReticleTex("ui_clockTable.png"), priority: 5),
        });
        _reloadPointer = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(gauge, gauge) },
            Position = new Vector3(0.0f, 0.0005f, 0.0f),
            MaterialOverride = FlatOverlayMaterial(LoadReticleTex("ui_clockPointer.png"), priority: 6),
        };
        _reloadGauge.AddChild(_reloadPointer);

        _spreadRing = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(1.0f, 1.0f) },
            MaterialOverride = FlatOverlayMaterial(MakeRingTexture(96), priority: 4,
                color: new Color(1.0f, 1.0f, 1.0f, 0.4f)),
            Visible = false,
        };
        AddChild(_spreadRing);
    }

    /// <summary>Position/refresh the aim-hand overlays each rendered frame: the
    /// reload clock at the aim reticle while reloading (pointer sweep = reload
    /// progress) and the spread ring sized by the live spread_heat (ABI v11).</summary>
    private void UpdateAimOverlays()
    {
        bool inPlay = !MenuOwnsScreen && _sim != null && _hasPlayerSnap
            && !_sim.GameOver && !_pauseMenu.IsPaused && !_perkMenu.Active
            && !_questPanel.Active && !_endNote.Active;
        XRController3D aimHand = _handSwap ? _leftHand : _rightHand;
        if (!inPlay || !aimHand.GetHasTrackingData() || _lastPlayer.Health <= 0.0f)
        {
            _reloadGauge.Visible = false;
            _spreadRing.Visible = false;
            return;
        }
        ComputeReticle(aimHand, out _, out Vector3 world);

        bool reloading = _lastPlayer.ReloadActive != 0
            && _lastPlayer.ReloadTimerMax > 1e-6f && _lastPlayer.ReloadTimer > 1e-6f;
        _reloadGauge.Visible = reloading;
        if (reloading)
        {
            _reloadGauge.GlobalPosition = world + new Vector3(0.0f, 0.004f, 0.0f);
            float progress = Mathf.Clamp(_lastPlayer.ReloadTimer / _lastPlayer.ReloadTimerMax, 0.0f, 1.0f);
            // Clockwise sweep seen from above; flip the sign if it reads backward
            // in-headset.
            _reloadPointer.RotationDegrees = new Vector3(0.0f, -progress * 360.0f, 0.0f);
        }

        // Spread ring radius: max(6, dist(player, aim) * spread_heat * 0.5)
        // game units (+2 for the native outline), centred on the aim point.
        var pos = new Vector2(_lastPlayer.X, _lastPlayer.Y);
        var aim = new Vector2(_lastPlayer.AimX, _lastPlayer.AimY);
        float radius = Mathf.Max(6.0f, pos.DistanceTo(aim) * _lastPlayer.SpreadHeat * 0.5f);
        float k = ArenaSideMeters / GameWorldSize;
        float side = (radius + 2.0f) * 2.0f * k;
        _spreadRing.Visible = true;
        _spreadRing.GlobalPosition = world + new Vector3(0.0f, 0.003f, 0.0f);
        _spreadRing.Scale = new Vector3(side, 1.0f, side);
    }

    private static StandardMaterial3D FlatOverlayMaterial(Texture2D? tex, int priority, Color? color = null)
        => new()
        {
            AlbedoTexture = tex,
            AlbedoColor = color ?? Colors.White,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            // Flip V like the reticles: texture top reads toward the far edge
            // from the player's downward view.
            Uv1Scale = new Vector3(1.0f, -1.0f, 1.0f),
            RenderPriority = priority,
        };

    /// <summary>Thin white ring with soft edges (the VR spread-circle stand-in).</summary>
    private static ImageTexture MakeRingTexture(int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2((x - c) / c, (y - c) / c).Length();
                // Band centred at r=0.92 with ~0.07 half-width, soft falloff.
                float a = Mathf.Clamp(1.0f - Mathf.Abs(d - 0.92f) / 0.07f, 0.0f, 1.0f);
                img.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, a * a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    private static Texture2D? LoadReticleTex(string name)
    {
        string path = "res://assets/sprites/" + name;
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    // Reticle = a flat textured quad on the play plane (the torus ring was
    // redundant with the vertical guide line, so it's gone). The cursor/target
    // texture is set per-frame by hand role in UpdateHandVisual; AlbedoColor tints
    // it and carries the over-arena / trigger brighten. The diorama is coplanar
    // and ordered by RenderPriority (creatures start at 6, terrain at 1), so the
    // reticle sits just above the ground/decals but BELOW creatures (priority 4)
    // — it reads as painted on the arena floor and enemies pass over it, rather
    // than floating on top of everything.
    private static Node3D MakeReticle(Color color)
        => new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(ReticleSizeMeters, ReticleSizeMeters) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                // Flip V: on the flat PlaneMesh the cursor art read upside down from
                // the player's downward view; this rights it (texture top -> far edge).
                Uv1Scale = new Vector3(1.0f, -1.0f, 1.0f),
                NoDepthTest = true,
                RenderPriority = 4,
            },
        };

    private static Node3D MakeGuide(Color color)
        => new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0015f, BottomRadius = 0.0015f, Height = 1.0f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };

    private void BuildStatusLabel()
    {
        _status = new Label3D
        {
            Position = new Vector3(0, ArenaHeightMeters + 0.35f, -0.6f),
            FontSize = 48,
            PixelSize = 0.001f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        AddChild(_status);
    }

    // ---- Simulation: fixed 60 Hz tick ----

    public override void _PhysicsProcess(double delta)
    {
        if (_sim == null)
        {
            return;
        }

        // The main menu owns the screen at boot (and while settings is open from
        // it): hold the sim so gameplay input (trigger fire, movement) can't leak
        // through and start play underneath the menu.
        if (MenuOwnsScreen)
        {
            return;
        }

        // First-run prompt holds the sim until the player accepts.
        if (_startPrompt.Pending)
        {
            return;
        }

        // Quest end panel (completed or failed) or the 5.10 end note up: hold
        // the sim until a button routes somewhere.
        if (_questPanel.Active || _endNote.Active)
        {
            return;
        }

        // Death with BANKED level-ups (e.g. the 50/50 bonus killing you with
        // picks pending): resolve the perk picks FIRST, like the base game —
        // the death flow otherwise stacked the name-entry keyboard over the
        // perk cards and the sim never ticked again (no death audio). The
        // normal tick path below keeps running (dead player input is inert),
        // feeding the perk choice and draining audio; the death flow starts
        // once no picks remain.
        if (_sim.GameOver && _sim.LastResult.PerkPendingCount > 0
            && !_keyboard.Active && !_gameOverPanel.Active && !_questPanel.Active)
        {
            if (!_perkMenu.Active && !_pauseMenu.IsPaused)
            {
                _perkMenu.Open();
            }
        }
        // Death handling (base game_over.py flow): a short pacing delay, then
        // name entry (only when the score ranks, like the base top-100 gate —
        // ours is the local top-10), then the results panel with Play Again /
        // Main Menu. VR adaptation (user request): the world is NOT frozen
        // under the death screen — the branch is skipped while the keyboard/
        // panel is up, so the tick path below keeps the swarm milling around
        // the corpse (dead input is inert; the score was pinned at show time).
        // Quest mode swaps the score card for the Quest Failed panel (no
        // highscores in quests) and freezes via the quest-panel guard above.
        if (_sim.GameOver && _sim.LastResult.PerkPendingCount == 0
            && !_keyboard.Active && !_gameOverPanel.Active)
        {
            // A pause opened just before death keeps the screen until resolved
            // (Resume resumes the death flow; Quit already routes to the menu).
            if (_pauseMenu.IsPaused)
            {
                return;
            }
            // Death cinematic (VR adaptation): the view zooms in on the corpse
            // over the death animation — same physical arena window, magnified
            // content. The sim KEEPS TICKING through the pacing window (falls
            // through to the tick path below) so the corpse frame ramp plays
            // (death_timer only drains on ticks) and the creatures keep
            // milling; the world freezes once the score panel is up.
            if (_deathTicks == 0)
            {
                _deathZoomCenter = _playerGame;
                // Zoom target: the corpse spans ~a quarter of the arena side.
                float corpseSize = _hasPlayerSnap ? Mathf.Max(_lastPlayer.Size, 1.0f) : 32.0f;
                _deathZoomMax = Mathf.Clamp(
                    DeathZoomCorpseFraction * GameWorldSize / corpseSize, 2.0f, 16.0f);
                // Perk-kill deaths (Grim Deal, a lost Fatal Lottery) set health
                // directly with no damage path, so the sim emits no death VO —
                // natively they're silent. VR adaptation: give every death the
                // trooper death cry; skip it when the damage path just played
                // one so ordinary deaths don't double up.
                if (!_audio.TrooperDieRecent(withinMs: 500))
                {
                    _audio.PlayTrooperDie(_playerGame);
                }
            }
            _deathTicks++;
            float zoomT = Mathf.Clamp(_deathTicks / (float)DeathZoomTicks, 0.0f, 1.0f);
            _diorama.SetViewZoom(
                1.0f + (_deathZoomMax - 1.0f) * Mathf.SmoothStep(0.0f, 1.0f, zoomT),
                _deathZoomCenter);
            if (_deathTicks >= DeathPacingTicks)
            {
                _audio.PlayUi(AudioBank.UiPanel);
                RecordRunStats();
                if (_gameMode == GameModeQuests)
                {
                    _questEndShown = true;
                    _questPanel.Show(completed: false, _questTitle, _sim.LastResult.ElapsedMsSim, hasNext: false);
                    return;
                }
                int score = _sim.LastResult.PlayerExperience;
                // Pin the score now: the sim keeps ticking under the death
                // screen, and posthumous kills must not drift the saved entry
                // away from the rank/panel computed here.
                _deathScore = score;
                _deathRank = HighscoreRank(score);
                // One composite death screen (base game_over.py two-phase panel):
                // the results panel appears immediately; a ranking score raises it
                // and puts the name-entry keyboard in front (phase 0), otherwise
                // it opens straight in the buttons phase.
                if (_deathRank < HighscoreTableMax)
                {
                    _gameOverPanel.ShowForNameEntry(_sim.LastResult, _deathRank);
                    _keyboard.Show(score);
                }
                else
                {
                    _gameOverPanel.Show(_sim.LastResult, _deathRank);
                }
                return;
            }
            // Fall through: the world stays live during the cinematic (dead
            // player input is inert in the sim).
        }

        // Quest completed (ABI v14): pace like the death flow, then show the
        // results panel and advance the persisted unlock frontier once.
        if (_gameMode == GameModeQuests && _sim.LastResult.QuestCompleted != 0 && !_questEndShown)
        {
            if (_pauseMenu.IsPaused)
            {
                return;
            }
            _deathTicks++;
            if (_deathTicks >= DeathPacingTicks)
            {
                _questEndShown = true;
                RecordRunStats();
                int gi = QuestGlobalIndex(_questKey);
                // advance_quest_unlocks (quests/results.py): completion always
                // raises the casual unlock; a HARDCORE completion additionally
                // raises the full index (which gates the Splitter Gun drop).
                int nextUnlock = gi + 1;
                bool changed = false;
                if (nextUnlock > _settings.QuestUnlockIndex)
                {
                    _settings.QuestUnlockIndex = nextUnlock;
                    changed = true;
                }
                if (_hardcore && nextUnlock > _settings.QuestUnlockIndexFull)
                {
                    _settings.QuestUnlockIndexFull = nextUnlock;
                    changed = true;
                }
                if (changed)
                {
                    _settings.Save();
                }
                _audio.PlayUi(AudioBank.UiPanel);
                // Quest 5.10 (global index 49) swaps "Next Quest" for the
                // native "Show End Note" finale flow.
                _questPanel.Show(completed: true, _questTitle, _sim.LastResult.ElapsedMsSim,
                    hasNext: gi + 1 <= _settings.QuestUnlockIndex && gi < 49,
                    showEndNote: gi == 49);
            }
            return;
        }
        // The death cinematic falls through here still counting; only a live
        // run resets the pacing counter.
        if (!_sim.GameOver)
        {
            _deathTicks = 0;
        }

        // Paused: freeze the sim (stop advancing). _Process still renders the last
        // frame and polls the pause panel so Resume/Quit work.
        if (_pauseMenu.IsPaused)
        {
            return;
        }

        HandSample left = SampleHand(_leftHand, 0);
        HandSample right = SampleHand(_rightHand, 1);
        (HandSample move, HandSample aim) = VrInput.ResolveRoles(left, right, _handSwap);
        Sim.HostInput input = VrInput.Build(move, aim, _playerGame, _deadZone);

        // Perk pick: a level-up leaves the pick PENDING but the game keeps running
        // (the Level Up! button shows) — the sim only pauses (perk_menu_active) once
        // the player opens the cards. Once a card is poked, feed the choice index;
        // the ABI applies it on the tick perk_choice_index is set.
        if (_sim.LastResult.PerkPendingCount > 0)
        {
            if (_perkMenu.Active)
            {
                input.PerkMenuActive = 1;
            }
            if (_perkChoice >= 0)
            {
                input.PerkChoiceIndex = _perkChoice;
                _perkChoice = -1;
            }
        }

        Sim.TickResult result = _sim.Tick(input);

        // Level-up cue: a new pending pick appeared this tick -> play the UI sound.
        if (result.PerkPendingCount > _prevPerkPending)
        {
            _audio.PlayLevelUp();
        }
        _prevPerkPending = result.PerkPendingCount;

        SnapshotView snap = _sim.CaptureSnapshot();
        float health = _prevHealth;
        bool reloadActive = _prevReloadActive;
        if (snap.Header.PlayerCount > 0)
        {
            Sim.PlayerSnap p = snap.Players[0];
            _playerGame = new Vector2(p.X, p.Y);
            _hud.Update(result, p);
            _lastPlayer = p;
            _hasPlayerSnap = true;
            health = p.Health;
            reloadActive = p.ReloadActive != 0;
        }
        _perkMenu.Update(snap);
        _diorama.PushSnapshot(snap);

        // Accumulate this tick's blood/scorch splats + corpse stamps (ABI v3).
        TerrainFxView terrainFx = _sim.CaptureTerrainFx();
        _diorama.RenderTerrainFx(terrainFx);

        // Play the audio this tick emitted, positioned relative to the player.
        AudioEventsView audio = _sim.CaptureAudio();
        _audio.Route(audio, _playerGame);

        UpdateHaptics(audio, health, reloadActive);
    }

    /// <summary>Haptics from this tick: a light fire pulse on the aim hand, a
    /// strong both-hand pulse when the player takes damage, and a tick on reload
    /// complete. Uses the OpenXR "haptic" output action.</summary>
    private void UpdateHaptics(in AudioEventsView audio, float health, bool reloadActive)
    {
        XRController3D aim = _handSwap ? _leftHand : _rightHand;
        if (audio.Header.ShotCount > 0)
        {
            Pulse(aim, 0.3f, 0.04f);
        }
        if (health < _prevHealth - 0.001f)
        {
            Pulse(_leftHand, 0.8f, 0.12f);
            Pulse(_rightHand, 0.8f, 0.12f);
        }
        if (_prevReloadActive && !reloadActive)
        {
            Pulse(aim, 0.5f, 0.05f);
        }
        _prevHealth = health;
        _prevReloadActive = reloadActive;
    }

    private static void Pulse(XRController3D hand, float amplitude, float duration)
    {
        if (hand.GetHasTrackingData())
        {
            hand.TriggerHapticPulse("haptic", 0.0, amplitude, duration, 0.0);
        }
    }

    private HandSample SampleHand(XRController3D hand, int index)
    {
        if (!hand.GetHasTrackingData())
        {
            _prevTrigger[index] = false;
            _prevGrip[index] = false;
            return new HandSample { ReticleGame = _playerGame };
        }

        Vector2 game = ComputeReticle(hand, out _, out _);

        bool triggerHeld = hand.GetFloat("trigger") > TriggerThreshold;
        bool triggerPressed = triggerHeld && !_prevTrigger[index];
        _prevTrigger[index] = triggerHeld;

        bool gripDown = hand.GetFloat("grip") > GripThreshold;
        bool reloadPressed = gripDown && !_prevGrip[index];
        _prevGrip[index] = gripDown;

        return new HandSample
        {
            ReticleGame = game,
            TriggerHeld = triggerHeld,
            TriggerPressed = triggerPressed,
            ReloadPressed = reloadPressed,
        };
    }

    // ---- Rendering: reticles + interpolated diorama at headset refresh ----

    public override void _Process(double delta)
    {
        HandleRecenter();

        // While the main menu (or settings opened from it) owns the screen, the
        // aim/move reticles are off — the hands are poking menu items, not aiming.
        if (MenuOwnsScreen)
        {
            _leftReticle.Visible = false;
            _rightReticle.Visible = false;
            _leftGuide.Visible = false;
            _rightGuide.Visible = false;
        }
        else
        {
            UpdateHandVisual(_leftHand, _leftReticle, _leftGuide, isMoveHand: !_handSwap);
            UpdateHandVisual(_rightHand, _rightReticle, _rightGuide, isMoveHand: _handSwap);
        }
        UpdateAimOverlays();

        // Ease the whole HUD out over the perk pick and on death instead of
        // hard-toggling (survival_mode.py hud_alpha, 400 ms transition).
        float hudTarget = (_perkMenu.Active || _questPanel.Active || _endNote.Active
            || (_sim != null && _sim.GameOver)) ? 0.0f : 1.0f;
        _hudFade = Mathf.MoveToward(_hudFade, hudTarget, (float)delta / 0.4f);
        _hud.SetFade(_hudFade);

        if (_sim != null)
        {
            _diorama.Interpolate((float)Engine.GetPhysicsInterpolationFraction());
        }
        // Level-up button shows beside the arena while a perk pick is pending and
        // the cards aren't already open (and we're in play, not a menu/pause); the
        // badge shows how many picks have accumulated.
        int pendingPicks = _sim?.LastResult.PerkPendingCount ?? 0;
        _pauseMenu.SetLevelUp(_perkMenu.Pending && !_perkMenu.Active && !_pauseMenu.IsPaused && !MenuOwnsScreen, pendingPicks);

        // Menus must respond regardless of sim state (so the player can interact
        // and report even if the native lib failed to load).
        PollMenuPoke();
        UpdateHandMarkers();
        if (_debug)
        {
            UpdatePokeMarkers();
        }
    }

    /// <summary>Debug: show a marker at each controller's poke tip so the physical
    /// poke point is visible against the menu buttons.</summary>
    private void UpdatePokeMarkers()
    {
        UpdateMarker(0, _leftHand);
        UpdateMarker(1, _rightHand);
    }

    private void UpdateMarker(int index, XRController3D hand)
    {
        bool tracking = hand.GetHasTrackingData();
        _pokeMarkers[index].Visible = tracking;
        if (tracking)
        {
            _pokeMarkers[index].GlobalPosition = PokeTip(hand);
        }
    }

    /// <summary>Feed controller tips to the diegetic menus (poke) each rendered
    /// frame. The pause toggle is always live; the perk cards only while a pick is
    /// pending. The tip is a point just ahead of the grip pose (controller front).</summary>
    private void PollMenuPoke()
    {
        Span<HandProbe> probes = stackalloc HandProbe[2];
        probes[0] = MakeProbe(_leftHand);
        probes[1] = MakeProbe(_rightHand);
        ReadOnlySpan<HandProbe> p = probes;

        // The validation checklist stands off to the right of the arena and is always
        // pokeable, in any game state (menu, gameplay, paused) — poll it first.
        _checklist.PollPoke(p);
        _debugMenu.PollPoke(p);

        // Main menu owns the screen while open: poke its items. Gameplay menus stay
        // dormant.
        if (_mainMenu.IsOpen)
        {
            _mainMenu.PollPoke(p);
            return;
        }
        if (_playGameMenu.IsOpen)
        {
            _playGameMenu.PollPoke(p);
            return;
        }
        if (_questSelect.IsOpen)
        {
            _questSelect.PollPoke(p);
            return;
        }
        if (_statsMenu.IsOpen)
        {
            _statsMenu.PollPoke(p);
            return;
        }
        if (_databaseMenu.IsOpen)
        {
            _databaseMenu.PollPoke(p);
            return;
        }
        // Options / VR Settings opened from the main menu (sim gated by
        // MenuOwnsScreen): poke whichever is showing.
        if (_optionsFromMenu && (_optionsOpen || _vrSettingsOpen))
        {
            if (_vrSettingsOpen)
            {
                _settingsMenu.PollPoke(p);
            }
            else
            {
                _optionsMenu.PollPoke(p);
            }
            return;
        }

        if (_startPrompt.Pending)
        {
            _startPrompt.PollPoke(p);
            return;
        }
        // Quest end panel (completed or failed): its buttons own the poke.
        if (_questPanel.Active)
        {
            _questPanel.PollPoke(p);
            return;
        }
        if (_endNote.Active)
        {
            _endNote.PollPoke(p);
            return;
        }
        // Death screen: keyboard (name-entry phase) and panel can be up together.
        if (_sim != null && _sim.GameOver && (_keyboard.Active || _gameOverPanel.Active))
        {
            if (_keyboard.Active)
            {
                _keyboard.PollPoke(p);
            }
            _gameOverPanel.PollPoke(p);
            return;
        }

        _pauseMenu.PollPoke(p);
        if (_pauseMenu.IsPaused && _optionsOpen)
        {
            _optionsMenu.PollPoke(p);
        }
        if (_pauseMenu.IsPaused && _vrSettingsOpen)
        {
            _settingsMenu.PollPoke(p);
        }
        // Perk cards only while a pick is pending AND the pause menu isn't up (they
        // share the space; the pause panel takes precedence). While paused the sim
        // is frozen so Update won't run — hide the cards explicitly.
        if (_perkMenu.Active && _pauseMenu.IsPaused)
        {
            _perkMenu.Visible = false;
        }
        else if (_perkMenu.Active)
        {
            _perkMenu.Visible = true;
            _perkMenu.PollPoke(p);
            if (_perkMenu.Chosen >= 0)
            {
                _perkChoice = _perkMenu.Chosen;
                _perkMenu.Chosen = -1;
            }
        }

        // The pause-menu overlays (options + checklist opened from pause) are only
        // valid while paused (e.g. the pause toggle was poked off underneath one);
        // reconcile. Options opened from the MAIN MENU is exempt — it lives outside
        // the paused state.
        if (!_pauseMenu.IsPaused && !_optionsFromMenu)
        {
            if (_optionsOpen || _vrSettingsOpen)
            {
                _vrSettingsOpen = false;
                _settingsMenu.SetShown(false);
                CloseOptions();
            }
        }
    }

    private HandProbe MakeProbe(XRController3D hand)
        => hand.GetHasTrackingData()
            ? new HandProbe(true, PokeTip(hand), hand.GetFloat("grip") > GripThreshold)
            : default;

    private void SetDebug(bool on)
    {
        _debug = on;
        _settings.Debug = on;
        _settings.Save();
        _debugMenu.SetShown(on);
        // Deliberately NOT wired to _diorama.SetDebug: that overlay is the
        // per-creature facing needle, a one-off sprite-calibration tool. The
        // settings debug flag means "fx showcase" now; flip the needle on in
        // code if a new sheet ever needs recalibrating.
        if (!on)
        {
            foreach (MeshInstance3D m in _pokeMarkers)
            {
                m.Visible = false;
            }
        }
    }

    /// <summary>0-based insertion rank of a score in the current mode's local
    /// highscore table (existing entries win ties, matching rank_index in the
    /// base game).</summary>
    private int HighscoreRank(int score)
    {
        int rank = 0;
        foreach (HighscoreEntry e in _settings.HighscoresFor(_gameMode))
        {
            if (e.Score >= score)
            {
                rank++;
            }
        }
        return rank;
    }

    /// <summary>Name-entry phase done: record the name (empty = skip saving, like
    /// the base game's blank-name guard) and reveal the panel's buttons phase.</summary>
    private void SubmitHighscoreName(string name)
    {
        if (_sim == null)
        {
            return;
        }
        // The score pinned when the death screen opened, NOT the live tick
        // result: the world keeps simulating during name entry and posthumous
        // kills would otherwise drift the entry away from the shown rank.
        int score = _deathScore;
        if (!string.IsNullOrEmpty(name))
        {
            _settings.AddHighscore(name, score, _gameMode);
        }
        GD.Print($"CrimsonVR: highscore {(string.IsNullOrEmpty(name) ? "(skipped)" : name)} - {score}");
        _keyboard.Dismiss();
        _gameOverPanel.ShowButtons();
    }

    /// <summary>Results panel "Play Again": start a fresh run immediately in
    /// the same mode.</summary>
    private void PlayAgain()
    {
        if (_sim == null)
        {
            return;
        }
        _gameOverPanel.Dismiss();
        _questEndShown = false;
        RestartSession();
        // Fresh run: fade the old tune out; the first hit rolls a new one.
        _audio.StopMusic();
    }

    // The poke point is the grip position - i.e. the centre of the visible hand
    // marker sphere, so the sphere the player sees IS the collider (no offset).
    private static Vector3 PokeTip(XRController3D hand)
        => hand.GlobalPosition;

    /// <summary>Shared vertical projection of a controller onto the arena plane
    /// (PLAN §4). Returns the clamped game-space point; also reports whether the
    /// hand is over the arena footprint and the clamped world position.</summary>
    private Vector2 ComputeReticle(XRController3D hand, out bool over, out Vector3 clampedWorld)
    {
        float planeY = _arenaRoot.GlobalPosition.Y;
        Vector3 handPos = hand.GlobalPosition;
        Vector3 hit = Mapper.ProjectVertically(handPos, planeY);
        Vector3 arenaLocal = _arenaRoot.ToLocal(hit);
        over = Mapper.IsOverArena(arenaLocal, ArenaSideMeters);
        // Off-arena hands clamp along the PLAYER-to-hand line (not per-axis):
        // the cursor stays on the aiming line at the boundary instead of being
        // dragged sideways toward the nearest corner.
        Vector2 raw = Mapper.ArenaLocalToGameUnclamped(arenaLocal, ArenaSideMeters, GameWorldSize);
        Vector2 game = Mapper.ClampGameTowards(_playerGame, raw, GameWorldSize);
        Vector3 clampedLocal = Mapper.GameToArenaLocal(game, ArenaSideMeters, GameWorldSize);
        clampedWorld = _arenaRoot.ToGlobal(clampedLocal);
        return game;
    }

    private void UpdateHandVisual(XRController3D hand, Node3D reticle, Node3D guide, bool isMoveHand)
    {
        bool tracking = hand.GetHasTrackingData();
        // Only the MOVE hand keeps a static reticle (the ui_aim ring art): the
        // aim point is marked by the dynamic spread ring + reload gauge, and
        // the old ui_cursor pointer is retired outright.
        reticle.Visible = tracking && isMoveHand;
        guide.Visible = tracking;
        if (!tracking)
        {
            return;
        }

        ComputeReticle(hand, out bool over, out Vector3 clampedWorld);
        if (isMoveHand)
        {
            reticle.GlobalPosition = clampedWorld + new Vector3(0, 0.002f, 0);

            var mesh = (MeshInstance3D)reticle;
            var material = (StandardMaterial3D)mesh.MaterialOverride;
            material.AlbedoTexture = _aimTex;
            float triggerValue = hand.GetFloat("trigger");
            var baseColor = new Color(0.2f, 0.5f, 1.0f);
            // Opaque reticle material: dim by darkening RGB (an alpha change would
            // be invisible without alpha transparency enabled).
            material.AlbedoColor = over
                ? baseColor.Lerp(Colors.White, triggerValue)
                : baseColor.Darkened(0.6f);
        }

        // Vertical guide line from the controller down to the plane point.
        float planeY = _arenaRoot.GlobalPosition.Y;
        Vector3 handPos = hand.GlobalPosition;
        float guideHeight = Mathf.Max(0.02f, handPos.Y - planeY);
        guide.GlobalPosition = new Vector3(handPos.X, planeY + guideHeight * 0.5f, handPos.Z);
        ((MeshInstance3D)guide).Scale = new Vector3(1, guideHeight, 1);
    }

    private void HandleRecenter()
    {
        // Request a recenter on the rising edge of either hand's menu/AX button.
        // The initial _recenterPending places the table on the first valid head
        // frame (PLAN §5). Reload uses grip, so AX stays free for recenter.
        _framesSinceStart++;
        bool held = (_leftHand.GetHasTrackingData() && _leftHand.IsButtonPressed("menu_button"))
                    || (_rightHand.GetHasTrackingData() && _rightHand.IsButtonPressed("ax_button"));
        if (held && !_prevRecenterHeld)
        {
            _recenterPending = true;
        }
        _prevRecenterHeld = held;

        if (!_recenterPending || !_xrActive || _framesSinceStart < 15)
        {
            return;
        }
        RecenterArena();
        _recenterPending = false;
    }

    private void RecenterArena()
    {
        // Place the arena ArenaDistance in front of the head and a comfortable
        // drop BELOW the current head pose, yawed to face the player. Tracking
        // head height (rather than a fixed world height) is what makes recenter
        // reset the vertical too, so a standing player doesn't get the table far
        // below them. Forward is flattened to horizontal so table tilt never
        // follows head pitch. (Height becomes a tunable in the M4 arena-
        // customisation tool — PLAN §5.)
        Vector3 headPos = _camera.GlobalPosition;
        Vector3 forward = -_camera.GlobalTransform.Basis.Z;
        forward.Y = 0.0f;
        if (forward.LengthSquared() < 1e-5f)
        {
            forward = Vector3.Forward;
        }
        forward = forward.Normalized();

        Vector3 pos = headPos + forward * ArenaDistanceMeters;
        // At least MinVerticalDropMeters below the head; never below the floor.
        float drop = Mathf.Max(VerticalDropMeters, MinVerticalDropMeters);
        pos.Y = Mathf.Max(headPos.Y - drop, 0.05f);
        float yaw = Mathf.Atan2(forward.X, forward.Z);
        _arenaRoot.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, 0)), pos);
    }

    private static uint TryQueryAbiVersion()
    {
        try
        {
            return Sim.AbiVersion();
        }
        catch (System.Exception)
        {
            return 0;
        }
    }
}
