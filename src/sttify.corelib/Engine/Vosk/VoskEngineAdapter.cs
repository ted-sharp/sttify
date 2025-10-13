using System.Text.Json;
using Sttify.Corelib.Config;
using Vosk;

namespace Sttify.Corelib.Engine.Vosk;

public class VoskEngineAdapter : ISttEngine
{
    private readonly Queue<byte[]> _audioQueue = new();
    private readonly object _lockObject = new();

    private readonly VoskEngineSettings _settings;
    private bool _isRunning;
    private Model? _model;
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;

    // Vosk objects
    private VoskRecognizer? _recognizer;

    public VoskEngineAdapter(VoskEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter Constructor - Instance ID: {GetHashCode()} ***");
    }

    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine is already running");

        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter - Starting with ModelPath: '{_settings.ModelPath}' ***");

        // Validate model path
        if (string.IsNullOrEmpty(_settings.ModelPath) || !Directory.Exists(_settings.ModelPath))
        {
            var error = $"Vosk model path is invalid or does not exist: '{_settings.ModelPath}'";
            System.Diagnostics.Debug.WriteLine($"*** {error} ***");
            OnError?.Invoke(this, new SttErrorEventArgs(new DirectoryNotFoundException(error), error));
            return;
        }

        try
        {
            // Initialize Vosk model
            System.Diagnostics.Debug.WriteLine("*** Loading Vosk model... ***");
            _model = new Model(_settings.ModelPath);

            // Create recognizer with sample rate from settings
            System.Diagnostics.Debug.WriteLine($"*** Creating Vosk recognizer with sample rate: {_settings.SampleRate} ***");
            _recognizer = new VoskRecognizer(_model, _settings.SampleRate);

            // Enable phrase list if configured
            if (!string.IsNullOrEmpty(_settings.Grammar))
            {
                _recognizer.SetWords(true);
                System.Diagnostics.Debug.WriteLine("*** Vosk word recognition enabled ***");
            }

            lock (_lockObject)
            {
                _isRunning = true;
                _processingCancellation = new CancellationTokenSource();
            }

            _processingTask = Task.Run(() => ProcessAudioLoop(_processingCancellation.Token), cancellationToken);

            System.Diagnostics.Debug.WriteLine("*** VoskEngineAdapter - Started successfully ***");
            await Task.Delay(100, cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter StartAsync failed: {ex.Message} ***");
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

        // Cleanup Vosk objects
        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;

        _processingCancellation?.Dispose();
        _processingCancellation = null;
        _processingTask = null;

        System.Diagnostics.Debug.WriteLine("*** VoskEngineAdapter - Stopped successfully ***");
    }

    public void PushAudio(ReadOnlySpan<byte> audioData)
    {
        if (!_isRunning)
            return;

        var buffer = audioData.ToArray();
        lock (_audioQueue)
        {
            _audioQueue.Enqueue(buffer);
            if (_audioQueue.Count == 1) // Only log first audio push to avoid spam
            {
                System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter (Mock) - First audio received: {audioData.Length} bytes ***");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter Dispose error: {ex.Message} ***");
        }
        finally
        {
            _recognizer?.Dispose();
            _model?.Dispose();
        }
    }

    private async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _recognizer != null)
            {
                byte[]? audioChunk = null;

                lock (_audioQueue)
                {
                    if (_audioQueue.Count > 0)
                    {
                        audioChunk = _audioQueue.Dequeue();
                    }
                }

                if (audioChunk != null)
                {
                    try
                    {
                        // Process audio through Vosk
                        bool hasMoreData = _recognizer.AcceptWaveform(audioChunk, audioChunk.Length);

                        if (hasMoreData)
                        {
                            // Final result available
                            var result = _recognizer.Result();
                            var parsedResult = ParseVoskResult(result);

                            if (!string.IsNullOrWhiteSpace(parsedResult.Text))
                            {
                                System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter - FINAL recognition: '{parsedResult.Text}' ***");
                                var duration = DateTime.UtcNow - startTime;
                                OnFinal?.Invoke(this, new FinalRecognitionEventArgs(parsedResult.Text, parsedResult.Confidence, duration));
                                startTime = DateTime.UtcNow;
                            }
                        }
                        else
                        {
                            // Partial result available
                            var partialResult = _recognizer.PartialResult();
                            var parsedPartial = ParseVoskPartialResult(partialResult);

                            if (!string.IsNullOrWhiteSpace(parsedPartial.Text))
                            {
                                System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter - PARTIAL recognition: '{parsedPartial.Text}' ***");
                                OnPartial?.Invoke(this, new PartialRecognitionEventArgs(parsedPartial.Text, parsedPartial.Confidence));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter - Error processing audio chunk: {ex.Message} ***");
                        OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error processing audio: {ex.Message}"));
                    }
                }

                await Task.Delay(10, cancellationToken); // Shorter delay for better responsiveness
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("*** VoskEngineAdapter - ProcessAudioLoop cancelled ***");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter - ProcessAudioLoop error: {ex.Message} ***");
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in Vosk processing loop: {ex.Message}"));
        }
    }

    private VoskResult ParseVoskResult(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
            var confidence = root.TryGetProperty("confidence", out var confElement) ? confElement.GetDouble() : 0.0;

            // Add punctuation if enabled and text doesn't end with punctuation
            if (_settings.Punctuation && !string.IsNullOrEmpty(text) &&
                !text.EndsWith('。') && !text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?'))
            {
                text += "。";
            }

            return new VoskResult
            {
                Text = text,
                IsPartial = false,
                IsFinal = true,
                Confidence = confidence
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Error parsing Vosk result JSON: {ex.Message} ***");
            return new VoskResult { Text = "", IsPartial = false, IsFinal = false, Confidence = 0.0 };
        }
    }

    private VoskResult ParseVoskPartialResult(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var text = root.TryGetProperty("partial", out var partialElement) ? partialElement.GetString() ?? "" : "";

            return new VoskResult
            {
                Text = text,
                IsPartial = true,
                IsFinal = false,
                Confidence = 0.5 // Partial results typically have lower confidence
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Error parsing Vosk partial result JSON: {ex.Message} ***");
            return new VoskResult { Text = "", IsPartial = true, IsFinal = false, Confidence = 0.0 };
        }
    }

    private class VoskResult
    {
        public string Text { get; set; } = "";
        public bool IsPartial { get; set; }
        public bool IsFinal { get; set; }
        public double Confidence { get; set; }
    }
}
