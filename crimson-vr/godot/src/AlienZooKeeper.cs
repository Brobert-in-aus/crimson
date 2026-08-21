using System;
using Godot;

namespace CrimsonVR;

/// <summary>The credits easter egg from the base game: swap any two aliens,
/// clear the first horizontal/vertical triple, and earn two seconds.</summary>
public sealed partial class AlienZooKeeper : Node3D
{
    private const int Side = 6;
    private static readonly string[] Glyphs = { "A", "B", "C", "D", "E" };
    private static readonly Color[] Colors = {
        new(.35f,.85f,.45f), new(.45f,.65f,1), new(1,.55f,.35f), new(.8f,.45f,1), new(1,.85f,.3f) };
    private readonly int[] _board = new int[Side * Side];
    private readonly VrButton[] _tiles = new VrButton[Side * Side];
    private readonly RandomNumberGenerator _rng = new();
    private SmallFontLabel? _status; private Label3D? _fallback; private VrButton _reset = null!, _back = null!;
    private int _selected = -1, _score; private double _remaining;
    public bool IsOpen { get; private set; } public event Action? OnBack;

    public void Build(float s)
    {
        Position = SpatialMenuPlacement.PlayerFacing(s); RotationDegrees = new Vector3(-12, 180, 0);
        ClassicPanel.Build(this, s * 1.15f, s * 1.3f, -.012f);
        if (SmallFont.Shared() is { } font) { _status = new SmallFontLabel(); AddChild(_status); _status.Build(font, s / 520f, Godot.Colors.White); _status.Position = new Vector3(0, s * .52f, 0); }
        else { _fallback = new Label3D { FontSize = 60, PixelSize = s / 1200f, Position = new Vector3(0, s * .52f, 0) }; AddChild(_fallback); }
        float size = s * .115f, gap = s * .012f, span = size + gap;
        for (int y = 0; y < Side; y++) for (int x = 0; x < Side; x++)
        {
            int i = y * Side + x; var b = new VrButton(); _tiles[i] = b; AddChild(b);
            b.BuildClassic(size, size, "A"); b.Position = new Vector3((x - 2.5f) * span, s * .37f - y * span, 0);
            int tile = i; b.OnPress += () => Select(tile);
        }
        _reset = Button(s, -.22f, "Reset", Reset); _back = Button(s, .22f, "Back", () => OnBack?.Invoke());
        Visible = false;
    }
    private VrButton Button(float s, float x, string text, Action action) { var b = new VrButton(); AddChild(b); b.BuildClassic(s * .32f, s * .1f, text); b.Position = new Vector3(x * s, -s * .52f, 0); b.OnPress += action; return b; }
    public void Open() { IsOpen = Visible = true; _rng.Randomize(); Reset(); }
    public void Close() { IsOpen = Visible = false; }
    public override void _Process(double delta) { if (!IsOpen || _remaining <= 0) return; _remaining = Math.Max(0, _remaining - delta); RefreshStatus(); }
    public void PollPoke(ReadOnlySpan<HandProbe> p) { if (!IsOpen) return; foreach (VrButton b in _tiles) b.PollPoke(p); _reset.PollPoke(p); _back.PollPoke(p); }
    private void Reset()
    {
        _score = 0; _remaining = 9.6; _selected = -1;
        do { for (int i = 0; i < _board.Length; i++) _board[i] = (int)(_rng.Randi() % Glyphs.Length); } while (FindMatch(out _, out _, out _));
        _reset.ResetPress(); _back.ResetPress(); foreach (VrButton b in _tiles) b.ResetPress(); Refresh();
    }
    private void Select(int index)
    {
        if (_remaining <= 0) return;
        if (_selected < 0) { _selected = index; Refresh(); return; }
        if (_selected == index) { _selected = -1; Refresh(); return; }
        (_board[_selected], _board[index]) = (_board[index], _board[_selected]); _selected = -1;
        if (FindMatch(out int start, out int step, out int length))
        {
            for (int n = 0; n < length; n++) _board[start + n * step] = (int)(_rng.Randi() % Glyphs.Length);
            _score++; _remaining += 2.0;
        }
        Refresh();
    }
    private bool FindMatch(out int start, out int step, out int length)
    {
        for (int y = 0; y < Side; y++) for (int x = 0; x <= Side - 3; x++) if (_board[y*Side+x] == _board[y*Side+x+1] && _board[y*Side+x] == _board[y*Side+x+2]) { start=y*Side+x; step=1; length=3; return true; }
        for (int x = 0; x < Side; x++) for (int y = 0; y <= Side - 3; y++) if (_board[y*Side+x] == _board[(y+1)*Side+x] && _board[y*Side+x] == _board[(y+2)*Side+x]) { start=y*Side+x; step=Side; length=3; return true; }
        start = step = length = 0; return false;
    }
    private void Refresh()
    {
        for (int i = 0; i < _tiles.Length; i++) { _tiles[i].SetText(Glyphs[_board[i]]); Color c = Colors[_board[i]]; _tiles[i].SetColor(i == _selected ? c.Lightened(.35f) : c); }
        RefreshStatus();
    }
    private void RefreshStatus() { string text = _remaining > 0 ? $"ALIEN ZOO KEEPER     Score {_score}     {_remaining:F1}s" : $"TIME UP     Score {_score}"; _status?.SetText(text); if (_fallback != null) _fallback.Text = text; }
}
