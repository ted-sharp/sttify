using Microsoft.Extensions.DependencyInjection;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Output;
using Sttify.Corelib.Session;
using Xunit;

namespace Sttify.Integration.Tests;

public class BasicIntegrationTests
{
    [Fact]
    public async Task ServiceProvider_ShouldResolveAllDependencies()
    {
        // Arrange
        var services = new ServiceCollection();
        
        services.AddSingleton<SettingsProvider>();
        services.AddSingleton<AudioCapture>();
        services.AddSingleton<ISttEngine>(provider =>
        {
            var settingsProvider = provider.GetRequiredService<SettingsProvider>();
            var voskSettings = settingsProvider.GetSettingsAsync().GetAwaiter().GetResult().Engine.Vosk;
            return new RealVoskEngineAdapter(voskSettings);
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
}