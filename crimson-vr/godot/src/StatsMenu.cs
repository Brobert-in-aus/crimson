using System;
using System.Text;
using Godot;

namespace CrimsonVR;

/// <summary>Lifetime statistics plus routes to the score browser, databases,
/// and credits. Keeping these on a hub avoids crowding the main VR menu.</summary>
public sealed partial class StatsMenu : Node3D
{
    private UserSettings _settings = null!; private SmallFontLabel? _statsText; private Label3D? _fallback;
    private VrButton _weapons = null!, _perks = null!, _scores = null!, _credits = null!, _back = null!;
    public bool IsOpen { get; private set; }
    public event Action? OnBack; public event Action? OnWeapons; public event Action? OnPerks;
    public event Action? OnHighScores; public event Action? OnCredits;

    public void Build(float s, UserSettings settings)
    {
        _settings = settings; Position = SpatialMenuPlacement.PlayerFacing(s); RotationDegrees = new Vector3(-12, 180, 0);
        ClassicPanel.Build(this, s * 1.5f, s * 1.15f, -.012f);
        ClassicTitle.BuildRow(this, s * .5f, ClassicTitle.RowStatistics, s * .47f);
        if (SmallFont.Shared() is { } font) { _statsText = new SmallFontLabel(); AddChild(_statsText); _statsText.Build(font, s / 620f, new Color(1,1,1,.85f)); _statsText.Position = new Vector3(0, s * .13f, 0); }
        else { _fallback = new Label3D { FontSize = 70, PixelSize = s / 1300f, Position = new Vector3(0, s * .13f, 0), HorizontalAlignment = HorizontalAlignment.Center }; AddChild(_fallback); }
        _weapons = Button(s, -.38f, -.27f, "Weapons", () => OnWeapons?.Invoke());
        _perks = Button(s, 0, -.27f, "Perks", () => OnPerks?.Invoke());
        _scores = Button(s, .38f, -.27f, "High Scores", () => OnHighScores?.Invoke());
        _credits = Button(s, -.2f, -.48f, "Credits", () => OnCredits?.Invoke());
        _back = Button(s, .2f, -.48f, "Back", () => OnBack?.Invoke()); Visible = false;
    }
    private VrButton Button(float s, float x, float y, string text, Action action) { var b = new VrButton(); AddChild(b); b.BuildClassic(s * .3f, s * .11f, text); b.Position = new Vector3(x*s,y*s,0); b.OnPress += action; return b; }
    public void Open() { IsOpen = Visible = true; foreach (var b in new[] { _weapons,_perks,_scores,_credits,_back }) b.ResetPress(); Refresh(); }
    public void Close() { IsOpen = Visible = false; }
    public void PollPoke(ReadOnlySpan<HandProbe> p) { if (!IsOpen) return; foreach (var b in new[] { _weapons,_perks,_scores,_credits,_back }) b.PollPoke(p); }
    private void Refresh() { string text = BuildStats(); _statsText?.SetText(text); if (_fallback != null) _fallback.Text = text; }
    private string BuildStats() { var sb = new StringBuilder(); Append(sb,"Survival",1); Append(sb,"Rush",2); Append(sb,"Quests",3); Append(sb,"Tutorial",8); return sb.ToString(); }
    private void Append(StringBuilder sb, string name, int mode)
    {
        ModeStats st = _settings.StatsFor(mode); long mins = st.PlayMs / 60000; float acc = st.Shots > 0 ? 100f * st.Hits / st.Shots : 0;
        sb.Append(name).Append(":  ").Append(st.Runs).Append(" games   ").Append(mins/60).Append('h').Append((mins%60).ToString("D2"))
          .Append("m   ").Append(st.Kills).Append(" kills   ").Append(acc.ToString("F0")).Append("% acc");
        if (mode is 1 or 2) sb.Append("   best ").Append(st.BestScore); sb.Append('\n');
    }
}
