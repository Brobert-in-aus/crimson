using System;
using Godot;

namespace CrimsonVR;

public sealed partial class CreditsMenu : Node3D
{
    private static readonly string[] Pages = {
        "2026 Remake\nbanteg\n\nCRIMSONLAND\n\nGame Design and Programming\nTero Alatalo\n\nProducer\nZach Young",
        "2D Art and 3D Modelling\nTero Alatalo\nTimo Palonen\n\nMusic\nValtteri Pihlajam\nVille Eriksson",
        "Sound Effects\nIon Hardie\nTero Alatalo\nValtteri Pihlajam\nVille Eriksson\n\nManual\nMiikka Kulmala\nZach Young",
        "Special Thanks\nPetri J\nPeter Hajba / Remedy\n\n10tons logo\nPasi Heinonen\n\n2003 (c) 10tons entertainment",
        "Open-source reimplementation\ncrimson contributors\n\nVR adaptation\nCrimsonVR contributors\n\nThank you for playing!"
    };
    private SmallFontLabel? _text; private Label3D? _fallback;
    private VrButton _prev = null!, _next = null!, _secret = null!, _back = null!; private int _page;
    public bool IsOpen { get; private set; }
    public event Action? OnBack; public event Action? OnSecret;
    public void Build(float s)
    {
        Position = new Vector3(0, s * .85f, s * .25f); RotationDegrees = new Vector3(-12, 180, 0);
        ClassicPanel.Build(this, s * 1.25f, s * 1.05f, -.012f);
        if (SmallFont.Shared() is { } font) { _text = new SmallFontLabel(); AddChild(_text); _text.Build(font, s / 570f, Colors.White); _text.Position = new Vector3(0, s * .08f, 0); }
        else { _fallback = new Label3D { FontSize = 65, PixelSize = s / 1200f, Position = new Vector3(0, s * .08f, 0), HorizontalAlignment = HorizontalAlignment.Center }; AddChild(_fallback); }
        _prev = Button(s, -.38f, "<", () => { _page = (_page + Pages.Length - 1) % Pages.Length; Refresh(); });
        _secret = Button(s, 0, "Secret", () => OnSecret?.Invoke());
        _next = Button(s, .38f, ">", () => { _page = (_page + 1) % Pages.Length; Refresh(); });
        _back = Button(s, 0, "Back", () => OnBack?.Invoke(), -.48f);
        Visible = false;
    }
    private VrButton Button(float s, float x, string label, Action a, float y = -.36f) { var b = new VrButton(); AddChild(b); b.BuildClassic(s * .26f, s * .1f, label); b.Position = new Vector3(x * s, y * s, 0); b.OnPress += a; return b; }
    public void Open() { IsOpen = Visible = true; foreach (var b in new[] { _prev, _next, _secret, _back }) b.ResetPress(); Refresh(); }
    public void Close() { IsOpen = Visible = false; }
    public void PollPoke(ReadOnlySpan<HandProbe> p) { if (!IsOpen) return; foreach (var b in new[] { _prev, _next, _secret, _back }) b.PollPoke(p); }
    private void Refresh() { string text = Pages[_page] + $"\n\n{_page + 1} / {Pages.Length}"; _text?.SetText(text); if (_fallback != null) _fallback.Text = text; }
}
