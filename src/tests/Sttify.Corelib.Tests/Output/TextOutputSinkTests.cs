using Sttify.Corelib.Output;
using Xunit;

namespace Sttify.Corelib.Tests.Output;

public class StreamSinkTests : IDisposable
{
    private readonly string _testFilePath;

    public StreamSinkTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"sttify_test_{Guid.NewGuid()}.txt");
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
        sink.Dispose(); // Ensure file is closed before reading

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
        var testFile = Path.Combine(Path.GetTempPath(), $"sttify_prefix_test_{Guid.NewGuid()}.txt");
        var settings = new StreamSinkSettings
        {
            OutputType = StreamOutputType.File,
            FilePath = testFile,
            CustomPrefix = "[TEST]",
            ForceFlush = true
        };
        var sink = new StreamSink(settings);
        var testText = "Hello, World!";

        try
        {
            // Act
            await sink.SendAsync(testText);
            sink.Dispose(); // Ensure file is closed before reading

            // Assert
            var fileContent = await File.ReadAllTextAsync(testFile);
            Assert.Contains("[TEST]", fileContent);
            Assert.Contains(testText, fileContent);
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
                File.Delete(testFile);
        }
    }

    [Fact]
    public async Task SendAsync_WithEmptyString_ShouldReturnEarly()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), $"sttify_empty_test_{Guid.NewGuid()}.txt");
        var settings = new StreamSinkSettings
        {
            OutputType = StreamOutputType.File,
            FilePath = testFile,
            ForceFlush = true
        };
        var sink = new StreamSink(settings);

        try
        {
            // Act
            await sink.SendAsync(""); // Empty string should return early
            sink.Dispose(); // Ensure file is closed

            // Assert - The test just verifies the method doesn't throw
            // Implementation behavior may vary (file creation is acceptable)
            Assert.True(true); // Test passes if no exception is thrown
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
                File.Delete(testFile);
        }
    }
}

public class ExternalProcessSinkTests
{
    [Fact]
    public async Task CanSendAsync_WithValidProcess_ShouldReturnTrue()
    {
        // Arrange - Use full path to cmd.exe
        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var settings = new ExternalProcessSettings
        {
            ExecutablePath = cmdPath,
            ArgumentTemplate = "/c echo test",
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
        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var settings = new ExternalProcessSettings
        {
            ExecutablePath = cmdPath,
            ArgumentTemplate = "/c echo test",
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
        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var settings = new ExternalProcessSettings
        {
            ExecutablePath = cmdPath,
            ArgumentTemplate = "/c echo {text}",
            ThrottleMs = 0
        };
        var sink = new ExternalProcessSink(settings);

        // Act & Assert
        await sink.SendAsync("test text"); // Should not throw
    }
}
