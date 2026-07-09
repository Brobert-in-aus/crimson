using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M4 slice 6: an in-VR virtual keyboard for highscore name entry (PLAN M4 UI
/// model). On death it floats above the arena as a grid of poke keys (A-Z) plus
/// Space / Del / Enter and a live text readout; Enter raises
/// <see cref="OnSubmit"/> with the entered name (empty = skip).
///
/// A child of ArenaRoot, hidden until shown. Reuses the VrButton poke primitive.
/// Key size/spacing are first-pass — tune in-headset (poking small keys on the
/// 0.4 m scale is the thing most likely to need adjusting).
/// </summary>
public sealed partial class VirtualKeyboard : Node3D
{
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int Cols = 7;
    private const int MaxLen = 12;

    private readonly List<VrButton> _keys = new();
    private Label3D _display = null!;
    private Label3D _prompt = null!;
    private readonly StringBuilder _text = new();

    public bool Active { get; private set; }
    public event Action<string>? OnSubmit;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        Position = new Vector3(0.0f, s * 0.85f, 0.0f);
        RotationDegrees = new Vector3(-12.0f, 180.0f, 0.0f);

        _prompt = MakeLabel("Enter your name", s / 1300.0f, new Color(0.85f, 0.85f, 0.9f), new Vector3(0.0f, s * 0.72f, 0.0f));
        _display = MakeLabel("_", s / 650.0f, new Color(0.95f, 0.9f, 0.45f), new Vector3(0.0f, s * 0.6f, 0.0f));

        float kw = s * 0.11f;
        float kh = s * 0.11f;
        float gap = s * 0.02f;
        float startX = -((Cols * (kw + gap)) - gap) * 0.5f + kw * 0.5f;
        float rowTop = s * 0.46f;

        for (int i = 0; i < Letters.Length; i++)
        {
            int r = i / Cols;
            int c = i % Cols;
            char ch = Letters[i];
            var b = new VrButton();
            AddChild(b);
            b.Build(kw, kh, ch.ToString(), new Color(0.5f, 0.55f, 0.66f));
            b.Position = new Vector3(startX + c * (kw + gap), rowTop - r * (kh + gap), 0.0f);
            b.OnPress += () => Append(ch);
            _keys.Add(b);
        }

        // Control row under the letters: Space / Del / Enter.
        int rows = (Letters.Length + Cols - 1) / Cols;
        float ctrlY = rowTop - rows * (kh + gap);
        AddKey("Space", kw * 2.2f, kh, startX + kw * 1.4f, ctrlY, () => Append(' '));
        AddKey("Del", kw * 1.6f, kh, startX + kw * 3.4f, ctrlY, Backspace, new Color(0.6f, 0.5f, 0.5f));
        AddKey("Enter", kw * 2.0f, kh, startX + kw * 5.4f, ctrlY, Submit, new Color(0.4f, 0.7f, 0.45f));

        Visible = false;
    }

    /// <summary>Show the keyboard with a score context in the prompt.</summary>
    public void Show(int score)
    {
        _text.Clear();
        UpdateDisplay();
        _prompt.Text = $"Game over - score {score}. Enter your name:";
        foreach (VrButton k in _keys)
        {
            k.ResetPress();
        }
        Active = true;
        Visible = true;
    }

    public void Dismiss()
    {
        Active = false;
        Visible = false;
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Active)
        {
            return;
        }
        foreach (VrButton k in _keys)
        {
            k.PollPoke(probes);
        }
    }

    private void AddKey(string text, float w, float h, float x, float y, Action onPress, Color? color = null)
    {
        var b = new VrButton();
        AddChild(b);
        b.Build(w, h, text, color ?? new Color(0.5f, 0.55f, 0.66f));
        b.Position = new Vector3(x, y, 0.0f);
        b.OnPress += onPress;
        _keys.Add(b);
    }

    private Label3D MakeLabel(string text, float pixelSize, Color color, Vector3 pos)
    {
        var l = new Label3D
        {
            Text = text,
            FontSize = 110,
            PixelSize = pixelSize,
            Modulate = color,
            Position = pos,
            NoDepthTest = true,
        };
        AddChild(l);
        return l;
    }

    private void Append(char ch)
    {
        if (_text.Length < MaxLen)
        {
            _text.Append(ch);
            UpdateDisplay();
        }
    }

    private void Backspace()
    {
        if (_text.Length > 0)
        {
            _text.Remove(_text.Length - 1, 1);
            UpdateDisplay();
        }
    }

    private void Submit() => OnSubmit?.Invoke(_text.ToString().Trim());

    private void UpdateDisplay()
        => _display.Text = _text.Length > 0 ? _text.ToString() : "_";
}
