using Godot;
using Xunit;

namespace CrimsonVR.Tests;

public sealed class TypoShooterTests
{
    [Fact]
    public void EarlySequencesUseOnlyFaceButtonsAndStayShort()
    {
        for (long identity = 1; identity <= 64; identity++)
        {
            TypoControl[] sequence = TypoSequenceDirector.BuildSequence(identity, elapsedMs: 0);
            Assert.InRange(sequence.Length, 1, 2);
            Assert.All(sequence, token => Assert.InRange((int)token, 0, 3));
        }
    }

    [Fact]
    public void AlphabetExpandsThroughTriggersGripsClicksAndDirections()
    {
        Assert.All(TypoSequenceDirector.BuildSequence(7, 300_000),
            token => Assert.InRange((int)token, 0, (int)TypoControl.RT));
        Assert.All(TypoSequenceDirector.BuildSequence(7, 500_000),
            token => Assert.InRange((int)token, 0, (int)TypoControl.RG));
        Assert.All(TypoSequenceDirector.BuildSequence(7, 650_000),
            token => Assert.InRange((int)token, 0, (int)TypoControl.RS));
        Assert.All(TypoSequenceDirector.BuildSequence(7, 900_000),
            token => Assert.InRange((int)token, 0, (int)TypoControl.RLeft));
    }

    [Fact]
    public void FirstCorrectInputLocksNearestMatchingEnemy()
    {
        var director = new TypoSequenceDirector();
        director.Sync(new[]
        {
            new TypoCreature(10, 10, new Vector2(100, 100)),
            new TypoCreature(20, 20, new Vector2(500, 500)),
        }, 0);
        TypoPrompt near = director.Prompts[10];
        TypoPrompt far = director.Prompts[20];
        // Make both candidates share the same initial token for this targeting test.
        far.Sequence[0] = near.Sequence[0];

        TypoPrompt? hit = director.Press(near.Sequence[0], new Vector2(90, 90));

        Assert.Same(near, hit);
        if (!near.Complete) Assert.Equal(near.Identity, director.ActiveIdentity);
    }

    [Fact]
    public void WrongInputDoesNotAdvanceLockedPrompt()
    {
        var director = new TypoSequenceDirector();
        director.Sync(new[] { new TypoCreature(42, 3, new Vector2(50, 50)) }, 180_000);
        TypoPrompt prompt = director.Prompts[42];
        TypoControl first = prompt.Sequence[0];
        director.Press(first, Vector2.Zero);
        int progress = prompt.Progress;
        if (prompt.Complete) return;
        TypoControl wrong = prompt.Next == TypoControl.A ? TypoControl.B : TypoControl.A;

        Assert.Null(director.Press(wrong, Vector2.Zero));
        Assert.Equal(progress, prompt.Progress);
    }

    [Fact]
    public void CompletedPromptIsNotRegeneratedUntilCreatureDisappears()
    {
        var director = new TypoSequenceDirector();
        var creature = new TypoCreature(9, 2, new Vector2(20, 20));
        director.Sync(new[] { creature }, 0);
        TypoPrompt prompt = director.Prompts[9];
        while (!prompt.Complete) director.Press(prompt.Next, Vector2.Zero);

        director.Sync(new[] { creature }, 0);

        Assert.True(director.Prompts[9].Complete);
        Assert.Null(director.ActiveIdentity);
    }

    [Theory]
    [InlineData("/interaction_profiles/valve/index_controller", TypoControllerKind.Index)]
    [InlineData("/interaction_profiles/htc/vive_controller", TypoControllerKind.ViveWand)]
    [InlineData("/interaction_profiles/valve/frame_controller_valve", TypoControllerKind.SteamFrame)]
    [InlineData("/interaction_profiles/oculus/touch_controller", TypoControllerKind.Touch)]
    [InlineData("/interaction_profiles/khr/generic_controller", TypoControllerKind.Generic)]
    [InlineData("/interaction_profiles/khr/simple_controller", TypoControllerKind.Minimal)]
    public void DetectsRuntimeInteractionProfile(string profile, TypoControllerKind expected)
        => Assert.Equal(expected, TypoControllerLayout.FromProfiles(profile, profile).Kind);

    [Fact]
    public void ViveNeverGeneratesImpossibleFaceButtons()
    {
        TypoControllerLayout layout = TypoControllerLayout.FromProfiles(
            "/interaction_profiles/htc/vive_controller", null);
        for (int elapsed = 0; elapsed <= 900_000; elapsed += 60_000)
        {
            for (long identity = 0; identity < 32; identity++)
            {
                Assert.DoesNotContain(TypoSequenceDirector.BuildSequence(identity, elapsed, layout),
                    control => control is TypoControl.A or TypoControl.B or TypoControl.X or TypoControl.Y);
            }
        }
    }

    [Fact]
    public void SteamFrameUsesAllNativeFaceAndDpadButtonsInitially()
    {
        TypoControllerLayout layout = TypoControllerLayout.FromProfiles(
            "/interaction_profiles/valve/frame_controller_valve", null);
        TypoControl[] alphabet = layout.AlphabetForTier(0);

        Assert.Equal(8, alphabet.Length);
        Assert.Equal("D←", layout.Label(TypoControl.X));
        Assert.Equal("X", layout.Label(TypoControl.RExtra1));
    }

    [Fact]
    public void IndexLabelsDisambiguateSameNamedButtonsByHand()
    {
        TypoControllerLayout layout = TypoControllerLayout.FromProfiles(
            "/interaction_profiles/valve/index_controller", null);

        Assert.Equal("L A", layout.Label(TypoControl.X));
        Assert.Equal("R A", layout.Label(TypoControl.A));
    }

    [Fact]
    public void UnknownControllersHaveSafeTriggerGripFallback()
    {
        TypoControllerLayout layout = TypoControllerLayout.FromProfiles("mystery", null);
        Assert.All(layout.AlphabetForTier(5), control =>
            Assert.Contains(control, new[] { TypoControl.LT, TypoControl.RT, TypoControl.LG, TypoControl.RG }));
    }
}
