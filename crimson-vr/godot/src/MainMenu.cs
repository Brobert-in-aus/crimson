using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4: the VR main menu — the boot screen. Rendered to look like the original
/// Crimsonland main menu (the ui_signCrimson logo above a vertical stack of
/// ui_menuItem plates labelled from the ui_itemTexts atlas), over the diorama's
/// terrain ground. The ONLY departure from the original is that the items sit
/// proud and are physically poked (PLAN M4 UI model) instead of mouse-clicked;
/// they float freely rather than being wrapped in VrButton chrome.
///
/// A child of ArenaRoot so it inherits the tabletop placement/scale/yaw. It gates
/// the sim: Main holds the tick while <see cref="IsOpen"/>. Poking PLAY GAME
/// starts play; OPTIONS opens settings; QUIT exits. STATISTICS is kept for a
/// faithful layout but is inert until a stats screen exists.
/// </summary>
public sealed partial class MainMenu : Node3D
{
    // ui_itemTexts atlas rows (see src/crimson/screens/menu.py):
    //   0 BUY NOW, 1 PLAY GAME, 2 OPTIONS, 3 STATISTICS, 4 MODS, 5 OTHER GAMES,
    //   6 QUIT, 7 BACK.
    private const int RowPlay = 1;
    private const int RowOptions = 2;
    private const int RowStatistics = 3;
    private const int RowQuit = 6;

    private VrMenuItem _play = null!;
    private VrMenuItem _options = null!;
    private VrMenuItem _statistics = null!;
    private VrMenuItem _quit = null!;
    private VrButton _confirmQuit = null!;
    private VrButton _cancelQuit = null!;
    private Label3D _quitWarning = null!;

    public bool IsOpen { get; private set; }
    public bool QuitConfirmationPending { get; private set; }

    public event Action? OnPlay;
    public event Action? OnOptions;
    public event Action? OnStatistics;
    public event Action? OnQuit;

    public void Build(float arenaSideMeters, Texture2D? sign, Texture2D? itemTex, Texture2D? labelTex)
    {
        float s = arenaSideMeters;

        // Face the player, above the arena — same anchor as the pause/perk panels
        // (RecenterArena yaws the arena so a 180 deg yaw faces the near/player
        // side; the small back-lean tips the top away). Vertical Label3D/quad
        // content reads upright under this transform.
        // Every player-facing surface uses one tested depth contract. Keeping
        // transitions coplanar prevents a newly-opened menu from appearing around
        // the hand that just operated the previous one.
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        // The crimson logo up top (ui_signCrimson is 512x128 -> 4:1).
        if (sign != null)
        {
            float signW = s * 1.05f;
            float signH = signW * (128.0f / 512.0f);
            var logo = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(signW, signH) },
                Position = new Vector3(0.0f, s * 0.55f, 0.0f),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoTexture = sign,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                },
            };
            AddChild(logo);
        }

        // Vertical item stack. Items are the full ui_menuItem bar (aspect 8:1);
        // pitch leaves a gap so proud plates never overlap.
        float itemW = s * 0.8f;
        float pitch = itemW * VrMenuItemHeightFactor() * 1.5f;
        float y = s * 0.22f;

        _play = MakeItem(itemW, itemTex, labelTex, RowPlay, y); y -= pitch;
        _options = MakeItem(itemW, itemTex, labelTex, RowOptions, y); y -= pitch;
        _statistics = MakeItem(itemW, itemTex, labelTex, RowStatistics, y); y -= pitch;
        _quit = MakeItem(itemW, itemTex, labelTex, RowQuit, y);

        _play.OnPress += () => OnPlay?.Invoke();
        _options.OnPress += () => OnOptions?.Invoke();
        _statistics.OnPress += () => OnStatistics?.Invoke();
        _quit.OnPress += () => SetQuitConfirmation(true);

        // Quitting the app is deliberately not a repeat press on the original
        // target. The confirmation sits about 15 cm ABOVE Quit; after a player
        // puts on or hands over the headset, a hand left at Quit's height cannot
        // terminate the app when the confirmation state appears.
        float confirmW = s * 0.65f;
        float confirmH = s * 0.13f;
        _quitWarning = new Label3D
        {
            Text = "Quit CrimsonVR?",
            FontSize = 82,
            PixelSize = s / 1200.0f,
            Modulate = new Color(1.0f, 0.72f, 0.52f),
            OutlineSize = 20,
            OutlineModulate = Colors.Black,
            Position = new Vector3(0.0f, s * 0.34f, 0.002f),
            NoDepthTest = true,
            Visible = false,
        };
        AddChild(_quitWarning);

        _confirmQuit = new VrButton();
        AddChild(_confirmQuit);
        _confirmQuit.BuildClassic(confirmW, confirmH, "Confirm Quit");
        _confirmQuit.Position = new Vector3(0.0f, s * 0.15f, 0.0f);
        _confirmQuit.OnPress += () =>
        {
            SetQuitConfirmation(false);
            OnQuit?.Invoke();
        };
        _confirmQuit.Visible = false;

        _cancelQuit = new VrButton();
        AddChild(_cancelQuit);
        _cancelQuit.BuildClassic(confirmW, confirmH, "Go Back");
        _cancelQuit.Position = new Vector3(0.0f, -s * 0.20f, 0.0f);
        _cancelQuit.OnPress += () => SetQuitConfirmation(false);
        _cancelQuit.Visible = false;

        // Statistics opens the stats + high-scores screen (wired in Main).

        Visible = false;
    }

    private static float VrMenuItemHeightFactor() => 64.0f / 512.0f; // full bar aspect

    private VrMenuItem MakeItem(float width, Texture2D? itemTex, Texture2D? labelTex, int row, float y)
    {
        var item = new VrMenuItem();
        AddChild(item);
        item.Build(width, itemTex, labelTex, row);
        item.Position = new Vector3(0.0f, y, 0.0f);
        return item;
    }

    public void Open()
    {
        IsOpen = true;
        Visible = true;
        SetQuitConfirmation(false);
        // Re-arm + start the settle window so a finger lingering where an item pops
        // up (e.g. returning from Options with the hand over Quit) can't instant-fire.
        _play.ResetPress();
        _options.ResetPress();
        _statistics.ResetPress();
        _quit.ResetPress();
    }

    public void Close()
    {
        IsOpen = false;
        SetQuitConfirmation(false);
        Visible = false;
        _play.ResetPress();
        _options.ResetPress();
        _statistics.ResetPress();
        _quit.ResetPress();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!IsOpen)
        {
            return;
        }
        if (QuitConfirmationPending)
        {
            _confirmQuit.PollPoke(probes);
            _cancelQuit.PollPoke(probes);
            return;
        }
        _play.PollPoke(probes);
        _options.PollPoke(probes);
        _statistics.PollPoke(probes);
        _quit.PollPoke(probes);
    }

    private void SetQuitConfirmation(bool pending)
    {
        QuitConfirmationPending = pending;
        _play.Visible = !pending;
        _options.Visible = !pending;
        _statistics.Visible = !pending;
        _quit.Visible = !pending;
        _quitWarning.Visible = pending;
        _confirmQuit.Visible = pending;
        _cancelQuit.Visible = pending;

        _play.ResetPress();
        _options.ResetPress();
        _statistics.ResetPress();
        _quit.ResetPress();
        _confirmQuit.ResetPress();
        _cancelQuit.ResetPress();
    }
}
