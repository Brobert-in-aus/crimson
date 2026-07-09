using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// A floating, pressable main-menu item rendered from the ORIGINAL Crimsonland
/// menu art (PLAN M4): the ui_menuItem plate with a label sub-rect from the
/// ui_itemTexts atlas composited on top. Unlike <see cref="VrButton"/> it has no
/// fabricated socket/box chrome — the textured plate itself sits proud of the
/// menu and is poked in with a controller tip (the visible hand sphere is the
/// collider, matching VrButton), firing <see cref="OnPress"/> on the press-down
/// edge at ~90% depression.
///
/// Local frame matches VrButton: the item lies in local XY, the front normal is
/// local +Z (toward the player), and the plate rests proud at local +Z = _proud.
/// Built as a child of the menu root so it inherits the player-facing placement.
/// </summary>
public sealed partial class VrMenuItem : Node3D
{
    // Plate art aspect: ui_menuItem.png is 512x64, so height = width/8.
    private const float PlateAspect = 64.0f / 512.0f;
    // ui_itemTexts.png atlas: 128x256, one label row is 122x32 (visible 28 tall).
    private const float AtlasW = 128.0f;
    private const float AtlasH = 256.0f;
    private const float LabelRectW = 122.0f;
    private const float LabelRectH = 32.0f;
    // The hand-marker sphere (this radius) is the collider — matches VrButton.
    private const float PokeRadius = 0.02f;

    private Node3D _group = null!;   // plate + label; slides along local Z on poke
    private float _halfW;
    private float _halfH;
    private float _proud;
    private float _pressDepth;
    private bool _pressed;
    // Must be seen released once before it can fire again — stops an instant press
    // when the menu is (re)shown with a fingertip already inside the item volume.
    private bool _armed;
    private bool _enabled = true;

    private StandardMaterial3D _plateMat = null!;
    private StandardMaterial3D? _labelMat;

    /// <summary>Fired once when the item is pushed past the press depth.</summary>
    public event Action? OnPress;

    /// <summary>Caller payload (e.g. a menu action id); purely for the owner.</summary>
    public int Payload;

    public void Build(float width, Texture2D? plateTex, Texture2D? labelTex, int labelRow, float proud = 0.02f)
    {
        float height = width * PlateAspect;
        _halfW = width * 0.5f;
        _halfH = height * 0.5f;
        _proud = proud;
        _pressDepth = proud * 0.1f; // fires once pushed ~90% of the way in

        _group = new Node3D { Position = new Vector3(0.0f, 0.0f, _proud) };
        AddChild(_group);

        // The plate (ui_menuItem). Falls back to a translucent grey slab if the
        // art isn't staged, so the menu still works without user-supplied assets.
        _plateMat = FlatTexMat(plateTex, new Color(0.55f, 0.57f, 0.65f, 0.9f), priority: 30);
        var plate = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(width, height) },
            MaterialOverride = _plateMat,
        };
        _group.AddChild(plate);

        // The label (one row of ui_itemTexts) centred a hair in front of the plate
        // so it composites on top, sized to sit within the plate keeping the row's
        // 122:32 aspect. The single row is selected with a UV crop: AtlasTexture
        // does NOT crop when sampled as a 3D material albedo (the GPU samples the
        // whole sheet, so every item showed all 8 rows stacked), whereas Uv1
        // scale/offset remaps the QuadMesh UVs into just this row's sub-rect.
        if (labelTex != null)
        {
            float labelH = height * 0.62f;
            float labelW = labelH * (LabelRectW / LabelRectH);
            _labelMat = new StandardMaterial3D
            {
                AlbedoTexture = labelTex,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                TextureRepeat = false,
                Uv1Scale = new Vector3(LabelRectW / AtlasW, LabelRectH / AtlasH, 1.0f),
                Uv1Offset = new Vector3(0.0f, labelRow * LabelRectH / AtlasH, 0.0f),
                RenderPriority = 31,
            };
            var label = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(labelW, labelH) },
                Position = new Vector3(0.0f, 0.0f, 0.002f),
                MaterialOverride = _labelMat,
            };
            _group.AddChild(label);
        }
    }

    private static StandardMaterial3D FlatTexMat(Texture2D? tex, Color fallback, int priority)
        => new StandardMaterial3D
        {
            AlbedoTexture = tex,
            AlbedoColor = tex != null ? Colors.White : fallback,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            RenderPriority = priority,
        };

    /// <summary>Disable interaction and dim the item (e.g. a not-yet-implemented
    /// entry kept for a faithful layout).</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Modulate(enabled ? 1.0f : 0.4f);
    }

    private void Modulate(float a)
    {
        _plateMat.AlbedoColor = _plateMat.AlbedoTexture != null
            ? new Color(1.0f, 1.0f, 1.0f, a)
            : new Color(0.55f, 0.57f, 0.65f, 0.9f * a);
        if (_labelMat != null)
        {
            _labelMat.AlbedoColor = new Color(1.0f, 1.0f, 1.0f, a);
        }
    }

    /// <summary>Update the depress state from the controller probes. Call every
    /// rendered frame while the item is visible.</summary>
    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!_enabled)
        {
            return;
        }
        bool inside = false;
        float frontZ = _proud; // least-pressed default
        foreach (HandProbe h in probes)
        {
            if (!h.Valid)
            {
                continue;
            }
            Vector3 local = ToLocal(h.Tip);
            float surface = local.Z - PokeRadius; // sphere front toward the panel
            if (Mathf.Abs(local.X) <= _halfW + PokeRadius && Mathf.Abs(local.Y) <= _halfH + PokeRadius && surface < _proud)
            {
                inside = true;
                frontZ = Mathf.Min(frontZ, surface);
            }
        }

        float z = inside ? Mathf.Clamp(frontZ, 0.0f, _proud) : _proud;
        _group.Position = new Vector3(0.0f, 0.0f, z);

        bool nowPressed = inside && z <= _pressDepth;
        if (!nowPressed)
        {
            _armed = true; // released -> may fire on the next press-down edge
        }
        else if (_armed && !_pressed)
        {
            OnPress?.Invoke();
        }
        _pressed = nowPressed;
    }

    /// <summary>Reset to the unpressed rest state (e.g. when the menu hides).</summary>
    public void ResetPress()
    {
        _pressed = false;
        _armed = false; // require a release before the next press can fire
        if (_group != null)
        {
            _group.Position = new Vector3(0.0f, 0.0f, _proud);
        }
    }
}
