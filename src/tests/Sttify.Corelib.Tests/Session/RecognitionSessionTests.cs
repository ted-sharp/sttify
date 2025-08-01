using Moq;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Output;
using Sttify.Corelib.Session;
using Xunit;

namespace Sttify.Corelib.Tests.Session;

public class RecognitionSessionTests
{
    [Fact]
    public void Constructor_WithValidParameters_ShouldInitialize()
    {
        // Arrange
        var mockAudioCapture = new Mock<AudioCapture>();
        var mockSttEngine = new Mock<ISttEngine>();
        var mockOutputSinks = new List<ITextOutputSink> { new Mock<ITextOutputSink>().Object };
        var settings = new RecognitionSessionSettings();

        // Act
        var session = new RecognitionSession(
            mockAudioCapture.Object,
            mockSttEngine.Object,
            mockOutputSinks,
            settings);

        // Assert
        Assert.NotNull(session);
        Assert.Equal(RecognitionMode.Ptt, session.CurrentMode);
        Assert.Equal(SessionState.Idle, session.CurrentState);
    }

    [Fact]
    public void Constructor_WithNullAudioCapture_ShouldThrowArgumentNullException()
    {
        // Arrange
        var mockSttEngine = new Mock<ISttEngine>();
        var mockOutputSinks = new List<ITextOutputSink>();
        var settings = new RecognitionSessionSettings();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new RecognitionSession(
            null!,
            mockSttEngine.Object,
            mockOutputSinks,
            settings));
    }

    [Fact]
    public void SetCurrentMode_ShouldUpdateMode()
    {
        // Arrange
        var mockAudioCapture = new Mock<AudioCapture>();
        var mockSttEngine = new Mock<ISttEngine>();
        var mockOutputSinks = new List<ITextOutputSink>();
        var settings = new RecognitionSessionSettings();
        
        var session = new RecognitionSession(
            mockAudioCapture.Object,
            mockSttEngine.Object,
            mockOutputSinks,
            settings);

        // Act
        session.CurrentMode = RecognitionMode.Continuous;

        // Assert
        Assert.Equal(RecognitionMode.Continuous, session.CurrentMode);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var mockAudioCapture = new Mock<AudioCapture>();
        var mockSttEngine = new Mock<ISttEngine>();
        var mockOutputSinks = new List<ITextOutputSink>();
        var settings = new RecognitionSessionSettings();
        
        var session = new RecognitionSession(
            mockAudioCapture.Object,
            mockSttEngine.Object,
            mockOutputSinks,
            settings);

        // Act & Assert (should not throw)
        session.Dispose();
    }
}