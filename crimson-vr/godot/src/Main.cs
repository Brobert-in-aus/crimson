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
    // 0.4 m/side is the UI REFERENCE scale: every menu, panel and prompt sizes
    // itself from it (Build(ArenaSideMeters)), and it remains the unit the
    // diorama geometry is built in. It is no longer the player's reach envelope
    // — see the control rectangle below.
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

    // ---- Control rectangle (hands) vs playfield (eyes) ----
    // Adopted from OpenTyrianVR's hand-rectangle steering: the hands project
    // onto their OWN small rectangle, and its extent — not the drawn arena's —
    // is what maps onto the playfield. Previously the two were the same 0.4 m
    // square, so the arena had to stay inside arm's reach, which is what forced
    // the player to sit looking down at a table. Split apart, hand travel is
    // fixed by ControlRectSide while the playfield is free to be large, far and
    // tilted up into a comfortable gaze line.
    //
    // The control rectangle deliberately reproduces the old table EXACTLY —
    // same side, same near-edge offset, same drop, and level — so the hand
    // mapping is bit-for-bit what previous headset passes were tuned against
    // and this first pass isolates one variable: the picture moved, the controls
    // did not. Shrinking it (less arm travel, now that the board no longer has
    // to be within reach) is the obvious next trial, but it costs precision —
    // at 0.40 m one mm of hand tremor is ~2.6 game units, at 0.30 m ~3.4 — so
    // it is worth changing on its own, after the tilt is judged.
    // Level by default: hovering over a level surface is the proven gesture, and
    // the projection drops along the rect's own normal, so tilting changes that
    // feel. Tilt is exposed for in-headset trials.
    private const float ControlRectSideMeters = 0.40f;
    private const float ControlRectNearEdgeMeters = 0.10f;
    private const float ControlRectDropMeters = 0.5f;
    private const float ControlRectPitchDegrees = 0.0f;

    // Playfield presentation. Scale multiplies the diorama's built geometry
    // (uniform, so decals/shadows/lifts scale coherently); pitch lifts the FAR
    // edge so the board tilts toward the player like a cabinet screen. Placement
    // is by near edge again, but now measured against the head rather than
    // reach: at 3x/40 deg the board's centre lands near eye level instead of
    // ~60 deg down. All four are first-pass values to dial in-headset.
    private const float PlayfieldScale = 3.0f;
    private const float PlayfieldPitchDegrees = 40.0f;
    private const float PlayfieldNearEdgeMeters = 0.6f;
    private const float PlayfieldNearDropMeters = 0.45f;

    // Live values behind the Layout panel's sliders; seeded from the constants
    // above. Placement is recomputed against the LAST RECENTER pose rather than
    // the live head pose, so dragging a slider never makes the board chase the
    // player's head while they look at the slider.
    private float _playfieldScale = PlayfieldScale;
    private float _playfieldPitch = PlayfieldPitchDegrees;
    private float _playfieldNearEdge = PlayfieldNearEdgeMeters;
    private float _playfieldNearDrop = PlayfieldNearDropMeters;
    private float PlayfieldSideMeters => ArenaSideMeters * _playfieldScale;
    private Vector3 _recenterHeadPos;
    private Vector3 _recenterForward = Vector3.Forward;
    private bool _hasRecentered;

    // Aim pillars: vertical lines rising from each hand's cursor so the aim
    // point is findable on a large tilted board (the flat reticle art alone is
    // hard to pick out at distance). Length is a FRACTION of the arena side so
    // it scales with the board instead of shrinking into it.
    private const float AimLineFractionDefault = 0.30f;
    private float _aimLineFraction = AimLineFractionDefault;
    private float _spriteHeightScale = 1.0f;
    private LayoutMenu _layoutMenu = null!;

    private ControlMode _controlMode = ControlMode.Cabinet;

    /// <summary>The plane the hands are projected onto. In CABINET this is the
    /// separate control rectangle and the board is free to be large, far and
    /// tilted. In TABLETOP it is the board itself — the original scheme, where
    /// reaching into the arena IS the control — so the board must stay within
    /// arm's reach. Everything downstream is identical between the two; the mode
    /// only chooses this node and the placement defaults.</summary>
    private Node3D ControlSurface => _controlMode == ControlMode.Cabinet ? _controlRect : _playfieldRoot;

    /// <summary>Side length of the control surface in ITS OWN local units. The
    /// playfield's local frame is the unscaled 0.4 m reference square (ToLocal
    /// divides out the node scale), so this is ArenaSideMeters regardless of how
    /// large the board is drawn — which is exactly what keeps hand travel
    /// unchanged when the arena scale slider moves in Tabletop mode.</summary>
    private float ControlSurfaceSide =>
        _controlMode == ControlMode.Cabinet ? ControlRectSideMeters : ArenaSideMeters;
    private Node3D _leftAimPillar = null!;
    private Node3D _rightAimPillar = null!;
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
    // Sibling of _arenaRoot, not a child: the playfield carries its own scale,
    // pitch and placement so growing/tilting the board never drags the menus
    // and panels (which stay parented to _arenaRoot at the reference scale).
    private Node3D _playfieldRoot = null!;
    // The hands' input surface. Everything the player POINTS AT lives here;
    // everything they LOOK AT lives under _playfieldRoot.
    private Node3D _controlRect = null!;
    // Cancels the playfield tilt for the HUD so the scoreboard stays vertical.
    private Node3D _hudPivot = null!;
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
    private ControlsScreen _controlsScreen = null!;
    private SettingsMenu _settingsMenu = null!; // VR Settings submenu (hand/dead-zone/debug)
    private bool _optionsOpen;
    private bool _vrSettingsOpen;
    private bool _controlsOpen;
    private bool _arenaLayoutOpen;
    private ArenaLayoutMenu _arenaLayout = null!;
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
        || (_optionsFromMenu && (_optionsOpen || _vrSettingsOpen || _controlsOpen || _arenaLayoutOpen));
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
        // The healthy build-stamp line is a dev readout and stays behind the
        // debug gate so it is out of recorded footage. A FAILURE line always
        // shows regardless: diagnostics must remain visible in-headset when the
        // native side did not come up, which is the whole point of the label.
        _status.Visible = _sim == null || _settings.Debug;
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

        // The playfield and everything registered TO it (terrain, entities,
        // positional audio, the scoreboard) hang off PlayfieldRoot, so they
        // inherit its scale and tilt. Menus and panels stay on ArenaRoot.
        _diorama = new Diorama();
        _playfieldRoot.AddChild(_diorama);
        _diorama.Configure(ArenaSideMeters, GameWorldSize);

        // Audio is anchored under the playfield so its players sit on the board
        // (positions are arena-local meters, like the diorama) and pan/attenuate
        // from where the action actually is now that the board has moved.
        _audio = new AudioBank();
        _playfieldRoot.AddChild(_audio);
        _audio.Configure(ArenaSideMeters, GameWorldSize);
        // Every diegetic poke button plays a UI click cue (menu = click, keyboard
        // keys = type, Enter = type-enter), via the global VrButton press hook.
        VrButton.OnAnyPress = kind => _audio.PlayUi(kind);

        // HUD scoreboard, standing at the playfield's far edge. It rides the
        // board's position and scale but NOT its tilt: a sign leaning back 40
        // degrees with the floor is hard to read and looks pasted on, so a pivot
        // at its far-edge anchor cancels the playfield pitch and the panel stays
        // world-vertical. The pivot carries the anchor offset; Hud.Build places
        // itself relative to that anchor (its own float above the plane only).
        _hudPivot = new Node3D
        {
            Name = "HudPivot",
            Position = new Vector3(0.0f, 0.0f, ArenaSideMeters * 0.5f * Diorama.FloorMarginScale),
            RotationDegrees = new Vector3(PlayfieldPitchDegrees, 0.0f, 0.0f),
        };
        _playfieldRoot.AddChild(_hudPivot);
        _hud = new Hud();
        _hudPivot.AddChild(_hud);
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
        // EdgeRoot is mounted by SetControlMode, once the mode is known.
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
        _optionsMenu.OnControls += OpenControls;
        _optionsMenu.OnSfxChanged += v => { _settings.SfxVolume = v; _audio.SetSfxVolume(v); _settings.Save(); };
        _optionsMenu.OnMusicChanged += v => { _settings.MusicVolume = v; _audio.SetMusicVolume(v); _settings.Save(); };
        _optionsMenu.OnDetailChanged += v => { _settings.GraphicsDetail = v; _diorama.SetGraphicsDetail(v); _settings.Save(); };
        _optionsMenu.OnInfoTextsChanged += v => { _settings.UiInfoTexts = v; _settings.Save(); };

        // VR Settings submenu (opened from Options): hand-swap + dead-zone + debug.
        _settingsMenu = new SettingsMenu();
        _arenaRoot.AddChild(_settingsMenu);
        _settingsMenu.Build(ArenaSideMeters, _handSwap, _deadZone, _settings.Debug,
            _settings.PokeMarkers, (ControlMode)_settings.ControlMode,
            _settings.RenderScale, _settings.Msaa,
            LoadReticleTex("ui_rectOn.png"), LoadReticleTex("ui_rectOff.png"));
        _settingsMenu.OnBack += CloseVrSettings;
        _settingsMenu.OnHandSwapChanged += v =>
        {
            _handSwap = v;
            _settings.HandSwap = v;
            _settings.Save();
            _controlsScreen.SetHandSwap(v);
        };
        _settingsMenu.OnDeadZoneChanged += v => { _deadZone = v; _settings.DeadZone = v; _settings.Save(); };
        _settingsMenu.OnDebugChanged += SetDebug;
        _settingsMenu.OnControlModeChanged += m => SetControlMode(m);
        _settingsMenu.OnArenaLayout += OpenArenaLayout;
        _settingsMenu.OnPokeMarkersChanged += v =>
        {
            _settings.PokeMarkers = v;
            _settings.Save();
            // Turning it off must clear them now: the per-frame updater simply
            // stops running, so a stale marker would otherwise hang in the air.
            if (!PokeMarkersVisible)
            {
                HidePokeMarkers();
            }
        };
        _settingsMenu.OnRenderScaleChanged += v => { _settings.RenderScale = v; ApplyRenderQuality(); _settings.Save(); };
        _settingsMenu.OnMsaaChanged += v => { _settings.Msaa = v; ApplyRenderQuality(); _settings.Save(); };

        // Controls reference card (the base Options screen's Controls button):
        // read-only VR mapping, honouring hand-swap.
        _controlsScreen = new ControlsScreen();
        _arenaRoot.AddChild(_controlsScreen);
        _controlsScreen.Build(ArenaSideMeters, _handSwap);
        _controlsScreen.OnBack += CloseControls;

        // Validation checklist: a standing panel 90 deg to the RIGHT of the arena,
        // always visible so it can be ticked off in any game state, results persisted.
        _checklist = new ValidationChecklist();
        _arenaRoot.AddChild(_checklist);
        _checklist.Build(ArenaSideMeters, _settings.Checklist);
        _checklist.OnItemChanged += (id, state) => { _settings.Checklist[id] = state; _settings.Save(); };
        // Dev overlay, same gate as the debug menu below it: this was shown
        // unconditionally, so it stood in every session — including recorded
        // footage — with no way to dismiss it.
        _checklist.SetShown(_settings.Debug);

        // Debug FX menu: runtime force-toggles for the effect render passes,
        // mirrored on the player's left. Visible only while debug is on.
        _debugMenu = new DebugMenu();
        _arenaRoot.AddChild(_debugMenu);
        _debugMenu.Build(ArenaSideMeters);
        _debugMenu.SetShown(_settings.Debug);

        // Layout tuning panel, mirrored on the player's right. Every value it
        // drives was previously a constant needing a full Quest rebuild to try.
        _layoutMenu = new LayoutMenu();
        _arenaRoot.AddChild(_layoutMenu);
        _layoutMenu.Build(
            ArenaSideMeters,
            _spriteHeightScale, v => { _spriteHeightScale = v; _diorama.SetHeightScale(v); },
            _aimLineFraction, v => _aimLineFraction = v,
            LoadReticleTex("ui_rectOn.png"), LoadReticleTex("ui_rectOff.png"));
        _layoutMenu.OnLogPressed += LogArenaPlacement;
        _layoutMenu.SetShown(_settings.Debug);

        // Apply the saved control mode now that the layout panel and the pause
        // buttons both exist: it mounts EdgeRoot, sets the board placement for
        // the mode, and shows or hides the control rectangle.
        // Player-facing arena/layout screen, opened from VR Settings.
        _arenaLayout = new ArenaLayoutMenu();
        _arenaRoot.AddChild(_arenaLayout);
        _arenaLayout.Build(
            ArenaSideMeters,
            _playfieldScale, v => { _playfieldScale = v; ApplyPlayfieldPlacement(); SaveArenaPlacement(); },
            _playfieldPitch, v => { _playfieldPitch = v; ApplyPlayfieldPlacement(); SaveArenaPlacement(); },
            _playfieldNearEdge, v => { _playfieldNearEdge = v; ApplyPlayfieldPlacement(); SaveArenaPlacement(); },
            _playfieldNearDrop, v => { _playfieldNearDrop = v; ApplyPlayfieldPlacement(); SaveArenaPlacement(); },
            LoadReticleTex("ui_rectOn.png"), LoadReticleTex("ui_rectOff.png"));
        _arenaLayout.OnBack += CloseArenaLayout;
        _arenaLayout.OnReset += ResetUiLayout;

        // A saved placement wins over the mode default: the player put the board
        // where they wanted it, and a mode they never switched should not undo
        // that on every launch.
        SetControlMode((ControlMode)_settings.ControlMode, loadDefaults: !_settings.HasArenaPlacement);
        if (_settings.HasArenaPlacement)
        {
            _playfieldScale = _settings.ArenaScale;
            _playfieldPitch = _settings.ArenaPitch;
            _playfieldNearEdge = _settings.ArenaDistance;
            _playfieldNearDrop = _settings.ArenaDrop;
            _arenaLayout.SyncPlacement(_playfieldScale, _playfieldPitch, _playfieldNearEdge, _playfieldNearDrop);
            ApplyPlayfieldPlacement();
        }
        BuildUiEditables();
        SetLogButtonsVisible(_settings.Debug);

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
        _pauseMenu.SetMenuVisible(visible);
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

    /// <summary>Open the Arena &amp; Layout screen. Edit mode runs for exactly as
    /// long as this screen is open, which is what keeps it from ever leaking
    /// into gameplay: the only way out is the same action that ends it.</summary>
    private void OpenArenaLayout()
    {
        _vrSettingsOpen = false;
        _settingsMenu.SetShown(false);
        _arenaLayoutOpen = true;
        _arenaLayout.SetShown(true);
        SetUiEditMode(true);
    }

    private void CloseArenaLayout()
    {
        _arenaLayoutOpen = false;
        SetUiEditMode(false);
        _arenaLayout.SetShown(false);
        _vrSettingsOpen = true;
        _settingsMenu.SetShown(true);
    }

    private void SaveArenaPlacement()
    {
        _settings.ArenaScale = _playfieldScale;
        _settings.ArenaPitch = _playfieldPitch;
        _settings.ArenaDistance = _playfieldNearEdge;
        _settings.ArenaDrop = _playfieldNearDrop;
        _settings.Save();
    }

    private void OpenControls()
    {
        _optionsOpen = false;
        _controlsOpen = true;
        _optionsMenu.SetShown(false);
        _controlsScreen.SetShown(true);
    }

    private void CloseControls()
    {
        _controlsOpen = false;
        _controlsScreen.SetShown(false);
        _optionsOpen = true;
        _optionsMenu.SetShown(true);
    }

    private void CloseVrSettings()
    {
        _vrSettingsOpen = false;
        // Belt and braces: edit mode is owned by the Arena & Layout screen and
        // ended by CloseArenaLayout, but leaving VR Settings must never drop the
        // player into a match with the action buttons inert.
        SetUiEditMode(false);
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
        // Initial positions are placeholders; RecenterArena() repositions both
        // roots in front of the head once tracking is valid (see HandleRecenter).
        _arenaRoot = new Node3D { Position = new Vector3(0.0f, ArenaHeightMeters, -ArenaDistanceMeters) };
        AddChild(_arenaRoot);

        _playfieldRoot = new Node3D
        {
            Name = "PlayfieldRoot",
            Position = new Vector3(0.0f, ArenaHeightMeters, -ArenaDistanceMeters),
            Scale = Vector3.One * PlayfieldScale,
        };
        AddChild(_playfieldRoot);

        BuildControlRect();

        // The visible ground is the Diorama terrain floor (textured from the base
        // slot, extended past the playfield, greyed by fog). The old brown
        // placeholder plane + rim were removed: they sat on top of the floor
        // (hiding the terrain and z-fighting its edge). A larger world floor at
        // foot level (BuildWorldFloor) extends that same ground around the player.
    }

    /// <summary>Build the hands' control surface: a small level square the
    /// player hovers over, sized by <see cref="ControlRectSideMeters"/> and
    /// placed by RecenterArena. It is drawn faintly because it is no longer
    /// coincident with the visible playfield — without a mark, the limits of
    /// the control area would be invisible and the player would run their hand
    /// off the edge with no cue. Kept deliberately dim so it reads as a desk
    /// surface, not a second thing to look at.</summary>
    private void BuildControlRect()
    {
        _controlRect = new Node3D { Name = "ControlRect" };
        AddChild(_controlRect);

        // Sibling, not child: the action buttons follow the control surface's
        // placement but must not inherit the size or tilt the player gives it.
        _uiAnchor = new Node3D { Name = "UiAnchor" };
        AddChild(_uiAnchor);

        float s = ControlRectSideMeters;
        _controlRect.AddChild(new MeshInstance3D
        {
            Name = "Bounds",
            Mesh = new PlaneMesh { Size = new Vector2(s, s) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.55f, 0.9f, 0.10f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        });

        // A brighter rim so the edge (where the cursor stops clamping) is
        // legible at a glance without looking down at it.
        float w = s * 0.012f;
        float half = s * 0.5f;
        var rimMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.65f, 1.0f, 0.35f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        foreach ((float cx, float cz, float sx, float sz) in new[]
        {
            (0.0f, -half, s + w, w),
            (0.0f, half, s + w, w),
            (-half, 0.0f, w, s + w),
            (half, 0.0f, w, s + w),
        })
        {
            _controlRect.AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(sx, sz) },
                MaterialOverride = rimMat,
                Position = new Vector3(cx, 0.0005f, cz),
            });
        }
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
        // Cursor art lives ON the board (inherits its tilt/scale); the guide
        // lines belong to the HANDS and stay in world space, dropping to the
        // control rectangle.
        _playfieldRoot.AddChild(_leftReticle);
        _playfieldRoot.AddChild(_rightReticle);
        AddChild(_leftGuide);
        AddChild(_rightGuide);
        _leftAimPillar = MakeAimPillar(new Color(0.2f, 0.5f, 1.0f, 0.5f));
        _rightAimPillar = MakeAimPillar(new Color(1.0f, 0.3f, 0.25f, 0.5f));
        _playfieldRoot.AddChild(_leftAimPillar);
        _playfieldRoot.AddChild(_rightAimPillar);
        BuildAimOverlays();
    }

    /// <summary>A cursor pillar: a pivot (child of PlayfieldRoot, so it rides the
    /// board's position and scale) holding a cylinder that is counter-rotated to
    /// stand WORLD-vertical rather than normal to a tilted board — a consistent
    /// upright pillar reads as a position marker from any angle, where a leaning
    /// one reads as part of the scenery. Length is set each frame from the
    /// arena-side fraction; the mesh is unit-height so scale.Y IS the length.</summary>
    private static Node3D MakeAimPillar(Color color)
    {
        // Hidden until gameplay actually starts: the first frames are the main
        // menu, and a pillar defaulting to visible stands on the board there.
        var pivot = new Node3D { Visible = false };
        pivot.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0015f, BottomRadius = 0.0015f, Height = 1.0f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });
        return pivot;
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
        _playfieldRoot.AddChild(_reloadGauge);
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
        _playfieldRoot.AddChild(_spreadRing);
    }

    /// <summary>Position/refresh the aim-hand overlays each rendered frame: the
    /// reload clock at the aim reticle while reloading (pointer sweep = reload
    /// progress) and the spread ring sized by the live spread_heat (ABI v11).</summary>
    private void UpdateAimOverlays()
    {
        bool inPlay = InPlay;
        XRController3D aimHand = _handSwap ? _leftHand : _rightHand;
        if (!inPlay || !aimHand.GetHasTrackingData() || _lastPlayer.Health <= 0.0f)
        {
            _reloadGauge.Visible = false;
            _spreadRing.Visible = false;
            return;
        }
        ComputeReticle(aimHand, out _, out Vector3 local);

        bool reloading = _lastPlayer.ReloadActive != 0
            && _lastPlayer.ReloadTimerMax > 1e-6f && _lastPlayer.ReloadTimer > 1e-6f;
        _reloadGauge.Visible = reloading;
        if (reloading)
        {
            // Local lift: +y is the BOARD's normal, so this stays a hair above
            // the surface however the board is tilted.
            _reloadGauge.Position = local + new Vector3(0.0f, 0.004f, 0.0f);
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
        _spreadRing.Position = local + new Vector3(0.0f, 0.003f, 0.0f);
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
            _hud.Update(result, p, snap.Header);
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
        UpdateAimPillars();
        UpdateControlRectVisibility();

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
        if (PokeMarkersVisible)
        {
            UpdatePokeMarkers();
        }
    }

    /// <summary>Whether poke-tip markers should be drawn: either the player asked
    /// for them outright, or Debug is on and shows every dev overlay.</summary>
    private bool PokeMarkersVisible => _debug || _settings.PokeMarkers;

    private void HidePokeMarkers()
    {
        foreach (MeshInstance3D m in _pokeMarkers)
        {
            m.Visible = false;
        }
    }

    /// <summary>Show a marker at each controller's poke tip so the physical poke
    /// point is visible against the menu buttons. No longer debug-only: the tips
    /// sit over the control rectangle, not the playfield, so they obscure nothing
    /// during play (see UserSettings.PokeMarkers).</summary>
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

        // UI edit mode first: while a corner is held, every normal poke is
        // suppressed for the frame. Grabs use grip and presses use the tip, but
        // a hand closed around the pause button's corner is also sitting right
        // on its face — without this, repositioning it would pause the game.
        if (PollUiEdit(p))
        {
            return;
        }
        PollLogButtons(p);

        // The validation checklist stands off to the right of the arena and is always
        // pokeable, in any game state (menu, gameplay, paused) — poll it first.
        _checklist.PollPoke(p);
        _debugMenu.PollPoke(p);
        _layoutMenu.PollPoke(p);

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
        // Options / VR Settings / Controls opened from the main menu (sim gated
        // by MenuOwnsScreen): poke whichever is showing.
        if (_optionsFromMenu && (_optionsOpen || _vrSettingsOpen || _controlsOpen || _arenaLayoutOpen))
        {
            if (_arenaLayoutOpen)
            {
                _arenaLayout.PollPoke(p);
            }
            else if (_vrSettingsOpen)
            {
                _settingsMenu.PollPoke(p);
            }
            else if (_controlsOpen)
            {
                _controlsScreen.PollPoke(p);
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
        if (_pauseMenu.IsPaused && _controlsOpen)
        {
            _controlsScreen.PollPoke(p);
        }
        if (_pauseMenu.IsPaused && _arenaLayoutOpen)
        {
            _arenaLayout.PollPoke(p);
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
            if (_optionsOpen || _vrSettingsOpen || _controlsOpen || _arenaLayoutOpen)
            {
                _vrSettingsOpen = false;
                // Resuming out of the pause stack closes these panels wholesale,
                // so it is the other way edit mode can be left behind.
                _arenaLayoutOpen = false;
                SetUiEditMode(false);
                _arenaLayout.SetShown(false);
                _settingsMenu.SetShown(false);
                _controlsOpen = false;
                _controlsScreen.SetShown(false);
                CloseOptions();
            }
        }
    }

    private HandProbe MakeProbe(XRController3D hand)
        => hand.GetHasTrackingData()
            ? new HandProbe(true, PokeTip(hand), hand.GetFloat("grip") > GripThreshold,
                hand.GlobalBasis)
            : default;

    private void SetDebug(bool on)
    {
        _debug = on;
        _settings.Debug = on;
        _settings.Save();
        _debugMenu.SetShown(on);
        // Every dev overlay follows the one switch, so a session can be cleaned
        // up for recording without a rebuild. The status line keeps showing a
        // sim FAILURE even with debug off (see _Ready).
        _checklist.SetShown(on);
        _layoutMenu.SetShown(on);
        SetLogButtonsVisible(on);
        _status.Visible = _sim == null || on;
        // Deliberately NOT wired to _diorama.SetDebug: that overlay is the
        // per-creature facing needle, a one-off sprite-calibration tool. The
        // settings debug flag means "fx showcase" now; flip the needle on in
        // code if a new sheet ever needs recalibrating.
        // Debug off no longer implies markers off: the player may have turned them
        // on in their own right, in which case they stay.
        if (!PokeMarkersVisible)
        {
            HidePokeMarkers();
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

    /// <summary>Shared projection of a controller onto the CONTROL rectangle
    /// (PLAN §4, revised): the hand's position drops onto the small rect the
    /// player hovers over, and its position within that rect maps onto the
    /// playfield. Returns the clamped game-space point; also reports whether the
    /// hand is over the control footprint and the clamped PLAYFIELD-LOCAL
    /// position, which is where the cursor is drawn — the hand and the thing it
    /// commands are now in two different places, exactly as intended. Callers
    /// place cursor art as children of PlayfieldRoot using that local point, so
    /// the art inherits the board's tilt and scale and stays flat ON the board
    /// instead of needing a world-space lift that a tilt would break.
    ///
    /// Input never reads the playfield's transform, so the board's scale, tilt
    /// and distance are free to change without touching the feel of the controls.
    /// Hand travel is fixed by ControlRectSideMeters alone.</summary>
    private Vector2 ComputeReticle(XRController3D hand, out bool over, out Vector3 clampedLocal)
    {
        float side = ControlSurfaceSide;
        Vector3 rectLocal = Mapper.FlattenToPlane(ControlSurface.ToLocal(hand.GlobalPosition));
        over = Mapper.IsOverArena(rectLocal, side);
        // Off-surface hands clamp along the PLAYER-to-hand line (not per-axis):
        // the cursor stays on the aiming line at the boundary instead of being
        // dragged sideways toward the nearest corner.
        Vector2 raw = Mapper.ArenaLocalToGameUnclamped(rectLocal, side, GameWorldSize);
        Vector2 game = Mapper.ClampGameTowards(_playerGame, raw, GameWorldSize);
        clampedLocal = Mapper.GameToArenaLocal(game, ArenaSideMeters, GameWorldSize);
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

        ComputeReticle(hand, out bool over, out Vector3 clampedLocal);
        if (isMoveHand)
        {
            // Playfield-local: the reticle is a child of PlayfieldRoot, so this
            // lands it flat on the board at whatever tilt/scale the board has.
            reticle.Position = clampedLocal + new Vector3(0, 0.002f, 0);

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

        // Vertical guide line from the controller down to the CONTROL rectangle
        // (the surface the hand is actually addressing), not the playfield. With
        // the board now far away and tilted, a guide drawn to it would be a long
        // line across the scene pointing at nothing the hand touches.
        float planeY = _controlRect.GlobalPosition.Y;
        Vector3 handPos = hand.GlobalPosition;
        float guideHeight = Mathf.Max(0.02f, handPos.Y - planeY);
        guide.GlobalPosition = new Vector3(handPos.X, planeY + guideHeight * 0.5f, handPos.Z);
        ((MeshInstance3D)guide).Scale = new Vector3(1, guideHeight, 1);
    }

    /// <summary>The control rectangle is a gameplay surface, so it goes away
    /// while a menu owns the screen — there the hands are poking items, not
    /// steering, and a lit rectangle under a menu just reads as clutter.
    ///
    /// UI EDIT MODE is the exception, and has to be: edit mode is entered from
    /// the settings panel, so hiding the rectangle whenever a menu is up would
    /// hide the very thing being edited (its corner handles are its children).
    /// Tabletop hides it always — there the board is the control surface.</summary>
    private void UpdateControlRectVisibility()
        => _controlRect.Visible =
            _controlMode == ControlMode.Cabinet && (!MenuOwnsScreen || _uiEditMode);

    /// <summary>True only during actual play. Menus, pause, the perk pick, the
    /// quest/end panels and game-over all count as out.</summary>
    private bool InPlay => !MenuOwnsScreen && _sim != null && _hasPlayerSnap
        && !_sim.GameOver && !_pauseMenu.IsPaused && !_perkMenu.Active
        && !_questPanel.Active && !_endNote.Active;

    /// <summary>Drive both cursor pillars. Called unconditionally rather than from
    /// UpdateHandVisual, which the MenuOwnsScreen branch skips — that skip is why
    /// the pillars were left standing on the board at the main menu.
    ///
    /// Pillars follow the PHYSICAL hand (left blue, right red) so they keep
    /// matching the guide lines when the move/aim roles are swapped.</summary>
    private void UpdateAimPillars()
    {
        bool inPlay = InPlay;
        Drive(_leftHand, _leftAimPillar);
        Drive(_rightHand, _rightAimPillar);

        void Drive(XRController3D hand, Node3D pillar)
        {
            if (!inPlay || !hand.GetHasTrackingData())
            {
                pillar.Visible = false;
                return;
            }
            ComputeReticle(hand, out _, out Vector3 local);
            UpdateAimPillar(pillar, local, true);
        }
    }

    /// <summary>Stand a cursor pillar on the board at a playfield-local point.
    /// The pivot cancels the board's pitch so the cylinder is world-vertical, and
    /// the half-length offset is applied along that upright axis so the pillar
    /// sits ON the surface rather than through it.</summary>
    private void UpdateAimPillar(Node3D pivot, Vector3 boardLocal, bool visible)
    {
        float length = _aimLineFraction * ArenaSideMeters;
        pivot.Visible = visible && length > 1e-4f;
        if (!pivot.Visible)
        {
            return;
        }
        pivot.Position = boardLocal;
        // Normal to the BOARD, not world-vertical. I originally counter-rotated
        // these upright on the theory that an upright pillar reads as a marker;
        // in the headset the opposite is true — a pillar leaning out of the
        // playing surface reads as detached from the point it marks.
        pivot.RotationDegrees = Vector3.Zero;
        var bar = (MeshInstance3D)pivot.GetChild(0);
        bar.Scale = new Vector3(1.0f, length, 1.0f);
        bar.Position = new Vector3(0.0f, length * 0.5f, 0.0f);
    }

    /// <summary>Switch control mode: load that mode's placement defaults, move
    /// the poke buttons to whichever surface is now within reach, and show the
    /// control rectangle only when it is actually driving anything.</summary>
    private void SetControlMode(ControlMode mode, bool loadDefaults = true)
    {
        _controlMode = mode;
        _settings.ControlMode = (int)mode;
        _settings.Save();

        if (loadDefaults)
        {
            // Tabletop has to stay reachable, so it takes the old table's
            // placement at 1x and FLAT; Cabinet takes the large, distant, tilted
            // board. Tilt is reset per mode rather than carried across: the two
            // placements sit at very different distances, and a tilt that reads
            // fine on a board 0.6 m away rears up through the menus on one 0.1 m
            // from the player's face. (Carrying it was the original choice and
            // it did exactly that on the first mode switch.) The full 0-90 range
            // stays available in both — dial it from the slider, where the board
            // moves under your eye instead of jumping there.
            if (mode == ControlMode.Tabletop)
            {
                _playfieldScale = 1.0f;
                _playfieldPitch = 0.0f;
                _playfieldNearEdge = ArenaNearEdgeMeters;
                _playfieldNearDrop = VerticalDropMeters;
            }
            else
            {
                _playfieldScale = PlayfieldScale;
                _playfieldPitch = PlayfieldPitchDegrees;
                _playfieldNearEdge = PlayfieldNearEdgeMeters;
                _playfieldNearDrop = PlayfieldNearDropMeters;
            }
            _arenaLayout?.SyncPlacement(_playfieldScale, _playfieldPitch, _playfieldNearEdge, _playfieldNearDrop);
            SaveArenaPlacement();
        }

        // The rectangle is meaningless in Tabletop — the board is the control
        // surface — and leaving it drawn would read as a second, dead playfield.
        _controlRect.Visible = mode == ControlMode.Cabinet;
        ApplyHudLayout(mode);

        // Poke buttons hang off the anchor, which tracks whichever surface is in
        // reach but carries none of its scale or tilt (see UpdateUiAnchor).
        Node3D edge = _pauseMenu.EdgeRoot;
        if (edge.GetParent() != _uiAnchor)
        {
            edge.GetParent()?.RemoveChild(edge);
            _uiAnchor.AddChild(edge);
        }

        ApplyPlayfieldPlacement();
        UpdateUiAnchor();
    }

    // ---- UI edit mode ----

    private readonly System.Collections.Generic.List<UiEditable> _editables = new();
    private readonly System.Collections.Generic.Dictionary<string, Transform3D> _uiDefaults = new();
    private readonly System.Collections.Generic.Dictionary<string, Node3D> _uiEditTargets = new();
    private bool _uiEditMode;

    /// <summary>Put every editable widget back to its built-in placement and
    /// forget the saved overrides.</summary>
    private void ResetUiLayout()
    {
        foreach (System.Collections.Generic.KeyValuePair<string, Transform3D> kv in _uiDefaults)
        {
            if (_uiEditTargets.TryGetValue(kv.Key, out Node3D? target))
            {
                target.Transform = kv.Value;
            }
        }
        // The control rectangle is not restored by its transform: the recenter
        // placement owns it, so clearing the edit and re-applying is what
        // actually returns it (and re-parents everything hanging off it).
        _controlRectEdited = false;
        _controlRectOffset = Vector3.Zero;
        ApplyControlRectPlacement();

        _settings.UiLayout.Clear();
        _settings.Save();
        _uiLayoutDirty = false;
        GD.Print("[layout] reset to built-in placements");
    }

    /// <summary>Hang grab handles on the repositionable widgets and restore any
    /// placement the player authored previously. The control rectangle is
    /// ScaleTilt (its position comes from the recenter pose, but its size and
    /// pitch are pure feel); the action buttons are Free.</summary>
    private void BuildUiEditables()
    {
        Add("pause", _pauseMenu.ToggleButton, UiEditable.Mode.Free,
            _pauseMenu.ButtonWidth, _pauseMenu.ButtonHeight, cornersInXZ: false);
        Add("levelup", _pauseMenu.LevelUpButton, UiEditable.Mode.Free,
            _pauseMenu.ButtonWidth, _pauseMenu.ButtonHeight, cornersInXZ: false);
        Add("controlrect", _controlRect, UiEditable.Mode.ScaleTilt,
            ControlRectSideMeters, ControlRectSideMeters, cornersInXZ: true);

        void Add(string id, Node3D target, UiEditable.Mode mode, float w, float h, bool cornersInXZ)
        {
            bool isControlRect = mode == UiEditable.Mode.ScaleTilt;
            // Capture the built-in placement BEFORE any saved override lands on
            // it — that is the only moment it exists, and Reset needs it.
            _uiDefaults[id] = target.Transform;
            _uiEditTargets[id] = target;
            if (_settings.UiLayout.TryGetValue(id, out Transform3D saved))
            {
                if (isControlRect)
                {
                    // For the control rectangle the stored ORIGIN is an offset
                    // from the recenter placement, not a world position — an
                    // absolute one would be wrong the moment the player recenters
                    // somewhere else. Basis carries its tilt and size.
                    _controlRectOffset = saved.Origin;
                    _controlRectEditedBasis = saved.Basis;
                    _controlRectEdited = true;
                }
                else
                {
                    target.Transform = saved;
                }
            }
            var editable = new UiEditable();
            AddChild(editable);
            editable.Build(target, mode, w, h, cornersInXZ);
            editable.OnTransformChanged += _ =>
            {
                if (isControlRect)
                {
                    CaptureControlRectEdit();
                    _settings.UiLayout[id] = new Transform3D(_controlRectEditedBasis, _controlRectOffset);
                }
                else
                {
                    _settings.UiLayout[id] = target.Transform;
                }
                _uiLayoutDirty = true;
            };

            // Log button parked clear of the corner handles: below the face for
            // the upright button plates, and out past the near edge for the
            // control rectangle (which is grabbed from above, so anything inside
            // its footprint would sit under the player's hands).
            Vector3 logOffset = cornersInXZ
                ? new Vector3(0.0f, 0.0f, -h * 0.85f)
                : new Vector3(0.0f, -h * 2.5f, 0.0f);
            editable.BuildLogButton(logOffset, w * 0.7f, h * 0.6f);
            editable.OnLogPressed += () => LogWidgetPlacement(id, target, mode);
            _editables.Add(editable);
        }
    }

    /// <summary>Print a widget's placement in the form the constants are written
    /// in, so a position dialled in by hand can be read off and baked. Positions
    /// come out in metres AND in multiples of the arena reference side, because
    /// the layout code expresses them as `s * k`. Read with:
    ///     adb logcat -s godot:* | findstr layout
    /// </summary>
    private void LogWidgetPlacement(string id, Node3D target, UiEditable.Mode mode)
    {
        Transform3D t = target.Transform;
        Vector3 euler = t.Basis.GetEuler();
        Vector3 scale = t.Basis.Scale;
        float s = ArenaSideMeters;

        if (mode == UiEditable.Mode.ScaleTilt)
        {
            // Read the control rectangle in its RECENTER-LOCAL frame: its live
            // basis carries the recenter yaw, so euler.X off that would drift
            // with whichever way the player happened to be facing. The numbers
            // that matter are the ones that change the FEEL — pitch, the
            // effective side (the hand-travel range mapping onto the arena), and
            // the height offset from the default drop.
            Basis local = _controlRectEdited
                ? _controlRectEditedBasis
                : Basis.FromEuler(new Vector3(Mathf.DegToRad(-ControlRectPitchDegrees), 0.0f, 0.0f));
            Vector3 le = local.GetEuler();
            float lScale = local.Scale.X;
            GD.Print($"[layout] {id}  pitch={-Mathf.RadToDeg(le.X):0.0} deg" +
                     $"  scale={lScale:0.000}x" +
                     $"  effective side={ControlRectSideMeters * lScale:0.000} m" +
                     $"  offset=({_controlRectOffset.X:0.000}, {_controlRectOffset.Y:0.000}, {_controlRectOffset.Z:0.000}) m" +
                     $"  -> drop={ControlRectDropMeters - _controlRectOffset.Y:0.000} m" +
                     $"  near edge={ControlRectNearEdgeMeters + _controlRectOffset.Z:0.000} m");
            return;
        }

        GD.Print($"[layout] {id}  pos=({t.Origin.X:0.000}, {t.Origin.Y:0.000}, {t.Origin.Z:0.000}) m" +
                 $"  = s*({t.Origin.X / s:0.000}, {t.Origin.Y / s:0.000}, {t.Origin.Z / s:0.000})" +
                 $"  rot=({Mathf.RadToDeg(euler.X):0.0}, {Mathf.RadToDeg(euler.Y):0.0}, {Mathf.RadToDeg(euler.Z):0.0}) deg" +
                 $"  scale={scale.X:0.000}x");
    }

    /// <summary>Print the board and presentation values driven by the Layout
    /// sliders — the other half of the picture, since these live on sliders
    /// rather than on a grabbable widget.</summary>
    private void LogArenaPlacement()
    {
        GD.Print($"[layout] arena  scale={_playfieldScale:0.00}x" +
                 $"  tilt={_playfieldPitch:0.0} deg" +
                 $"  distance={_playfieldNearEdge:0.000} m" +
                 $"  drop={_playfieldNearDrop:0.000} m" +
                 $"  -> side={PlayfieldSideMeters:0.000} m");
        GD.Print($"[layout] presentation  sprite height={_spriteHeightScale:0.00}x" +
                 $"  aim line={_aimLineFraction * 100.0f:0}% ({_aimLineFraction * ArenaSideMeters:0.000} m)" +
                 $"  mode={_controlMode}");
    }

    private bool _uiLayoutDirty;

    private void SetLogButtonsVisible(bool visible)
    {
        foreach (UiEditable e in _editables)
        {
            e.SetLogVisible(visible);
        }
    }

    private void SetUiEditMode(bool on)
    {
        if (_uiEditMode == on)
        {
            return;
        }
        _uiEditMode = on;
        foreach (UiEditable e in _editables)
        {
            e.SetEditing(on);
        }
        // Show the action buttons for placement and make them inert while held.
        _pauseMenu.SetEditMode(on);
        if (!on && _uiLayoutDirty)
        {
            // Save on EXIT rather than per-frame: a drag fires every frame and
            // each save writes the whole config file.
            _settings.Save();
            _uiLayoutDirty = false;
        }
    }

    /// <summary>Drive the grab handles. Returns true while a widget is being
    /// dragged, so the caller can suppress that frame's normal poke handling —
    /// otherwise repositioning the pause button would also press it.</summary>
    private bool PollUiEdit(ReadOnlySpan<HandProbe> probes)
    {
        if (!_uiEditMode)
        {
            return false;
        }
        bool grabbing = false;
        foreach (UiEditable e in _editables)
        {
            grabbing |= e.PollGrab(probes);
        }
        return grabbing;
    }

    /// <summary>Log buttons are polled separately from edit mode: they are shown
    /// with DEBUG and are pokeable whenever visible, including mid-game.</summary>
    private void PollLogButtons(ReadOnlySpan<HandProbe> probes)
    {
        if (!_settings.Debug)
        {
            return;
        }
        foreach (UiEditable e in _editables)
        {
            e.PollLogPoke(probes);
        }
    }

    /// <summary>Place the health readout for the control mode.
    ///
    /// CABINET lays it FLAT in the board plane along the left edge, running near
    /// to far and filling toward the far edge. On a large tilted board a single
    /// standing panel puts every reading in one place at the top; splitting the
    /// health out to the edge means it sits beside the action rather than above
    /// it, and lying in-plane keeps it from occluding the left of the playfield
    /// at the shallow angles a tilted board is viewed from.
    ///
    /// TABLETOP leaves it in the panel: that board is small and viewed from
    /// almost overhead, where the one-panel layout already works.</summary>
    private void ApplyHudLayout(ControlMode mode)
    {
        _hud.SetCabinetLayout(mode == ControlMode.Cabinet);
        Node3D health = _hud.HealthRoot;
        Node3D host = mode == ControlMode.Cabinet ? _playfieldRoot : (Node3D)_hud;
        if (health.GetParent() != host)
        {
            health.GetParent()?.RemoveChild(health);
            host.AddChild(health);
        }

        if (mode != ControlMode.Cabinet)
        {
            health.Transform = Transform3D.Identity;
            return;
        }

        // Lay the group's XY page into the board's XZ plane, turned so the bar's
        // length (group +x) runs along the board's +z, near to far. Columns are
        // the images of the group's x/y/z axes.
        var basis = new Basis(
            new Vector3(0.0f, 0.0f, 1.0f),   // bar length -> board far
            new Vector3(1.0f, 0.0f, 0.0f),   // bar height -> across the edge
            new Vector3(0.0f, 1.0f, 0.0f));  // page normal -> board up
        // Just outside the playable square, on the visible floor margin, lifted
        // clear of the terrain decals so it never z-fights them.
        float x = -ArenaSideMeters * 0.5f * (1.0f + (Diorama.FloorMarginScale - 1.0f) * 0.5f);
        health.Transform = new Transform3D(basis, new Vector3(x, 0.004f, 0.0f));
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
        var yawBasis = Basis.FromEuler(new Vector3(0, yaw, 0));
        _arenaRoot.GlobalTransform = new Transform3D(yawBasis, pos);

        // The control rectangle takes over the old table's placement: same near
        // edge and drop, so the hands keep the hover geometry every previous
        // headset pass was tuned against. Only its size changed.
        Vector3 controlBase = headPos + forward * (ControlRectNearEdgeMeters + ControlRectSideMeters * 0.5f);
        controlBase.Y = Mathf.Max(headPos.Y - ControlRectDropMeters, 0.05f);
        _controlRectBasePos = controlBase;
        _controlRectYaw = yawBasis;
        ApplyControlRectPlacement();

        // Remember the pose so the Layout sliders can re-place the board later
        // without a fresh recenter (and without chasing the player's head).
        _recenterHeadPos = headPos;
        _recenterForward = forward;
        _hasRecentered = true;
        ApplyPlayfieldPlacement();
    }

    // The control rectangle's placement is (recenter base) + (player edit). The
    // base follows the head on every recenter; the edit is held in the RECENTER-
    // LOCAL frame so it survives the player turning around or standing up, and
    // is what makes a hand-authored height stick instead of being undone by the
    // next recenter.
    private Vector3 _controlRectBasePos;
    private Basis _controlRectYaw = Basis.Identity;
    private Vector3 _controlRectOffset;
    private Basis _controlRectEditedBasis;
    private bool _controlRectEdited;

    private void ApplyControlRectPlacement()
    {
        Basis local = _controlRectEdited
            ? _controlRectEditedBasis
            : Basis.FromEuler(new Vector3(Mathf.DegToRad(-ControlRectPitchDegrees), 0.0f, 0.0f));
        Vector3 pos = _controlRectBasePos + _controlRectYaw * _controlRectOffset;
        pos.Y = Mathf.Max(pos.Y, 0.05f);
        _controlRect.GlobalTransform = new Transform3D(_controlRectYaw * local, pos);
        UpdateUiAnchor();
    }

    /// <summary>Keep the action-button anchor on the active control surface's
    /// position and facing, WITHOUT its scale or tilt.
    ///
    /// The buttons used to hang off the surface node itself, so resizing the
    /// control rectangle resized them and tilting it tilted them. They should
    /// travel with the surface but stay their own size and stay upright — they
    /// are physical poke targets sized for a fingertip, not part of the
    /// playfield. Position and yaw only, therefore; never scale or pitch.</summary>
    private void UpdateUiAnchor()
    {
        if (_uiAnchor == null)
        {
            return;
        }
        if (_controlMode == ControlMode.Cabinet)
        {
            Vector3 pos = _controlRectBasePos + _controlRectYaw * _controlRectOffset;
            pos.Y = Mathf.Max(pos.Y, 0.05f);
            _uiAnchor.GlobalTransform = new Transform3D(_controlRectYaw, pos);
        }
        else
        {
            // Tabletop: the board is the control surface, so the buttons ride
            // its placement — but again not its scale, or growing the arena
            // would inflate them out of reach.
            float yaw = Mathf.Atan2(_recenterForward.X, _recenterForward.Z);
            _uiAnchor.GlobalTransform = new Transform3D(
                Basis.FromEuler(new Vector3(0, yaw, 0)), _playfieldRoot.GlobalPosition);
        }
    }

    private Node3D _uiAnchor = null!;

    /// <summary>Fold a hand-authored control-rect transform back into the
    /// base+offset form, so the next recenter keeps it.</summary>
    private void CaptureControlRectEdit()
    {
        Basis yawInv = _controlRectYaw.Inverse();
        _controlRectOffset = yawInv * (_controlRect.GlobalPosition - _controlRectBasePos);
        _controlRectEditedBasis = yawInv * _controlRect.GlobalBasis;
        _controlRectEdited = true;
    }

    /// <summary>Place the playfield from the live scale/pitch/distance values
    /// against the last recenter pose. Split out of RecenterArena so the Layout
    /// sliders re-apply it live.
    ///
    /// The board is placed by its NEAR edge. Local +z is the far edge, so a
    /// NEGATIVE x rotation lifts it; the near edge correspondingly dips by half
    /// the board, which the offsets below cancel so the near edge lands exactly
    /// at (nearEdge, nearDrop) from the head whatever the tilt and scale.</summary>
    private void ApplyPlayfieldPlacement()
    {
        if (!_hasRecentered)
        {
            return;
        }
        float yaw = Mathf.Atan2(_recenterForward.X, _recenterForward.Z);
        var yawBasis = Basis.FromEuler(new Vector3(0, yaw, 0));
        float pitch = Mathf.DegToRad(_playfieldPitch);
        float halfBoard = PlayfieldSideMeters * 0.5f;
        Vector3 playfieldPos = _recenterHeadPos
            + _recenterForward * (_playfieldNearEdge + halfBoard * Mathf.Cos(pitch))
            + Vector3.Up * (halfBoard * Mathf.Sin(pitch) - _playfieldNearDrop);
        playfieldPos.Y = Mathf.Max(playfieldPos.Y, 0.05f);
        _playfieldRoot.GlobalTransform = new Transform3D(
            yawBasis * Basis.FromEuler(new Vector3(-pitch, 0, 0)).Scaled(Vector3.One * _playfieldScale),
            playfieldPos);
        // The HUD pivot cancels the board pitch, so it has to follow it live.
        _hudPivot.RotationDegrees = new Vector3(_playfieldPitch, 0.0f, 0.0f);
        UpdateUiAnchor();
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
