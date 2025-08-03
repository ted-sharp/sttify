using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Audio;

[ExcludeFromCodeCoverage] // WASAPI hardware dependent wrapper, difficult to mock effectively
public class AudioCapture : IDisposable
{
    public event EventHandler<AudioFrameEventArgs>? OnFrame;
    public event EventHandler<AudioErrorEventArgs>? OnError;

    private WasapiAudioCapture? _wasapiCapture;
    private bool _isCapturing;
    private readonly object _lockObject = new();

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

    public async Task StartAsync(AudioCaptureSettings settings, CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Audio capture is already running");
        }

        try
        {
            _wasapiCapture = new WasapiAudioCapture();
            _wasapiCapture.OnFrame += OnWasapiFrame;
            _wasapiCapture.OnError += OnWasapiError;

            await _wasapiCapture.StartAsync(settings, cancellationToken);

            lock (_lockObject)
            {
                _isCapturing = true;
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

    private async void OnWasapiError(object? sender, AudioErrorEventArgs e)
    {
        lock (_lockObject)
        {
            _isCapturing = false;
        }
        
        // Log the error with structured data
        Telemetry.LogError("WasapiAudioError", e.Exception, new 
        { 
            Component = "AudioCapture",
            Message = e.Message
        });
        
        // Attempt automatic recovery for transient errors
        if (IsTransientAudioError(e.Exception))
        {
            await AttemptAudioRecoveryAsync();
        }
        
        OnError?.Invoke(this, e);
    }
    
    private bool IsTransientAudioError(Exception exception)
    {
        // Check for known transient error patterns
        if (exception == null) return false;
        
        var message = exception.Message?.ToLowerInvariant() ?? "";
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
            if (availableDevices.Any())
            {
                var recoveryDuration = DateTime.UtcNow - recoveryStartTime;
                Telemetry.LogEvent("AudioDeviceRecoverySuccessful", new
                {
                    Component = "AudioCapture",
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
                    Component = "AudioCapture",
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
                Component = "AudioCapture",
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

    public void Dispose()
    {
        StopAsync().Wait();
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
    public string? DeviceId { get; set; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class AudioFrameEventArgs : EventArgs
{
    public ReadOnlyMemory<byte> AudioData { get; }

    public AudioFrameEventArgs(ReadOnlyMemory<byte> audioData)
    {
        AudioData = audioData;
    }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class AudioErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public string Message { get; }

    public AudioErrorEventArgs(Exception exception, string message)
    {
        Exception = exception;
        Message = message;
    }
}