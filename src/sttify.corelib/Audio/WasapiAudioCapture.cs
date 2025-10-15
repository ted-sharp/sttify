using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Audio;

[ExcludeFromCodeCoverage] // WASAPI hardware dependent, system integration, difficult to mock effectively
public class WasapiAudioCapture : IDisposable
{
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly object _lockObject = new();
    private bool _isCapturing;
    private AudioCaptureSettings _settings = new();

    private WasapiCapture? _wasapiCapture;

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

    public WaveFormat? CurrentWaveFormat { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Avoid sync wait on UI thread; best-effort async stop
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    { await StopAsync(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"*** WasapiAudioCapture StopAsync in Dispose failed: {ex.Message} ***");
                    }
                    try
                    { _wasapiCapture?.Dispose(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"*** WasapiAudioCapture inner Dispose failed: {ex.Message} ***");
                    }
                    _wasapiCapture = null;
                });
            }
            catch
            {
                try
                { _wasapiCapture?.Dispose(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"*** WasapiAudioCapture outer Dispose failed: {ex.Message} ***");
                }
                _wasapiCapture = null;
            }
        }
    }

    public event EventHandler<AudioFrameEventArgs>? OnFrame;
    public event EventHandler<AudioErrorEventArgs>? OnError;

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
                    CurrentWaveFormat?.SampleRate,
                    CurrentWaveFormat?.Channels,
                    CurrentWaveFormat?.BitsPerSample,
                    _settings.DeviceId
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
            using var deviceEnumerator = new MMDeviceEnumerator();
            try
            {
                captureDevice = deviceEnumerator.GetDevice(_settings.DeviceId);
            }
            catch (Exception)
            {
                Telemetry.LogWarning("AudioDeviceNotFound",
                    $"Specified device not found: {_settings.DeviceId}",
                    new { _settings.DeviceId });

                // Fall back to default policy: Communications or Console based on Channels
                var role = _settings.Channels == 1 ? Role.Communications : Role.Console;
                captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            }
        }
        else
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            // New policy: prefer Console for stereo, Communications for mono
            var role = _settings.Channels == 1 ? Role.Communications : Role.Console;
            captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        }

        if (captureDevice == null)
        {
            throw new InvalidOperationException("No audio capture device available");
        }

        CurrentWaveFormat = new WaveFormat(_settings.SampleRate, _settings.BitsPerSample, _settings.Channels);

        _wasapiCapture = new WasapiCapture(captureDevice, true, 100);

        System.Diagnostics.Debug.WriteLine($"*** WASAPI Capture Format - Requested: {_settings.SampleRate}Hz, {_settings.Channels}ch, {_settings.BitsPerSample}bit ***");
        System.Diagnostics.Debug.WriteLine($"*** WASAPI Capture Format - Actual: {_wasapiCapture.WaveFormat.SampleRate}Hz, {_wasapiCapture.WaveFormat.Channels}ch, {_wasapiCapture.WaveFormat.BitsPerSample}bit ***");

        if (_wasapiCapture.WaveFormat.SampleRate != _settings.SampleRate ||
            _wasapiCapture.WaveFormat.Channels != _settings.Channels)
        {
            System.Diagnostics.Debug.WriteLine($"*** AUDIO FORMAT MISMATCH DETECTED! ***");
            Telemetry.LogEvent("AudioFormatMismatch", new
            {
                RequestedSampleRate = _settings.SampleRate,
                ActualSampleRate = _wasapiCapture.WaveFormat.SampleRate,
                RequestedChannels = _settings.Channels,
                ActualChannels = _wasapiCapture.WaveFormat.Channels
            });
        }

        CurrentWaveFormat = _wasapiCapture.WaveFormat;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded > 0 && IsCapturing && CurrentWaveFormat != null)
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
                        if (!AudioConverter.IsVoskCompatible(CurrentWaveFormat))
                        {
                            processedData = AudioConverter.ConvertToVoskFormat(audioSpan, CurrentWaveFormat);
                        }
                        else
                        {
                            // Create a copy for the event since we need to return the rented buffer
                            processedData = audioSpan.ToArray();
                        }

                        var level = AudioConverter.CalculateAudioLevel(processedData, AudioConverter.GetVoskTargetFormat());

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


    public static List<AudioDeviceInfo> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
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
}

[ExcludeFromCodeCoverage] // Simple data container class
public class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}
