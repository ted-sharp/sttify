using Sttify.Corelib.Audio;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Output;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Session;

public class RecognitionSession : IDisposable
{
    private readonly AudioCapture _audioCapture;
    private readonly ISttEngine _sttEngine;
    private readonly IEnumerable<ITextOutputSink> _outputSinks;
    private readonly RecognitionSessionSettings _settings;

    public event EventHandler<SessionStateChangedEventArgs>? OnStateChanged;
    public event EventHandler<TextRecognizedEventArgs>? OnTextRecognized;
    public event EventHandler<SilenceDetectedEventArgs>? OnSilenceDetected;
    public event EventHandler<VoiceActivityEventArgs>? OnVoiceActivity;

    private RecognitionMode _currentMode = RecognitionMode.Ptt;
    private SessionState _currentState = SessionState.Idle;
    private readonly object _lockObject = new();
    
    // Silence detection state
    private DateTime _lastVoiceActivity = DateTime.MinValue;
    private bool _inSilence = false;
    private readonly Timer _silenceTimer;
    private readonly Timer _finalizeTimer;
    
    // Wake word detection state
    private bool _waitingForWakeWord = false;
    private readonly List<string> _wakeWords = ["スティファイ", "sttify"];
    
    // Continuous mode state
    private CancellationTokenSource? _continuousModeCts;
    
    // PTT state
    private bool _pttPressed = false;
    
    // Single utterance state  
    private bool _utteranceStarted = false;

    public RecognitionSession(
        AudioCapture audioCapture,
        ISttEngine sttEngine,
        IEnumerable<ITextOutputSink> outputSinks,
        RecognitionSessionSettings settings)
    {
        _audioCapture = audioCapture ?? throw new ArgumentNullException(nameof(audioCapture));
        _sttEngine = sttEngine ?? throw new ArgumentNullException(nameof(sttEngine));
        _outputSinks = outputSinks ?? throw new ArgumentNullException(nameof(outputSinks));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _audioCapture.OnFrame += OnAudioFrame;
        _sttEngine.OnPartial += OnPartialRecognition;
        _sttEngine.OnFinal += OnFinalRecognition;
        
        // Initialize timers
        _silenceTimer = new Timer(OnSilenceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _finalizeTimer = new Timer(OnFinalizeTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        
        // Add wake words from settings if provided
        if (_settings.WakeWords?.Length > 0)
        {
            _wakeWords.AddRange(_settings.WakeWords);
        }
        
        Telemetry.LogEvent("RecognitionSessionCreated", new 
        { 
            Mode = _currentMode.ToString(),
            EndpointSilenceMs = _settings.EndpointSilenceMs,
            WakeWordsCount = _wakeWords.Count
        });
    }

    public RecognitionMode CurrentMode
    {
        get { lock (_lockObject) { return _currentMode; } }
        set 
        { 
            lock (_lockObject) 
            { 
                if (_currentMode != value)
                {
                    var oldMode = _currentMode;
                    _currentMode = value;
                    OnModeChanged(oldMode, value);
                }
            } 
        }
    }

    public SessionState CurrentState
    {
        get { lock (_lockObject) { return _currentState; } }
        private set
        {
            lock (_lockObject)
            {
                if (_currentState != value)
                {
                    var oldState = _currentState;
                    _currentState = value;
                    OnStateChanged?.Invoke(this, new SessionStateChangedEventArgs(oldState, value));
                }
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState != SessionState.Idle)
        {
            Telemetry.LogWarning("RecognitionSessionStartSkipped", $"Session already in state: {CurrentState}");
            return;
        }

        CurrentState = SessionState.Starting;
        
        try
        {
            await _sttEngine.StartAsync(cancellationToken);
            
            var audioCaptureSettings = new AudioCaptureSettings
            {
                SampleRate = _settings.SampleRate,
                Channels = _settings.Channels,
                BufferSize = (_settings.SampleRate * _settings.Channels * 2 * _settings.BufferSizeMs) / 1000 // Convert ms to buffer size
            };
            
            await _audioCapture.StartAsync(audioCaptureSettings, cancellationToken);
            
            // Initialize mode-specific behavior
            await InitializeModeAsync(cancellationToken);
            
            CurrentState = SessionState.Listening;
            
            Telemetry.LogEvent("RecognitionSessionStarted", new 
            { 
                Mode = CurrentMode.ToString(),
                AudioSettings = new { audioCaptureSettings.SampleRate, audioCaptureSettings.Channels }
            });
        }
        catch (Exception ex)
        {
            CurrentState = SessionState.Error;
            Telemetry.LogError("RecognitionSessionStartFailed", ex, new { Mode = CurrentMode.ToString() });
            throw;
        }
    }

    private async Task InitializeModeAsync(CancellationToken cancellationToken)
    {
        switch (CurrentMode)
        {
            case RecognitionMode.Continuous:
                _continuousModeCts = new CancellationTokenSource();
                // Start continuous recognition immediately
                break;
                
            case RecognitionMode.WakeWord:
                _waitingForWakeWord = true;
                Telemetry.LogEvent("WakeWordModeStarted", new { WakeWords = _wakeWords });
                break;
                
            case RecognitionMode.Ptt:
                // PTT waits for manual activation
                break;
                
            case RecognitionMode.SingleUtterance:
                // Single utterance starts immediately but stops after first result
                break;
        }
    }

    public async Task StopAsync()
    {
        CurrentState = SessionState.Stopping;
        
        await _audioCapture.StopAsync();
        await _sttEngine.StopAsync();
        
        CurrentState = SessionState.Idle;
    }

    private void OnAudioFrame(object? sender, AudioFrameEventArgs e)
    {
        if (CurrentState == SessionState.Listening)
        {
            _sttEngine.PushAudio(e.AudioData.Span);
        }
    }

    private void OnPartialRecognition(object? sender, PartialRecognitionEventArgs e)
    {
        OnTextRecognized?.Invoke(this, new TextRecognizedEventArgs(e.Text, false, e.Confidence));
    }

    private async void OnFinalRecognition(object? sender, FinalRecognitionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text))
            return;

        OnTextRecognized?.Invoke(this, new TextRecognizedEventArgs(e.Text, true, e.Confidence));

        foreach (var sink in _outputSinks)
        {
            try
            {
                if (await sink.CanSendAsync())
                {
                    await sink.SendAsync(e.Text);
                    break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send text via {sink.Name}: {ex.Message}");
            }
        }
    }

    // Add the missing methods that are referenced in the enhanced code
    private void OnModeChanged(RecognitionMode oldMode, RecognitionMode newMode)
    {
        Telemetry.LogEvent("RecognitionModeChanged", new 
        { 
            OldMode = oldMode.ToString(), 
            NewMode = newMode.ToString() 
        });
    }

    private void OnSilenceTimerElapsed(object? state)
    {
        if (!_inSilence)
        {
            _inSilence = true;
            var silenceDuration = DateTime.UtcNow - _lastVoiceActivity;
            
            OnSilenceDetected?.Invoke(this, new SilenceDetectedEventArgs(silenceDuration));
            
            // Handle silence based on current mode
            if (CurrentMode == RecognitionMode.SingleUtterance && _utteranceStarted)
            {
                // End single utterance on silence
                _ = Task.Run(async () => await StopAsync());
            }
        }
    }

    private void OnFinalizeTimerElapsed(object? state)
    {
        // Finalize any pending recognition
        Telemetry.LogEvent("RecognitionFinalized", new { Mode = CurrentMode.ToString() });
    }

    // PTT control methods
    public void StartPtt()
    {
        if (CurrentMode != RecognitionMode.Ptt) return;
        
        _pttPressed = true;
        Telemetry.LogEvent("PttPressed");
    }

    public void StopPtt()
    {
        if (CurrentMode != RecognitionMode.Ptt) return;
        
        _pttPressed = false;
        Telemetry.LogEvent("PttReleased");
    }

    // Voice activity detection
    private bool DetectVoiceActivity(ReadOnlySpan<byte> audioData)
    {
        if (audioData.Length == 0) return false;

        // Simple RMS calculation for voice activity detection
        double sum = 0;
        for (int i = 0; i < audioData.Length; i += 2) // Assuming 16-bit samples
        {
            if (i + 1 < audioData.Length)
            {
                short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
                sum += sample * sample;
            }
        }

        var rms = Math.Sqrt(sum / (audioData.Length / 2));
        var normalizedRms = rms / 32768.0; // Normalize to 0-1 range

        return normalizedRms > _settings.VoiceActivityThreshold;
    }

    // Wake word detection
    private bool DetectWakeWord(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var lowerText = text.ToLowerInvariant();
        
        foreach (var wakeWord in _wakeWords)
        {
            if (lowerText.Contains(wakeWord.ToLowerInvariant()))
            {
                Telemetry.LogEvent("WakeWordDetected", new { WakeWord = wakeWord, Text = text });
                return true;
            }
        }
        
        return false;
    }

    public void Dispose()
    {
        try
        {
            _continuousModeCts?.Cancel();
            _continuousModeCts?.Dispose();
            
            _silenceTimer?.Dispose();
            _finalizeTimer?.Dispose();
            
            StopAsync().Wait(5000); // Wait max 5 seconds
        }
        catch (Exception ex)
        {
            Telemetry.LogError("RecognitionSessionDisposeFailed", ex);
        }
        finally
        {
            _audioCapture.Dispose();
        }
    }
}

public enum RecognitionMode
{
    Ptt,
    SingleUtterance,
    Continuous,
    WakeWord
}

public enum SessionState
{
    Idle,
    Starting,
    Listening,
    Processing,
    Stopping,
    Error
}

public class RecognitionSessionSettings
{
    public TimeSpan FinalizeTimeoutMs { get; set; } = TimeSpan.FromMilliseconds(1500);
    public string Delimiter { get; set; } = "。";
    public int EndpointSilenceMs { get; set; } = 800;
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BufferSizeMs { get; set; } = 100;
    public string[] WakeWords { get; set; } = [];
    public double VoiceActivityThreshold { get; set; } = 0.01; // Audio level threshold for voice detection
    public int MinUtteranceLengthMs { get; set; } = 500; // Minimum utterance length
}

public class SessionStateChangedEventArgs : EventArgs
{
    public SessionState OldState { get; }
    public SessionState NewState { get; }

    public SessionStateChangedEventArgs(SessionState oldState, SessionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

public class TextRecognizedEventArgs : EventArgs
{
    public string Text { get; }
    public bool IsFinal { get; }
    public double Confidence { get; }

    public TextRecognizedEventArgs(string text, bool isFinal, double confidence)
    {
        Text = text;
        IsFinal = isFinal;
        Confidence = confidence;
    }
}