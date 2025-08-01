namespace Sttify.Corelib.Audio;

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

    private void OnWasapiError(object? sender, AudioErrorEventArgs e)
    {
        lock (_lockObject)
        {
            _isCapturing = false;
        }
        
        OnError?.Invoke(this, e);
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

public class AudioCaptureSettings
{
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    public int BufferSize { get; set; } = 3200;
    public int FrameIntervalMs { get; set; } = 100;
    public string? DeviceId { get; set; }
}

public class AudioFrameEventArgs : EventArgs
{
    public ReadOnlyMemory<byte> AudioData { get; }

    public AudioFrameEventArgs(ReadOnlyMemory<byte> audioData)
    {
        AudioData = audioData;
    }
}

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