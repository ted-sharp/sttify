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
    private global::Vosk.VoskRecognizer? _recognizer;
    private bool _isRunning;
    private readonly Queue<byte[]> _audioQueue = new();
    private readonly object _lockObject = new();
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;
    private DateTime _recognitionStartTime;
    private string _currentPartialText = "";

    public RealVoskEngineAdapter(VoskEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine is already running");

        try
        {
            await Task.Run(() => InitializeVosk(), cancellationToken);

            lock (_lockObject)
            {
                _isRunning = true;
                _processingCancellation = new CancellationTokenSource();
                _recognitionStartTime = DateTime.UtcNow;
            }

            _processingTask = Task.Run(() => ProcessAudioLoop(_processingCancellation.Token), cancellationToken);
            
            Telemetry.LogEvent("VoskEngineStarted", new
            {
                ModelPath = _settings.ModelPath,
                Language = _settings.Language,
                Punctuation = _settings.Punctuation
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
            _processingCancellation?.Cancel();
        }

        if (_processingTask != null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Finalize any remaining recognition
        if (_recognizer != null && !string.IsNullOrEmpty(_currentPartialText))
        {
            try
            {
                var finalResult = _recognizer.FinalResult();
                ProcessRecognitionResult(finalResult, true);
            }
            catch (Exception ex)
            {
                Telemetry.LogError("VoskFinalizationFailed", ex);
            }
        }

        _processingCancellation?.Dispose();
        _processingCancellation = null;
        _processingTask = null;
        _currentPartialText = "";

        Telemetry.LogEvent("VoskEngineStopped");
    }

    public void PushAudio(ReadOnlySpan<byte> audioData)
    {
        if (!_isRunning || audioData.IsEmpty)
            return;

        var buffer = audioData.ToArray();
        lock (_audioQueue)
        {
            _audioQueue.Enqueue(buffer);
            
            // Prevent queue from growing too large
            while (_audioQueue.Count > 100)
            {
                _audioQueue.Dequeue();
            }
        }
    }

    private void InitializeVosk()
    {
        if (string.IsNullOrEmpty(_settings.ModelPath) || !Directory.Exists(_settings.ModelPath))
        {
            throw new DirectoryNotFoundException($"Vosk model not found at: {_settings.ModelPath}");
        }

        try
        {
            // Set Vosk log level (0 = no logs, 1 = info, 2 = debug)
            global::Vosk.Vosk.SetLogLevel(0);

            _model = new global::Vosk.Model(_settings.ModelPath);
            
            // Create recognizer with 16kHz sample rate (standard for speech recognition)
            _recognizer = new global::Vosk.VoskRecognizer(_model, 16000);
            
            // Configure recognizer settings
            if (!string.IsNullOrEmpty(_settings.Language))
            {
                // Note: Language setting may not be directly available in C# Vosk binding
                // The model itself determines the language
            }

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

    private async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                byte[]? audioChunk = null;
                
                lock (_audioQueue)
                {
                    if (_audioQueue.Count > 0)
                    {
                        audioChunk = _audioQueue.Dequeue();
                    }
                }

                if (audioChunk != null && _recognizer != null)
                {
                    try
                    {
                        // Process audio data through Vosk
                        bool hasResult = _recognizer.AcceptWaveform(audioChunk, audioChunk.Length);
                        
                        if (hasResult)
                        {
                            // Final result available
                            var result = _recognizer.Result();
                            ProcessRecognitionResult(result, true);
                        }
                        else
                        {
                            // Partial result available
                            var partialResult = _recognizer.PartialResult();
                            ProcessRecognitionResult(partialResult, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Telemetry.LogError("VoskProcessingError", ex);
                        OnError?.Invoke(this, new SttErrorEventArgs(ex, "Error processing audio with Vosk"));
                    }
                }

                await Task.Delay(10, cancellationToken); // Small delay to prevent tight loop
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VoskProcessingLoopError", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in Vosk processing loop: {ex.Message}"));
        }
    }

    private void ProcessRecognitionResult(string jsonResult, bool isFinal)
    {
        try
        {
            if (string.IsNullOrEmpty(jsonResult))
                return;

            var result = JsonSerializer.Deserialize<VoskResult>(jsonResult);
            if (result == null)
                return;

            var text = result.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            // Apply punctuation if enabled
            if (_settings.Punctuation && isFinal)
            {
                text = ApplyPunctuation(text);
            }

            var confidence = result.Confidence ?? 0.0;

            if (isFinal)
            {
                var duration = DateTime.UtcNow - _recognitionStartTime;
                OnFinal?.Invoke(this, new FinalRecognitionEventArgs(text, confidence, duration));
                _currentPartialText = "";
                _recognitionStartTime = DateTime.UtcNow;
            }
            else
            {
                // Only fire partial events if text has changed significantly
                if (!string.Equals(_currentPartialText, text, StringComparison.OrdinalIgnoreCase))
                {
                    _currentPartialText = text;
                    OnPartial?.Invoke(this, new PartialRecognitionEventArgs(text, confidence));
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
        _recognizer?.Dispose();
        _model?.Dispose();
    }

    private class VoskResult
    {
        public string? Text { get; set; }
        public double? Confidence { get; set; }
    }
}