using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// A segmented value slider rendered like the original Crimsonland Options screen:
/// a row of cells drawn from ui_rectOn (filled) / ui_rectOff (empty). Instead of a
/// mouse drag it is POKED — a fingertip over a cell sets the value to that cell
/// (poke-drag while held), so it fits the physical VR UI model. Values are integer
/// steps in [min, max]; cell i is filled when i &lt; value.
///
/// Built as a child of a menu panel so it inherits the player-facing placement.
/// Cells lie in local XY with the front face at local +Z; poke points are world
/// space (via <see cref="PollPoke"/>).
/// </summary>
public sealed partial class VrSegmentedSlider : Node3D
{
    private const float PokeRadius = 0.02f;   // matches the hand-marker sphere
    private const float PressDepth = 0.008f;  // fingertip must be within this of the face
    private const float ActiveProud = 0.012f; // active pips sit proud; inactive behind

    private MeshInstance3D[] _cells = Array.Empty<MeshInstance3D>();
    private StandardMaterial3D[] _cellMats = Array.Empty<StandardMaterial3D>();
    private Texture2D? _onTex;
    private Texture2D? _offTex;

    private int _count;   // = max (number of cells)
    private int _min;
    private int _max;
    private int _value;
    private float _cellW;
    private float _halfH;
    private float _stripHalfW;

    public event Action<int>? OnValueChanged;

    public int Value => _value;

    public void Build(float cellWidth, int min, int max, int value, Texture2D? onTex, Texture2D? offTex)
    {
        _min = min;
        _max = max;
        _count = max;
        _value = Mathf.Clamp(value, min, max);
        _cellW = cellWidth;
        _onTex = onTex;
        _offTex = offTex;

        float cellH = cellWidth * 2.0f; // ui_rectOn is 8x16 (1:2)
        _halfH = cellH * 0.5f;
        _stripHalfW = _count * cellWidth * 0.5f;

        // Two layers per slot: a full row of inactive pips ALWAYS visible at the
        // back, and an active pip that sits PROUD in front, shown only where the
        // value reaches. So the inactive set is always the visible baseline and the
        // active pips read as raised-in-front markers.
        _cells = new MeshInstance3D[_count]; // active overlays (toggled)
        _cellMats = new StandardMaterial3D[_count];
        var mesh = new QuadMesh { Size = new Vector2(cellWidth * 0.9f, cellH) };
        for (int i = 0; i < _count; i++)
        {
            float x = -_stripHalfW + (i + 0.5f) * cellWidth;

            var backMat = new StandardMaterial3D
            {
                AlbedoTexture = offTex,
                AlbedoColor = new Color(1.0f, 1.0f, 1.0f, 0.55f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                RenderPriority = 31,
            };
            AddChild(new MeshInstance3D { Mesh = mesh, Position = new Vector3(x, 0.0f, 0.0f), MaterialOverride = backMat });

            var mat = new StandardMaterial3D
            {
                AlbedoTexture = onTex,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                RenderPriority = 33,
            };
            var cell = new MeshInstance3D
            {
                Mesh = mesh,
                Position = new Vector3(x, 0.0f, ActiveProud), // proud in front
                MaterialOverride = mat,
            };
            _cellMats[i] = mat;
            _cells[i] = cell;
            AddChild(cell);
        }
        Refresh();
    }

    private void Refresh()
    {
        for (int i = 0; i < _count; i++)
        {
            _cells[i].Visible = i < _value; // active pip shown only where reached
        }
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        foreach (HandProbe h in probes)
        {
            if (!h.Valid)
            {
                continue;
            }
            Vector3 local = ToLocal(h.Tip);
            if (Mathf.Abs(local.Y) > _halfH + PokeRadius || Mathf.Abs(local.X) > _stripHalfW + PokeRadius)
            {
                continue;
            }
            if (local.Z - PokeRadius > PressDepth) // not pushed in far enough
            {
                continue;
            }
            // Which cell is the fingertip over -> value = that cell + 1.
            float rel = local.X + _stripHalfW;
            int cell = (int)Mathf.Floor(rel / _cellW) + 1;
            int v = Mathf.Clamp(cell, _min, _max);
            if (v != _value)
            {
                _value = v;
                Refresh();
                OnValueChanged?.Invoke(_value);
            }
        }
    }
}
