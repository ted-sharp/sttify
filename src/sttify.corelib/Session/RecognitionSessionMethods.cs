using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Session;

// Additional methods for RecognitionSession - these would be added to the main class
public static class RecognitionSessionExtensions
{
    public static void OnModeChanged(this RecognitionSession session, RecognitionMode oldMode, RecognitionMode newMode)
    {
        Telemetry.LogEvent("RecognitionModeChanged", new
        {
            OldMode = oldMode.ToString(),
            NewMode = newMode.ToString()
        });
    }

    // PTT (Push-to-Talk) methods
    public static Task StartPttAsync(this RecognitionSession session)
    {
        if (session.CurrentMode != RecognitionMode.Ptt)
            return Task.CompletedTask;

        // This would be implemented in the main class
        Telemetry.LogEvent("PttStarted");
        return Task.CompletedTask;
    }

    public static Task StopPttAsync(this RecognitionSession session)
    {
        if (session.CurrentMode != RecognitionMode.Ptt)
            return Task.CompletedTask;

        // This would be implemented in the main class
        Telemetry.LogEvent("PttStopped");
        return Task.CompletedTask;
    }

    // Wake word detection
    public static bool DetectWakeWord(this RecognitionSession session, string text, string[] wakeWords)
    {
        if (string.IsNullOrEmpty(text) || wakeWords.Length == 0)
            return false;

        var lowerText = text.ToLowerInvariant();

        foreach (var wakeWord in wakeWords)
        {
            if (lowerText.Contains(wakeWord.ToLowerInvariant()))
            {
                Telemetry.LogEvent("WakeWordDetected", new { WakeWord = wakeWord, Text = text });
                return true;
            }
        }

        return false;
    }

    // Voice activity detection
    public static bool DetectVoiceActivity(this RecognitionSession session, ReadOnlySpan<byte> audioData, double threshold)
    {
        if (audioData.Length == 0)
            return false;

        // Simple RMS calculation for voice activity detection
        double sum = 0;
        for (int i = 0; i < audioData.Length; i += 2) // Assuming 16-bit samples
        {
            if (i + 1 < audioData.Length)
            {
                short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
                sum += sample * sample;
            }
        }

        var rms = Math.Sqrt(sum / (audioData.Length / 2.0));
        var normalizedRms = rms / 32768.0; // Normalize to 0-1 range

        return normalizedRms > threshold;
    }
}
