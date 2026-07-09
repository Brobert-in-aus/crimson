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

    // Survival, seed 1, standard 1024 world at 60 Hz (mirrors HostSessionConfig).
    private const string SurvivalConfig =
        "{\"seed\":1,\"game_mode\":1,\"player_count\":1,\"world_size\":1024.0,\"tick_rate\":60}";

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
    private int _deathRank = int.MaxValue; // 0-based insertion rank of the death score

    // ~1.2 s at 60 Hz between death and the results flow, standing in for the
    // base game's death VO + death-timer delay before the panel slides in
    // (player_damage.py). Tune in-headset.
    private const int DeathPacingTicks = 72;
    private const int HighscoreTableMax = 10; // UserSettings.AddHighscore cap
    private readonly UserSettings _settings = new();
    private ValidationChecklist _checklist = null!;
    private MainMenu _mainMenu = null!;

    /// <summary>The menu flow (main menu, or the options/VR-settings screens opened
    /// from it) owns the screen: the sim must not tick and gameplay input must not
    /// reach the game. Options opened from the pause menu is gated by IsPaused.</summary>
    private bool MenuOwnsScreen => _mainMenu.IsOpen || (_optionsFromMenu && (_optionsOpen || _vrSettingsOpen));
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
    private const float FogEndMeters = 12.0f;

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

        _status.Text = _sim != null
            ? $"CrimsonVR | sim abi v{TryQueryAbiVersion()}"
            : "sim unavailable (native lib missing)";
    }

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
        _mainMenu.OnPlay += StartGame;
        _mainMenu.OnOptions += () => OpenOptions(fromMenu: true);
        _mainMenu.OnStatistics += () => GD.Print("CrimsonVR: statistics screen is a later slice");
        _mainMenu.OnQuit += () => GetTree().Quit();

        try
        {
            _sim = new SimSession(SurvivalConfig);
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

    /// <summary>Leave the main menu and begin play (switch to the in-game track).</summary>
    private void StartGame()
    {
        _mainMenu.Close();
        SetGameplayVisible(true);
        _audio.PlayMusic("gt1_ingame");
    }

    /// <summary>Quit the current game back to the main menu: reset the sim to a
    /// fresh run, unpause, and show the menu (Play starts clean). The MAIN MENU's
    /// Quit exits the app; every in-game Quit routes here instead.</summary>
    private void ReturnToMenu()
    {
        _keyboard.Dismiss();
        _gameOverPanel.Dismiss();
        _perkMenu.ForceHide(); // don't leave perk cards floating over the main menu
        _pauseMenu.ForceResume();
        _sim?.Restart();
        _diorama.ResetTerrainFx();
        _playerGame = new Vector2(GameWorldSize * 0.5f, GameWorldSize * 0.5f);
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

    private static MeshInstance3D MakeHandMarker(Color color)
        => new()
        {
            Mesh = new SphereMesh { Radius = 0.02f, Height = 0.04f },
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

        // Death handling (base game_over.py flow): a short pacing delay over the
        // frozen world, then name entry (only when the score ranks, like the base
        // top-100 gate — ours is the local top-10), then the results panel with
        // Play Again / Main Menu. Sim frozen throughout.
        if (_sim.GameOver)
        {
            if (_keyboard.Active || _gameOverPanel.Active)
            {
                return;
            }
            // A pause opened just before death keeps the screen until resolved
            // (Resume resumes the death flow; Quit already routes to the menu).
            if (_pauseMenu.IsPaused)
            {
                return;
            }
            _deathTicks++;
            if (_deathTicks >= DeathPacingTicks)
            {
                int score = _sim.LastResult.PlayerExperience;
                _deathRank = HighscoreRank(score);
                _audio.PlayUi(AudioBank.UiPanel);
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
            }
            return;
        }
        _deathTicks = 0;

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

        // Main menu owns the screen while open: poke its items. Gameplay menus stay
        // dormant.
        if (_mainMenu.IsOpen)
        {
            _mainMenu.PollPoke(p);
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
        _diorama.SetDebug(on);
        if (!on)
        {
            foreach (MeshInstance3D m in _pokeMarkers)
            {
                m.Visible = false;
            }
        }
    }

    /// <summary>0-based insertion rank of a score in the local highscore table
    /// (existing entries win ties, matching rank_index in the base game).</summary>
    private int HighscoreRank(int score)
    {
        int rank = 0;
        foreach (HighscoreEntry e in _settings.Highscores)
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
        int score = _sim.LastResult.PlayerExperience;
        if (!string.IsNullOrEmpty(name))
        {
            _settings.AddHighscore(name, score);
        }
        GD.Print($"CrimsonVR: highscore {(string.IsNullOrEmpty(name) ? "(skipped)" : name)} - {score}");
        _keyboard.Dismiss();
        _gameOverPanel.ShowButtons();
    }

    /// <summary>Results panel "Play Again": start a fresh run immediately.</summary>
    private void PlayAgain()
    {
        if (_sim == null)
        {
            return;
        }
        _gameOverPanel.Dismiss();
        _sim.Restart();
        _diorama.ResetTerrainFx();
        _playerGame = new Vector2(GameWorldSize * 0.5f, GameWorldSize * 0.5f);
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
        Vector2 game = Mapper.ArenaLocalToGame(arenaLocal, ArenaSideMeters, GameWorldSize);
        Vector3 clampedLocal = Mapper.GameToArenaLocal(game, ArenaSideMeters, GameWorldSize);
        clampedWorld = _arenaRoot.ToGlobal(clampedLocal);
        return game;
    }

    private void UpdateHandVisual(XRController3D hand, Node3D reticle, Node3D guide, bool isMoveHand)
    {
        bool tracking = hand.GetHasTrackingData();
        reticle.Visible = tracking;
        guide.Visible = tracking;
        if (!tracking)
        {
            return;
        }

        ComputeReticle(hand, out bool over, out Vector3 clampedWorld);
        reticle.GlobalPosition = clampedWorld + new Vector3(0, 0.002f, 0);

        var mesh = (MeshInstance3D)reticle;
        var material = (StandardMaterial3D)mesh.MaterialOverride;
        // Aim hand shows the crosshair, move hand the cursor (roles swap with the
        // hand-swap setting, so pick the texture by role each frame).
        material.AlbedoTexture = isMoveHand ? _cursorTex : _aimTex;
        float triggerValue = hand.GetFloat("trigger");
        Color baseColor = isMoveHand ? new Color(0.2f, 0.5f, 1.0f) : new Color(1.0f, 0.3f, 0.25f);
        // Opaque reticle material: dim by darkening RGB (an alpha change would be
        // invisible without alpha transparency enabled).
        material.AlbedoColor = over
            ? baseColor.Lerp(Colors.White, triggerValue)
            : baseColor.Darkened(0.6f);

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
