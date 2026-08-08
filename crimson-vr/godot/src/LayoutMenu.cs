using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// In-headset layout tuning: segmented sliders for the comfort values introduced
/// by the control-rectangle split (sprite height, cursor pillar length, and the
/// playfield's scale / tilt / distance / height).
///
/// These exist because those values can only honestly be judged with a head in
/// the headset, and every one of them previously cost a full ~5 minute Quest
/// rebuild to change. Shown only while the debug setting is on, like the debug
/// FX menu, and standing to the player's RIGHT so it can be open alongside the
/// checklist (left) and debug FX (also left).
///
/// Values are session-only by design: this is a dial-it-in tool, and the numbers
/// it lands on are meant to be promoted to the constants in Main rather than
/// silently persisted per-install.
/// </summary>
public sealed partial class LayoutMenu : Node3D
{
    /// <summary>One tunable row: label text, integer slider range, and the
    /// mapping from slider step to real value.</summary>
    private sealed class Row
    {
        public string Name = string.Empty;
        public int Steps;
        public float Min;
        public float Step;
        public string Unit = string.Empty;
        public Action<float>? Apply;
        public VrSegmentedSlider Slider = null!;
        public Label3D Label = null!;
        public float ValueAt(int v) => Min + v * Step;
        public int StepFor(float value) =>
            Mathf.Clamp(Mathf.RoundToInt((value - Min) / Step), 0, Steps);
    }

    private Row[] _rows = Array.Empty<Row>();
    private VrButton _log = null!;

    /// <summary>Poked to dump the current board/presentation values.</summary>
    public event Action? OnLogPressed;

    public void Build(
        float arenaSideMeters,
        float spriteHeight, Action<float> onSpriteHeight,
        float aimLine, Action<float> onAimLine,
        Texture2D? rectOn, Texture2D? rectOff)
    {
        float s = arenaSideMeters;
        // Three dev panels, two side walls. The checklist owns the RIGHT wall
        // outright (it is the tall one, and it pages), so the two tuning panels
        // share the LEFT: debug FX at the centre, this one forward of it along
        // +z. Their footprints along the wall are about s*0.8 wide centred on
        // their origins, so s*1.1 of separation leaves roughly s*0.3 of gap.
        //
        // Do not place this at +x: that is the checklist's exact spot, origin
        // and yaw both, and the two rendered inside each other.
        Position = new Vector3(-s * 1.25f, s * 0.9f, s * 1.1f);
        RotationDegrees = new Vector3(0.0f, 90.0f, 0.0f);

        var title = new Label3D
        {
            Text = "Layout",
            FontSize = 120,
            PixelSize = s / 1100.0f,
            Modulate = new Color(0.9f, 0.9f, 0.95f),
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, s * 0.52f, 0.0f),
            NoDepthTest = true,
        };
        AddChild(title);

        // Arena PLACEMENT moved to the player-facing Arena & Layout screen. Only
        // the measure-once-and-bake values stay here, and each value now has
        // exactly one owner — the same slider in two panels would desync the
        // moment either was touched.
        _rows = new[]
        {
            // Sprite lift multiplier: 0 pins every entity flat on the terrain,
            // 1.0 is the tabletop-tuned original, 2.0 doubles the stack.
            new Row { Name = "Sprite height", Steps = 20, Min = 0.0f, Step = 0.1f, Unit = "x", Apply = onSpriteHeight },
            // Cursor pillar as a fraction of the arena side.
            new Row { Name = "Aim line", Steps = 20, Min = 0.0f, Step = 0.05f, Unit = "arena", Apply = onAimLine },
        };

        float[] current = { spriteHeight, aimLine };
        float pitchStep = s * 0.17f;
        float y = s * 0.36f;

        for (int i = 0; i < _rows.Length; i++)
        {
            Row row = _rows[i];
            int value = row.StepFor(current[i]);

            row.Label = new Label3D
            {
                Text = RowText(row, value),
                FontSize = 68,
                PixelSize = s / 1100.0f,
                Modulate = new Color(0.85f, 0.87f, 0.95f),
                OutlineSize = 20,
                OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
                Position = new Vector3(0.0f, y + s * 0.055f, 0.0f),
                NoDepthTest = true,
            };
            AddChild(row.Label);

            row.Slider = new VrSegmentedSlider();
            AddChild(row.Slider);
            // Narrower cells than the settings sliders: these strips carry up to
            // 20 steps and would otherwise run wider than the panel.
            row.Slider.Build(s * 0.022f, 0, row.Steps, value, rectOn, rectOff);
            row.Slider.Position = new Vector3(0.0f, y, 0.0f);
            Row captured = row;
            row.Slider.OnValueChanged += v =>
            {
                captured.Label.Text = RowText(captured, v);
                captured.Apply?.Invoke(captured.ValueAt(v));
            };
            y -= pitchStep;
        }

        // Well below the last slider strip: this panel is poked constantly while
        // tuning, and a log button within a finger's width of a pip row would be
        // hit by accident on every pass.
        _log = new VrButton();
        AddChild(_log);
        _log.Build(s * 0.34f, s * 0.09f, "log arena", new Color(0.4f, 0.75f, 0.55f), plate: true);
        _log.Position = new Vector3(0.0f, y - s * 0.10f, 0.0f);
        _log.OnPress += () => OnLogPressed?.Invoke();

        Visible = false;
    }

    private static string RowText(Row row, int value)
    {
        float v = row.ValueAt(value);
        string shown = row.Unit == "deg" ? $"{v:0}" : $"{v:0.00}";
        return row.Unit == "arena"
            ? $"{row.Name}: {v * 100.0f:0}% arena"
            : $"{row.Name}: {shown}{(row.Unit == "deg" ? " deg" : row.Unit == "m" ? " m" : row.Unit)}";
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        foreach (Row r in _rows)
        {
            r.Slider.ResetPress();
        }
        _log.ResetPress();
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        foreach (Row r in _rows)
        {
            r.Slider.PollPoke(probes);
        }
        _log.PollPoke(probes);
    }
}
