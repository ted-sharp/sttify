using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Output;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Plugins;

namespace Sttify.Corelib.Session;

public class RecognitionSession : IDisposable
{
    private readonly AudioCapture _audioCapture;
    private ISttEngine? _sttEngine;
    private readonly IOutputSinkProvider _outputSinkProvider;
    private readonly RecognitionSessionSettings _settings;
    private readonly PluginManager? _pluginManager;
    private readonly Sttify.Corelib.Config.SettingsProvider _settingsProvider;

    public event EventHandler<SessionStateChangedEventArgs>? OnStateChanged;
    public event EventHandler<TextRecognizedEventArgs>? OnTextRecognized;
    public event EventHandler<SilenceDetectedEventArgs>? OnSilenceDetected;

    // Currently not implemented but reserved for future voice activity detection features
#pragma warning disable CS0067 // Event is declared but never used - reserved for future features
    public event EventHandler<VoiceActivityEventArgs>? OnVoiceActivity;
#pragma warning restore CS0067

    private RecognitionMode _currentMode = RecognitionMode.Ptt;
    private SessionState _currentState = SessionState.Idle;
    private readonly object _lockObject = new();

    // Silence detection state
    private DateTime _lastVoiceActivity = DateTime.MinValue;
    private bool _inSilence = false;
    private readonly Timer _silenceTimer;
    private readonly Timer _finalizeTimer;

    // Wake word detection state - reserved for future implementation
#pragma warning disable CS0414 // Field is assigned but never used - reserved for future features
    private bool _waitingForWakeWord = false;
#pragma warning restore CS0414
    private readonly List<string> _wakeWords = ["スティファイ", "sttify"];

    // Continuous mode state
    private CancellationTokenSource? _continuousModeCts;

    // PTT state - reserved for future implementation
#pragma warning disable CS0414 // Field is assigned but never used - reserved for future features
    private bool _pttPressed = false;
#pragma warning restore CS0414

    // Single utterance state
    private bool _utteranceStarted = false;

    public RecognitionSession(
        AudioCapture audioCapture,
        Sttify.Corelib.Config.SettingsProvider settingsProvider,
        IOutputSinkProvider outputSinkProvider,
        RecognitionSessionSettings settings,
        PluginManager? pluginManager = null)
    {
        System.Diagnostics.Debug.WriteLine($"*** RecognitionSession Constructor - Instance ID: {GetHashCode()} ***");
        _audioCapture = audioCapture ?? throw new ArgumentNullException(nameof(audioCapture));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _outputSinkProvider = outputSinkProvider ?? throw new ArgumentNullException(nameof(outputSinkProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _pluginManager = pluginManager;

        _audioCapture.OnFrame += OnAudioFrame;

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
            SessionState oldState;
            bool changed = false;
            lock (_lockObject)
            {
                if (_currentState != value)
                {
                    oldState = _currentState;
                    _currentState = value;
                    changed = true;
                }
                else
                {
                    oldState = _currentState;
                }
            }

            if (changed)
            {
                System.Diagnostics.Debug.WriteLine($"*** STATE CHANGE: {oldState} → {value} ***");
                OnStateChanged?.Invoke(this, new SessionStateChangedEventArgs(oldState, value));
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Debug.WriteLine($"*** RecognitionSession.StartAsync ENTRY - Current State: {CurrentState} ***");
        Telemetry.LogEvent("RecognitionSession_StartRequested", new { CurrentState = CurrentState.ToString() });

        if (CurrentState != SessionState.Idle)
        {
            System.Diagnostics.Debug.WriteLine($"*** RecognitionSession EARLY RETURN - State is {CurrentState}, not Idle ***");
            Telemetry.LogWarning("RecognitionSessionStartSkipped", $"Session already in state: {CurrentState}");
            return;
        }

        System.Diagnostics.Debug.WriteLine("*** RecognitionSession proceeding with startup ***");

        try
        {
            CurrentState = SessionState.Starting;
            Telemetry.LogEvent("RecognitionSession_StateChangedToStarting");

            // Create engine with latest settings on each start
            var appSettings = await _settingsProvider.GetSettingsAsync().ConfigureAwait(false);
            var engine = Sttify.Corelib.Engine.SttEngineFactory.CreateEngine(appSettings.Engine);
            engine.OnPartial += OnPartialRecognition;
            engine.OnFinal += OnFinalRecognition;
            _sttEngine = engine;

            System.Diagnostics.Debug.WriteLine($"*** About to call _sttEngine.StartAsync() on {_sttEngine.GetType().Name} ***");
            Telemetry.LogEvent("RecognitionSession_StartingEngine");
            // Guard against engine start hanging indefinitely
            await _sttEngine.StartAsync(cancellationToken).WithTimeout(TimeSpan.FromSeconds(10), "SttEngine.Start");
            System.Diagnostics.Debug.WriteLine($"*** _sttEngine.StartAsync() completed successfully ***");
            Telemetry.LogEvent("RecognitionSession_EngineStarted");

            var audioCaptureSettings = new AudioCaptureSettings
            {
                SampleRate = _settings.SampleRate,
                Channels = _settings.Channels,
                BufferSize = (_settings.SampleRate * _settings.Channels * 2 * _settings.BufferSizeMs) / 1000 // Convert ms to buffer size
            };

            Telemetry.LogEvent("RecognitionSession_StartingAudioCapture", new { audioCaptureSettings.SampleRate, audioCaptureSettings.Channels, audioCaptureSettings.BufferSize });
            // Guard against audio capture start hanging indefinitely
            await _audioCapture.StartAsync(audioCaptureSettings, cancellationToken).WithTimeout(TimeSpan.FromSeconds(10), "AudioCapture.Start");
            Telemetry.LogEvent("RecognitionSession_AudioCaptureStarted");

            // Initialize mode-specific behavior
            Telemetry.LogEvent("RecognitionSession_InitializingMode", new { Mode = CurrentMode.ToString() });
            await InitializeModeAsync(cancellationToken);
            Telemetry.LogEvent("RecognitionSession_ModeInitialized");

            CurrentState = SessionState.Listening;
            Telemetry.LogEvent("RecognitionSession_StateChangedToListening");

            Telemetry.LogEvent("RecognitionSessionStarted", new
            {
                Mode = CurrentMode.ToString(),
                AudioSettings = new { audioCaptureSettings.SampleRate, audioCaptureSettings.Channels }
            });
            System.Diagnostics.Debug.WriteLine("*** RecognitionSession startup completed successfully ***");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** EXCEPTION in RecognitionSession.StartAsync: {ex.GetType().Name} - {ex.Message} ***");
            System.Diagnostics.Debug.WriteLine($"*** Exception Stack Trace: {ex.StackTrace} ***");
            CurrentState = SessionState.Error;
            Telemetry.LogError("RecognitionSessionStartFailed", ex, new { Mode = CurrentMode.ToString() });
            throw;
        }
    }

    private Task InitializeModeAsync(CancellationToken cancellationToken)
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

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try
        {
            CurrentState = SessionState.Stopping;
            await _audioCapture.StopAsync().WithTimeout(TimeSpan.FromSeconds(5), "AudioCapture.Stop");
            if (_sttEngine != null)
            {
                await _sttEngine.StopAsync().WithTimeout(TimeSpan.FromSeconds(5), "SttEngine.Stop");
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("RecognitionSessionStopFailed", ex);
        }
        finally
        {
            CurrentState = SessionState.Idle;
        }
    }

    private void OnAudioFrame(object? sender, AudioFrameEventArgs e)
    {
        // Avoid taking state lock inside engine lock: read once without re-entrancy risk
        var stateSnapshot = CurrentState;
        if (stateSnapshot == SessionState.Listening && _sttEngine != null)
        {
            _sttEngine.PushAudio(e.AudioData.Span);
        }
    }

    private void OnPartialRecognition(object? sender, PartialRecognitionEventArgs e)
    {
        // Avoid heavy work or nested locks while holding engine callbacks
        System.Diagnostics.Debug.WriteLine($"*** PARTIAL RECOGNITION: '{e.Text}' (Confidence: {e.Confidence}) ***");
        OnTextRecognized?.Invoke(this, new TextRecognizedEventArgs(e.Text, false, e.Confidence));
    }

    private void OnFinalRecognition(object? sender, FinalRecognitionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"*** FINAL RECOGNITION: '{e.Text}' (Confidence: {e.Confidence}) ***");

        if (string.IsNullOrWhiteSpace(e.Text))
        {
            System.Diagnostics.Debug.WriteLine("*** FINAL RECOGNITION IGNORED - Empty text ***");
            return;
        }

        AsyncHelper.FireAndForget(async () =>
        {
            // Process text through plugins if available
            var processedText = e.Text;
            if (_pluginManager != null)
            {
                processedText = await ProcessTextThroughPluginsAsync(e.Text).ConfigureAwait(false);
            }

            OnTextRecognized?.Invoke(this, new TextRecognizedEventArgs(processedText, true, e.Confidence));

            await SendTextToOutputSinksAsync(processedText).ConfigureAwait(false);
        }, nameof(OnFinalRecognition), new { e.Text, e.Confidence });
    }

    private async Task<string> ProcessTextThroughPluginsAsync(string text)
    {
        if (_pluginManager == null)
            return text;

        var processedText = text;
        var plugins = _pluginManager.GetLoadedPlugins();

        foreach (var plugin in plugins)
        {
            try
            {
                // Only process through text processing plugins
                if (plugin.CanHandleTextProcessing())
                {
                    processedText = await plugin.ProcessTextAsync(processedText);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Plugin {plugin.Name} failed to process text: {ex.Message}");
            }
        }

        return processedText;
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
                AsyncHelper.FireAndForget(() => StopAsync(), "StopAfterSilence");
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

    private async Task SendTextToOutputSinksAsync(string text)
    {
        var sinks = _outputSinkProvider.GetSinks();
        System.Diagnostics.Debug.WriteLine($"*** SendTextToOutputSinksAsync - Text: '{text}', Sinks Count: {sinks.Count()} ***");

        bool textSentSuccessfully = false;
        var failedSinks = new List<string>();

        foreach (var sink in sinks)
        {
            System.Diagnostics.Debug.WriteLine($"*** Trying output sink: {sink.Name} ({sink.GetType().Name}) ***");
            try
            {
                bool canSend = await sink.CanSendAsync();
                System.Diagnostics.Debug.WriteLine($"*** Sink {sink.Name} CanSend: {canSend} ***");

                if (canSend)
                {
                    System.Diagnostics.Debug.WriteLine($"*** Sending text '{text}' to {sink.Name} ***");
                    await sink.SendAsync(text);
                    textSentSuccessfully = true;
                    System.Diagnostics.Debug.WriteLine($"*** Successfully sent to {sink.Name} ***");

                    Telemetry.LogEvent("TextOutputSuccessful", new
                    {
                        SinkName = sink.Name,
                        TextLength = text.Length,
                        Mode = "Session"
                    });
                    break;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"*** Sink {sink.Name} cannot send (CanSend returned false) ***");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"*** Exception in sink {sink.Name}: {ex.Message} ***");
                failedSinks.Add(sink.Name);

                Telemetry.LogError("OutputSinkFailed", ex, new
                {
                    SinkName = sink.Name,
                    TextLength = text.Length,
                    Mode = "Session" // Basic mode identifier
                });
            }
        }

        if (!textSentSuccessfully)
        {
            Telemetry.LogError("AllOutputSinksFailed",
                new InvalidOperationException("No output sinks available"),
                new
                {
                    TextLength = text.Length,
                    FailedSinks = failedSinks,
                    FailedSinkCount = failedSinks.Count
                });
        }
    }

    public void Dispose()
    {
        try
        {
            _continuousModeCts?.Cancel();
            _continuousModeCts?.Dispose();

            _silenceTimer?.Dispose();
            _finalizeTimer?.Dispose();

            // Unsubscribe events to prevent memory leaks
            _audioCapture.OnFrame -= OnAudioFrame;
            if (_sttEngine != null)
            {
                _sttEngine.OnPartial -= OnPartialRecognition;
                _sttEngine.OnFinal -= OnFinalRecognition;
            }

            StopAsync().Wait(5000); // Wait max 5 seconds
        }
        catch (Exception ex)
        {
            Telemetry.LogError("RecognitionSessionDisposeFailed", ex);
        }
        finally
        {
            _audioCapture.Dispose();
            _sttEngine?.Dispose();
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

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
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

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
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

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
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
