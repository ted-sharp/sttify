using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
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
    private bool _isRunning;
    private readonly object _lockObject = new();
    private DateTime _recognitionStartTime;
    
    // Voice Activity Detection (VAD) - Speech boundary detection
    private readonly List<byte> _audioBuffer = new();
    private bool _isSpeaking = false;
    private DateTime _lastVoiceActivity = DateTime.MinValue;
    private readonly System.Timers.Timer _silenceTimer;
    private int _frameCount = 0;
    private const int SilenceThresholdMs = 800; // 800ms of silence to trigger processing
    private const double VoiceThreshold = 0.005; // Minimum voice level threshold (raised to allow silence detection)

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

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
        }
        
        // Stop VAD timer
        _silenceTimer?.Stop();
        
        // Process any remaining audio data
        if (_audioBuffer.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"*** Processing remaining {_audioBuffer.Count} bytes on stop ***");
            await ProcessBufferedAudio();
        }
        
        Telemetry.LogEvent("VoskEngineStopped");
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
                // Voice detected - update activity timestamp
                _lastVoiceActivity = DateTime.UtcNow;
                
                if (!_isSpeaking)
                {
                    // Start of speech detected
                    _isSpeaking = true;
                    _audioBuffer.Clear(); // Start fresh buffer
                    System.Diagnostics.Debug.WriteLine($"*** SPEECH STARTED - Level: {audioLevel:F4} ***");
                }
                
                // Add audio data to buffer
                _audioBuffer.AddRange(audioData.ToArray());
                
                // Stop silence timer (we have voice)
                _silenceTimer.Stop();
            }
            else if (_isSpeaking)
            {
                // Still in speech mode but current frame is silent
                // Add to buffer and start/restart silence timer
                _audioBuffer.AddRange(audioData.ToArray());
                
                // Restart silence timer
                _silenceTimer.Stop();
                _silenceTimer.Start();
                
                if (_frameCount % 50 == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"*** VAD: Silent frame during speech - Buffer size: {_audioBuffer.Count} bytes ***");
                }
            }
            else
            {
                // Not speaking and no voice - ignore audio but log occasionally
                if (_frameCount % 100 == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"*** VAD: Background silence - Level: {audioLevel:F4} ***");
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

            // Load Vosk model only - VoskRecognizer will be created per speech recognition event
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

    private async void OnSilenceDetected(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_isRunning)
            return;

        System.Diagnostics.Debug.WriteLine($"*** SILENCE DETECTED - Processing {_audioBuffer.Count} bytes with Vosk (Timer triggered after {SilenceThresholdMs}ms) ***");
        
        // Process the buffered audio
        await ProcessBufferedAudio();
        
        // Reset speech state
        lock (_lockObject)
        {
            _isSpeaking = false;
            _audioBuffer.Clear();
        }
    }

    private async Task ProcessBufferedAudio()
    {
        if (_model == null || _audioBuffer.Count == 0)
            return;

        await Task.Run(() =>
        {
            try
            {
                // Create new VoskRecognizer for this speech segment (like reference code)
                using var voskRecognizer = new global::Vosk.VoskRecognizer(_model, 16000.0f);
                voskRecognizer.SetMaxAlternatives(0);

                // Process audio data in chunks (like reference code)
                byte[] audioArray = _audioBuffer.ToArray();
                int chunkSize = 4096;
                
                for (int offset = 0; offset < audioArray.Length; offset += chunkSize)
                {
                    int currentChunkSize = Math.Min(chunkSize, audioArray.Length - offset);
                    byte[] chunk = new byte[currentChunkSize];
                    Array.Copy(audioArray, offset, chunk, 0, currentChunkSize);
                    
                    voskRecognizer.AcceptWaveform(chunk, currentChunkSize);
                }

                // Get final result from Vosk (like reference code)
                string jsonResult = voskRecognizer.FinalResult();
                System.Diagnostics.Debug.WriteLine($"*** Vosk FinalResult: {jsonResult} ***");

                // Process the result
                ProcessVoskResult(jsonResult);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"*** ProcessBufferedAudio Error: {ex.Message} ***");
                Telemetry.LogError("ProcessBufferedAudioError", ex);
                OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error processing buffered audio: {ex.Message}"));
            }
        });
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

                    var confidence = 0.95; // VAD + Vosk combination has high confidence
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
        StopAsync().Wait();
        
        _silenceTimer?.Stop();
        _silenceTimer?.Dispose();
        _model?.Dispose();
    }

}