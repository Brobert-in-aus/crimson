using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The Options screen, rebuilt to mirror the original Crimsonland Options panel
/// (src/crimson/screens/panels/options.py) as closely as VR allows: a ui_menuPanel
/// background with Sound / Music / Graphics-detail segmented sliders (ui_rectOn/Off)
/// and a "UI Info texts" checkbox (ui_checkOn/Off). Controls that don't apply to VR
/// (resolution, mouse sensitivity) are dropped; the original "Controls" button is
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
    private VrCheckbox _infoTexts = null!;
    private VrButton _vrSettings = null!;
    private VrButton _back = null!;

    public event Action? OnBack;
    public event Action? OnVrSettings;
    public event Action<int>? OnSfxChanged;
    public event Action<int>? OnMusicChanged;
    public event Action<int>? OnDetailChanged;
    public event Action<bool>? OnInfoTextsChanged;

    public void Build(
        float s, int sfx, int music, int detail, bool infoTexts,
        Texture2D? panelTex, Texture2D? rectOn, Texture2D? rectOff, Texture2D? checkOn, Texture2D? checkOff)
    {
        Position = new Vector3(0.0f, s * 0.95f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        float wp = s * 0.62f;
        float hp = s * 0.78f;

        // No full-screen backing panel — the terrain shows through, matching the
        // original menu. The buttons keep their own dark neon-bar plate. (panelTex
        // is unused for now; kept in the signature for a future fitted panel skin.)
        _ = panelTex;

        float y = hp * 0.5f - s * 0.06f;
        AddChild(new Label3D
        {
            Text = "Options",
            FontSize = 110,
            PixelSize = s / 1100.0f,
            Modulate = TitleColor,
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.0f),
            NoDepthTest = true,
        });
        y -= s * 0.13f;

        _sfx = AddSliderRow("Sound volume", s, y, 0, 10, sfx, rectOn, rectOff, v => OnSfxChanged?.Invoke(v));
        y -= s * 0.13f;
        _music = AddSliderRow("Music volume", s, y, 0, 10, music, rectOn, rectOff, v => OnMusicChanged?.Invoke(v));
        y -= s * 0.13f;
        _detail = AddSliderRow("Graphics detail", s, y, 1, 5, detail, rectOn, rectOff, v => OnDetailChanged?.Invoke(v));
        y -= s * 0.12f;

        _infoTexts = new VrCheckbox();
        AddChild(_infoTexts);
        _infoTexts.Build(s * 0.045f, "UI Info texts", infoTexts, checkOn, checkOff);
        _infoTexts.Position = new Vector3(-s * 0.16f, y, 0.0f);
        _infoTexts.OnToggled += b => OnInfoTextsChanged?.Invoke(b);
        y -= s * 0.12f;

        float bw = s * 0.26f;
        float bh = s * 0.075f;
        _vrSettings = new VrButton();
        AddChild(_vrSettings);
        _vrSettings.Build(bw, bh, "VR Settings", new Color(0.5f, 0.6f, 0.85f), plate: true);
        _vrSettings.Position = new Vector3(-(bw * 0.5f + s * 0.02f), y, 0.0f);
        _vrSettings.OnPress += () => OnVrSettings?.Invoke();

        _back = new VrButton();
        AddChild(_back);
        _back.Build(bw, bh, "Back", new Color(0.6f, 0.6f, 0.66f), plate: true);
        _back.Position = new Vector3(bw * 0.5f + s * 0.02f, y, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    private VrSegmentedSlider AddSliderRow(
        string label, float s, float y, int min, int max, int value,
        Texture2D? rectOn, Texture2D? rectOff, Action<int> onChanged)
    {
        AddChild(new Label3D
        {
            Text = label,
            FontSize = 72,
            PixelSize = s / 1500.0f,
            Modulate = new Color(0.9f, 0.92f, 0.98f),
            OutlineSize = 20,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y + s * 0.045f, 0.0f),
            NoDepthTest = true,
        });
        var slider = new VrSegmentedSlider();
        AddChild(slider);
        slider.Build(s * 0.03f, min, max, value, rectOn, rectOff);
        slider.Position = new Vector3(0.0f, y, 0.0f);
        slider.OnValueChanged += v => onChanged(v);
        return slider;
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        // Re-arm + start the settle window on show AND hide, so a finger where a
        // control appears can't instant-fire (e.g. VR Settings Back reopens this).
        _vrSettings.ResetPress();
        _back.ResetPress();
        _infoTexts.ResetPress();
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
        _infoTexts.PollPoke(probes);
        _vrSettings.PollPoke(probes);
        _back.PollPoke(probes);
    }
}
