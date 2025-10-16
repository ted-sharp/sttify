using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Audio;

public class EndpointDetector : IDisposable
{
    private readonly ConcurrentQueue<EndpointEvent> _eventHistory = new();
    private readonly EndpointSettings _settings;
    private readonly Timer _timeoutTimer;
    private readonly VoiceActivityDetector _vad;
    private bool _disposed;
    private DateTime _lastActivityTime;

    private DateTime _sessionStartTime;

    public EndpointDetector(EndpointSettings? settings = null, VoiceActivityDetector? vad = null)
    {
        _settings = settings ?? new EndpointSettings();
        _vad = vad ?? new VoiceActivityDetector();
        _sessionStartTime = DateTime.UtcNow;
        _lastActivityTime = DateTime.UtcNow;

        // Subscribe to VAD events
        _vad.OnVoiceActivityChanged += OnVoiceActivityChanged;
        _vad.OnSilenceDetected += OnSilenceDetected;
        _vad.OnEndpointDetected += OnVadEndpointDetected;

        // Setup timeout timer
        _timeoutTimer = new Timer(CheckTimeouts, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public bool IsInUtterance { get; private set; }

    public TimeSpan SessionDuration => DateTime.UtcNow - _sessionStartTime;
    public double TotalSpeechDuration { get; private set; }

    public int UtteranceCount { get; private set; }

    public TimeSpan TimeSinceLastActivity => DateTime.UtcNow - _lastActivityTime;

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
            _timeoutTimer.Dispose();
            _vad.Dispose();
            while (_eventHistory.TryDequeue(out _))
            {
                // Intentionally empty - clearing queue
            }
        }

        _disposed = true;
    }

    public event EventHandler<UtteranceStartedEventArgs>? OnUtteranceStarted;
    public event EventHandler<UtteranceEndedEventArgs>? OnUtteranceEnded;
    public event EventHandler<EndpointTriggeredEventArgs>? OnEndpointTriggered;
    public event EventHandler<SessionTimeoutEventArgs>? OnSessionTimeout;

    public EndpointResult ProcessAudioFrame(ReadOnlySpan<byte> audioData, int sampleRate, int channels)
    {
        try
        {
            var timestamp = DateTime.UtcNow;

            // Process through VAD
            var vadResult = _vad.ProcessAudioFrame(audioData, sampleRate, channels);

            // Update activity tracking
            if (vadResult.IsVoice)
            {
                _lastActivityTime = timestamp;
            }

            // Detect endpoints based on multiple criteria
            var endpointResult = AnalyzeForEndpoints(vadResult, timestamp);

            // Record event history
            RecordEvent(new EndpointEvent
            {
                Timestamp = timestamp,
                Type = EndpointEventType.AudioProcessed,
                VadResult = vadResult,
                EndpointResult = endpointResult
            });

            return endpointResult;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("EndpointDetectionError", ex, new
            {
                DataLength = audioData.Length,
                SampleRate = sampleRate,
                Channels = channels
            });

            return new EndpointResult
            {
                HasEndpoint = false,
                EndpointType = EndpointType.Manual,
                Confidence = 0.0,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private EndpointResult AnalyzeForEndpoints(VadResult vadResult, DateTime timestamp)
    {
        var result = new EndpointResult
        {
            HasEndpoint = false,
            Timestamp = timestamp
        };

        // 1. Silence-based endpoint detection
        result = CheckSilenceEndpoint(vadResult, timestamp, result);

        // 2. Energy-based endpoint detection
        result = CheckEnergyEndpoint(vadResult, result);

        // 3. Maximum utterance length
        result = CheckUtteranceTimeout(result);

        // 4. Session timeout
        result = CheckSessionTimeout(timestamp, result);

        // 5. Adaptive endpoint based on speech patterns
        result = CheckAdaptiveEndpoint(timestamp, result);

        return result;
    }

    private EndpointResult CheckSilenceEndpoint(VadResult vadResult, DateTime timestamp, EndpointResult currentResult)
    {
        if (!vadResult.IsVoice && IsInUtterance)
        {
            var silenceDuration = timestamp - _lastActivityTime;

            if (silenceDuration.TotalMilliseconds >= _settings.SilenceTimeoutMs)
            {
                currentResult.HasEndpoint = true;
                currentResult.EndpointType = EndpointType.SilenceBased;
                currentResult.Confidence = CalculateSilenceConfidence(silenceDuration);
                currentResult.SilenceDuration = silenceDuration;

                Telemetry.LogEvent("SilenceEndpointDetected", new
                {
                    SilenceDuration = silenceDuration.TotalMilliseconds,
                    currentResult.Confidence
                });
            }
        }

        return currentResult;
    }

    private EndpointResult CheckEnergyEndpoint(VadResult vadResult, EndpointResult currentResult)
    {
        if (_settings.EnableEnergyEndpoint)
        {
            var energyEndpoint = DetectEnergyEndpoint(vadResult);
            if (energyEndpoint.HasEndpoint && energyEndpoint.Confidence > currentResult.Confidence)
            {
                return energyEndpoint;
            }
        }

        return currentResult;
    }

    private EndpointResult CheckUtteranceTimeout(EndpointResult currentResult)
    {
        if (IsInUtterance && _settings.MaxUtteranceDurationMs > 0)
        {
            var utteranceDuration = GetCurrentUtteranceDuration();
            if (utteranceDuration.TotalMilliseconds >= _settings.MaxUtteranceDurationMs)
            {
                currentResult.HasEndpoint = true;
                currentResult.EndpointType = EndpointType.Timeout;
                currentResult.Confidence = 1.0;
                currentResult.UtteranceDuration = utteranceDuration;

                Telemetry.LogEvent("TimeoutEndpointDetected", new
                {
                    UtteranceDuration = utteranceDuration.TotalMilliseconds
                });
            }
        }

        return currentResult;
    }

    private EndpointResult CheckSessionTimeout(DateTime timestamp, EndpointResult currentResult)
    {
        if (_settings.MaxSessionDurationMs > 0)
        {
            var sessionDuration = timestamp - _sessionStartTime;
            if (sessionDuration.TotalMilliseconds >= _settings.MaxSessionDurationMs)
            {
                currentResult.HasEndpoint = true;
                currentResult.EndpointType = EndpointType.Timeout;
                currentResult.Confidence = 1.0;
                currentResult.SessionDuration = sessionDuration;
                currentResult.IsSessionTimeout = true;

                Telemetry.LogEvent("SessionTimeoutDetected", new
                {
                    SessionDuration = sessionDuration.TotalMilliseconds
                });
            }
        }

        return currentResult;
    }

    private EndpointResult CheckAdaptiveEndpoint(DateTime timestamp, EndpointResult currentResult)
    {
        if (_settings.EnableAdaptiveEndpoint && UtteranceCount > 0)
        {
            var adaptiveEndpoint = DetectAdaptiveEndpoint(timestamp);
            if (adaptiveEndpoint.HasEndpoint && adaptiveEndpoint.Confidence > currentResult.Confidence)
            {
                return adaptiveEndpoint;
            }
        }

        return currentResult;
    }

    private EndpointResult DetectEnergyEndpoint(VadResult vadResult)
    {
        var result = new EndpointResult { HasEndpoint = false };

        if (IsInUtterance && vadResult.Energy < _settings.EnergyEndpointThreshold)
        {
            // Check recent energy trend
            var recentEvents = GetRecentEvents(TimeSpan.FromMilliseconds(500));
            var recentEnergies = recentEvents
                .Where(e => e.VadResult != null)
                .Select(e => e.VadResult!.Energy)
                .ToArray();

            if (recentEnergies.Length > 3)
            {
                var avgRecentEnergy = recentEnergies.Average();
                if (avgRecentEnergy < _settings.EnergyEndpointThreshold)
                {
                    result.HasEndpoint = true;
                    result.EndpointType = EndpointType.EnergyBased;
                    result.Confidence = CalculateEnergyConfidence(avgRecentEnergy);
                }
            }
        }

        return result;
    }

    private EndpointResult DetectAdaptiveEndpoint(DateTime timestamp)
    {
        var result = new EndpointResult { HasEndpoint = false };

        // Analyze speech patterns from previous utterances
        var recentUtterances = GetRecentUtteranceStats();
        if (recentUtterances.Count < 2)
            return result;

        var avgUtteranceLength = recentUtterances.Average(u => u.Duration.TotalMilliseconds);
        var avgSilenceLength = recentUtterances.Average(u => u.EndSilence.TotalMilliseconds);

        if (IsInUtterance)
        {
            var currentUtteranceDuration = GetCurrentUtteranceDuration();
            var timeSinceLastVoice = timestamp - _lastActivityTime;

            // If current utterance is significantly longer than average
            // and we have some silence, it might be an endpoint
            if (currentUtteranceDuration.TotalMilliseconds > avgUtteranceLength * 1.5 &&
                timeSinceLastVoice.TotalMilliseconds > avgSilenceLength * 0.5)
            {
                result.HasEndpoint = true;
                result.EndpointType = EndpointType.SilenceBased;
                result.Confidence = CalculateAdaptiveConfidence(
                    currentUtteranceDuration, avgUtteranceLength,
                    timeSinceLastVoice, avgSilenceLength);
            }
        }

        return result;
    }

    private void OnVoiceActivityChanged(object? sender, VoiceActivityEventArgs e)
    {
        if (e.IsActive && !IsInUtterance)
        {
            // Utterance started
            IsInUtterance = true;
            UtteranceCount++;

            var startEvent = new UtteranceStartedEventArgs(UtteranceCount, e.Confidence, e.Timestamp);
            OnUtteranceStarted?.Invoke(this, startEvent);

            RecordEvent(new EndpointEvent
            {
                Timestamp = e.Timestamp,
                Type = EndpointEventType.UtteranceStarted,
                Confidence = e.Confidence
            });

            Telemetry.LogEvent("UtteranceStarted", new
            {
                UtteranceNumber = UtteranceCount,
                e.Confidence,
                SessionDuration = (e.Timestamp - _sessionStartTime).TotalSeconds
            });
        }
        else if (!e.IsActive && IsInUtterance)
        {
            // Potential utterance end - will be confirmed by endpoint detection
            _lastActivityTime = e.Timestamp;
        }
    }

    private void OnSilenceDetected(object? sender, SilenceDetectedEventArgs e)
    {
        if (IsInUtterance)
        {
            TotalSpeechDuration += e.VoiceDuration.TotalSeconds;
        }
    }

    private void OnVadEndpointDetected(object? sender, EndpointDetectedEventArgs e)
    {
        if (IsInUtterance)
        {
            TriggerEndpoint(new EndpointResult
            {
                HasEndpoint = true,
                EndpointType = e.Type,
                Confidence = 0.8,
                SilenceDuration = e.Duration,
                Timestamp = e.Timestamp
            });
        }
    }

    public void TriggerManualEndpoint()
    {
        TriggerEndpoint(new EndpointResult
        {
            HasEndpoint = true,
            EndpointType = EndpointType.Manual,
            Confidence = 1.0,
            Timestamp = DateTime.UtcNow
        });

        Telemetry.LogEvent("ManualEndpointTriggered");
    }

    private void TriggerEndpoint(EndpointResult result)
    {
        if (IsInUtterance)
        {
            IsInUtterance = false;

            var utteranceDuration = GetCurrentUtteranceDuration();

            var endEvent = new UtteranceEndedEventArgs(
                UtteranceCount, utteranceDuration, result.EndpointType, result.Confidence, result.Timestamp);
            OnUtteranceEnded?.Invoke(this, endEvent);

            RecordEvent(new EndpointEvent
            {
                Timestamp = result.Timestamp,
                Type = EndpointEventType.UtteranceEnded,
                EndpointResult = result,
                UtteranceDuration = utteranceDuration
            });

            Telemetry.LogEvent("UtteranceEnded", new
            {
                UtteranceNumber = UtteranceCount,
                Duration = utteranceDuration.TotalMilliseconds,
                EndpointType = result.EndpointType.ToString(),
                result.Confidence
            });
        }

        OnEndpointTriggered?.Invoke(this, new EndpointTriggeredEventArgs(result));

        if (result.IsSessionTimeout)
        {
            OnSessionTimeout?.Invoke(this, new SessionTimeoutEventArgs(result.SessionDuration ?? TimeSpan.Zero));
        }
    }

    private void CheckTimeouts(object? state)
    {
        var now = DateTime.UtcNow;

        // Check for session timeout
        if (_settings.MaxSessionDurationMs > 0)
        {
            var sessionDuration = now - _sessionStartTime;
            if (sessionDuration.TotalMilliseconds >= _settings.MaxSessionDurationMs)
            {
                TriggerEndpoint(new EndpointResult
                {
                    HasEndpoint = true,
                    EndpointType = EndpointType.Timeout,
                    Confidence = 1.0,
                    SessionDuration = sessionDuration,
                    IsSessionTimeout = true,
                    Timestamp = now
                });
            }
        }

        // Check for inactivity timeout
        if (_settings.InactivityTimeoutMs > 0)
        {
            var inactivityDuration = now - _lastActivityTime;
            if (inactivityDuration.TotalMilliseconds >= _settings.InactivityTimeoutMs && IsInUtterance)
            {
                TriggerEndpoint(new EndpointResult
                {
                    HasEndpoint = true,
                    EndpointType = EndpointType.Timeout,
                    Confidence = 0.9,
                    SilenceDuration = inactivityDuration,
                    Timestamp = now
                });
            }
        }
    }

    private double CalculateSilenceConfidence(TimeSpan silenceDuration)
    {
        var ratio = silenceDuration.TotalMilliseconds / _settings.SilenceTimeoutMs;
        return Math.Min(1.0, 0.5 + ratio * 0.5);
    }

    private double CalculateEnergyConfidence(double avgEnergy)
    {
        var threshold = _settings.EnergyEndpointThreshold;
        var ratio = (threshold - avgEnergy) / threshold;
        return Math.Max(0.0, Math.Min(1.0, ratio));
    }

    private static double CalculateAdaptiveConfidence(TimeSpan currentDuration, double avgDuration,
                                             TimeSpan currentSilence, double avgSilence)
    {
        var durationFactor = Math.Min(1.0, currentDuration.TotalMilliseconds / avgDuration);
        var silenceFactor = Math.Min(1.0, currentSilence.TotalMilliseconds / avgSilence);
        return (durationFactor + silenceFactor) / 2.0;
    }

    private TimeSpan GetCurrentUtteranceDuration()
    {
        var startEvent = _eventHistory
            .Where(e => e.Type == EndpointEventType.UtteranceStarted)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefault();

        return startEvent != null ? DateTime.UtcNow - startEvent.Timestamp : TimeSpan.Zero;
    }

    private List<EndpointEvent> GetRecentEvents(TimeSpan timespan)
    {
        var cutoff = DateTime.UtcNow - timespan;
        return _eventHistory.Where(e => e.Timestamp >= cutoff).OrderByDescending(e => e.Timestamp).ToList();
    }

    private List<UtteranceStats> GetRecentUtteranceStats()
    {
        var stats = new List<UtteranceStats>();
        var events = _eventHistory.OrderByDescending(e => e.Timestamp).ToArray();

        EndpointEvent? endEvent = null;
        for (int i = 0; i < events.Length; i++)
        {
            var evt = events[i];

            if (evt.Type == EndpointEventType.UtteranceEnded && endEvent == null)
            {
                endEvent = evt;
            }
            else if (evt.Type == EndpointEventType.UtteranceStarted && endEvent != null)
            {
                stats.Add(new UtteranceStats
                {
                    Duration = endEvent.Timestamp - evt.Timestamp,
                    EndSilence = endEvent.EndpointResult?.SilenceDuration ?? TimeSpan.Zero
                });

                endEvent = null;
                if (stats.Count >= 5)
                    break; // Limit to recent utterances
            }
        }

        return stats;
    }

    private void RecordEvent(EndpointEvent evt)
    {
        _eventHistory.Enqueue(evt);

        // Keep history manageable
        while (_eventHistory.Count > _settings.MaxEventHistory)
        {
            _eventHistory.TryDequeue(out _);
        }
    }

    public EndpointStatistics GetStatistics()
    {
        return new EndpointStatistics
        {
            SessionDuration = SessionDuration,
            TotalSpeechDuration = TotalSpeechDuration,
            UtteranceCount = UtteranceCount,
            IsInUtterance = IsInUtterance,
            TimeSinceLastActivity = TimeSinceLastActivity,
            EventHistoryCount = _eventHistory.Count,
            VadStatistics = _vad.GetStatistics()
        };
    }

    public void Reset()
    {
        _sessionStartTime = DateTime.UtcNow;
        _lastActivityTime = DateTime.UtcNow;
        IsInUtterance = false;
        TotalSpeechDuration = 0;
        UtteranceCount = 0;

        while (_eventHistory.TryDequeue(out _))
        {
            // Intentionally empty - clearing queue
        }

        _vad.Reset();

        Telemetry.LogEvent("EndpointDetectorReset");
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class EndpointSettings
{
    public int SilenceTimeoutMs { get; set; } = 800;
    public int MaxUtteranceDurationMs { get; set; } = 30000;
    public int MaxSessionDurationMs { get; set; } = 300000; // 5 minutes
    public int InactivityTimeoutMs { get; set; } = 10000;
    public int MaxEventHistory { get; set; } = 1000;

    public bool EnableEnergyEndpoint { get; set; } = true;
    public double EnergyEndpointThreshold { get; set; } = -40.0; // dB

    public bool EnableAdaptiveEndpoint { get; set; } = true;
}

[ExcludeFromCodeCoverage] // Simple data container class
public class EndpointResult
{
    public bool HasEndpoint { get; set; }
    public EndpointType EndpointType { get; set; }
    public double Confidence { get; set; }
    public DateTime Timestamp { get; set; }
    public TimeSpan? SilenceDuration { get; set; }
    public TimeSpan? UtteranceDuration { get; set; }
    public TimeSpan? SessionDuration { get; set; }
    public bool IsSessionTimeout { get; set; }
}

public class EndpointEvent
{
    public DateTime Timestamp { get; set; }
    public EndpointEventType Type { get; set; }
    public double Confidence { get; set; }
    public VadResult? VadResult { get; set; }
    public EndpointResult? EndpointResult { get; set; }
    public TimeSpan? UtteranceDuration { get; set; }
}

public class UtteranceStats
{
    public TimeSpan Duration { get; set; }
    public TimeSpan EndSilence { get; set; }
}

[ExcludeFromCodeCoverage] // Diagnostic data container class
public class EndpointStatistics
{
    public TimeSpan SessionDuration { get; set; }
    public double TotalSpeechDuration { get; set; }
    public int UtteranceCount { get; set; }
    public bool IsInUtterance { get; set; }
    public TimeSpan TimeSinceLastActivity { get; set; }
    public int EventHistoryCount { get; set; }
    public VadStatistics? VadStatistics { get; set; }
}

public enum EndpointEventType
{
    AudioProcessed,
    UtteranceStarted,
    UtteranceEnded,
    EndpointDetected,
    Timeout
}

// Event argument classes
[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class UtteranceStartedEventArgs : EventArgs
{
    public UtteranceStartedEventArgs(int utteranceNumber, double confidence, DateTime timestamp)
    {
        UtteranceNumber = utteranceNumber;
        Confidence = confidence;
        Timestamp = timestamp;
    }

    public int UtteranceNumber { get; }
    public double Confidence { get; }
    public DateTime Timestamp { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class UtteranceEndedEventArgs : EventArgs
{
    public UtteranceEndedEventArgs(int utteranceNumber, TimeSpan duration, EndpointType endpointType, double confidence, DateTime timestamp)
    {
        UtteranceNumber = utteranceNumber;
        Duration = duration;
        EndpointType = endpointType;
        Confidence = confidence;
        Timestamp = timestamp;
    }

    public int UtteranceNumber { get; }
    public TimeSpan Duration { get; }
    public EndpointType EndpointType { get; }
    public double Confidence { get; }
    public DateTime Timestamp { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class EndpointTriggeredEventArgs : EventArgs
{
    public EndpointTriggeredEventArgs(EndpointResult result)
    {
        Result = result;
    }

    public EndpointResult Result { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class SessionTimeoutEventArgs : EventArgs
{
    public SessionTimeoutEventArgs(TimeSpan sessionDuration)
    {
        SessionDuration = sessionDuration;
    }

    public TimeSpan SessionDuration { get; }
}

