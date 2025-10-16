using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Audio;

[ExcludeFromCodeCoverage] // WASAPI hardware dependent wrapper, difficult to mock effectively
public class AudioCapture : IDisposable
{
    private const string ComponentName = "AudioCapture";
    private const int MaxRestartAttempts = 1; // simple lightweight recovery
    private readonly object _lockObject = new();
    private bool _disposed;
    private bool _isCapturing;
    private AudioCaptureSettings? _lastSettings;
    private int _restartAttempts;

    private WasapiAudioCapture? _wasapiCapture;

    public bool IsCapturing
    {
        get
        {
            lock (_lockObject)
            {
                return _isCapturing;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Avoid blocking the calling thread (UI) on dispose
            AsyncHelper.FireAndForget(() => StopAsync(), ComponentName + ".Dispose");
        }

        _disposed = true;
    }

    public event EventHandler<AudioFrameEventArgs>? OnFrame;
    public event EventHandler<AudioErrorEventArgs>? OnError;

    public async Task StartAsync(AudioCaptureSettings settings, CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Audio capture is already running");
        }

        try
        {
            _lastSettings = settings;
            _wasapiCapture = new WasapiAudioCapture();
            _wasapiCapture.OnFrame += OnWasapiFrame;
            _wasapiCapture.OnError += OnWasapiError;

            await _wasapiCapture.StartAsync(settings, cancellationToken);

            lock (_lockObject)
            {
                _isCapturing = true;
                _restartAttempts = 0; // reset on successful start
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new AudioErrorEventArgs(ex, "Failed to start audio capture"));
            throw;
        }
    }

    public async Task StopAsync()
    {
        lock (_lockObject)
        {
            if (!_isCapturing)
                return;

            _isCapturing = false;
        }

        if (_wasapiCapture != null)
        {
            await _wasapiCapture.StopAsync();
            _wasapiCapture.OnFrame -= OnWasapiFrame;
            _wasapiCapture.OnError -= OnWasapiError;
            _wasapiCapture.Dispose();
            _wasapiCapture = null;
        }
    }

    private void OnWasapiFrame(object? sender, AudioFrameEventArgs e)
    {
        OnFrame?.Invoke(this, e);
    }

    private void OnWasapiError(object? sender, AudioErrorEventArgs e)
    {
        AsyncHelper.FireAndForget(async () =>
        {
            lock (_lockObject)
            {
                _isCapturing = false;
            }

            Telemetry.LogError("WasapiAudioError", e.Exception, new
            {
                Component = ComponentName,
                e.Message
            });

            if (IsTransientAudioError(e.Exception))
            {
                await AttemptAudioRecoveryAsync().ConfigureAwait(false);
            }

            OnError?.Invoke(this, e);
        }, nameof(OnWasapiError));
    }

    private static bool IsTransientAudioError(Exception exception)
    {
        // Check for known transient error patterns
        var message = exception.Message.ToLowerInvariant();
        return message.Contains("device in use") ||
               message.Contains("device not available") ||
               message.Contains("access denied") ||
               exception is UnauthorizedAccessException ||
               exception is InvalidOperationException;
    }

    private async Task AttemptAudioRecoveryAsync()
    {
        var recoveryStartTime = DateTime.UtcNow;

        try
        {
            // Wait a short time for the device to become available
            await Task.Delay(1000);

            // Try to get available devices to see if any are accessible
            var availableDevices = GetAvailableDevices();
            if (availableDevices.Count > 0)
            {
                // Try one-time restart using last known settings if we have them
                if (_lastSettings != null && _restartAttempts < MaxRestartAttempts)
                {
                    _restartAttempts++;
                    try
                    {
                        var lastSettings = _lastSettings;
                        await StopAsync();
                        await StartAsync(lastSettings);
                        Telemetry.LogEvent("AudioDeviceRestarted", new { Attempts = _restartAttempts });
                    }
                    catch (Exception ex)
                    {
                        Telemetry.LogError("AudioDeviceRestartFailed", ex, new { Attempts = _restartAttempts });
                    }
                }

                var recoveryDuration = DateTime.UtcNow - recoveryStartTime;
                Telemetry.LogEvent("AudioDeviceRecoverySuccessful", new
                {
                    Component = ComponentName,
                    ErrorCode = "AUDIO_DEVICE_FAILURE",
                    RecoveryDurationMs = recoveryDuration.TotalMilliseconds,
                    RecoveryAction = "Device became available after wait"
                });

                Telemetry.LogEvent("AudioRecoverySuccessful", new
                {
                    AvailableDevices = availableDevices.Count,
                    RecoveryDurationMs = recoveryDuration.TotalMilliseconds
                });
            }
            else
            {
                var recoveryDuration = DateTime.UtcNow - recoveryStartTime;
                Telemetry.LogEvent("AudioDeviceRecoveryFailed", new
                {
                    Component = ComponentName,
                    ErrorCode = "AUDIO_DEVICE_FAILURE",
                    RecoveryDurationMs = recoveryDuration.TotalMilliseconds,
                    RecoveryAction = "No devices available after recovery attempt"
                });
            }
        }
        catch (Exception ex)
        {
            var recoveryDuration = DateTime.UtcNow - recoveryStartTime;
            Telemetry.LogEvent("AudioDeviceRecoveryException", new
            {
                Component = ComponentName,
                ErrorCode = "AUDIO_DEVICE_FAILURE",
                RecoveryDurationMs = recoveryDuration.TotalMilliseconds,
                RecoveryAction = $"Recovery failed: {ex.Message}",
                Exception = ex.Message
            });
        }
    }

    public static List<AudioDeviceInfo> GetAvailableDevices()
    {
        return WasapiAudioCapture.GetAvailableDevices();
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class AudioCaptureSettings
{
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    public int BufferSize { get; set; } = 3200;
    public int FrameIntervalMs { get; set; } = 100;
    public string? DeviceId { get; init; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class AudioFrameEventArgs : EventArgs
{
    public AudioFrameEventArgs(ReadOnlyMemory<byte> audioData)
    {
        AudioData = audioData;
    }

    public ReadOnlyMemory<byte> AudioData { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class AudioErrorEventArgs : EventArgs
{
    public AudioErrorEventArgs(Exception exception, string message)
    {
        Exception = exception;
        Message = message;
    }

    public Exception Exception { get; }
    public string Message { get; }
}
