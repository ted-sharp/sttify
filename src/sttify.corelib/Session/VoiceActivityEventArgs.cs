using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Session;

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class VoiceActivityEventArgs : EventArgs
{
    public VoiceActivityEventArgs(bool isVoiceActive, double audioLevel)
    {
        IsVoiceActive = isVoiceActive;
        AudioLevel = audioLevel;
        Timestamp = DateTime.UtcNow;
    }

    public bool IsVoiceActive { get; }
    public double AudioLevel { get; }
    public DateTime Timestamp { get; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class SilenceDetectedEventArgs : EventArgs
{
    public SilenceDetectedEventArgs(TimeSpan silenceDuration)
    {
        SilenceDuration = silenceDuration;
        Timestamp = DateTime.UtcNow;
    }

    public TimeSpan SilenceDuration { get; }
    public DateTime Timestamp { get; }
}
