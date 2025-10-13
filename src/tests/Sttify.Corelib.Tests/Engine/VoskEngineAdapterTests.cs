using Sttify.Corelib.Config;
using Sttify.Corelib.Engine.Vosk;
using Xunit;

namespace Sttify.Corelib.Tests.Engine;

public class VoskEngineAdapterTests
{
    [Fact]
    public void Constructor_WithValidSettings_ShouldNotThrow()
    {
        // Arrange
        var settings = new VoskEngineSettings
        {
            ModelPath = "test-model-path",
            Language = "ja",
            Punctuation = true
        };

        // Act & Assert
        var adapter = new RealVoskEngineAdapter(settings);
        Assert.NotNull(adapter);
    }

    [Fact]
    public void Constructor_WithNullSettings_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new RealVoskEngineAdapter(null!));
    }

    [Fact]
    public async Task StartAsync_WithInvalidModelPath_ShouldThrowException()
    {
        // Arrange
        var settings = new VoskEngineSettings
        {
            ModelPath = "invalid-path",
            Language = "ja"
        };
        var adapter = new RealVoskEngineAdapter(settings);

        // Act & Assert
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => adapter.StartAsync());
    }

    [Fact]
    public void PushAudio_WhenNotStarted_ShouldNotThrow()
    {
        // Arrange
        var settings = new VoskEngineSettings
        {
            ModelPath = "test-path",
            Language = "ja"
        };
        var adapter = new RealVoskEngineAdapter(settings);
        var audioData = new byte[1024];

        // Act & Assert (should not throw)
        adapter.PushAudio(audioData);
    }
}
