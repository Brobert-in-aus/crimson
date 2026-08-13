using System;
using System.Collections.Generic;
using Godot;

namespace CrimsonVR;

public enum TypoControl
{
    A, B, X, Y,
    LT, RT, LG, RG,
    LS, RS,
    LUp, LRight, LDown, LLeft,
    RUp, RRight, RDown, RLeft,
    LExtra1, LExtra2, RExtra1, RExtra2,
}

public enum TypoControllerKind { Touch, Index, ViveWand, SteamFrame, Generic, Minimal }

/// <summary>The controls physically available on one OpenXR controller pair.</summary>
public sealed class TypoControllerLayout
{
    private static readonly TypoControl[] TouchFace = { TypoControl.A, TypoControl.B, TypoControl.X, TypoControl.Y };
    private static readonly TypoControl[] FrameFace = {
        TypoControl.A, TypoControl.B, TypoControl.RExtra1, TypoControl.RExtra2,
        TypoControl.X, TypoControl.Y, TypoControl.LExtra1, TypoControl.LExtra2,
    };
    private static readonly TypoControl[] Triggers = { TypoControl.LT, TypoControl.RT };
    private static readonly TypoControl[] Grips = { TypoControl.LG, TypoControl.RG };
    private static readonly TypoControl[] Clicks = { TypoControl.LS, TypoControl.RS };
    private static readonly TypoControl[] Directions = {
        TypoControl.LUp, TypoControl.LRight, TypoControl.LDown, TypoControl.LLeft,
        TypoControl.RUp, TypoControl.RRight, TypoControl.RDown, TypoControl.RLeft,
    };

    public TypoControllerKind Kind { get; }
    public string ProfilePath { get; }

    private TypoControllerLayout(TypoControllerKind kind, string profilePath)
    {
        Kind = kind;
        ProfilePath = profilePath;
    }

    public static TypoControllerLayout FromProfiles(string? leftProfile, string? rightProfile)
    {
        string profile = $"{leftProfile} {rightProfile}".ToLowerInvariant();
        if (profile.Contains("frame_controller")) return new(TypoControllerKind.SteamFrame, profile);
        if (profile.Contains("vive_controller")) return new(TypoControllerKind.ViveWand, profile);
        if (profile.Contains("index_controller")) return new(TypoControllerKind.Index, profile);
        if (profile.Contains("touch_controller") || profile.Contains("pico")) return new(TypoControllerKind.Touch, profile);
        if (profile.Contains("generic_controller")) return new(TypoControllerKind.Generic, profile);
        return new(TypoControllerKind.Minimal, profile);
    }

    public static TypoControllerLayout Touch => new(TypoControllerKind.Touch, "touch");

    public TypoControl[] AlphabetForTier(int tier)
    {
        var controls = new List<TypoControl>();
        if (Kind is TypoControllerKind.Touch or TypoControllerKind.Index or TypoControllerKind.Generic)
            controls.AddRange(TouchFace);
        else if (Kind == TypoControllerKind.SteamFrame)
            controls.AddRange(FrameFace);

        // Controllers without face buttons begin with their real primary
        // controls instead of generating impossible ABXY prompts.
        bool faceButtons = controls.Count > 0;
        if (!faceButtons || tier >= 2) controls.AddRange(Triggers);
        if (tier >= 3) controls.AddRange(Grips);
        if (Kind != TypoControllerKind.Minimal && tier >= 4) controls.AddRange(Clicks);
        if (Kind != TypoControllerKind.Minimal && tier >= 5) controls.AddRange(Directions);
        if (controls.Count == 0) controls.AddRange(Triggers);
        return controls.ToArray();
    }

    public string Label(TypoControl input) => (Kind, input) switch
    {
        (TypoControllerKind.Index, TypoControl.X) => "L A",
        (TypoControllerKind.Index, TypoControl.Y) => "L B",
        (TypoControllerKind.Index, TypoControl.A) => "R A",
        (TypoControllerKind.Index, TypoControl.B) => "R B",
        (TypoControllerKind.Generic, TypoControl.X) => "L 1",
        (TypoControllerKind.Generic, TypoControl.Y) => "L 2",
        (TypoControllerKind.Generic, TypoControl.A) => "R 1",
        (TypoControllerKind.Generic, TypoControl.B) => "R 2",
        (TypoControllerKind.ViveWand, TypoControl.LS) => "L PAD",
        (TypoControllerKind.ViveWand, TypoControl.RS) => "R PAD",
        (TypoControllerKind.SteamFrame, TypoControl.X) => "D←",
        (TypoControllerKind.SteamFrame, TypoControl.Y) => "D↑",
        (TypoControllerKind.SteamFrame, TypoControl.LExtra1) => "D↓",
        (TypoControllerKind.SteamFrame, TypoControl.LExtra2) => "D→",
        (TypoControllerKind.SteamFrame, TypoControl.RExtra1) => "X",
        (TypoControllerKind.SteamFrame, TypoControl.RExtra2) => "Y",
        (_, TypoControl.LUp) => Kind == TypoControllerKind.ViveWand ? "L PAD↑" : "L↑",
        (_, TypoControl.LRight) => Kind == TypoControllerKind.ViveWand ? "L PAD→" : "L→",
        (_, TypoControl.LDown) => Kind == TypoControllerKind.ViveWand ? "L PAD↓" : "L↓",
        (_, TypoControl.LLeft) => Kind == TypoControllerKind.ViveWand ? "L PAD←" : "L←",
        (_, TypoControl.RUp) => Kind == TypoControllerKind.ViveWand ? "R PAD↑" : "R↑",
        (_, TypoControl.RRight) => Kind == TypoControllerKind.ViveWand ? "R PAD→" : "R→",
        (_, TypoControl.RDown) => Kind == TypoControllerKind.ViveWand ? "R PAD↓" : "R↓",
        (_, TypoControl.RLeft) => Kind == TypoControllerKind.ViveWand ? "R PAD←" : "R←",
        _ => input.ToString(),
    };
}

public readonly record struct TypoCreature(long Identity, int PoolIndex, Vector2 Position);

public sealed class TypoPrompt
{
    public long Identity { get; }
    public int PoolIndex { get; }
    public Vector2 Position { get; set; }
    public TypoControl[] Sequence { get; }
    public int Progress { get; set; }

    public bool Complete => Progress >= Sequence.Length;
    public TypoControl Next => Sequence[Math.Min(Progress, Sequence.Length - 1)];

    public TypoPrompt(long identity, int poolIndex, Vector2 position, TypoControl[] sequence)
    {
        Identity = identity;
        PoolIndex = poolIndex;
        Position = position;
        Sequence = sequence;
    }

    public string DisplayText(TypoControllerLayout layout)
    {
        var parts = new string[Sequence.Length];
        for (int i = 0; i < Sequence.Length; i++)
        {
            string token = layout.Label(Sequence[i]);
            parts[i] = i == Progress ? $"[{token}]" : i < Progress ? $"{token}·" : token;
        }
        return string.Join("  →  ", parts);
    }
}

/// <summary>Pure deterministic targeting/progression for VR Typ-o-Shooter.</summary>
public sealed class TypoSequenceDirector
{
    private readonly Dictionary<long, TypoPrompt> _prompts = new();
    public IReadOnlyDictionary<long, TypoPrompt> Prompts => _prompts;
    public long? ActiveIdentity { get; private set; }
    public TypoControllerLayout Layout { get; private set; } = TypoControllerLayout.Touch;

    public void SetLayout(TypoControllerLayout layout)
    {
        Layout = layout;
        Reset();
    }

    public void Reset()
    {
        _prompts.Clear();
        ActiveIdentity = null;
    }

    public void Sync(ReadOnlySpan<TypoCreature> creatures, long elapsedMs)
    {
        var live = new HashSet<long>();
        foreach (TypoCreature creature in creatures)
        {
            live.Add(creature.Identity);
            if (_prompts.TryGetValue(creature.Identity, out TypoPrompt? prompt))
            {
                prompt.Position = creature.Position;
            }
            else
            {
                _prompts.Add(creature.Identity, new TypoPrompt(
                    creature.Identity, creature.PoolIndex, creature.Position,
                    BuildSequence(creature.Identity, elapsedMs, Layout)));
            }
        }

        var stale = new List<long>();
        foreach (long identity in _prompts.Keys)
        {
            if (!live.Contains(identity)) stale.Add(identity);
        }
        foreach (long identity in stale) _prompts.Remove(identity);
        if (ActiveIdentity is long active && !_prompts.ContainsKey(active)) ActiveIdentity = null;
    }

    /// <returns>The creature to shoot for a correct step, otherwise null.</returns>
    public TypoPrompt? Press(TypoControl input, Vector2 playerPosition)
    {
        TypoPrompt? target = null;
        if (ActiveIdentity is long active)
        {
            _prompts.TryGetValue(active, out target);
        }
        else
        {
            float bestDistance = float.MaxValue;
            foreach (TypoPrompt candidate in _prompts.Values)
            {
                if (candidate.Complete) continue;
                if (candidate.Next != input) continue;
                float distance = candidate.Position.DistanceSquaredTo(playerPosition);
                if (target == null || distance < bestDistance
                    || (Mathf.IsEqualApprox(distance, bestDistance) && candidate.PoolIndex < target.PoolIndex))
                {
                    target = candidate;
                    bestDistance = distance;
                }
            }
            if (target != null) ActiveIdentity = target.Identity;
        }

        if (target == null || target.Next != input) return null;
        target.Progress++;
        if (target.Complete)
        {
            ActiveIdentity = null;
        }
        return target;
    }

    public static TypoControl[] BuildSequence(long identity, long elapsedMs)
        => BuildSequence(identity, elapsedMs, TypoControllerLayout.Touch);

    public static TypoControl[] BuildSequence(long identity, long elapsedMs, TypoControllerLayout layout)
    {
        int tier = elapsedMs switch
        {
            < 120_000 => 0,
            < 300_000 => 1,
            < 480_000 => 2,
            < 600_000 => 3,
            < 720_000 => 4,
            _ => 5,
        };
        int minLength = tier switch { 0 => 1, 1 => 2, 2 => 3, 3 => 3, 4 => 4, _ => 5 };
        int variance = tier switch { 0 => 1, 1 => 2, 2 => 2, 3 => 3, 4 => 3, _ => 4 };
        TypoControl[] alphabet = layout.AlphabetForTier(tier);
        ulong state = Mix(unchecked((ulong)identity) ^ (ulong)(tier * 0x9E37));
        int length = minLength + (int)(state % (uint)variance);
        var result = new TypoControl[length];
        for (int i = 0; i < length; i++)
        {
            state = Mix(state + (ulong)i + 1);
            result[i] = alphabet[state % (uint)alphabet.Length];
        }
        return result;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

}

/// <summary>Creature-attached prompt labels on the tilted playfield.</summary>
public sealed partial class TypoPromptLayer : Node3D
{
    private readonly Dictionary<long, Label3D> _labels = new();

    public void Render(TypoSequenceDirector director, float arenaSideMeters, float worldSize)
    {
        var live = new HashSet<long>();
        foreach ((long identity, TypoPrompt prompt) in director.Prompts)
        {
            if (prompt.Complete) continue;
            live.Add(identity);
            if (!_labels.TryGetValue(identity, out Label3D? label))
            {
                label = new Label3D
                {
                    FontSize = 54,
                    PixelSize = arenaSideMeters / 3100.0f,
                    OutlineSize = 16,
                    OutlineModulate = Colors.Black,
                    NoDepthTest = true,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                    RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f),
                };
                AddChild(label);
                _labels.Add(identity, label);
            }
            label.Text = prompt.DisplayText(director.Layout);
            label.Modulate = director.ActiveIdentity == identity
                ? new Color(1.0f, 0.86f, 0.24f)
                : new Color(0.82f, 0.94f, 1.0f);
            label.Position = Mapper.GameToArenaLocal(prompt.Position, arenaSideMeters, worldSize)
                + new Vector3(0.0f, 0.018f, -arenaSideMeters * 0.035f);
        }

        var stale = new List<long>();
        foreach ((long identity, Label3D label) in _labels)
        {
            if (!live.Contains(identity))
            {
                label.QueueFree();
                stale.Add(identity);
            }
        }
        foreach (long identity in stale) _labels.Remove(identity);
        Visible = _labels.Count > 0;
    }

    public void Clear()
    {
        foreach (Label3D label in _labels.Values) label.QueueFree();
        _labels.Clear();
        Visible = false;
    }
}
