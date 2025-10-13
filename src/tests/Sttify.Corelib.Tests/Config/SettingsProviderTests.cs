using Sttify.Corelib.Config;
using Xunit;

namespace Sttify.Corelib.Tests.Config;

public class SettingsProviderTests : IDisposable
{
    private readonly SettingsProvider _settingsProvider;
    private readonly string _testConfigPath;

    public SettingsProviderTests()
    {
        _settingsProvider = new SettingsProvider();

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _testConfigPath = Path.Combine(appDataPath, "sttify", "config.json");
    }

    public void Dispose()
    {
        // Cleanup test config file
        if (File.Exists(_testConfigPath))
        {
            try
            {
                File.Delete(_testConfigPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        var backupPath = Path.ChangeExtension(_testConfigPath, ".backup.json");
        if (File.Exists(backupPath))
        {
            try
            {
                File.Delete(backupPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task GetSettingsAsync_WhenConfigDoesNotExist_ShouldReturnDefaults()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);

        // Act
        var settings = await _settingsProvider.GetSettingsAsync();

        // Assert
        Assert.NotNull(settings);
        Assert.Equal("vosk", settings.Engine.Profile);
        Assert.Equal("ptt", settings.Session.Mode);
        Assert.Equal("sendinput", settings.Output.Primary);
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldCreateConfigFile()
    {
        // Arrange
        var settings = new SttifySettings
        {
            Engine = new EngineSettings
            {
                Profile = "test-engine",
                // Ensure no infinite values that cause JSON serialization issues
                Vosk = new VoskEngineSettings
                {
                    ModelPath = "test-model",
                    Language = "ja",
                    Punctuation = true
                }
            }
        };

        // Act
        await _settingsProvider.SaveSettingsAsync(settings);

        // Assert
        Assert.True(File.Exists(_testConfigPath));

        var loadedSettings = await _settingsProvider.GetSettingsAsync();
        Assert.Equal("test-engine", loadedSettings.Engine.Profile);
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldCacheSettings()
    {
        // Arrange
        var settings = new SttifySettings();
        await _settingsProvider.SaveSettingsAsync(settings);

        // Act
        var settings1 = await _settingsProvider.GetSettingsAsync();
        var settings2 = await _settingsProvider.GetSettingsAsync();

        // Assert
        Assert.Same(settings1, settings2); // Should return cached instance
    }
}
