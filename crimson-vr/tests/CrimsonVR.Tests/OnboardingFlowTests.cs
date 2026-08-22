using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

public class OnboardingFlowTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void TutorialRecommendation_IsOnlyShownBeforeItIsSeenOrCompleted(
        bool promptSeen, bool tutorialCompleted, bool expected)
        => Assert.Equal(expected,
            OnboardingFlow.ShouldRecommendTutorial(promptSeen, tutorialCompleted));

    [Fact]
    public void LayoutTutorial_IsOnlyShownOnFirstEditorEntry()
    {
        Assert.True(OnboardingFlow.ShouldShowLayoutTutorial(layoutTutorialDone: false));
        Assert.False(OnboardingFlow.ShouldShowLayoutTutorial(layoutTutorialDone: true));
    }
}
