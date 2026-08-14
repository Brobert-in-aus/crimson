# Animated Quest controller models in Godot

This note documents the controller-model path validated on Quest 3 with Godot
4.7 and the Godot OpenXR Vendors plugin. The important discovery is that two
different OpenXR render-model APIs are involved:

- `OpenXRRenderModelManager` consumes `XR_EXT_interaction_render_model`. This is
  the portable path demonstrated by Godot's
  [OpenXR render-model demo](https://github.com/godotengine/godot-demo-projects/tree/master/xr/openxr_render_models).
- Meta Quest supplies its accurate Touch model through the older vendor
  extension `XR_FB_render_model`. In Godot this is exposed by the vendors
  plugin's dynamically registered `OpenXRFbRenderModel` node.

Using only `OpenXRRenderModelManager` on Quest can therefore produce no model,
even though the headset's render-model service and permission are working.
Using only `OpenXRFbRenderModel` produces the correct high-quality model, but
its controls remain static: the Meta node loads the glTF and does not bind its
animation to Godot input actions. A complete Quest implementation must load the
FB model and drive its skeleton.

## Project and export setup

Install the Godot OpenXR Vendors plugin and enable both render-model paths in
`project.godot`:

```ini
[xr]

openxr/enabled=true
openxr/extensions/render_model=true
openxr/extensions/meta/render_model=true
```

The Android export must include Meta render-model support. Verify the merged APK
manifest contains:

```xml
<uses-permission android:name="com.oculus.permission.RENDER_MODEL" />
<uses-feature android:name="com.oculus.feature.RENDER_MODEL"
              android:required="false" />
```

Keep the feature optional if the application has a fallback controller. This
allows the same package to start when the runtime does not expose a model.

The OpenXR action map must expose the values used to animate the controls. In
Crimson these action names are:

| Action | Godot read | Model control |
|---|---|---|
| `trigger` | `controller.GetFloat("trigger")` | Index trigger |
| `grip` | `controller.GetFloat("grip")` | Grip trigger |
| `primary` | `controller.GetVector2("primary")` | Thumbstick X/Y |
| `primary_click` | `controller.IsButtonPressed(...)` | Thumbstick click |
| `ax_button` | `controller.IsButtonPressed(...)` | A or X |
| `by_button` | `controller.IsButtonPressed(...)` | B or Y |
| `menu_button` / `recenter` | `controller.IsButtonPressed(...)` | Meta/menu button |

## Scene hierarchy

Create one `XRController3D` per hand using the grip pose. Under each controller,
keep a presentation container with three possible representations:

```text
XRController3D (left_hand or right_hand, pose=grip)
└── ControllerModelContainer
    ├── OpenXRRenderModelManager
    ├── OpenXRFbRenderModel
    ├── MetaControllerAnimator
    ├── ProceduralControllerFallback
    └── RuntimeModelSelector
```

Configure `OpenXRRenderModelManager` exactly as in the Godot demo:

- tracker `LeftHand` or `RightHand`;
- `MakeLocalToPose = "grip"`;
- parented below the matching grip-tracked controller.

`OpenXRFbRenderModel` is a GDExtension class, so a C# project should not assume a
compile-time type. Create and configure it dynamically:

```csharp
if (ClassDB.ClassExists("OpenXRFbRenderModel") &&
    ClassDB.Instantiate("OpenXRFbRenderModel").AsGodotObject() is Node3D model)
{
    model.Set("render_model_type", isLeft ? 0 : 1);
    container.AddChild(model);
}
```

The node must be in the scene tree after the OpenXR session starts. Loading is
asynchronous. Poll `has_render_model_node()` or connect
`openxr_fb_render_model_loaded`; then retrieve the generated glTF scene with
`get_render_model_node()`.

## Binding Meta's model to input

The Quest Touch Plus glTF observed during validation contains:

- one `Skeleton3D` deforming the controller mesh;
- an `AnimationPlayer` with an `All Animations` reference clip;
- control bones named `b_trigger_front`, `b_trigger_grip`, `b_thumbstick`,
  `b_button_oculus`, and the handed face buttons (`b_button_x`/`b_button_y` or
  `b_button_a`/`b_button_b`).

The reference clip contains separate position or rotation tracks for these
bones, but it is a demonstration timeline rather than a live input binding. Do
not play it. Stop the `AnimationPlayer`, disable playback, and use the clip as
model-specific calibration data:

1. Find the generated `Skeleton3D` and `AnimationPlayer` recursively.
2. For every recognized animation track, take key zero as the neutral pose.
3. Find the key farthest from neutral and store it as the fully actuated pose.
4. Set the skeleton back to the stored neutral poses.
5. Every frame, blend trigger and grip rotations from neutral to fully actuated
   using their `0.0`-to-`1.0` OpenXR values.
6. Blend face-button positions using their boolean pressed states.

Deriving endpoints from the runtime clip is preferable to hardcoding trigger
angles or button travel. It remains correct if Meta changes the supplied model.
The implementation is in
[`MetaControllerAnimator.cs`](../godot/src/MetaControllerAnimator.cs).

### Thumbstick axes and handedness

Meta's reference clip provides a combined thumbstick sweep, not independent X
and Y calibration tracks. Apply small local pose rotations around the two visible
tilt axes instead:

- local X: thumbstick left/right;
- local Y: thumbstick up/down;
- local Z: shaft twist, which does not produce visible tilt and must not be used;
- tilt magnitude: approximately 12 degrees at full deflection.

The right controller skeleton is mirrored. Reverse both X and Y tilt signs for
the right hand:

```csharp
float handSign = isLeft ? -1.0f : 1.0f;
Quaternion vertical = new(Vector3.Up,
    Mathf.DegToRad(12.0f * handSign * stick.Y));
Quaternion horizontal = new(Vector3.Right,
    Mathf.DegToRad(12.0f * handSign * stick.X));
skeleton.SetBonePoseRotation(thumbBone,
    neutralThumbRotation * vertical * horizontal);
```

Apply thumbstick click as a small pose-position offset if the runtime clip does
not provide a click track.

## Runtime selection and fallback

Select one representation in this order:

1. `OpenXRRenderModelManager` child, when the runtime supports the promoted API;
2. loaded Meta FB model with the local skeleton driver;
3. an app-provided animated fallback.

Do not display the Meta model and promoted model simultaneously. Keep the Meta
provider in the tree while it loads, show the fallback immediately, and switch
visibility only after the Meta skeleton driver has initialized successfully.
This avoids blank hands during asynchronous loading and retains support for
other OpenXR runtimes.

Do not extract Meta's runtime glTF and package it with the application. Runtime
loading automatically selects the correct hardware model and avoids treating a
runtime-provided asset as a redistributable project asset.

## Quest validation

On a successful Quest launch, Crimson logs both hands:

```text
CrimsonVR: Meta left Touch runtime model loaded; controls animated from OpenXR input
CrimsonVR: using Meta runtime Touch model with local input animation
CrimsonVR: Meta right Touch runtime model loaded; controls animated from OpenXR input
CrimsonVR: using Meta runtime Touch model with local input animation
```

Check the following physically because initialization logs cannot verify visual
axis direction:

- both controllers follow grip position and orientation;
- both thumbsticks tilt left, right, up, and down in the same direction as the
  physical controls;
- right-hand X and Y are not inverted;
- trigger and grip travel smoothly over their analog range;
- A/B/X/Y and thumbstick click visibly depress;
- switching to optical hand tracking hides controllers and shows the selected
  hand model;
- the procedural fallback appears if the Meta runtime model is unavailable.
