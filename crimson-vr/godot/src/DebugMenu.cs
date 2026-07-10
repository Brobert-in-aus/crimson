using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// In-headset debug FX menu: poke toggles that force individual effect render
/// passes on/off at runtime (see <see cref="DebugFx"/>). Shown only while the
/// debug setting is on. Mirrors the ValidationChecklist panel, standing off to
/// the player's LEFT (arena-local -x, the checklist's mirror) so both can be
/// open at once. Toggles are session-only and default off.
/// </summary>
public sealed partial class DebugMenu : Node3D
{
    private static readonly (string Label, Func<bool> Get, Action<bool> Set)[] Items =
    {
        ("Laser sight", () => DebugFx.LaserSight, v => DebugFx.LaserSight = v),
        ("Radioactive aura", () => DebugFx.RadioactiveAura, v => DebugFx.RadioactiveAura = v),
        ("Shield ring", () => DebugFx.ShieldRing, v => DebugFx.ShieldRing = v),
        ("Monster vision", () => DebugFx.MonsterVision, v => DebugFx.MonsterVision = v),
        ("Creature auras 1-in-10", () => DebugFx.CreatureAuras, v => DebugFx.CreatureAuras = v),
    };

    private static readonly Color OffColor = new(0.45f, 0.45f, 0.5f);
    private static readonly Color OnColor = new(0.3f, 0.7f, 0.35f);

    private readonly VrButton[] _rows = new VrButton[Items.Length];

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        // Mirror of the checklist panel (which stands at +x): off to the
        // player's LEFT, faced inward. Tune yaw sign in-headset if flipped.
        Position = new Vector3(-s * 1.25f, s * 0.9f, 0.0f);
        RotationDegrees = new Vector3(0.0f, 90.0f, 0.0f);

        var title = new Label3D
        {
            Text = "Debug FX",
            FontSize = 120,
            PixelSize = s / 1100.0f,
            Modulate = new Color(0.9f, 0.9f, 0.95f),
            OutlineSize = 24,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            Position = new Vector3(0.0f, s * 0.42f, 0.0f),
            NoDepthTest = true,
        };
        AddChild(title);

        // Same row metrics as the checklist (the 2cm poke sphere needs the
        // doubled height + gap to not span two rows).
        float rw = s * 0.78f;
        float rh = s * 0.18f;
        float gap = s * 0.03f;
        float top = s * 0.32f;

        for (int i = 0; i < Items.Length; i++)
        {
            var b = new VrButton();
            AddChild(b);
            b.Build(rw, rh, string.Empty, OffColor);
            b.Position = new Vector3(0.0f, top - i * (rh + gap), 0.0f);
            int row = i;
            b.OnPress += () => ToggleItem(row);
            _rows[i] = b;
        }
        RefreshAll();

        Visible = false;
    }

    public void SetShown(bool visible)
    {
        Visible = visible;
        if (visible)
        {
            RefreshAll();
        }
        else
        {
            foreach (VrButton r in _rows)
            {
                r.ResetPress();
            }
        }
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!Visible)
        {
            return;
        }
        foreach (VrButton r in _rows)
        {
            r.PollPoke(probes);
        }
    }

    private void ToggleItem(int row)
    {
        (string label, Func<bool> get, Action<bool> set) = Items[row];
        bool on = !get();
        set(on);
        ApplyRow(row);
        GD.Print($"CVRDEBUG {label} -> {(on ? "ON" : "OFF")}");
    }

    private void RefreshAll()
    {
        for (int i = 0; i < Items.Length; i++)
        {
            ApplyRow(i);
        }
    }

    private void ApplyRow(int row)
    {
        (string label, Func<bool> get, _) = Items[row];
        bool on = get();
        _rows[row].SetText((on ? "[ON]  " : "[OFF] ") + label);
        _rows[row].SetColor(on ? OnColor : OffColor);
    }
}
