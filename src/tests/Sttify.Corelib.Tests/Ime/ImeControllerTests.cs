using Sttify.Corelib.Ime;
using Xunit;

namespace Sttify.Corelib.Tests.Ime;

public class ImeControllerTests
{
    [Fact]
    public void Constructor_WithValidSettings_ShouldCreateInstance()
    {
        // Arrange
        var settings = new ImeSettings();

        // Act
        using var controller = new ImeController(settings);

        // Assert
        Assert.NotNull(controller);
    }

    [Fact]
    public void Constructor_WithNullSettings_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new ImeController(null!));
    }

    [Fact]
    public void SuppressImeTemporarily_WhenImeControlDisabled_ShouldReturnNull()
    {
        // Arrange
        var settings = new ImeSettings { EnableImeControl = false };
        using var controller = new ImeController(settings);

        // Act
        var result = controller.SuppressImeTemporarily();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void IsImeComposing_WhenImeControlDisabled_ShouldReturnFalse()
    {
        // Arrange
        var settings = new ImeSettings { EnableImeControl = false };
        using var controller = new ImeController(settings);

        // Act
        var result = controller.IsImeComposing();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetCurrentImeStatus_WhenDisposed_ShouldReturnEmptyStatus()
    {
        // Arrange
        var settings = new ImeSettings();
        var controller = new ImeController(settings);
        controller.Dispose();

        // Act
        var status = controller.GetCurrentImeStatus();

        // Assert
        Assert.False(status.HasImeContext);
        Assert.False(status.IsOpen);
        Assert.False(status.IsComposing);
        Assert.Equal(IntPtr.Zero, status.WindowHandle);
    }

    [Fact]
    public void ImeSettings_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new ImeSettings();

        // Assert
        Assert.True(settings.EnableImeControl);
        Assert.True(settings.CloseImeWhenSending);
        Assert.True(settings.SetAlphanumericMode);
        Assert.True(settings.ClearCompositionString);
        Assert.True(settings.RestoreImeStateAfterSending);
        Assert.Equal(100, settings.RestoreDelayMs);
        Assert.True(settings.SkipWhenImeComposing);
    }

    [Fact]
    public void ImeStatus_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var status = new ImeStatus();

        // Assert
        Assert.False(status.HasImeContext);
        Assert.False(status.IsOpen);
        Assert.Equal(0, status.ConversionMode);
        Assert.Equal(0, status.SentenceMode);
        Assert.False(status.IsComposing);
        Assert.Equal(IntPtr.Zero, status.WindowHandle);
        Assert.False(status.IsNativeMode);
        Assert.True(status.IsAlphanumericMode);
        Assert.False(status.IsFullShape);
    }

    [Fact]
    public void ImeStatus_ConversionModeProperties_ShouldWorkCorrectly()
    {
        // Arrange
        var status = new ImeStatus();

        // Test alphanumeric mode (using known constant value)
        status.ConversionMode = 0x0000; // IME_CMODE_ALPHANUMERIC
        Assert.True(status.IsAlphanumericMode);
        Assert.False(status.IsNativeMode);
        Assert.False(status.IsFullShape);

        // Test native mode (using known constant value)
        status.ConversionMode = 0x0001; // IME_CMODE_NATIVE
        Assert.False(status.IsAlphanumericMode);
        Assert.True(status.IsNativeMode);
        Assert.False(status.IsFullShape);

        // Test full shape mode (using known constant value)
        status.ConversionMode = 0x0008; // IME_CMODE_FULLSHAPE
        Assert.False(status.IsAlphanumericMode);
        Assert.False(status.IsNativeMode);
        Assert.True(status.IsFullShape);

        // Test combined modes (using known constant values)
        status.ConversionMode = 0x0001 | 0x0008; // IME_CMODE_NATIVE | IME_CMODE_FULLSHAPE
        Assert.False(status.IsAlphanumericMode);
        Assert.True(status.IsNativeMode);
        Assert.True(status.IsFullShape);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var settings = new ImeSettings();
        var controller = new ImeController(settings);

        // Act & Assert (should not throw)
        controller.Dispose();
        
        // Multiple disposes should also not throw
        controller.Dispose();
    }

    [Fact]
    public void ImeSettings_AllPropertiesCanBeSetAndGet()
    {
        // Arrange
        var settings = new ImeSettings();

        // Act & Assert
        settings.EnableImeControl = false;
        Assert.False(settings.EnableImeControl);

        settings.CloseImeWhenSending = false;
        Assert.False(settings.CloseImeWhenSending);

        settings.SetAlphanumericMode = false;
        Assert.False(settings.SetAlphanumericMode);

        settings.ClearCompositionString = false;
        Assert.False(settings.ClearCompositionString);

        settings.RestoreImeStateAfterSending = false;
        Assert.False(settings.RestoreImeStateAfterSending);

        settings.RestoreDelayMs = 500;
        Assert.Equal(500, settings.RestoreDelayMs);

        settings.SkipWhenImeComposing = false;
        Assert.False(settings.SkipWhenImeComposing);
    }
}