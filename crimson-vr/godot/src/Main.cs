using System;
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
    private const int RestartDelayTicks = 120; // ~2 s pause on death before reset

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
    private const float ReticleSizeMeters = 0.05f; // first-pass; tune in-headset

    private SimSession? _sim;
    private Diorama _diorama = null!;
    private AudioBank _audio = null!;
    private Hud _hud = null!;
    private PerkMenu _perkMenu = null!;
    private int _perkChoice = -1; // pending poke choice for the next tick, -1 = none
    private PauseMenu _pauseMenu = null!;
    private SettingsMenu _settingsMenu = null!;
    private bool _settingsOpen;
    private float _deadZone = VrInput.DefaultDeadZoneGameUnits;
    private Vector2 _playerGame = new(GameWorldSize * 0.5f, GameWorldSize * 0.5f);
    private int _deadTicks;

    // Hand roles: default left = movement, right = aim/fire (PLAN §1); swap is a
    // settings toggle wired in M4. Index 0 = left, 1 = right.
    private bool _handSwap;
    private readonly bool[] _prevTrigger = new bool[2];
    private readonly bool[] _prevGrip = new bool[2];

    private bool _xrActive;

    // Display mode (PLAN §6 / MR): Auto uses passthrough when the headset supports
    // it (Quest 3 etc. report ALPHA_BLEND as a supported environment blend mode),
    // else a skybox. The tabletop diorama is a natural MR fit — in passthrough it
    // sits in the player's real room. Forcing Skybox keeps the VR void; forcing
    // Passthrough warns and falls back if unsupported. (A settings toggle is M4.)
    public enum DisplayMode { Auto, Skybox, Passthrough }
    private DisplayMode _displayMode = DisplayMode.Auto;
    private XRInterface? _xrInterface;
    private WorldEnvironment _worldEnv = null!;
    private bool _passthroughActive;

    // Skybox-mode grey fog (rapid view-distance falloff). First-pass; tune in-headset.
    private static readonly Color FogGrey = new(0.55f, 0.55f, 0.58f);
    private const float FogDensityValue = 0.35f;

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
        _diorama = new Diorama();
        _arenaRoot.AddChild(_diorama);
        _diorama.Configure(ArenaSideMeters, GameWorldSize);

        // Audio is anchored under ArenaRoot so its players sit at the tabletop
        // (positions are arena-local meters, like the diorama).
        _audio = new AudioBank();
        _arenaRoot.AddChild(_audio);
        _audio.Configure(ArenaSideMeters, GameWorldSize);

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
        _pauseMenu.OnQuit += () => GetTree().Quit();
        _pauseMenu.OnSettings += OpenSettings;

        // Settings (MVP): hand-swap + dead-zone, opened from the pause menu.
        _settingsMenu = new SettingsMenu();
        _arenaRoot.AddChild(_settingsMenu);
        _settingsMenu.Build(ArenaSideMeters, _handSwap, _deadZone);
        _settingsMenu.OnBack += CloseSettings;
        _settingsMenu.OnHandSwapChanged += v => _handSwap = v;
        _settingsMenu.OnDeadZoneChanged += v => _deadZone = v;

        try
        {
            _sim = new SimSession(SurvivalConfig);
            // Static terrain generation info (ABI v3): slots pick the ground atlas
            // sheets, seed drives the stamp layout. The terrain-base quad render is
            // a follow-up; log it so the plumbing is exercised meanwhile.
            Sim.TerrainInfo t = _sim.TerrainInfo();
            _diorama.ApplyTerrainInfo(t); // texture the arena floor from the base slot
            GD.Print($"CrimsonVR: terrain slots=({t.Slot0},{t.Slot1},{t.Slot2}) seed={t.TerrainSeed} size={t.TerrainSize}");
        }
        catch (System.Exception e)
        {
            GD.PushError($"CrimsonVR: sim session create failed: {e.Message}");
            _sim = null;
        }
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
            // Thick grey fog: rapid view-distance falloff so the diorama reads as
            // sitting in a contained foggy space (and it masks the off-arena spawn
            // margin, §6). FogSkyAffect greys the background sky to match. Disabled
            // in passthrough (TrySetupPassthrough) so the real room shows through.
            // Density/color are first-pass — tune in-headset.
            FogEnabled = true,
            FogLightColor = FogGrey,
            FogLightEnergy = 1.0f,
            FogDensity = FogDensityValue,
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

        _leftHand.AddChild(MakeHandMarker(new Color(0.2f, 0.5f, 1.0f)));
        _rightHand.AddChild(MakeHandMarker(new Color(1.0f, 0.3f, 0.25f)));
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

        var surface = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(ArenaSideMeters, ArenaSideMeters) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.22f, 0.12f),
                Roughness = 1.0f,
            },
        };
        _arenaRoot.AddChild(surface);

        // Rim so the playfield edge reads clearly in-headset.
        var rim = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(ArenaSideMeters + 0.04f, 0.02f, ArenaSideMeters + 0.04f) },
            Position = new Vector3(0, -0.011f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.18f) },
        };
        _arenaRoot.AddChild(rim);
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
    // it and carries the over-arena / trigger brighten. NoDepthTest keeps it on
    // top like a cursor.
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
                NoDepthTest = true,
                RenderPriority = 50,
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

        // Death handling: hold for a moment, then restart the session (PLAN M2
        // "death -> restart").
        if (_sim.GameOver)
        {
            if (++_deadTicks >= RestartDelayTicks)
            {
                _sim.Restart();
                _diorama.ResetTerrainFx(); // clear accumulated blood/corpses
                _deadTicks = 0;
                _playerGame = new Vector2(GameWorldSize * 0.5f, GameWorldSize * 0.5f);
            }
            return;
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

        // Perk pick: while perks are pending (from the prior tick), pause the sim
        // (perk_menu_active) and, once a card is poked, feed the choice index. The
        // ABI applies the choice on the tick perk_choice_index is set.
        if (_sim.LastResult.PerkPendingCount > 0)
        {
            input.PerkMenuActive = 1;
            if (_perkChoice >= 0)
            {
                input.PerkChoiceIndex = _perkChoice;
                _perkChoice = -1;
            }
        }

        Sim.TickResult result = _sim.Tick(input);
        SnapshotView snap = _sim.CaptureSnapshot();
        if (snap.Header.PlayerCount > 0)
        {
            Sim.PlayerSnap p = snap.Players[0];
            _playerGame = new Vector2(p.X, p.Y);
            _hud.Update(result, p);
        }
        _perkMenu.Update(snap);
        _diorama.PushSnapshot(snap);

        // Accumulate this tick's blood/scorch splats + corpse stamps (ABI v3).
        TerrainFxView terrainFx = _sim.CaptureTerrainFx();
        _diorama.RenderTerrainFx(terrainFx);

        // Play the audio this tick emitted, positioned relative to the player.
        AudioEventsView audio = _sim.CaptureAudio();
        _audio.Route(audio, _playerGame);
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
        UpdateHandVisual(_leftHand, _leftReticle, _leftGuide, isMoveHand: !_handSwap);
        UpdateHandVisual(_rightHand, _rightReticle, _rightGuide, isMoveHand: _handSwap);

        if (_sim != null)
        {
            _diorama.Interpolate((float)Engine.GetPhysicsInterpolationFraction());
            PollMenuPoke();
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

        _pauseMenu.PollPoke(p);
        if (_pauseMenu.IsPaused && _settingsOpen)
        {
            _settingsMenu.PollPoke(p);
        }
        if (_perkMenu.Active)
        {
            _perkMenu.PollPoke(p);
            if (_perkMenu.Chosen >= 0)
            {
                _perkChoice = _perkMenu.Chosen;
                _perkMenu.Chosen = -1;
            }
        }

        // Settings is only valid while paused (e.g. the toggle was poked off
        // underneath the overlay); reconcile.
        if (_settingsOpen && !_pauseMenu.IsPaused)
        {
            CloseSettings();
        }
    }

    private HandProbe MakeProbe(XRController3D hand)
        => hand.GetHasTrackingData()
            ? new HandProbe(true, PokeTip(hand), hand.GetFloat("grip") > GripThreshold)
            : default;

    private void OpenSettings()
    {
        _settingsOpen = true;
        _pauseMenu.SetPanelVisible(false);
        _settingsMenu.SetShown(true);
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        _settingsMenu.SetShown(false);
        if (_pauseMenu.IsPaused)
        {
            _pauseMenu.SetPanelVisible(true);
        }
    }

    // A point ~4 cm ahead of the grip (grip -Z faces out the controller front),
    // used as the poke fingertip for physical menu buttons.
    private static Vector3 PokeTip(XRController3D hand)
        => hand.GlobalPosition + hand.GlobalTransform.Basis.Z * -0.04f;

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
