using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// A poke checkbox rendered from the original ui_checkOn / ui_checkOff art with a
/// text label beside it (mirrors the Options screen "UI Info texts" toggle). Poke
/// the box to flip it; fires <see cref="OnToggled"/> with the new state. Uses the
/// same arm-on-release guard as the buttons so being shown under a fingertip does
/// not instantly toggle.
///
/// Built as a child of a menu panel; the box lies in local XY with its front face
/// at local +Z, label to its right.
/// </summary>
public sealed partial class VrCheckbox : Node3D
{
    private const float PokeRadius = 0.02f;
    private const float PressDepth = 0.008f;

    private StandardMaterial3D _boxMat = null!;
    private Texture2D? _onTex;
    private Texture2D? _offTex;
    private float _half;
    private bool _state;
    private bool _pressed;
    private bool _armed;
    private ulong _readyAtMs;
    private const ulong ShowCooldownMs = 350;

    public event Action<bool>? OnToggled;

    public bool State => _state;

    public void Build(float boxSize, string label, bool state, Texture2D? onTex, Texture2D? offTex)
    {
        _half = boxSize * 0.5f;
        _state = state;
        _onTex = onTex;
        _offTex = offTex;

        _boxMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            RenderPriority = 32,
        };
        var box = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(boxSize, boxSize) },
            MaterialOverride = _boxMat,
        };
        AddChild(box);

        var text = new Label3D
        {
            Text = label,
            FontSize = 72,
            PixelSize = boxSize / 220.0f,
            Modulate = new Color(0.9f, 0.9f, 0.92f),
            OutlineSize = 16,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f),
            HorizontalAlignment = HorizontalAlignment.Left,
            Position = new Vector3(boxSize * 0.8f, 0.0f, 0.0f),
            NoDepthTest = true,
        };
        AddChild(text);
        Refresh();
    }

    private void Refresh() => _boxMat.AlbedoTexture = _state ? _onTex : _offTex;

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        bool nowPressed = false;
        bool overFootprint = false;
        foreach (HandProbe h in probes)
        {
            if (!h.Valid)
            {
                continue;
            }
            Vector3 local = ToLocal(h.Tip);
            if (Mathf.Abs(local.X) <= _half + PokeRadius && Mathf.Abs(local.Y) <= _half + PokeRadius)
            {
                overFootprint = true;
                if (local.Z - PokeRadius <= PressDepth)
                {
                    nowPressed = true;
                }
            }
        }
        // Arm only once the fingertip has left the box footprint entirely.
        if (!overFootprint)
        {
            _armed = true;
        }
        else if (nowPressed && !_pressed && _armed && Time.GetTicksMsec() >= _readyAtMs)
        {
            _state = !_state;
            Refresh();
            OnToggled?.Invoke(_state);
        }
        _pressed = nowPressed;
    }

    public void ResetPress()
    {
        _pressed = false;
        _armed = false;
        _readyAtMs = Time.GetTicksMsec() + ShowCooldownMs;
    }
}
