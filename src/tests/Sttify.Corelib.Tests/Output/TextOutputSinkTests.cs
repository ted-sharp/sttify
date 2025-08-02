using Moq;
using Sttify.Corelib.Output;
using Xunit;

namespace Sttify.Corelib.Tests.Output;

public class StreamSinkTests : IDisposable
{
    private readonly string _testFilePath;

    public StreamSinkTests()
    {
        _testFilePath = Path.GetTempFileName();
    }

    [Fact]
    public async Task SendAsync_ToFile_ShouldWriteText()
    {
        // Arrange
        var settings = new StreamSinkSettings
        {
            OutputType = StreamOutputType.File,
            FilePath = _testFilePath,
            ForceFlush = true
        };
        var sink = new StreamSink(settings);
        var testText = "Hello, World!";

        // Act
        await sink.SendAsync(testText);

        // Assert
        var fileContent = await File.ReadAllTextAsync(_testFilePath);
        Assert.Contains(testText, fileContent);
    }

    [Fact]
    public async Task CanSendAsync_WithValidSettings_ShouldReturnTrue()
    {
        // Arrange
        var settings = new StreamSinkSettings
        {
            OutputType = StreamOutputType.File,
            FilePath = _testFilePath
        };
        var sink = new StreamSink(settings);

        // Act
        var canSend = await sink.CanSendAsync();

        // Assert
        Assert.True(canSend);
    }

    [Fact]
    public async Task SendAsync_WithCustomPrefix_ShouldIncludePrefix()
    {
        // Arrange
        var settings = new StreamSinkSettings
        {
            OutputType = StreamOutputType.File,
            FilePath = _testFilePath,
            CustomPrefix = "[TEST]",
            ForceFlush = true
        };
        var sink = new StreamSink(settings);
        var testText = "Hello, World!";

        // Act
        await sink.SendAsync(testText);

        // Assert
        var fileContent = await File.ReadAllTextAsync(_testFilePath);
        Assert.Contains("[TEST]", fileContent);
        Assert.Contains(testText, fileContent);
    }

    [Fact]
    public async Task SendAsync_WithEmptyString_ShouldNotWrite()
    {
        // Arrange
        var settings = new StreamSinkSettings
        {
            OutputType = StreamOutputType.File,
            FilePath = _testFilePath,
            ForceFlush = true
        };
        var sink = new StreamSink(settings);

        // Act
        await sink.SendAsync("");

        // Assert
        var fileExists = File.Exists(_testFilePath);
        if (fileExists)
        {
            var fileContent = await File.ReadAllTextAsync(_testFilePath);
            Assert.Empty(fileContent);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
        {
            try
            {
                File.Delete(_testFilePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}

public class ExternalProcessSinkTests
{
    [Fact]
    public async Task CanSendAsync_WithValidProcess_ShouldReturnTrue()
    {
        // Arrange
        var settings = new ExternalProcessSettings
        {
            ExecutablePath = "notepad.exe", // Available on Windows
            ArgumentTemplate = "",
            ThrottleMs = 0
        };
        var sink = new ExternalProcessSink(settings);

        // Act
        var canSend = await sink.CanSendAsync();

        // Assert
        Assert.True(canSend);
    }

    [Fact]
    public async Task CanSendAsync_WithThrottling_ShouldRespectThrottle()
    {
        // Arrange
        var settings = new ExternalProcessSettings
        {
            ExecutablePath = "notepad.exe",
            ArgumentTemplate = "",
            ThrottleMs = 1000 // 1 second throttle
        };
        var sink = new ExternalProcessSink(settings);

        // Simulate recent send
        await sink.SendAsync("test");

        // Act
        var canSendImmediately = await sink.CanSendAsync();

        // Assert
        Assert.False(canSendImmediately);
    }

    [Fact]
    public async Task SendAsync_WithValidText_ShouldNotThrow()
    {
        // Arrange
        var settings = new ExternalProcessSettings
        {
            ExecutablePath = "cmd.exe",
            ArgumentTemplate = "/c echo {text}",
            ThrottleMs = 0
        };
        var sink = new ExternalProcessSink(settings);

        // Act & Assert
        await sink.SendAsync("test text"); // Should not throw
    }
}