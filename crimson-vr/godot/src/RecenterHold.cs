namespace CrimsonVR;

public readonly record struct RecenterHoldState(bool Holding, bool Triggered, float Progress);

/// <summary>One-shot hold gesture: triggers once, then requires a full release.</summary>
public sealed class RecenterHold
{
    public const ulong DurationMs = 700;
    private bool _held;
    private bool _triggered;
    private ulong _startedMs;

    public RecenterHoldState Update(bool held, ulong nowMs)
    {
        if (!held)
        {
            _held = false;
            _triggered = false;
            return default;
        }
        if (!_held)
        {
            _held = true;
            _triggered = false;
            _startedMs = nowMs;
        }

        ulong elapsed = nowMs >= _startedMs ? nowMs - _startedMs : 0;
        float progress = System.Math.Clamp(elapsed / (float)DurationMs, 0.0f, 1.0f);
        bool triggerNow = !_triggered && elapsed >= DurationMs;
        if (triggerNow) _triggered = true;
        return new RecenterHoldState(true, triggerNow, progress);
    }
}
