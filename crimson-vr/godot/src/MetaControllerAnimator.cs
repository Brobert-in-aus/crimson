using Godot;
using System.Collections.Generic;

namespace CrimsonVR;

/// <summary>
/// Drives the separate control bones in the controller glTF supplied by
/// Meta's XR_FB_render_model implementation. Meta includes a reference
/// animation in the glTF but does not bind it to OpenXR actions, so we use the
/// clip's rest/fully-actuated poses and blend them from the live controller.
/// </summary>
public sealed partial class MetaControllerAnimator : Node
{
    private sealed class PositionControl
    {
        public int Bone;
        public Vector3 Rest;
        public Vector3 Pressed;
    }

    private sealed class RotationControl
    {
        public int Bone;
        public Quaternion Rest;
        public Quaternion Pressed;
    }

    private Node3D? _provider;
    private XRController3D? _input;
    private Skeleton3D? _skeleton;
    private bool _left;
    private bool _ready;
    private readonly Dictionary<string, PositionControl> _positions = new();
    private readonly Dictionary<string, RotationControl> _rotations = new();
    private int _thumbBone = -1;
    private Quaternion _thumbRestRotation = Quaternion.Identity;
    private Vector3 _thumbRestPosition;

    public bool HasRuntimeModel => _ready;

    public void Configure(Node3D provider, XRController3D input, bool left)
    {
        _provider = provider;
        _input = input;
        _left = left;
    }

    public override void _Process(double delta)
    {
        if (!_ready)
        {
            TryInitialize();
            return;
        }
        if (_input == null || _skeleton == null)
            return;

        float trigger = Mathf.Clamp(_input.GetFloat("trigger"), 0.0f, 1.0f);
        float grip = Mathf.Clamp(_input.GetFloat("grip"), 0.0f, 1.0f);
        Vector2 stick = _input.GetVector2("primary");

        ApplyRotation("b_trigger_front", trigger);
        ApplyRotation("b_trigger_grip", grip);
        ApplyPosition(_left ? "b_button_x" : "b_button_a",
            _input.IsButtonPressed("ax_button") ? 1.0f : 0.0f);
        ApplyPosition(_left ? "b_button_y" : "b_button_b",
            _input.IsButtonPressed("by_button") ? 1.0f : 0.0f);
        ApplyPosition("b_button_oculus",
            (_input.IsButtonPressed("menu_button") || _input.IsButtonPressed("recenter")) ? 1.0f : 0.0f);

        if (_thumbBone >= 0)
        {
            // The glTF's reference clip describes an arbitrary stick sweep,
            // rather than independent X/Y tracks. Apply the two live axes as
            // small local tilts around the model-provided neutral pose.
            Quaternion verticalTilt = new(Vector3.Up,
                Mathf.DegToRad((_left ? -12.0f : 12.0f) * stick.Y));
            Quaternion horizontalTilt = new(Vector3.Right,
                Mathf.DegToRad((_left ? -12.0f : 12.0f) * stick.X));
            Quaternion tilt = verticalTilt * horizontalTilt;
            _skeleton.SetBonePoseRotation(_thumbBone, _thumbRestRotation * tilt);
            _skeleton.SetBonePosePosition(_thumbBone, _thumbRestPosition +
                new Vector3(0.0f, 0.0f, _input.IsButtonPressed("primary_click") ? -0.0015f : 0.0f));
        }
    }

    private void TryInitialize()
    {
        if (_provider == null || !_provider.Call("has_render_model_node").AsBool())
            return;
        if (_provider.Call("get_render_model_node").AsGodotObject() is not Node runtimeRoot)
            return;

        _skeleton = FindDescendant<Skeleton3D>(runtimeRoot);
        AnimationPlayer? player = FindDescendant<AnimationPlayer>(runtimeRoot);
        if (_skeleton == null || player == null || player.GetAnimationList().Length == 0)
        {
            GD.PushWarning($"CrimsonVR: Meta {Side} controller model lacks its skeleton or reference animation");
            return;
        }

        player.Stop(true);
        player.PlaybackActive = false;
        Animation animation = player.GetAnimation(player.GetAnimationList()[0]);
        CaptureAnimationControls(animation);

        _thumbBone = _skeleton.FindBone("b_thumbstick");
        if (_thumbBone >= 0)
        {
            _thumbRestRotation = RotationRest("b_thumbstick", _skeleton.GetBonePoseRotation(_thumbBone));
            _thumbRestPosition = _skeleton.GetBonePosePosition(_thumbBone);
            _skeleton.SetBonePoseRotation(_thumbBone, _thumbRestRotation);
        }

        _ready = _rotations.ContainsKey("b_trigger_front") &&
                 _rotations.ContainsKey("b_trigger_grip") && _thumbBone >= 0;
        if (_ready)
            GD.Print($"CrimsonVR: Meta {Side} Touch runtime model loaded; controls animated from OpenXR input");
        else
            GD.PushWarning($"CrimsonVR: Meta {Side} controller model has no recognized control tracks");
    }

    private void CaptureAnimationControls(Animation animation)
    {
        for (int track = 0; track < animation.GetTrackCount(); track++)
        {
            string path = animation.TrackGetPath(track).ToString();
            int separator = path.LastIndexOf(':');
            if (separator < 0 || separator == path.Length - 1)
                continue;
            string boneName = path[(separator + 1)..];
            int bone = _skeleton!.FindBone(boneName);
            int keys = animation.TrackGetKeyCount(track);
            if (bone < 0 || keys == 0)
                continue;

            if (animation.TrackGetType(track) == Animation.TrackType.Position3D)
            {
                Vector3 rest = animation.TrackGetKeyValue(track, 0).AsVector3();
                Vector3 pressed = rest;
                float farthest = 0.0f;
                for (int key = 1; key < keys; key++)
                {
                    Vector3 value = animation.TrackGetKeyValue(track, key).AsVector3();
                    float distance = rest.DistanceSquaredTo(value);
                    if (distance > farthest)
                    {
                        farthest = distance;
                        pressed = value;
                    }
                }
                _positions[boneName] = new PositionControl { Bone = bone, Rest = rest, Pressed = pressed };
                _skeleton.SetBonePosePosition(bone, rest);
            }
            else if (animation.TrackGetType(track) == Animation.TrackType.Rotation3D)
            {
                Quaternion rest = animation.TrackGetKeyValue(track, 0).AsQuaternion().Normalized();
                Quaternion pressed = rest;
                float farthest = 0.0f;
                for (int key = 1; key < keys; key++)
                {
                    Quaternion value = animation.TrackGetKeyValue(track, key).AsQuaternion().Normalized();
                    float distance = 1.0f - Mathf.Abs(rest.Dot(value));
                    if (distance > farthest)
                    {
                        farthest = distance;
                        pressed = value;
                    }
                }
                _rotations[boneName] = new RotationControl { Bone = bone, Rest = rest, Pressed = pressed };
                _skeleton.SetBonePoseRotation(bone, rest);
            }
        }
    }

    private Quaternion RotationRest(string name, Quaternion fallback) =>
        _rotations.TryGetValue(name, out RotationControl? control) ? control.Rest : fallback;

    private void ApplyPosition(string name, float amount)
    {
        if (_positions.TryGetValue(name, out PositionControl? control))
            _skeleton!.SetBonePosePosition(control.Bone, control.Rest.Lerp(control.Pressed, amount));
    }

    private void ApplyRotation(string name, float amount)
    {
        if (_rotations.TryGetValue(name, out RotationControl? control))
            _skeleton!.SetBonePoseRotation(control.Bone, control.Rest.Slerp(control.Pressed, amount));
    }

    private static T? FindDescendant<T>(Node root) where T : Node
    {
        if (root is T match)
            return match;
        foreach (Node child in root.GetChildren())
            if (FindDescendant<T>(child) is { } found)
                return found;
        return null;
    }

    private string Side => _left ? "left" : "right";
}
