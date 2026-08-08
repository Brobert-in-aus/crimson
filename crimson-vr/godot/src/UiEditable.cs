using System;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Makes a widget draggable in UI edit mode by grabbing the corners of its
/// footprint. Attach one per editable widget; it draws four corner handles while
/// editing is on and rewrites the target's local transform from the grabs.
///
/// Grab rules (per the requested design):
/// <list type="bullet">
/// <item>ONE hand on any corner: translate.</item>
/// <item>TWO hands, <see cref="Mode.Free"/>: translate by the midpoint, scale by
/// the change in hand separation, and rotate by the rotation that carries the
/// old hand-to-hand vector onto the new one. Used for the action buttons, which
/// should be placeable anywhere at any angle.</item>
/// <item>TWO hands, <see cref="Mode.ScaleTilt"/>: scale and TILT only — position
/// and yaw are held. Used for the control rectangle, whose placement is derived
/// from the recenter pose and whose yaw must keep matching the board, but whose
/// size (hand travel) and pitch are worth tuning by feel.</item>
/// </list>
///
/// Grabs use GRIP, like <see cref="VrSlider"/>, so they never collide with the
/// poke that presses a button. Probes arrive as a fixed [left, right] span so a
/// grab keeps a stable hand identity across frames.
/// </summary>
public sealed partial class UiEditable : Node3D
{
    public enum Mode
    {
        /// <summary>Translate, scale and rotate freely (action buttons).</summary>
        Free,

        /// <summary>Scale and tilt only; position and yaw held (control rect).</summary>
        ScaleTilt,
    }

    private const float HandleRadius = 0.018f;  // generous: grabbed, not poked
    private const float GrabRadius = 0.045f;

    private Node3D _target = null!;
    private Mode _mode;
    private bool _cornersInXZ;
    private readonly MeshInstance3D[] _handles = new MeshInstance3D[4];
    private readonly Vector3[] _corners = new Vector3[4];

    // Per hand: which corner it holds, and where it was when it grabbed.
    private readonly int[] _heldCorner = { -1, -1 };
    private readonly Vector3[] _grabHand = new Vector3[2];
    private Transform3D _grabTarget;
    private bool _editing;

    /// <summary>Raised whenever a grab changes the target's transform, so the
    /// owner can persist it or mirror derived values (the control rectangle's
    /// scale IS its hand-travel range, for instance).</summary>
    public event Action<Transform3D>? OnTransformChanged;

    /// <param name="target">Node whose LOCAL transform is edited.</param>
    /// <param name="width">Footprint width in target-local metres.</param>
    /// <param name="height">Footprint height/depth in target-local metres.</param>
    /// <param name="cornersInXZ">true when the footprint lies in the local XZ
    /// plane (the control rectangle) rather than XY (button plates).</param>
    public void Build(Node3D target, Mode mode, float width, float height, bool cornersInXZ)
    {
        _target = target;
        _mode = mode;
        _cornersInXZ = cornersInXZ;

        float hw = width * 0.5f;
        float hh = height * 0.5f;
        for (int i = 0; i < 4; i++)
        {
            float sx = (i == 0 || i == 3) ? -hw : hw;
            float sy = (i < 2) ? -hh : hh;
            _corners[i] = cornersInXZ ? new Vector3(sx, 0.0f, sy) : new Vector3(sx, sy, 0.0f);

            var handle = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = HandleRadius, Height = HandleRadius * 2.0f },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(1.0f, 0.75f, 0.2f, 0.85f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest = true,
                },
                Position = _corners[i],
                Visible = false,
            };
            _handles[i] = handle;
            _target.AddChild(handle);
        }
    }

    public void SetEditing(bool editing)
    {
        _editing = editing;
        foreach (MeshInstance3D h in _handles)
        {
            h.Visible = editing;
        }
        if (!editing)
        {
            _heldCorner[0] = _heldCorner[1] = -1;
        }
    }

    /// <summary>Update grabs. Returns true while at least one hand holds a
    /// corner, so the caller can suppress the widget's normal interactions.</summary>
    public bool PollGrab(ReadOnlySpan<HandProbe> probes)
    {
        if (!_editing)
        {
            return false;
        }

        // Acquire / release.
        for (int i = 0; i < 2 && i < probes.Length; i++)
        {
            if (!probes[i].Valid || !probes[i].Grip)
            {
                _heldCorner[i] = -1;
                continue;
            }
            if (_heldCorner[i] >= 0)
            {
                continue;
            }
            int nearest = NearestCorner(probes[i].Tip);
            if (nearest >= 0)
            {
                _heldCorner[i] = nearest;
                _grabHand[i] = probes[i].Tip;
                _grabTarget = _target.Transform;
                // A second hand joining re-bases BOTH, so the two-handed gesture
                // measures from the moment it actually became two-handed rather
                // than from whenever the first hand happened to grab.
                int other = 1 - i;
                if (_heldCorner[other] >= 0)
                {
                    _grabHand[other] = probes[other].Tip;
                }
            }
        }

        bool a = _heldCorner[0] >= 0, b = _heldCorner[1] >= 0;
        if (!a && !b)
        {
            return false;
        }

        if (a && b)
        {
            ApplyTwoHanded(probes[0].Tip, probes[1].Tip);
        }
        else
        {
            int h = a ? 0 : 1;
            ApplyOneHanded(probes[h].Tip, h);
        }

        OnTransformChanged?.Invoke(_target.Transform);
        return true;
    }

    private int NearestCorner(Vector3 worldTip)
    {
        int best = -1;
        float bestDist = GrabRadius;
        for (int i = 0; i < 4; i++)
        {
            float d = _handles[i].GlobalPosition.DistanceTo(worldTip);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Translate by the hand's world delta, expressed in the parent's
    /// space so the widget tracks the hand regardless of how the parent is
    /// oriented (the control rect's parent is yawed to the recenter pose).</summary>
    private void ApplyOneHanded(Vector3 tip, int hand)
    {
        if (_mode == Mode.ScaleTilt)
        {
            // Position is owned by the recenter placement; a one-handed drag
            // would fight it on the next recenter and read as a bug.
            return;
        }
        Vector3 deltaWorld = tip - _grabHand[hand];
        Node3D parent = (Node3D)_target.GetParent();
        Vector3 delta = parent.GlobalBasis.Inverse() * deltaWorld;
        Transform3D t = _grabTarget;
        t.Origin += delta;
        _target.Transform = t;
    }

    private void ApplyTwoHanded(Vector3 tipA, Vector3 tipB)
    {
        Vector3 v0 = _grabHand[1] - _grabHand[0];
        Vector3 v = tipB - tipA;
        float len0 = v0.Length();
        if (len0 < 1e-4f || v.Length() < 1e-4f)
        {
            return;
        }
        float scale = Mathf.Clamp(v.Length() / len0, 0.2f, 6.0f);
        var parent = (Node3D)_target.GetParent();
        Basis parentInv = parent.GlobalBasis.Inverse();

        if (_mode == Mode.ScaleTilt)
        {
            // Scale uniformly, and read TILT off the change in the hand-vector's
            // elevation: raise one hand relative to the other and the rectangle
            // pitches by the same amount. Yaw and position are untouched.
            float elev0 = Mathf.Asin(Mathf.Clamp(v0.Normalized().Y, -1.0f, 1.0f));
            float elev = Mathf.Asin(Mathf.Clamp(v.Normalized().Y, -1.0f, 1.0f));
            float pitchDelta = elev - elev0;

            Vector3 euler = _grabTarget.Basis.GetEuler();
            float pitch = Mathf.Clamp(euler.X + pitchDelta, -Mathf.Pi * 0.5f, Mathf.Pi * 0.5f);
            var basis = Basis.FromEuler(new Vector3(pitch, euler.Y, euler.Z))
                .Scaled(_grabTarget.Basis.Scale * scale);
            _target.Transform = new Transform3D(basis, _grabTarget.Origin);
            return;
        }

        // Free: the rotation carrying v0 onto v, plus midpoint translation.
        Vector3 from = (parentInv * v0).Normalized();
        Vector3 to = (parentInv * v).Normalized();
        Basis rot = Basis.Identity;
        Vector3 axis = from.Cross(to);
        float dot = Mathf.Clamp(from.Dot(to), -1.0f, 1.0f);
        if (axis.LengthSquared() > 1e-8f)
        {
            rot = new Basis(axis.Normalized(), Mathf.Acos(dot));
        }
        else if (dot < 0.0f)
        {
            // Exactly reversed: any perpendicular axis gives the 180 turn.
            Vector3 perp = Mathf.Abs(from.X) < 0.9f ? Vector3.Right : Vector3.Up;
            rot = new Basis(from.Cross(perp).Normalized(), Mathf.Pi);
        }

        Vector3 midDelta = parentInv * (((tipA + tipB) * 0.5f) - ((_grabHand[0] + _grabHand[1]) * 0.5f));
        var newBasis = rot * _grabTarget.Basis;
        newBasis = newBasis.Orthonormalized().Scaled(_grabTarget.Basis.Scale * scale);
        _target.Transform = new Transform3D(newBasis, _grabTarget.Origin + midDelta);
    }
}
