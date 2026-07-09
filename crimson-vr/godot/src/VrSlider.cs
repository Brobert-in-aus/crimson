using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// A diegetic grab-and-drag slider for VR menus (PLAN M4 UI model). A knob sits
/// proud of a track along local X; grabbing it (grip squeezed while the tip is on
/// the knob) latches to that hand and the value follows the tip's position along
/// the track until the grip releases. Value maps to a caller [min, max] range.
///
/// Probes come in as a fixed [left, right] span so a grab keeps a stable hand
/// identity across frames. Built from code as a child of a menu root. Dimensions
/// are metres; first-pass, tune in-headset.
/// </summary>
public sealed partial class VrSlider : Node3D
{
    private MeshInstance3D _knob = null!;
    private StandardMaterial3D _knobMat = null!;

    private float _length;
    private float _knobHalfW;
    private float _trackHalfH;
    private float _proud;
    private float _min;
    private float _max;
    private float _value01;
    private int _grabHand = -1;

    private static readonly Color KnobIdle = new(0.7f, 0.75f, 0.85f);
    private static readonly Color KnobHeld = Colors.White;

    public float Value => _min + _value01 * (_max - _min);

    /// <summary>Fired with the mapped value while the knob is dragged.</summary>
    public event Action<float>? OnValueChanged;

    public void Build(float length, float thickness, float min, float max, float value)
    {
        _length = length;
        _min = min;
        _max = max;
        _value01 = max > min ? Mathf.Clamp((value - min) / (max - min), 0.0f, 1.0f) : 0.0f;
        _trackHalfH = thickness * 0.5f;
        _proud = thickness * 1.4f;
        _knobHalfW = length * 0.06f;

        var track = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(length, thickness, thickness * 0.6f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.12f, 0.12f, 0.15f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(track);

        _knobMat = new StandardMaterial3D
        {
            AlbedoColor = KnobIdle,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _knob = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(_knobHalfW * 2.0f, thickness * 2.4f, thickness * 1.4f) },
            MaterialOverride = _knobMat,
        };
        AddChild(_knob);
        UpdateKnob();
    }

    public void SetValue(float value)
    {
        _value01 = _max > _min ? Mathf.Clamp((value - _min) / (_max - _min), 0.0f, 1.0f) : 0.0f;
        UpdateKnob();
    }

    /// <summary>Update the grab/drag from the controller probes each frame.</summary>
    public void PollGrab(ReadOnlySpan<HandProbe> probes)
    {
        if (_grabHand < 0)
        {
            float knobX = (_value01 - 0.5f) * _length;
            for (int i = 0; i < probes.Length; i++)
            {
                if (!probes[i].Valid || !probes[i].Grip)
                {
                    continue;
                }
                Vector3 local = ToLocal(probes[i].Tip);
                if (Mathf.Abs(local.X - knobX) <= _knobHalfW + 0.03f
                    && Mathf.Abs(local.Y) <= _trackHalfH + 0.03f
                    && Mathf.Abs(local.Z - _proud) <= 0.04f)
                {
                    _grabHand = i;
                    _knobMat.AlbedoColor = KnobHeld;
                    break;
                }
            }
            return;
        }

        if (_grabHand >= probes.Length || !probes[_grabHand].Valid || !probes[_grabHand].Grip)
        {
            _grabHand = -1;
            _knobMat.AlbedoColor = KnobIdle;
            return;
        }

        float localX = ToLocal(probes[_grabHand].Tip).X;
        float v = Mathf.Clamp((localX + _length * 0.5f) / _length, 0.0f, 1.0f);
        if (Mathf.Abs(v - _value01) > 1e-4f)
        {
            _value01 = v;
            UpdateKnob();
            OnValueChanged?.Invoke(Value);
        }
    }

    public void ResetGrab()
    {
        _grabHand = -1;
        if (_knobMat != null)
        {
            _knobMat.AlbedoColor = KnobIdle;
        }
    }

    private void UpdateKnob()
        => _knob.Position = new Vector3((_value01 - 0.5f) * _length, 0.0f, _proud);
}
