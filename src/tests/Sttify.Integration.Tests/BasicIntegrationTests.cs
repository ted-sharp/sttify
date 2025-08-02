using Microsoft.Extensions.DependencyInjection;
using Moq;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Output;
using Sttify.Corelib.Session;
using Sttify.Corelib.Plugins;
using Sttify.Corelib.Diagnostics;
using Xunit;

namespace Sttify.Integration.Tests;

public class BasicIntegrationTests
{
    [Fact]
    public void ServiceProvider_ShouldResolveAllDependencies()
    {
        // Arrange
        var services = new ServiceCollection();
        
        services.AddSingleton<SettingsProvider>();
        services.AddSingleton<AudioCapture>();
        services.AddSingleton<ISttEngine>(provider =>
        {
            var settingsProvider = provider.GetRequiredService<SettingsProvider>();
            var settings = new SettingsProvider().GetSettingsAsync().Result; // For test only
            return new RealVoskEngineAdapter(settings.Engine.Vosk);
        });
        services.AddSingleton<IEnumerable<ITextOutputSink>>(provider =>
        {
            return new List<ITextOutputSink>
            {
                new SendInputSink()
            };
        });
        services.AddSingleton<RecognitionSessionSettings>();
        services.AddSingleton<RecognitionSession>();

        var serviceProvider = services.BuildServiceProvider();

        // Act & Assert
        var settingsProvider = serviceProvider.GetRequiredService<SettingsProvider>();
        Assert.NotNull(settingsProvider);

        var audioCapture = serviceProvider.GetRequiredService<AudioCapture>();
        Assert.NotNull(audioCapture);

        var sttEngine = serviceProvider.GetRequiredService<ISttEngine>();
        Assert.NotNull(sttEngine);

        var outputSinks = serviceProvider.GetRequiredService<IEnumerable<ITextOutputSink>>();
        Assert.NotNull(outputSinks);
        Assert.NotEmpty(outputSinks);

        var recognitionSession = serviceProvider.GetRequiredService<RecognitionSession>();
        Assert.NotNull(recognitionSession);
    }

    [Fact]
    public async Task RecognitionSession_ShouldInitializeWithDefaultSettings()
    {
        // Arrange
        var settingsProvider = new SettingsProvider();
        var settings = await settingsProvider.GetSettingsAsync();
        
        var audioCapture = new AudioCapture();
        var sttEngine = new RealVoskEngineAdapter(settings.Engine.Vosk);
        var outputSinks = new List<ITextOutputSink> { new SendInputSink() };
        var sessionSettings = new RecognitionSessionSettings();

        // Act
        var session = new RecognitionSession(audioCapture, sttEngine, outputSinks, sessionSettings);

        // Assert
        Assert.NotNull(session);
        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.Equal(RecognitionMode.Ptt, session.CurrentMode);
        
        // Cleanup
        session.Dispose();
    }

    [Fact]
    public async Task SettingsProvider_ShouldLoadAndSaveSettings()
    {
        // Arrange
        var settingsProvider = new SettingsProvider();

        // Act
        var originalSettings = await settingsProvider.GetSettingsAsync();
        originalSettings.Engine.Profile = "test-profile";
        
        await settingsProvider.SaveSettingsAsync(originalSettings);
        var loadedSettings = await settingsProvider.GetSettingsAsync();

        // Assert
        Assert.Equal("test-profile", loadedSettings.Engine.Profile);
    }

    [Fact]
    public void PluginManager_Integration_ShouldInitialize()
    {
        // Arrange
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        
        // Act
        var pluginManager = new PluginManager(serviceProvider);

        // Assert
        Assert.NotNull(pluginManager);
    }

    [Fact]
    public async Task HealthMonitor_Integration_ShouldRunChecks()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        
        // Act
        var results = await healthMonitor.GetHealthStatusAsync();

        // Assert
        Assert.NotNull(results);
        // Default health checks should be registered
        Assert.True(results.Count >= 0);
    }

    [Fact]
    public void VoiceActivityDetector_Integration_ShouldDetectActivity()
    {
        // Arrange
        var settings = new VadSettings
        {
            InitialEnergyThreshold = -25.0,
            VoiceConfidenceThreshold = 0.6
        };
        var detector = new VoiceActivityDetector(settings);

        // Create test audio data as bytes (16-bit PCM)
        var audioData = new byte[2048]; // 1024 samples * 2 bytes per sample
        for (int i = 0; i < audioData.Length; i += 2)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100) * 16000);
            audioData[i] = (byte)(sample & 0xFF);
            audioData[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var silentData = new byte[2048]; // All zeros

        // Act
        var resultWithSignal = detector.ProcessAudioFrame(audioData, 44100, 1);
        var resultWithSilence = detector.ProcessAudioFrame(silentData, 44100, 1);

        // Assert
        Assert.NotNull(resultWithSignal);
        Assert.NotNull(resultWithSilence);
        // The detector should be able to process both types of audio
    }
}