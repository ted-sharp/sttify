using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Session;

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class VoiceActivityEventArgs : EventArgs
{
    public bool IsVoiceActive { get; }
    public double AudioLevel { get; }
    public DateTime Timestamp { get; }

    public VoiceActivityEventArgs(bool isVoiceActive, double audioLevel)
    {
        IsVoiceActive = isVoiceActive;
        AudioLevel = audioLevel;
        Timestamp = DateTime.UtcNow;
    }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class SilenceDetectedEventArgs : EventArgs
{
    public TimeSpan SilenceDuration { get; }
    public DateTime Timestamp { get; }

    public SilenceDetectedEventArgs(TimeSpan silenceDuration)
    {
        SilenceDuration = silenceDuration;
        Timestamp = DateTime.UtcNow;
    }
}