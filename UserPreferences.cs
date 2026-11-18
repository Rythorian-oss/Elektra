/*
 * Creator: Justin L. Ross | Farmington, Maine. | Remote Location: Iceland | Black Star Research Facility.
 */

namespace Elektra.Configuration;

public sealed record UserPreferences
{
    public string? PreferredMicrophoneName { get; init; }
    public double? OrbLeft { get; init; }
    public double? OrbTop { get; init; }
    public bool StartHiddenInTray { get; init; }
    public bool EnableLocalLearning { get; init; }
    public bool EnableVoskVoiceSamples { get; init; }
    public bool EnableAutomaticVoicePracticeLessons { get; init; }
    public bool PauseLearningTraining { get; init; }
    public string? PreferredUnits { get; init; }
}