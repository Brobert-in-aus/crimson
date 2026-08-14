using Godot;

namespace CrimsonVR;

/// <summary>Selects the best controller representation available at runtime:
/// promoted OpenXR model, Meta FB model, then the local fallback.</summary>
public sealed partial class RenderModelFallback : Node
{
    private OpenXRRenderModelManager? _manager;
    private Node3D? _metaModel;
    private MetaControllerAnimator? _metaAnimator;
    private Node3D? _fallback;
    private int _activePath = -1;

    public void Configure(OpenXRRenderModelManager manager, Node3D? metaModel,
        MetaControllerAnimator? metaAnimator, Node3D fallback)
    {
        _manager = manager;
        _metaModel = metaModel;
        _metaAnimator = metaAnimator;
        _fallback = fallback;
    }

    public override void _Process(double delta)
    {
        if (_manager == null || _fallback == null)
        {
            return;
        }

        bool promotedModel = _manager.GetChildCount() > 0;
        bool metaModel = !promotedModel && _metaAnimator?.HasRuntimeModel == true;
        _manager.Visible = promotedModel;
        if (_metaModel != null)
            _metaModel.Visible = metaModel;
        _fallback.Visible = !promotedModel && !metaModel;

        int path = promotedModel ? 2 : metaModel ? 1 : 0;
        if (_activePath != path)
        {
            _activePath = path;
            GD.Print(path switch
            {
                2 => "CrimsonVR: using promoted OpenXR animated controller model",
                1 => "CrimsonVR: using Meta runtime Touch model with local input animation",
                _ => "CrimsonVR: runtime render model unavailable; using animated Touch fallback",
            });
        }
    }
}
