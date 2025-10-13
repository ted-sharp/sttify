using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Output;
using Sttify.Corelib.Plugins;

namespace Sttify.Corelib.Session;

public class RecognitionSession : IDisposable
{
    private readonly AudioCapture _audioCapture;
    private readonly EndpointDetector _endpointDetector;
    private readonly object _lockObject = new();
    private readonly IOutputSinkProvider _outputSinkProvider;
    private readonly PluginManager? _pluginManager;
    private readonly RecognitionSessionSettings _settings;
    private readonly Config.SettingsProvider _settingsProvider;
    private readonly List<string> _wakeWords = ["スティファイ", "sttify"];

    // Continuous mode state
    private CancellationTokenSource? _continuousModeCts;

    // Voice activity detection events
    // OnVoiceActivity event removed - not used

    private RecognitionMode _currentMode = RecognitionMode.Ptt;
    private SessionState _currentState = SessionState.Idle;

    // PTT state
    private ISttEngine? _sttEngine;

    // Silence detection timers removed; handled by engine/VAD components

    // Wake word detection state

    // Single utterance state removed (handled by endpoint detector callbacks)

    public RecognitionSession(
        AudioCapture audioCapture,
        Config.SettingsProvider settingsProvider,
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

        // Initialize endpoint detector using session settings
        _endpointDetector = new EndpointDetector(new EndpointSettings
        {
            SilenceTimeoutMs = _settings.EndpointSilenceMs
        });
        _endpointDetector.OnEndpointTriggered += OnEndpointTriggered;
        _endpointDetector.OnUtteranceStarted += (_, __) =>
        {
            Telemetry.LogEvent("SessionUtteranceStarted");
        };
        _endpointDetector.OnUtteranceEnded += (_, e) =>
        {
            Telemetry.LogEvent("SessionUtteranceEnded", new { e.Duration, e.EndpointType, e.Confidence });
        };

        // No session-level silence/finalize timers

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

                    // Reset state when mode changes
                    if (value == RecognitionMode.Ptt)
                    {
                        IsPttPressed = false;
                    }
                    else if (value == RecognitionMode.WakeWord)
                    {
                        IsWaitingForWakeWord = true;
                    }

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

    /// <summary>
    /// Check if PTT is currently pressed
    /// </summary>
    public bool IsPttPressed { get; private set; } = false;

    /// <summary>
    /// Check if currently waiting for wake word
    /// </summary>
    public bool IsWaitingForWakeWord { get; private set; } = false;

    public void Dispose()
    {
        try
        {
            _continuousModeCts?.Cancel();
            _continuousModeCts?.Dispose();

            // Dispose endpoint detector
            _endpointDetector?.Dispose();

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

    public event EventHandler<SessionStateChangedEventArgs>? OnStateChanged;
    public event EventHandler<TextRecognizedEventArgs>? OnTextRecognized;

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
            // Dispose any existing engine to avoid leaks between restarts
            if (_sttEngine != null)
            {
                try
                {
                    _sttEngine.OnPartial -= OnPartialRecognition;
                    _sttEngine.OnFinal -= OnFinalRecognition;
                    _sttEngine.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Telemetry.LogError("RecognitionSession_PreviousEngineDisposeFailed", disposeEx);
                }
                finally
                {
                    _sttEngine = null;
                }
            }

            var appSettings = await _settingsProvider.GetSettingsAsync().ConfigureAwait(false);
            var engine = SttEngineFactory.CreateEngine(appSettings.Engine);
            engine.OnPartial += OnPartialRecognition;
            engine.OnFinal += OnFinalRecognition;
            _sttEngine = engine;

            System.Diagnostics.Debug.WriteLine($"*** About to call _sttEngine.StartAsync() on {_sttEngine.GetType().Name} ***");
            Telemetry.LogEvent("RecognitionSession_StartingEngine");
            // Guard against engine start hanging indefinitely
            await _sttEngine.StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10));
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
            await _audioCapture.StartAsync(audioCaptureSettings, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10));
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
                IsWaitingForWakeWord = true;
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
            await _audioCapture.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (_sttEngine != null)
            {
                await _sttEngine.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
        // Feed endpoint detector for boundary detection
        _endpointDetector.ProcessAudioFrame(e.AudioData.Span, _settings.SampleRate, _settings.Channels);
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

    // Removed unused silence/finalize timers; engine-side VAD handles boundaries

    /// <summary>
    /// Start PTT (Push-to-Talk) mode
    /// </summary>
    public void StartPtt()
    {
        if (_currentMode == RecognitionMode.Ptt)
        {
            IsPttPressed = true;
            // Additional PTT logic can be added here
        }
    }

    /// <summary>
    /// Stop PTT (Push-to-Talk) mode
    /// </summary>
    public void StopPtt()
    {
        if (_currentMode == RecognitionMode.Ptt)
        {
            IsPttPressed = false;
            // Additional PTT logic can be added here
        }
    }

    /// <summary>
    /// Check if wake word is detected in the given text
    /// </summary>
    public bool IsWakeWordDetected(string text)
    {
        if (!IsWaitingForWakeWord || string.IsNullOrEmpty(text))
            return false;

        foreach (var wakeWord in _wakeWords)
        {
            if (text.Contains(wakeWord, StringComparison.OrdinalIgnoreCase))
            {
                IsWaitingForWakeWord = false;
                return true;
            }
        }
        return false;
    }

    // Wake word detection
    private bool DetectWakeWord(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

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

    private void OnEndpointTriggered(object? sender, EndpointTriggeredEventArgs e)
    {
        Telemetry.LogEvent("RecognitionSession_EndpointTriggered", new
        {
            e.Result.EndpointType,
            e.Result.Confidence,
            e.Result.SilenceDuration,
            e.Result.UtteranceDuration
        });

        if (CurrentMode == RecognitionMode.SingleUtterance)
        {
            // End session on first endpoint in single-utterance mode
            AsyncHelper.FireAndForget(() => StopAsync(), nameof(OnEndpointTriggered));
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
    public SessionStateChangedEventArgs(SessionState oldState, SessionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public SessionState OldState { get; }
    public SessionState NewState { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class TextRecognizedEventArgs : EventArgs
{
    public TextRecognizedEventArgs(string text, bool isFinal, double confidence)
    {
        Text = text;
        IsFinal = isFinal;
        Confidence = confidence;
    }

    public string Text { get; }
    public bool IsFinal { get; }
    public double Confidence { get; }
}
