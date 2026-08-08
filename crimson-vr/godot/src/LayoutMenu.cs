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

    public void Build(
        float arenaSideMeters,
        float spriteHeight, Action<float> onSpriteHeight,
        float aimLine, Action<float> onAimLine,
        float scale, Action<float> onScale,
        float pitch, Action<float> onPitch,
        float distance, Action<float> onDistance,
        float drop, Action<float> onDrop,
        Texture2D? rectOn, Texture2D? rectOff)
    {
        float s = arenaSideMeters;
        // Mirror of the debug FX panel, which stands at -x: this goes to the
        // player's RIGHT, faced inward, so both can be open at once.
        Position = new Vector3(s * 1.25f, s * 0.9f, 0.0f);
        RotationDegrees = new Vector3(0.0f, -90.0f, 0.0f);

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

        _rows = new[]
        {
            // Sprite lift multiplier: 0 pins every entity flat on the terrain,
            // 1.0 is the tabletop-tuned original, 2.0 doubles the stack.
            new Row { Name = "Sprite height", Steps = 20, Min = 0.0f, Step = 0.1f, Unit = "x", Apply = onSpriteHeight },
            // Cursor pillar as a fraction of the arena side.
            new Row { Name = "Aim line", Steps = 20, Min = 0.0f, Step = 0.05f, Unit = "arena", Apply = onAimLine },
            // Board size multiplier on the 0.4 m reference square.
            new Row { Name = "Arena scale", Steps = 20, Min = 0.5f, Step = 0.25f, Unit = "x", Apply = onScale },
            // Far-edge lift. 0 is the old flat tabletop, 90 stands it vertical.
            new Row { Name = "Arena tilt", Steps = 18, Min = 0.0f, Step = 5.0f, Unit = "deg", Apply = onPitch },
            // Near edge, forward of the head.
            new Row { Name = "Arena distance", Steps = 20, Min = 0.2f, Step = 0.1f, Unit = "m", Apply = onDistance },
            // Near edge, below the head. Larger = board sits lower.
            new Row { Name = "Arena drop", Steps = 20, Min = 0.0f, Step = 0.05f, Unit = "m", Apply = onDrop },
        };

        float[] current = { spriteHeight, aimLine, scale, pitch, distance, drop };
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

    /// <summary>Push placement values in from outside (a control-mode switch
    /// replaces all four at once) so the pips and labels match reality without
    /// firing the Apply callbacks back at the caller.</summary>
    public void SyncPlacement(float scale, float pitch, float distance, float drop)
    {
        float[] values = { scale, pitch, distance, drop };
        // Rows 2..5 are the placement group; 0..1 are sprite height and aim line.
        for (int i = 0; i < values.Length && i + 2 < _rows.Length; i++)
        {
            Row row = _rows[i + 2];
            int step = row.StepFor(values[i]);
            row.Slider.SetValue(step);
            row.Label.Text = RowText(row, step);
        }
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        foreach (Row r in _rows)
        {
            r.Slider.ResetPress();
        }
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
    }
}
