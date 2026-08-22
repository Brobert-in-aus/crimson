namespace CrimsonVR;

/// <summary>Pure policy for one-time VR teaching, kept separate from scene state.</summary>
public static class OnboardingFlow
{
    public static bool ShouldShowLayoutTutorial(bool layoutTutorialDone)
        => !layoutTutorialDone;

    public static bool ShouldRecommendTutorial(bool promptSeen, bool tutorialCompleted)
        => !promptSeen && !tutorialCompleted;
}
