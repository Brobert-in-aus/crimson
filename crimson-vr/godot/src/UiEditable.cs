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
    private readonly Basis[] _grabRot = new Basis[2];
    private readonly Basis[] _handRot = new Basis[2];
    private readonly bool[] _grabRotValid = new bool[2];
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

    /// <summary>Add a small "log" button that prints this widget's placement.
    /// Deliberately parked well clear of the corner handles and of the widget's
    /// own face — it is pressed occasionally and by intent, so a mis-hit costs
    /// more than the reach does. <paramref name="offset"/> is target-local.</summary>
    public void BuildLogButton(Vector3 offset, float width, float height)
    {
        _logButton = new VrButton();
        _target.AddChild(_logButton);
        _logButton.Build(width, height, "log", new Color(0.4f, 0.75f, 0.55f), plate: true);
        _logButton.Position = offset;
        _logButton.OnPress += () => OnLogPressed?.Invoke();
        _logButton.Visible = false;
    }

    /// <summary>Raised when the log button is poked.</summary>
    public event Action? OnLogPressed;

    private VrButton? _logButton;

    /// <summary>Show/hide the log button. Tied to the DEBUG flag, not to edit
    /// mode: reading placements back is useful while simply playing, which is
    /// when a position actually proves itself good or bad.</summary>
    public void SetLogVisible(bool visible)
    {
        if (_logButton != null)
        {
            _logButton.Visible = visible;
            _logButton.ResetPress();
        }
    }

    public void PollLogPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (_logButton is { Visible: true })
        {
            _logButton.PollPoke(probes);
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

        int prevMask = HeldMask();

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
            }
        }

        // Re-base on EVERY change to which hands are holding, not just on
        // acquire. Both gestures are measured as a delta from _grabTarget, so a
        // stale base is a jump: releasing one hand after a two-handed scale used
        // to fall into the one-handed branch, which rewrites the transform from
        // _grabTarget and therefore threw away the scale and rotation just
        // applied. Since the two hands practically never release on the same
        // frame, that path ran on essentially every release — the snap-back.
        int mask = HeldMask();
        if (mask != 0 && mask != prevMask)
        {
            _grabTarget = _target.Transform;
            for (int i = 0; i < 2 && i < probes.Length; i++)
            {
                if (_heldCorner[i] >= 0)
                {
                    _grabHand[i] = probes[i].Tip;
                    _grabRot[i] = probes[i].Rot;
                    // A default Basis is all-zero, not identity — inverting one
                    // would poison the twist. Only trust a real orientation.
                    _grabRotValid[i] = probes[i].Rot.Determinant() > 1e-6f;
                }
            }
        }

        for (int i = 0; i < 2 && i < probes.Length; i++)
        {
            _handRot[i] = probes[i].Rot;
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

    private int HeldMask() => (_heldCorner[0] >= 0 ? 1 : 0) | (_heldCorner[1] >= 0 ? 2 : 0);

    /// <summary>How far the two controllers have rolled about <paramref name="axis"/>
    /// since the grab began, averaged. Measured by carrying a reference vector
    /// perpendicular to the axis through each controller's rotation delta,
    /// flattening it back onto the plane, and reading the signed angle — a
    /// swing-twist decomposition done geometrically, which avoids the sign and
    /// wrap traps of pulling an angle out of a quaternion.</summary>
    private float AverageTwist(Vector3 axis)
    {
        Vector3 perp = axis.Cross(Vector3.Up);
        if (perp.LengthSquared() < 1e-6f)
        {
            perp = axis.Cross(Vector3.Right);
        }
        perp = perp.Normalized();

        float total = 0.0f;
        int n = 0;
        for (int i = 0; i < 2; i++)
        {
            if (_heldCorner[i] < 0 || !_grabRotValid[i])
            {
                continue;
            }
            Basis delta = _handRot[i] * _grabRot[i].Inverse();
            Vector3 rotated = delta * perp;
            rotated -= axis * rotated.Dot(axis);
            if (rotated.LengthSquared() < 1e-8f)
            {
                continue;
            }
            rotated = rotated.Normalized();
            total += Mathf.Atan2(axis.Dot(perp.Cross(rotated)), perp.Dot(rotated));
            n++;
        }
        return n > 0 ? total / n : 0.0f;
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
            Vector3 euler = _grabTarget.Basis.GetEuler();
            float pitch = euler.X;

            // TILT must follow the hands, which means the measurement vector has
            // to run NEAR-corner to FAR-corner rather than hand-A to hand-B:
            // whichever hand grabbed whichever corner, raising the one on the far
            // side should raise the far edge. Measuring A->B instead made the
            // sign depend on which hand happened to be where, and inverted the
            // tilt for the common grab.
            float zA = _corners[_heldCorner[0]].Z;
            float zB = _corners[_heldCorner[1]].Z;
            if (!Mathf.IsEqualApprox(zA, zB))
            {
                bool aIsNear = zA < zB;
                Vector3 n0 = aIsNear ? v0 : -v0;
                Vector3 n = aIsNear ? v : -v;
                float elev0 = Mathf.Asin(Mathf.Clamp(n0.Normalized().Y, -1.0f, 1.0f));
                float elev = Mathf.Asin(Mathf.Clamp(n.Normalized().Y, -1.0f, 1.0f));
                // Positive x-rotation pushes local +z (the far edge) DOWN, so
                // lifting the far hand has to DECREASE pitch.
                pitch = Mathf.Clamp(euler.X - (elev - elev0), -Mathf.Pi * 0.5f, Mathf.Pi * 0.5f);
            }
            // Both hands on the same edge: no near/far axis to tilt about, so
            // that grab is a pure resize.

            var basis = Basis.FromEuler(new Vector3(pitch, euler.Y, euler.Z))
                .Scaled(_grabTarget.Basis.Scale * scale);
            // Two-handed MOVES as well as sizing and tilting: having to let go
            // and re-grab with one hand just to raise it made the gesture feel
            // half-finished. Yaw is still held (it tracks the recenter pose).
            Vector3 mid = parentInv * (((tipA + tipB) * 0.5f) - ((_grabHand[0] + _grabHand[1]) * 0.5f));
            _target.Transform = new Transform3D(basis, _grabTarget.Origin + mid);
            return;
        }

        // Free: SWING (the rotation carrying v0 onto v) + TWIST (roll about that
        // axis, taken from how far the controllers themselves have rolled) +
        // midpoint translation. Hand positions alone pin only two rotational
        // axes; without the twist term the widget could be aimed anywhere but
        // never rolled, which is the missing sixth degree of freedom.
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

        float twist = AverageTwist(to);
        if (Mathf.Abs(twist) > 1e-5f)
        {
            rot = new Basis(to, twist) * rot;
        }

        Vector3 midDelta = parentInv * (((tipA + tipB) * 0.5f) - ((_grabHand[0] + _grabHand[1]) * 0.5f));
        var newBasis = rot * _grabTarget.Basis;
        newBasis = newBasis.Orthonormalized().Scaled(_grabTarget.Basis.Scale * scale);
        _target.Transform = new Transform3D(newBasis, _grabTarget.Origin + midDelta);
    }
}
