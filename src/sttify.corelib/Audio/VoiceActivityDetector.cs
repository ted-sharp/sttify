using Sttify.Corelib.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sttify.Corelib.Audio;

public class VoiceActivityDetector : IDisposable
{
    private readonly VadSettings _settings;
    private readonly ConcurrentQueue<AudioFrame> _frameBuffer = new();
    private readonly double[] _energyHistory;
    private readonly double[] _spectralHistory;
    private int _historyIndex;
    private bool _isVoiceActive;
    private DateTime _lastVoiceTime;
    private DateTime _lastSilenceTime;
    private double _noiseFloor = -60.0; // dB
    private double _adaptiveThreshold;

    public event EventHandler<VoiceActivityEventArgs>? OnVoiceActivityChanged;
    public event EventHandler<SilenceDetectedEventArgs>? OnSilenceDetected;
    public event EventHandler<EndpointDetectedEventArgs>? OnEndpointDetected;

    public bool IsVoiceActive => _isVoiceActive;
    public TimeSpan TimeSinceLastVoice => DateTime.UtcNow - _lastVoiceTime;
    public TimeSpan TimeSinceLastSilence => DateTime.UtcNow - _lastSilenceTime;
    public double CurrentNoiseFloor => _noiseFloor;
    public double CurrentThreshold => _adaptiveThreshold;

    public VoiceActivityDetector(VadSettings? settings = null)
    {
        _settings = settings ?? new VadSettings();
        _energyHistory = new double[_settings.HistoryBufferSize];
        _spectralHistory = new double[_settings.HistoryBufferSize];
        _adaptiveThreshold = _settings.InitialEnergyThreshold;
        _lastVoiceTime = DateTime.MinValue;
        _lastSilenceTime = DateTime.UtcNow;
    }

    public VadResult ProcessAudioFrame(ReadOnlySpan<byte> audioData, int sampleRate, int channels)
    {
        try
        {
            var frame = new AudioFrame
            {
                Data = audioData.ToArray(),
                SampleRate = sampleRate,
                Channels = channels,
                Timestamp = DateTime.UtcNow
            };

            _frameBuffer.Enqueue(frame);
            
            // Keep buffer size manageable
            while (_frameBuffer.Count > _settings.MaxBufferFrames)
            {
                _frameBuffer.TryDequeue(out _);
            }

            var result = AnalyzeFrame(frame);
            
            // Update voice activity state
            UpdateVoiceActivityState(result);
            
            // Detect endpoints
            DetectEndpoints(result);

            return result;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VADProcessingError", ex, new { 
                DataLength = audioData.Length,
                SampleRate = sampleRate,
                Channels = channels 
            });
            
            return new VadResult
            {
                IsVoice = false,
                Confidence = 0.0,
                Energy = 0.0,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private VadResult AnalyzeFrame(AudioFrame frame)
    {
        var samples = ConvertBytesToSamples(frame.Data, frame.Channels);
        
        // Calculate energy features
        var energy = CalculateEnergy(samples);
        var zcr = CalculateZeroCrossingRate(samples);
        var spectralCentroid = CalculateSpectralCentroid(samples, frame.SampleRate);
        var spectralRolloff = CalculateSpectralRolloff(samples, frame.SampleRate);

        // Update noise floor estimation
        UpdateNoiseFloor(energy);
        
        // Update adaptive threshold
        UpdateAdaptiveThreshold(energy);

        // Store in history for temporal analysis
        _energyHistory[_historyIndex] = energy;
        _spectralHistory[_historyIndex] = spectralCentroid;
        _historyIndex = (_historyIndex + 1) % _settings.HistoryBufferSize;

        // Multi-feature voice activity detection
        var result = DetectVoiceActivity(energy, zcr, spectralCentroid, spectralRolloff);
        
        result.Timestamp = frame.Timestamp;
        result.Energy = energy;
        result.ZeroCrossingRate = zcr;
        result.SpectralCentroid = spectralCentroid;
        result.SpectralRolloff = spectralRolloff;
        result.NoiseFloor = _noiseFloor;
        result.AdaptiveThreshold = _adaptiveThreshold;

        return result;
    }

    private short[] ConvertBytesToSamples(byte[] audioData, int channels)
    {
        var sampleCount = audioData.Length / (2 * channels); // 16-bit samples
        var samples = new short[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            var byteIndex = i * 2 * channels; // Take first channel for mono analysis
            samples[i] = (short)(audioData[byteIndex] | (audioData[byteIndex + 1] << 8));
        }
        
        return samples;
    }

    private double CalculateEnergy(short[] samples)
    {
        if (samples.Length == 0) return -100.0;

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }
        
        var rms = Math.Sqrt(sum / samples.Length);
        return rms > 0 ? 20.0 * Math.Log10(rms / 32768.0) : -100.0; // Convert to dB
    }

    private double CalculateZeroCrossingRate(short[] samples)
    {
        if (samples.Length < 2) return 0.0;

        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i] >= 0) != (samples[i - 1] >= 0))
            {
                crossings++;
            }
        }
        
        return (double)crossings / (samples.Length - 1);
    }

    private double CalculateSpectralCentroid(short[] samples, int sampleRate)
    {
        // Simplified spectral centroid calculation
        // In a real implementation, you'd use FFT
        var spectrum = SimpleSpectrum(samples);
        
        double weightedSum = 0;
        double magnitudeSum = 0;
        
        for (int i = 0; i < spectrum.Length; i++)
        {
            var frequency = (double)i * sampleRate / (2 * spectrum.Length);
            var magnitude = Math.Abs(spectrum[i]);
            
            weightedSum += frequency * magnitude;
            magnitudeSum += magnitude;
        }
        
        return magnitudeSum > 0 ? weightedSum / magnitudeSum : 0.0;
    }

    private double CalculateSpectralRolloff(short[] samples, int sampleRate)
    {
        var spectrum = SimpleSpectrum(samples);
        var totalEnergy = spectrum.Sum(s => s * s);
        var threshold = 0.85 * totalEnergy;
        
        double cumulativeEnergy = 0;
        for (int i = 0; i < spectrum.Length; i++)
        {
            cumulativeEnergy += spectrum[i] * spectrum[i];
            if (cumulativeEnergy >= threshold)
            {
                return (double)i * sampleRate / (2 * spectrum.Length);
            }
        }
        
        return sampleRate / 2.0;
    }

    private double[] SimpleSpectrum(short[] samples)
    {
        // Convert samples to complex numbers for FFT
        var fftSize = GetNextPowerOfTwo(Math.Min(samples.Length, 1024));
        var complex = new Complex[fftSize];
        
        // Fill with samples and apply Hamming window
        for (int i = 0; i < fftSize; i++)
        {
            if (i < samples.Length)
            {
                // Apply Hamming window to reduce spectral leakage
                var windowValue = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (fftSize - 1));
                complex[i] = new Complex(samples[i] * windowValue / 32768.0, 0);
            }
            else
            {
                complex[i] = Complex.Zero;
            }
        }
        
        // Perform FFT
        FFT(complex);
        
        // Calculate magnitude spectrum (only first half due to symmetry)
        var spectrum = new double[fftSize / 2];
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] = complex[i].Magnitude;
        }
        
        return spectrum;
    }
    
    private static int GetNextPowerOfTwo(int n)
    {
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }
    
    private static void FFT(Complex[] buffer)
    {
        int n = buffer.Length;
        if (n <= 1) return;
        
        // Bit-reverse permutation
        for (int i = 0; i < n; i++)
        {
            int j = BitReverse(i, (int)Math.Log2(n));
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }
        
        // Cooley-Tukey FFT
        for (int length = 2; length <= n; length *= 2)
        {
            double angle = -2.0 * Math.PI / length;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            
            for (int start = 0; start < n; start += length)
            {
                var w = Complex.One;
                
                for (int i = 0; i < length / 2; i++)
                {
                    var u = buffer[start + i];
                    var v = buffer[start + i + length / 2] * w;
                    
                    buffer[start + i] = u + v;
                    buffer[start + i + length / 2] = u - v;
                    
                    w *= wlen;
                }
            }
        }
    }
    
    private static int BitReverse(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }

    private void UpdateNoiseFloor(double energy)
    {
        // Exponential moving average for noise floor estimation
        if (_noiseFloor == -60.0) // First measurement
        {
            _noiseFloor = energy;
        }
        else
        {
            var alpha = _isVoiceActive ? 0.001 : 0.01; // Slower adaptation during voice
            _noiseFloor = alpha * energy + (1 - alpha) * _noiseFloor;
        }
    }

    private void UpdateAdaptiveThreshold(double energy)
    {
        // Adaptive threshold based on noise floor and recent energy history
        var margin = _settings.AdaptiveMarginDb;
        _adaptiveThreshold = _noiseFloor + margin;
        
        // Consider recent energy variations
        if (_historyIndex > 10) // Have some history
        {
            var recentEnergies = _energyHistory.Take(_historyIndex).Where(e => e > _noiseFloor).ToArray();
            if (recentEnergies.Length > 0)
            {
                var avgRecentEnergy = recentEnergies.Average();
                var dynamicMargin = Math.Max(margin, (avgRecentEnergy - _noiseFloor) * 0.3);
                _adaptiveThreshold = _noiseFloor + dynamicMargin;
            }
        }
    }

    private VadResult DetectVoiceActivity(double energy, double zcr, double spectralCentroid, double spectralRolloff)
    {
        var confidence = 0.0;
        
        // Energy-based detection
        var energyScore = energy > _adaptiveThreshold ? 1.0 : 0.0;
        if (energy > _adaptiveThreshold)
        {
            var energyRatio = (energy - _noiseFloor) / Math.Max(1.0, _adaptiveThreshold - _noiseFloor);
            energyScore = Math.Min(1.0, energyRatio);
        }
        
        // Zero crossing rate analysis (voice typically has moderate ZCR)
        var zcrScore = 0.0;
        if (zcr >= _settings.MinZeroCrossingRate && zcr <= _settings.MaxZeroCrossingRate)
        {
            zcrScore = 1.0 - Math.Abs(zcr - 0.1) / 0.1; // Optimal around 0.1
        }
        
        // Spectral features (voice has characteristic spectral shape)
        var spectralScore = 0.0;
        if (spectralCentroid >= _settings.MinSpectralCentroid && spectralCentroid <= _settings.MaxSpectralCentroid)
        {
            spectralScore = 1.0;
        }
        
        // Temporal consistency check
        var temporalScore = CalculateTemporalConsistency();
        
        // Weighted combination
        confidence = (_settings.EnergyWeight * energyScore +
                     _settings.ZcrWeight * zcrScore +
                     _settings.SpectralWeight * spectralScore +
                     _settings.TemporalWeight * temporalScore) /
                    (_settings.EnergyWeight + _settings.ZcrWeight + _settings.SpectralWeight + _settings.TemporalWeight);
        
        var isVoice = confidence >= _settings.VoiceConfidenceThreshold;
        
        return new VadResult
        {
            IsVoice = isVoice,
            Confidence = confidence,
            EnergyScore = energyScore,
            ZcrScore = zcrScore,
            SpectralScore = spectralScore,
            TemporalScore = temporalScore
        };
    }

    private double CalculateTemporalConsistency()
    {
        if (_historyIndex < 5) return 0.5; // Not enough history
        
        // Check consistency of recent energy measurements
        var recentCount = Math.Min(_historyIndex, 10);
        var recentEnergies = _energyHistory.Take(recentCount).ToArray();
        
        var aboveThresholdCount = recentEnergies.Count(e => e > _adaptiveThreshold);
        return (double)aboveThresholdCount / recentCount;
    }

    private void UpdateVoiceActivityState(VadResult result)
    {
        var wasVoiceActive = _isVoiceActive;
        
        if (result.IsVoice)
        {
            _lastVoiceTime = result.Timestamp;
            if (!_isVoiceActive)
            {
                _isVoiceActive = true;
                OnVoiceActivityChanged?.Invoke(this, new VoiceActivityEventArgs(true, result.Confidence, result.Timestamp));
                
                Telemetry.LogEvent("VoiceActivityStarted", new {
                    Confidence = result.Confidence,
                    Energy = result.Energy,
                    SilenceDuration = (result.Timestamp - _lastSilenceTime).TotalMilliseconds
                });
            }
        }
        else
        {
            _lastSilenceTime = result.Timestamp;
            if (_isVoiceActive)
            {
                var voiceDuration = result.Timestamp - _lastVoiceTime;
                if (voiceDuration.TotalMilliseconds >= _settings.MinVoiceDurationMs)
                {
                    _isVoiceActive = false;
                    OnVoiceActivityChanged?.Invoke(this, new VoiceActivityEventArgs(false, result.Confidence, result.Timestamp));
                    
                    OnSilenceDetected?.Invoke(this, new SilenceDetectedEventArgs(voiceDuration, result.Timestamp));
                    
                    Telemetry.LogEvent("VoiceActivityStopped", new {
                        VoiceDuration = voiceDuration.TotalMilliseconds,
                        Energy = result.Energy
                    });
                }
            }
        }
    }

    private void DetectEndpoints(VadResult result)
    {
        if (!_isVoiceActive && _lastVoiceTime != DateTime.MinValue)
        {
            var silenceDuration = result.Timestamp - _lastVoiceTime;
            
            if (silenceDuration.TotalMilliseconds >= _settings.EndpointSilenceMs)
            {
                OnEndpointDetected?.Invoke(this, new EndpointDetectedEventArgs(
                    EndpointType.SilenceBased, silenceDuration, result.Timestamp));
                
                Telemetry.LogEvent("EndpointDetected", new {
                    Type = "Silence",
                    Duration = silenceDuration.TotalMilliseconds,
                    LastVoiceTime = _lastVoiceTime
                });
            }
        }
    }

    public VadStatistics GetStatistics()
    {
        return new VadStatistics
        {
            IsCurrentlyActive = _isVoiceActive,
            LastVoiceTime = _lastVoiceTime,
            LastSilenceTime = _lastSilenceTime,
            CurrentNoiseFloor = _noiseFloor,
            CurrentThreshold = _adaptiveThreshold,
            BufferedFrameCount = _frameBuffer.Count,
            HistoryDepth = Math.Min(_historyIndex, _settings.HistoryBufferSize)
        };
    }

    public void Reset()
    {
        _isVoiceActive = false;
        _lastVoiceTime = DateTime.MinValue;
        _lastSilenceTime = DateTime.UtcNow;
        _noiseFloor = -60.0;
        _adaptiveThreshold = _settings.InitialEnergyThreshold;
        _historyIndex = 0;
        
        Array.Clear(_energyHistory);
        Array.Clear(_spectralHistory);
        
        while (_frameBuffer.TryDequeue(out _)) { }
        
        Telemetry.LogEvent("VADReset");
    }

    public void Dispose()
    {
        while (_frameBuffer.TryDequeue(out _)) { }
    }
}

public class AudioFrame
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public DateTime Timestamp { get; set; }
}

[ExcludeFromCodeCoverage] // Simple data container class\npublic class VadResult
{
    public bool IsVoice { get; set; }
    public double Confidence { get; set; }
    public double Energy { get; set; }
    public double ZeroCrossingRate { get; set; }
    public double SpectralCentroid { get; set; }
    public double SpectralRolloff { get; set; }
    public double NoiseFloor { get; set; }
    public double AdaptiveThreshold { get; set; }
    public DateTime Timestamp { get; set; }
    
    // Individual feature scores
    public double EnergyScore { get; set; }
    public double ZcrScore { get; set; }
    public double SpectralScore { get; set; }
    public double TemporalScore { get; set; }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class VadSettings
{
    public double InitialEnergyThreshold { get; set; } = -30.0; // dB
    public double AdaptiveMarginDb { get; set; } = 10.0;
    public double VoiceConfidenceThreshold { get; set; } = 0.6;
    public double MinZeroCrossingRate { get; set; } = 0.01;
    public double MaxZeroCrossingRate { get; set; } = 0.3;
    public double MinSpectralCentroid { get; set; } = 200.0; // Hz
    public double MaxSpectralCentroid { get; set; } = 4000.0; // Hz
    public int HistoryBufferSize { get; set; } = 50;
    public int MaxBufferFrames { get; set; } = 100;
    public int MinVoiceDurationMs { get; set; } = 100;
    public int EndpointSilenceMs { get; set; } = 800;
    
    // Feature weights for combination
    public double EnergyWeight { get; set; } = 0.4;
    public double ZcrWeight { get; set; } = 0.2;
    public double SpectralWeight { get; set; } = 0.2;
    public double TemporalWeight { get; set; } = 0.2;
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class VadStatistics
{
    public bool IsCurrentlyActive { get; set; }
    public DateTime LastVoiceTime { get; set; }
    public DateTime LastSilenceTime { get; set; }
    public double CurrentNoiseFloor { get; set; }
    public double CurrentThreshold { get; set; }
    public int BufferedFrameCount { get; set; }
    public int HistoryDepth { get; set; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class VoiceActivityEventArgs : EventArgs
{
    public bool IsActive { get; }
    public double Confidence { get; }
    public DateTime Timestamp { get; }

    public VoiceActivityEventArgs(bool isActive, double confidence, DateTime timestamp)
    {
        IsActive = isActive;
        Confidence = confidence;
        Timestamp = timestamp;
    }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class SilenceDetectedEventArgs : EventArgs
{
    public TimeSpan VoiceDuration { get; }
    public DateTime Timestamp { get; }

    public SilenceDetectedEventArgs(TimeSpan voiceDuration, DateTime timestamp)
    {
        VoiceDuration = voiceDuration;
        Timestamp = timestamp;
    }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class EndpointDetectedEventArgs : EventArgs
{
    public EndpointType Type { get; }
    public TimeSpan Duration { get; }
    public DateTime Timestamp { get; }

    public EndpointDetectedEventArgs(EndpointType type, TimeSpan duration, DateTime timestamp)
    {
        Type = type;
        Duration = duration;
        Timestamp = timestamp;
    }
}

public enum EndpointType
{
    SilenceBased,
    EnergyBased,
    Timeout,
    Manual
}