using Sttify.Corelib.Engine.Cloud;
using Xunit;

namespace Sttify.Corelib.Tests.Engine;

public class CloudEngineTests
{
    [Fact]
    public void CloudRecognitionResult_WithSuccessState_ShouldInitialize()
    {
        // Arrange & Act
        var result = new CloudRecognitionResult
        {
            Success = true,
            Text = "Test transcription",
            Confidence = 0.95f,
            ErrorMessage = null!
        };

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Test transcription", result.Text);
        Assert.Equal(0.95f, result.Confidence);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void CloudRecognitionResult_WithErrorState_ShouldInitialize()
    {
        // Arrange & Act
        var result = new CloudRecognitionResult
        {
            Success = false,
            Text = null!,
            Confidence = 0.0f,
            ErrorMessage = "API connection failed"
        };

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Equal(0.0f, result.Confidence);
        Assert.Equal("API connection failed", result.ErrorMessage);
    }

    [Fact]
    public void CloudRecognitionResult_DefaultValues_ShouldBeValid()
    {
        // Arrange & Act
        var result = new CloudRecognitionResult();

        // Assert
        Assert.False(result.Success); // Default should be false
        Assert.Equal(0.0f, result.Confidence); // Default confidence
    }
}