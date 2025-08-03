using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sttify.Corelib.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Buffers;

namespace Sttify.Corelib.Audio;

[ExcludeFromCodeCoverage] // WASAPI hardware dependent, system integration, difficult to mock effectively
public class WasapiAudioCapture : IDisposable
{
    public event EventHandler<AudioFrameEventArgs>? OnFrame;
    public event EventHandler<AudioErrorEventArgs>? OnError;

    private WasapiCapture? _wasapiCapture;
    private WaveFormat? _waveFormat;
    private bool _isCapturing;
    private readonly object _lockObject = new();
    private AudioCaptureSettings _settings = new();
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

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

    public WaveFormat? CurrentWaveFormat => _waveFormat;

    public async Task StartAsync(AudioCaptureSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        lock (_lockObject)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Audio capture is already running");
        }

        try
        {
            await Task.Run(() => InitializeCapture(), cancellationToken);
            
            if (_wasapiCapture != null)
            {
                _wasapiCapture.DataAvailable += OnDataAvailable;
                _wasapiCapture.RecordingStopped += OnRecordingStopped;
                
                _wasapiCapture.StartRecording();
                
                lock (_lockObject)
                {
                    _isCapturing = true;
                }

                Telemetry.LogEvent("WasapiCaptureStarted", new
                {
                    SampleRate = _waveFormat?.SampleRate,
                    Channels = _waveFormat?.Channels,
                    BitsPerSample = _waveFormat?.BitsPerSample,
                    DeviceId = _settings.DeviceId
                });
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("WasapiCaptureStartFailed", ex);
            OnError?.Invoke(this, new AudioErrorEventArgs(ex, "Failed to start WASAPI capture"));
            throw;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            lock (_lockObject)
            {
                if (!_isCapturing)
                    return;

                _isCapturing = false;
            }

            if (_wasapiCapture != null)
            {
                await Task.Run(() =>
                {
                    _wasapiCapture.StopRecording();
                    _wasapiCapture.DataAvailable -= OnDataAvailable;
                    _wasapiCapture.RecordingStopped -= OnRecordingStopped;
                });
            }

            Telemetry.LogEvent("WasapiCaptureStopped");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("WasapiCaptureStopFailed", ex);
            OnError?.Invoke(this, new AudioErrorEventArgs(ex, "Failed to stop WASAPI capture"));
        }
    }

    private void InitializeCapture()
    {
        MMDevice? captureDevice = null;

        if (!string.IsNullOrEmpty(_settings.DeviceId))
        {
            var deviceEnumerator = new MMDeviceEnumerator();
            try
            {
                captureDevice = deviceEnumerator.GetDevice(_settings.DeviceId);
            }
            catch (Exception)
            {
                Telemetry.LogWarning("AudioDeviceNotFound", 
                    $"Specified device not found: {_settings.DeviceId}", 
                    new { DeviceId = _settings.DeviceId });
                
                captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
        }
        else
        {
            var deviceEnumerator = new MMDeviceEnumerator();
            captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        if (captureDevice == null)
        {
            throw new InvalidOperationException("No audio capture device available");
        }

        _waveFormat = new WaveFormat(_settings.SampleRate, _settings.BitsPerSample, _settings.Channels);

        _wasapiCapture = new WasapiCapture(captureDevice, true, 100);
        
        if (_wasapiCapture.WaveFormat.SampleRate != _settings.SampleRate ||
            _wasapiCapture.WaveFormat.Channels != _settings.Channels)
        {
            Telemetry.LogEvent("AudioFormatMismatch", new
            {
                RequestedSampleRate = _settings.SampleRate,
                ActualSampleRate = _wasapiCapture.WaveFormat.SampleRate,
                RequestedChannels = _settings.Channels,
                ActualChannels = _wasapiCapture.WaveFormat.Channels
            });
        }

        _waveFormat = _wasapiCapture.WaveFormat;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded > 0 && IsCapturing && _waveFormat != null)
            {
                // Use buffer pool to avoid allocations
                var rentedBuffer = _bufferPool.Rent(e.BytesRecorded);
                try
                {
                    Array.Copy(e.Buffer, 0, rentedBuffer, 0, e.BytesRecorded);
                    var audioSpan = rentedBuffer.AsSpan(0, e.BytesRecorded);

                    byte[]? processedData = null;
                    try
                    {
                        // Convert to Vosk-compatible format if necessary
                        if (!AudioConverter.IsVoskCompatible(_waveFormat))
                        {
                            processedData = AudioConverter.ConvertToVoskFormat(audioSpan, _waveFormat);
                        }
                        else
                        {
                            // Create a copy for the event since we need to return the rented buffer
                            processedData = audioSpan.ToArray();
                        }

                        var level = AudioConverter.CalculateAudioLevel(processedData, _waveFormat);
                        
                        Telemetry.LogAudioCapture(e.BytesRecorded, level);
                        OnFrame?.Invoke(this, new AudioFrameEventArgs(processedData));
                    }
                    finally
                    {
                        // processedData will be handled by the event handler
                    }
                }
                finally
                {
                    _bufferPool.Return(rentedBuffer);
                }
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("AudioDataProcessingFailed", ex);
            OnError?.Invoke(this, new AudioErrorEventArgs(ex, "Error processing audio data"));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lockObject)
        {
            _isCapturing = false;
        }

        if (e.Exception != null)
        {
            Telemetry.LogError("WasapiRecordingStopped", e.Exception);
            OnError?.Invoke(this, new AudioErrorEventArgs(e.Exception, "WASAPI recording stopped with error"));
        }
        else
        {
            Telemetry.LogEvent("WasapiRecordingStoppedNormally");
        }
    }

    private double CalculateAudioLevel(byte[] audioData)
    {
        if (audioData.Length == 0 || _waveFormat == null)
            return 0.0;

        double sum = 0;
        int sampleCount = 0;

        if (_waveFormat.BitsPerSample == 16)
        {
            for (int i = 0; i < audioData.Length - 1; i += 2)
            {
                short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
                sum += Math.Abs(sample);
                sampleCount++;
            }
            
            if (sampleCount > 0)
            {
                double average = sum / sampleCount;
                return average / 32768.0; // Normalize to 0-1 range
            }
        }
        else if (_waveFormat.BitsPerSample == 32)
        {
            for (int i = 0; i < audioData.Length - 3; i += 4)
            {
                float sample = BitConverter.ToSingle(audioData, i);
                sum += Math.Abs(sample);
                sampleCount++;
            }
            
            if (sampleCount > 0)
            {
                return sum / sampleCount; // Already normalized for float
            }
        }

        return 0.0;
    }

    public static List<AudioDeviceInfo> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        
        try
        {
            var deviceEnumerator = new MMDeviceEnumerator();
            var captureDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in captureDevices)
            {
                try
                {
                    devices.Add(new AudioDeviceInfo
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        IsDefault = device.ID == deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID
                    });
                }
                catch (Exception ex)
                {
                    Telemetry.LogError("AudioDeviceEnumerationFailed", ex, new { DeviceId = device.ID });
                }
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("AudioDeviceEnumerationFailed", ex);
        }

        return devices;
    }

    public void Dispose()
    {
        StopAsync().Wait();
        
        _wasapiCapture?.Dispose();
        _wasapiCapture = null;
    }
}

[ExcludeFromCodeCoverage] // Simple data container class
public class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}