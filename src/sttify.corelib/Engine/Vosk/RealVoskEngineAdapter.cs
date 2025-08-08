using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using System.Text;
using System.Text.Json;
using Vosk;

namespace Sttify.Corelib.Engine.Vosk;

public class RealVoskEngineAdapter : ISttEngine, IDisposable
{
    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    private readonly VoskEngineSettings _settings;
    private global::Vosk.Model? _model;
    private global::Vosk.VoskRecognizer? _recognizer;
    private bool _isRunning;
    private readonly object _lockObject = new();
    private DateTime _recognitionStartTime;

    // Voice Activity Detection (VAD) - for forced finalization on silence
    private bool _isSpeaking = false;
    private DateTime _lastVoiceActivity = DateTime.MinValue;
    private readonly System.Timers.Timer _silenceTimer;
    private int _frameCount = 0;
    private const int SilenceThresholdMs = 800; // 800ms of silence to trigger processing
    private const double VoiceThreshold = 0.005; // Minimum voice level threshold (raised to allow silence detection)

    // Track last partial text to avoid duplicate events
    private string _currentPartialText = string.Empty;

    public RealVoskEngineAdapter(VoskEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Initialize silence timer for VAD
        _silenceTimer = new System.Timers.Timer(SilenceThresholdMs);
        _silenceTimer.Elapsed += OnSilenceDetected;
        _silenceTimer.AutoReset = false; // Only trigger once per silence period
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine is already running");

        try
        {
            await Task.Run(() => InitializeVoskModel(), cancellationToken);

            lock (_lockObject)
            {
                _isRunning = true;
                _recognitionStartTime = DateTime.UtcNow;
            }

            // Create a streaming recognizer
            CreateStreamingRecognizer();

            System.Diagnostics.Debug.WriteLine("*** Voice Activity Detection (VAD) Vosk Engine Started ***");

            Telemetry.LogEvent("VoskEngineStarted", new
            {
                ModelPath = _settings.ModelPath,
                Language = _settings.Language,
                Punctuation = _settings.Punctuation,
                VadEnabled = true
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VoskEngineStartFailed", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Failed to start Vosk engine: {ex.Message}"));
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (!_isRunning)
                return Task.CompletedTask;

            _isRunning = false;
        }

        // Stop VAD timer
        _silenceTimer?.Stop();

        // Flush any pending result
        ForceFinalizeRecognition();

        Telemetry.LogEvent("VoskEngineStopped");

        return Task.CompletedTask;
    }

    public void PushAudio(ReadOnlySpan<byte> audioData)
    {
        if (!_isRunning || audioData.IsEmpty)
            return;

        lock (_lockObject)
        {
            if (!_isRunning)
                return;

            // Calculate audio level for Voice Activity Detection
            double audioLevel = CalculateAudioLevel(audioData);
            bool hasVoice = audioLevel > VoiceThreshold;

            // Debug every 50th frame to avoid spam
            _frameCount++;
            if (_frameCount % 50 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"*** VAD: Level={audioLevel:F4}, Threshold={VoiceThreshold:F4}, HasVoice={hasVoice}, Speaking={_isSpeaking} ***");
            }

            if (hasVoice)
            {
                _lastVoiceActivity = DateTime.UtcNow;
                if (!_isSpeaking)
                {
                    _isSpeaking = true;
                    System.Diagnostics.Debug.WriteLine($"*** SPEECH STARTED - Level: {audioLevel:F4} ***");
                }
                _silenceTimer.Stop();
            }
            else if (_isSpeaking)
            {
                // Restart silence timer while in speaking mode and receiving silence
                _silenceTimer.Stop();
                _silenceTimer.Start();
            }

            // Stream audio to Vosk recognizer for partial/final results
            if (_recognizer != null)
            {
                try
                {
                    bool hasResult = _recognizer.AcceptWaveform(audioData.ToArray(), audioData.Length);
                    if (hasResult)
                    {
                        var resultJson = _recognizer.Result();
                        ProcessVoskResult(resultJson);
                        _recognitionStartTime = DateTime.UtcNow;
                        _currentPartialText = string.Empty;
                    }
                    else
                    {
                        var partialJson = _recognizer.PartialResult();
                        var partialText = ExtractPartialText(partialJson);
                        var normalizedPartial = NormalizeJapaneseSpacing(partialText);
                        if (!string.IsNullOrWhiteSpace(normalizedPartial) && !string.Equals(normalizedPartial, _currentPartialText, StringComparison.Ordinal))
                        {
                            _currentPartialText = normalizedPartial;
                            OnPartial?.Invoke(this, new PartialRecognitionEventArgs(normalizedPartial, 0.5));
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter - Error processing streaming audio: {ex.Message} ***");
                    OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error processing audio: {ex.Message}"));
                }
            }
        }
    }

    private void InitializeVoskModel()
    {
        if (string.IsNullOrEmpty(_settings.ModelPath) || !Directory.Exists(_settings.ModelPath))
        {
            throw new DirectoryNotFoundException($"Vosk model not found at: {_settings.ModelPath}");
        }

        try
        {
            // Set Vosk log level (0 = no logs, 1 = info, 2 = debug)
            global::Vosk.Vosk.SetLogLevel(0);

            // Load Vosk model only - recognizer will be created for streaming
            _model = new global::Vosk.Model(_settings.ModelPath);
            System.Diagnostics.Debug.WriteLine($"*** Vosk Model loaded from: {_settings.ModelPath} ***");

            Telemetry.LogEvent("VoskModelLoaded", new
            {
                ModelPath = _settings.ModelPath,
                ModelSize = GetDirectorySize(_settings.ModelPath)
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize Vosk model: {ex.Message}", ex);
        }
    }

    private double CalculateAudioLevel(ReadOnlySpan<byte> audioData)
    {
        if (audioData.Length < 2)
            return 0.0;

        // Calculate RMS (Root Mean Square) for 16-bit audio
        double sum = 0.0;
        int sampleCount = audioData.Length / 2; // 16-bit samples

        for (int i = 0; i < audioData.Length - 1; i += 2)
        {
            // Convert little-endian bytes to 16-bit signed integer
            short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
            sum += sample * sample;
        }

        return Math.Sqrt(sum / sampleCount) / 32768.0; // Normalize to 0.0-1.0 range
    }

    private void OnSilenceDetected(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_isRunning)
            return;

        System.Diagnostics.Debug.WriteLine($"*** SILENCE DETECTED - Forcing finalization (Timer triggered after {SilenceThresholdMs}ms) ***");

        // Force finalize current utterance
        ForceFinalizeRecognition();

        // Reset speech state
        lock (_lockObject)
        {
            _isSpeaking = false;
        }
    }

    private void ForceFinalizeRecognition()
    {
        try
        {
            if (_recognizer == null)
                return;

            var jsonResult = _recognizer.FinalResult();
            System.Diagnostics.Debug.WriteLine($"*** Vosk FinalResult (forced): {jsonResult} ***");
            ProcessVoskResult(jsonResult);

            // Recreate recognizer for next utterance to be safe after FinalResult
            CreateStreamingRecognizer();
            _recognitionStartTime = DateTime.UtcNow;
            _currentPartialText = string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** ForceFinalizeRecognition Error: {ex.Message} ***");
            Telemetry.LogError("ForceFinalizeRecognitionError", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error finalizing recognition: {ex.Message}"));
        }
    }

    private void ProcessVoskResult(string jsonResult)
    {
        try
        {
            if (string.IsNullOrEmpty(jsonResult))
                return;

            // Parse JSON result to extract text (like reference code)
            var jsonDoc = JsonDocument.Parse(jsonResult);
            if (jsonDoc.RootElement.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    // Apply punctuation if enabled
                    if (_settings.Punctuation)
                    {
                        text = ApplyPunctuation(text);
                    }

                    // Normalize spacing for Japanese output
                    text = NormalizeJapaneseSpacing(text);

                    var confidence = 0.95; // streaming Vosk confidence heuristic
                    var duration = DateTime.UtcNow - _recognitionStartTime;

                    System.Diagnostics.Debug.WriteLine($"*** FINAL RECOGNITION: '{text}' ***");

                    // Fire final recognition event
                    OnFinal?.Invoke(this, new FinalRecognitionEventArgs(text, confidence, duration));

                    _recognitionStartTime = DateTime.UtcNow;
                }
            }
        }
        catch (JsonException ex)
        {
            Telemetry.LogError("VoskResultParsingError", ex, new { JsonResult = jsonResult });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VoskResultProcessingError", ex);
        }
    }


    private string ApplyPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Simple punctuation rules for Japanese
        var punctuatedText = text;

        // Add period at the end if not present
        if (!punctuatedText.EndsWith("。") && !punctuatedText.EndsWith("？") && !punctuatedText.EndsWith("！"))
        {
            punctuatedText += "。";
        }

        return punctuatedText;
    }

    private long GetDirectorySize(string directoryPath)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            return directoryInfo.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        try
        {
            // Avoid potential deadlock by running stop without capturing context and with a short timeout
            Task.Run(async () => await StopAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(3));
        }
        catch { }

        _silenceTimer?.Stop();
        _silenceTimer?.Dispose();
        _recognizer?.Dispose();
        _model?.Dispose();
    }

    private void CreateStreamingRecognizer()
    {
        if (_model == null)
            return;

        try
        {
            _recognizer?.Dispose();
            var sampleRate = _settings.SampleRate > 0 ? _settings.SampleRate : 16000;
            _recognizer = new global::Vosk.VoskRecognizer(_model, sampleRate);
            _recognizer.SetMaxAlternatives(0);
            _recognizer.SetWords(true);
            if (_settings.Punctuation)
            {
                // Vosk doesn't add punctuation automatically for all models; this flag is kept for symmetry
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VoskRecognizerCreateFailed", ex);
            throw;
        }
    }

    private static bool IsJapaneseChar(int codePoint)
    {
        // Hiragana: 3040–309F, Katakana: 30A0–30FF, Katakana Phonetic Extensions: 31F0–31FF
        // CJK Unified Ideographs: 4E00–9FFF, Halfwidth Katakana: FF61–FF9F, prolonged sound mark: 30FC/FF70
        return (codePoint >= 0x3040 && codePoint <= 0x30FF) ||
               (codePoint >= 0x31F0 && codePoint <= 0x31FF) ||
               (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||
               (codePoint >= 0xFF61 && codePoint <= 0xFF9F) ||
               codePoint == 0x30FC || codePoint == 0xFF70;
    }

    private static string NormalizeJapaneseSpacing(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // Remove ASCII spaces between two Japanese chars only. Keep spaces elsewhere.
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == ' ')
            {
                // Look at previous and next visible characters
                int prev = i - 1;
                while (prev >= 0 && char.IsWhiteSpace(input[prev])) prev--;
                int next = i + 1;
                while (next < input.Length && char.IsWhiteSpace(input[next])) next++;

                if (prev >= 0 && next < input.Length)
                {
                    int prevCp = char.ConvertToUtf32(input, prev);
                    int nextCp = char.ConvertToUtf32(input, next);
                    if (IsJapaneseChar(prevCp) && IsJapaneseChar(nextCp))
                    {
                        // skip this space
                        continue;
                    }
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private string ExtractPartialText(string partialJson)
    {
        try
        {
            if (string.IsNullOrEmpty(partialJson)) return string.Empty;
            using var doc = JsonDocument.Parse(partialJson);
            if (doc.RootElement.TryGetProperty("partial", out var p))
            {
                return p.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }
        return string.Empty;
    }
}
