using Sttify.Corelib.Audio;
using Xunit;

namespace Sttify.Corelib.Tests.Audio;

public class VoiceActivityDetectorTests
{
    [Fact]
    public void Constructor_WithDefaultSettings_ShouldInitialize()
    {
        // Act
        var detector = new VoiceActivityDetector();

        // Assert
        Assert.NotNull(detector);
        Assert.False(detector.IsVoiceActive);
    }

    [Fact]
    public void Constructor_WithCustomSettings_ShouldInitialize()
    {
        // Arrange
        var settings = new VadSettings
        {
            InitialEnergyThreshold = -25.0,
            VoiceConfidenceThreshold = 0.7,
            MinVoiceDurationMs = 150
        };

        // Act
        var detector = new VoiceActivityDetector(settings);

        // Assert
        Assert.NotNull(detector);
        Assert.False(detector.IsVoiceActive);
    }

    [Fact]
    public void ProcessAudioFrame_WithValidData_ShouldReturnResult()
    {
        // Arrange
        var detector = new VoiceActivityDetector();
        var audioData = new byte[1024];
        
        // Fill with some sample data
        for (int i = 0; i < audioData.Length; i += 2)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100) * 16000);
            audioData[i] = (byte)(sample & 0xFF);
            audioData[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        // Act
        var result = detector.ProcessAudioFrame(audioData, 44100, 1);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void ProcessAudioFrame_WithSilentData_ShouldDetectSilence()
    {
        // Arrange
        var detector = new VoiceActivityDetector();
        var silentData = new byte[1024]; // All zeros

        // Act
        var result = detector.ProcessAudioFrame(silentData, 44100, 1);

        // Assert
        Assert.NotNull(result);
        // Silent audio should typically not be detected as voice
        Assert.False(detector.IsVoiceActive);
    }

    [Fact]
    public void IsVoiceActive_InitialState_ShouldBeFalse()
    {
        // Arrange
        var detector = new VoiceActivityDetector();

        // Act & Assert
        Assert.False(detector.IsVoiceActive);
    }

    [Fact]
    public void TimeSinceLastVoice_InitialState_ShouldBeValid()
    {
        // Arrange
        var detector = new VoiceActivityDetector();

        // Act
        var timeSinceVoice = detector.TimeSinceLastVoice;

        // Assert
        Assert.True(timeSinceVoice >= TimeSpan.Zero);
    }

    [Fact]
    public void VadSettings_DefaultValues_ShouldBeReasonable()
    {
        // Arrange & Act
        var settings = new VadSettings();

        // Assert
        Assert.True(settings.InitialEnergyThreshold < 0); // Should be in dB (negative)
        Assert.True(settings.VoiceConfidenceThreshold > 0 && settings.VoiceConfidenceThreshold <= 1);
        Assert.True(settings.MinVoiceDurationMs > 0);
        Assert.True(settings.EndpointSilenceMs > 0);
        Assert.True(settings.HistoryBufferSize > 0);
        Assert.True(settings.MaxBufferFrames > 0);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var detector = new VoiceActivityDetector();

        // Act & Assert
        detector.Dispose(); // Should not throw
    }
}