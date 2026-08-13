using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Player-facing layout screen, opened from VR Settings. Holds the arena's
/// placement sliders and, for as long as it is open, puts the widgets into edit
/// mode so their corner handles can be grabbed.
///
/// This is deliberately NOT behind the debug flag. Where a player wants the
/// board, how big, how steeply tilted, and where the action buttons sit are
/// comfort decisions that depend on their room, their chair and their reach —
/// nobody else can pick those numbers for them. The debug Layout panel keeps
/// only the values that exist to be measured once and baked into constants.
///
/// Edit mode being scoped to this menu's lifetime is what makes it safe: there
/// is no way to end up in gameplay with the action buttons inert, because
/// leaving the screen is the same action that ends editing.
/// </summary>
public sealed partial class ArenaLayoutMenu : Node3D
{
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
        public int StepFor(float value) => Mathf.Clamp(Mathf.RoundToInt((value - Min) / Step), 0, Steps);
    }

    private Row[] _rows = Array.Empty<Row>();
    private VrButton _reset = null!;
    private VrButton _back = null!;
    private ResetState _resetState;
    private enum ResetState { Idle, Confirm, Undo }

    public event Action? OnBack;
    public event Action? OnReset;
    public event Action? OnUndoReset;

    public void Build(
        float arenaSideMeters,
        float scale, Action<float> onScale,
        float pitch, Action<float> onPitch,
        float distance, Action<float> onDistance,
        float drop, Action<float> onDrop,
        float spriteHeight, Action<float> onSpriteHeight,
        Texture2D? rectOn, Texture2D? rectOff)
    {
        float s = arenaSideMeters;
        // Edit controls live on the left wall, leaving the centre clear for the
        // fixed perk-card preview and the mirrored action buttons being placed.
        Position = new Vector3(-s * 1.25f, s * 0.9f, 0.0f);
        RotationDegrees = new Vector3(0.0f, 90.0f, 0.0f);

        float y = s * 0.5f;
        AddTitle(s, y);
        y -= s * 0.15f;

        _rows = new[]
        {
            new Row { Name = "Arena size", Steps = 20, Min = 0.5f, Step = 0.25f, Unit = "x", Apply = onScale },
            new Row { Name = "Arena tilt", Steps = 18, Min = 0.0f, Step = 5.0f, Unit = "deg", Apply = onPitch },
            new Row { Name = "Arena distance", Steps = 20, Min = 0.2f, Step = 0.1f, Unit = "m", Apply = onDistance },
            new Row { Name = "Arena height", Steps = 20, Min = 0.0f, Step = 0.05f, Unit = "m", Apply = onDrop },
            new Row { Name = "Sprite height", Steps = 20, Min = 0.0f, Step = 0.1f, Unit = "x", Apply = onSpriteHeight },
        };
        float[] current = { scale, pitch, distance, drop, spriteHeight };

        for (int i = 0; i < _rows.Length; i++)
        {
            Row row = _rows[i];
            int value = row.StepFor(current[i]);

            row.Label = new Label3D
            {
                Text = RowText(row, value),
                FontSize = 72,
                PixelSize = s / 1100.0f,
                Modulate = new Color(0.85f, 0.87f, 0.95f),
                OutlineSize = 20,
                OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
                Position = new Vector3(0.0f, y + s * 0.06f, 0.002f),
                NoDepthTest = true,
            };
            AddChild(row.Label);

            row.Slider = new VrSegmentedSlider();
            AddChild(row.Slider);
            row.Slider.Build(s * 0.024f, 0, row.Steps, value, rectOn, rectOff);
            row.Slider.Position = new Vector3(0.0f, y, 0.0f);
            Row captured = row;
            row.Slider.OnValueChanged += v =>
            {
                captured.Label.Text = RowText(captured, v);
                captured.Apply?.Invoke(captured.ValueAt(v));
            };
            y -= s * 0.18f;
        }

        var hint = new Label3D
        {
            Text = "Grab a corner to move  •  two corners to size",
            FontSize = 52,
            PixelSize = s / 1100.0f,
            Modulate = new Color(0.7f, 0.72f, 0.8f),
            OutlineSize = 18,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.002f),
            NoDepthTest = true,
        };
        AddChild(hint);
        y -= s * 0.12f;

        _reset = new VrButton();
        AddChild(_reset);
        _reset.Build(s * 0.58f, s * 0.09f, "Reset buttons & pad", new Color(0.7f, 0.45f, 0.4f), plate: true);
        _reset.Position = new Vector3(0.0f, y, 0.0f);
        _reset.OnPress += PressReset;
        y -= s * 0.13f;

        _back = new VrButton();
        AddChild(_back);
        _back.Build(s * 0.35f, s * 0.09f, "Back", new Color(0.6f, 0.6f, 0.66f), plate: true);
        _back.Position = new Vector3(0.0f, y, 0.0f);
        _back.OnPress += () => OnBack?.Invoke();

        Visible = false;
    }

    private void AddTitle(float s, float y)
    {
        AddChild(new Label3D
        {
            Text = "Arena & Layout",
            FontSize = 120,
            PixelSize = s / 1000.0f,
            Modulate = new Color(0.45f, 0.72f, 1.0f),
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, y, 0.002f),
            NoDepthTest = true,
        });
    }

    private static string RowText(Row row, int value)
    {
        float v = row.ValueAt(value);
        return row.Unit switch
        {
            "deg" => $"{row.Name}: {v:0} deg",
            "m" => $"{row.Name}: {v:0.00} m",
            _ => $"{row.Name}: {v:0.00}x",
        };
    }

    /// <summary>Push values in from outside (a control-mode switch replaces all
    /// four at once) without firing the Apply callbacks back at the caller.</summary>
    public void SyncPlacement(float scale, float pitch, float distance, float drop)
    {
        float[] values = { scale, pitch, distance, drop };
        for (int i = 0; i < values.Length && i < _rows.Length; i++)
        {
            Row row = _rows[i];
            int step = row.StepFor(values[i]);
            row.Slider.SetValue(step);
            row.Label.Text = RowText(row, step);
        }
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        if (!visible)
        {
            SetResetState(ResetState.Idle);
        }
        foreach (Row r in _rows)
        {
            r.Slider.ResetPress();
        }
        _reset.ResetPress();
        _back.ResetPress();
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
        _reset.PollPoke(probes);
        _back.PollPoke(probes);
    }

    private void PressReset()
    {
        switch (_resetState)
        {
            case ResetState.Idle:
                SetResetState(ResetState.Confirm);
                break;
            case ResetState.Confirm:
                OnReset?.Invoke();
                SetResetState(ResetState.Undo);
                break;
            case ResetState.Undo:
                OnUndoReset?.Invoke();
                SetResetState(ResetState.Idle);
                break;
        }
    }

    private void SetResetState(ResetState state)
    {
        _resetState = state;
        if (_reset == null)
        {
            return;
        }
        _reset.SetText(state switch
        {
            ResetState.Confirm => "Confirm reset",
            ResetState.Undo => "Undo reset",
            _ => "Reset buttons & pad",
        });
        _reset.ResetPress();
    }
}
