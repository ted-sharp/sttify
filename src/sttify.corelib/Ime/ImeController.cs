using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Ime;

/// <summary>
/// Controls Windows IME (Input Method Editor) to prevent conflicts during text input
/// </summary>
[ExcludeFromCodeCoverage] // IME system integration, difficult to test reliably
public class ImeController : IDisposable
{
    private readonly ImeSettings _settings;
    private bool _disposed = false;

    public ImeController(ImeSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Temporarily disables IME for the current foreground window to prevent input conflicts
    /// </summary>
    /// <returns>IDisposable that restores IME state when disposed</returns>
    public IDisposable? SuppressImeTemporarily()
    {
        if (_disposed || !_settings.EnableImeControl)
            return null;

        try
        {
            var foregroundWindow = ImeNativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                Telemetry.LogEvent("ImeSuppressionFailed", new { Reason = "NoForegroundWindow" });
                return null;
            }

            var imeContext = ImeNativeMethods.ImmGetContext(foregroundWindow);
            if (imeContext == IntPtr.Zero)
            {
                Telemetry.LogEvent("ImeSuppressionSkipped", new { Reason = "NoImeContext" });
                return null;
            }

            // Save current IME state
            var isCurrentlyOpen = ImeNativeMethods.ImmGetOpenStatus(imeContext);
            ImeNativeMethods.ImmGetConversionStatus(imeContext, out int currentConversion, out int currentSentence);

            if (!isCurrentlyOpen && currentConversion == ImeNativeMethods.IME_CMODE_ALPHANUMERIC)
            {
                // IME is already in the desired state
                ImeNativeMethods.ImmReleaseContext(foregroundWindow, imeContext);
                return new NoOpImeRestorer();
            }

            // Disable IME
            bool suppressionSuccess = true;
            
            if (_settings.CloseImeWhenSending)
            {
                suppressionSuccess &= ImeNativeMethods.ImmSetOpenStatus(imeContext, false);
                Telemetry.LogEvent("ImeOpenStatusChanged", new { Open = false, Success = suppressionSuccess });
            }

            if (_settings.SetAlphanumericMode)
            {
                var modeSuccess = ImeNativeMethods.ImmSetConversionStatus(imeContext, 
                    ImeNativeMethods.IME_CMODE_ALPHANUMERIC, 
                    ImeNativeMethods.IME_SMODE_NONE);
                suppressionSuccess &= modeSuccess;
                Telemetry.LogEvent("ImeConversionModeChanged", new { Mode = "Alphanumeric", Success = modeSuccess });
            }

            if (_settings.ClearCompositionString)
            {
                var clearSuccess = ImeNativeMethods.ImmSetCompositionString(imeContext, 
                    ImeNativeMethods.SCS_SETSTR, "", 0, "", 0);
                suppressionSuccess &= clearSuccess;
                Telemetry.LogEvent("ImeCompositionCleared", new { Success = clearSuccess });
            }

            ImeNativeMethods.ImmReleaseContext(foregroundWindow, imeContext);

            if (suppressionSuccess)
            {
                Telemetry.LogEvent("ImeSuppressionSucceeded", new 
                { 
                    PreviousOpen = isCurrentlyOpen,
                    PreviousConversion = currentConversion,
                    PreviousSentence = currentSentence,
                    Window = foregroundWindow.ToString("X8")
                });

                return new ImeRestorer(foregroundWindow, isCurrentlyOpen, currentConversion, currentSentence, _settings);
            }
            else
            {
                Telemetry.LogEvent("ImeSuppressionPartiallyFailed", new { Window = foregroundWindow.ToString("X8") });
                return new ImeRestorer(foregroundWindow, isCurrentlyOpen, currentConversion, currentSentence, _settings);
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ImeSuppressionException", ex);
            return null;
        }
    }

    /// <summary>
    /// Checks if IME is currently composing text in the foreground window
    /// </summary>
    public bool IsImeComposing()
    {
        if (_disposed || !_settings.EnableImeControl)
            return false;

        try
        {
            var foregroundWindow = ImeNativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return false;

            var imeContext = ImeNativeMethods.ImmGetContext(foregroundWindow);
            if (imeContext == IntPtr.Zero)
                return false;

            try
            {
                // Check if there's an active composition string
                var compLength = ImeNativeMethods.ImmGetCompositionString(imeContext, 
                    ImeNativeMethods.GCS_COMPSTR, IntPtr.Zero, 0);
                
                return compLength > 0;
            }
            finally
            {
                ImeNativeMethods.ImmReleaseContext(foregroundWindow, imeContext);
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ImeCompositionCheckException", ex);
            return false;
        }
    }

    /// <summary>
    /// Gets the current IME status for the foreground window
    /// </summary>
    public ImeStatus GetCurrentImeStatus()
    {
        if (_disposed)
            return new ImeStatus();

        try
        {
            var foregroundWindow = ImeNativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return new ImeStatus();

            var imeContext = ImeNativeMethods.ImmGetContext(foregroundWindow);
            if (imeContext == IntPtr.Zero)
                return new ImeStatus { HasImeContext = false };

            try
            {
                var isOpen = ImeNativeMethods.ImmGetOpenStatus(imeContext);
                ImeNativeMethods.ImmGetConversionStatus(imeContext, out int conversion, out int sentence);
                
                var compLength = ImeNativeMethods.ImmGetCompositionString(imeContext, 
                    ImeNativeMethods.GCS_COMPSTR, IntPtr.Zero, 0);

                return new ImeStatus
                {
                    HasImeContext = true,
                    IsOpen = isOpen,
                    ConversionMode = conversion,
                    SentenceMode = sentence,
                    IsComposing = compLength > 0,
                    WindowHandle = foregroundWindow
                };
            }
            finally
            {
                ImeNativeMethods.ImmReleaseContext(foregroundWindow, imeContext);
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ImeStatusCheckException", ex);
            return new ImeStatus();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// Restores IME state when disposed
    /// </summary>
    private class ImeRestorer : IDisposable
    {
        private readonly IntPtr _window;
        private readonly bool _originalOpen;
        private readonly int _originalConversion;
        private readonly int _originalSentence;
        private readonly ImeSettings _settings;
        private bool _disposed = false;

        public ImeRestorer(IntPtr window, bool originalOpen, int originalConversion, int originalSentence, ImeSettings settings)
        {
            _window = window;
            _originalOpen = originalOpen;
            _originalConversion = originalConversion;
            _originalSentence = originalSentence;
            _settings = settings;
        }

        public void Dispose()
        {
            if (_disposed || !_settings.RestoreImeStateAfterSending)
                return;

            try
            {
                var imeContext = ImeNativeMethods.ImmGetContext(_window);
                if (imeContext == IntPtr.Zero)
                    return;

                try
                {
                    // Restore original IME state
                    if (_settings.CloseImeWhenSending)
                    {
                        var openSuccess = ImeNativeMethods.ImmSetOpenStatus(imeContext, _originalOpen);
                        Telemetry.LogEvent("ImeOpenStatusRestored", new { Open = _originalOpen, Success = openSuccess });
                    }

                    if (_settings.SetAlphanumericMode)
                    {
                        var modeSuccess = ImeNativeMethods.ImmSetConversionStatus(imeContext, _originalConversion, _originalSentence);
                        Telemetry.LogEvent("ImeConversionModeRestored", new 
                        { 
                            Conversion = _originalConversion, 
                            Sentence = _originalSentence, 
                            Success = modeSuccess 
                        });
                    }

                    Telemetry.LogEvent("ImeStateRestored", new { Window = _window.ToString("X8") });
                }
                finally
                {
                    ImeNativeMethods.ImmReleaseContext(_window, imeContext);
                }
            }
            catch (Exception ex)
            {
                Telemetry.LogError("ImeRestorationException", ex);
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// No-operation IME restorer for cases where no changes were made
    /// </summary>
    private class NoOpImeRestorer : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// IME control settings
/// </summary>
public class ImeSettings
{
    /// <summary>
    /// Enable IME control functionality
    /// </summary>
    public bool EnableImeControl { get; set; } = true;

    /// <summary>
    /// Close IME when sending text to prevent conflicts
    /// </summary>
    public bool CloseImeWhenSending { get; set; } = true;

    /// <summary>
    /// Set IME to alphanumeric mode when sending text
    /// </summary>
    public bool SetAlphanumericMode { get; set; } = true;

    /// <summary>
    /// Clear any active composition string before sending text
    /// </summary>
    public bool ClearCompositionString { get; set; } = true;

    /// <summary>
    /// Restore original IME state after sending text
    /// </summary>
    public bool RestoreImeStateAfterSending { get; set; } = true;

    /// <summary>
    /// Delay in milliseconds before restoring IME state
    /// </summary>
    public int RestoreDelayMs { get; set; } = 100;

    /// <summary>
    /// Skip text input if IME is actively composing
    /// </summary>
    public bool SkipWhenImeComposing { get; set; } = true;
}

/// <summary>
/// Current IME status information
/// </summary>
public class ImeStatus
{
    public bool HasImeContext { get; set; } = false;
    public bool IsOpen { get; set; } = false;
    public int ConversionMode { get; set; } = 0;
    public int SentenceMode { get; set; } = 0;
    public bool IsComposing { get; set; } = false;
    public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

    public bool IsNativeMode => (ConversionMode & ImeNativeMethods.IME_CMODE_NATIVE) != 0;
    public bool IsAlphanumericMode => ConversionMode == ImeNativeMethods.IME_CMODE_ALPHANUMERIC;
    public bool IsFullShape => (ConversionMode & ImeNativeMethods.IME_CMODE_FULLSHAPE) != 0;
}