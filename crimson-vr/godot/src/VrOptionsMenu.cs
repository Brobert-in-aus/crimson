using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The Options screen, rebuilt to mirror the original Crimsonland Options panel
/// (src/crimson/screens/panels/options.py) as closely as VR allows: a ui_menuPanel
/// background with Sound / Music / Graphics-detail segmented sliders
/// (ui_rectOn/Off). Controls that don't apply to VR (resolution, mouse sensitivity, and
/// the currently unimplemented desktop hover-info toggle) are dropped; "Controls" is
/// replaced by a VR Settings submenu (movement hand / dead zone / debug).
///
/// A child of ArenaRoot, hidden until opened; faces the player above the arena like
/// the other panels. Values are pushed out via events for Main to apply + persist.
/// </summary>
public sealed partial class VrOptionsMenu : Node3D
{
    // Blue neon-ish title colour, like the original menu's headings.
    private static readonly Color TitleColor = new(0.45f, 0.72f, 1.0f);

    private VrSegmentedSlider _sfx = null!;
    private VrSegmentedSlider _music = null!;
    private VrSegmentedSlider _detail = null!;
    private VrButton _controls = null!;
    private VrButton _vrSettings = null!;
    private VrButton _back = null!;

    public event Action? OnBack;
    public event Action? OnVrSettings;
    public event Action? OnControls;
    public event Action<int>? OnSfxChanged;
    public event Action<int>? OnMusicChanged;
    public event Action<int>? OnDetailChanged;

    public void Build(
        float s, int sfx, int music, int detail,
        Texture2D? panelTex, Texture2D? rectOn, Texture2D? rectOff)
    {
        // Shared menu anchor (see MainMenu): all menus coplanar + pushed back.
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        float wp = s * 0.62f;
        float hp = s * 0.78f;

        // No full-screen backing panel â€” the terrain shows through, matching the
        // original menu. The buttons keep their own dark neon-bar plate. (panelTex
        // is unused for now; kept in the signature for a future fitted panel skin.)
        _ = panelTex;

        float y = hp * 0.5f - s * 0.05f;
        AddTitleBacking(y, "Options", 110.0f, s / 1100.0f);
        AddChild(new Label3D
        {
            Text = "Options",
            FontSize = 110,
            PixelSize = s / 1100.0f,
            Modulate = TitleColor,
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.002f),
            NoDepthTest = true,
        });
        y -= s * 0.2f; // clear the title backing before the first row

        _sfx = AddSliderRow("SFX Volume", s, y, 0, 10, sfx, rectOn, rectOff, v => OnSfxChanged?.Invoke(v));
        y -= s * 0.18f;
        _music = AddSliderRow("Music volume", s, y, 0, 10, music, rectOn, rectOff, v => OnMusicChanged?.Invoke(v));
        y -= s * 0.18f;
        _detail = AddSliderRow("Graphics detail", s, y, 1, 5, detail, rectOn, rectOff, v => OnDetailChanged?.Invoke(v));
        y -= s * 0.18f;

        // Bottom row, three across: Controls (the base Options screen's
        // Controls button, as a VR reference card) / VR Settings / Back.
        float bw = s * 0.24f;
        float bh = s * 0.075f;
        float bPitch = bw + s * 0.03f;
        _controls = new VrButton();
        AddChild(_controls);
        _controls.Build(bw, bh, "Controls", new Color(0.5f, 0.6f, 0.85f), plate: true);
        _controls.Position = new Vector3(-bPitch, y, 0.0f);
        _controls.OnPress += () => OnControls?.Invoke();

        _vrSettings = new VrButton();
        AddChild(_vrSettings);
        _vrSettings.Build(bw, bh, "VR Settings", new Color(0.5f, 0.6f, 0.85f), plate: true);
        _vrSettings.Position = new Vector3(0.0f, y, 0.0f);
        _vrSettings.OnPress += () => OnVrSettings?.Invoke();

        _back = new VrButton();
        AddChild(_back);
        _back.Build(bw, bh, "Back", new Color(0.6f, 0.6f, 0.66f), plate: true);
        _back.Position = new Vector3(bPitch, y, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    private VrSegmentedSlider AddSliderRow(
        string label, float s, float y, int min, int max, int value,
        Texture2D? rectOn, Texture2D? rectOff, Action<int> onChanged)
    {
        float titleY = y + s * 0.08f; // more air between the title and its pips
        AddTitleBacking(titleY, label, 72.0f, s / 1500.0f);
        AddChild(new Label3D
        {
            Text = label,
            FontSize = 72,
            PixelSize = s / 1500.0f,
            Modulate = new Color(0.9f, 0.92f, 0.98f),
            OutlineSize = 20,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, titleY, 0.002f),
            NoDepthTest = true,
        });
        var slider = new VrSegmentedSlider();
        AddChild(slider);
        slider.Build(s * 0.03f, min, max, value, rectOn, rectOff);
        slider.Position = new Vector3(0.0f, y, 0.0f);
        slider.OnValueChanged += v => onChanged(v);
        return slider;
    }

    /// <summary>A dark translucent backing strip behind a title, sized from the
    /// text so the title always fits inside it (was fixed-width and overflowed).</summary>
    private void AddTitleBacking(float y, string text, float fontSize, float pixelSize)
    {
        float glyph = fontSize * pixelSize;                 // ~cap height in metres
        float width = text.Length * glyph * 0.62f + glyph;  // advance*chars + padding
        float height = glyph * 1.2f;                        // hug the text; don't spill onto neighbours
        AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(width, height) },
            Position = new Vector3(0.0f, y, 0.0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.04f, 0.05f, 0.08f, 0.72f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 59,
            },
        });
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        // Re-arm + start the settle window on show AND hide, so a finger where a
        // control appears can't instant-fire (e.g. VR Settings Back reopens this).
        _controls.ResetPress();
        _vrSettings.ResetPress();
        _back.ResetPress();
        _sfx.ResetPress();
        _music.ResetPress();
        _detail.ResetPress();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        _sfx.PollPoke(probes);
        _music.PollPoke(probes);
        _detail.PollPoke(probes);
        _controls.PollPoke(probes);
        _vrSettings.PollPoke(probes);
        _back.PollPoke(probes);
    }
}
