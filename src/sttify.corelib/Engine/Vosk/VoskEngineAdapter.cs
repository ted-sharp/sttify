using Sttify.Corelib.Config;
using System.Text.Json;

namespace Sttify.Corelib.Engine.Vosk;

public class VoskEngineAdapter : ISttEngine
{
    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    private readonly VoskEngineSettings _settings;
    private bool _isRunning;
    private readonly Queue<byte[]> _audioQueue = new();
    private readonly object _lockObject = new();
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;

    public VoskEngineAdapter(VoskEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter Constructor - Instance ID: {GetHashCode()} ***");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine is already running");

        // For mock implementation, continue even without model path
        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter (Mock) - Starting with ModelPath: '{_settings.ModelPath}' ***");
        
        lock (_lockObject)
        {
            _isRunning = true;
            _processingCancellation = new CancellationTokenSource();
        }

        _processingTask = Task.Run(() => ProcessAudioLoop(_processingCancellation.Token), cancellationToken);
        
        System.Diagnostics.Debug.WriteLine("*** VoskEngineAdapter (Mock) - Started successfully ***");
        await Task.Delay(100, cancellationToken);
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

        _processingCancellation?.Dispose();
        _processingCancellation = null;
        _processingTask = null;
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

    private async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        var partialCounter = 0;
        var currentText = "";
        var startTime = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
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
                    var mockResult = SimulateVoskProcessing(audioChunk, ref partialCounter, ref currentText);
                    
                    if (mockResult.IsPartial)
                    {
                        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter (Mock) - Generating PARTIAL: '{mockResult.Text}' ***");
                        OnPartial?.Invoke(this, new PartialRecognitionEventArgs(mockResult.Text, mockResult.Confidence));
                    }
                    else if (mockResult.IsFinal)
                    {
                        System.Diagnostics.Debug.WriteLine($"*** VoskEngineAdapter (Mock) - Generating FINAL: '{mockResult.Text}' ***");
                        var duration = DateTime.UtcNow - startTime;
                        OnFinal?.Invoke(this, new FinalRecognitionEventArgs(mockResult.Text, mockResult.Confidence, duration));
                        
                        partialCounter = 0;
                        currentText = "";
                        startTime = DateTime.UtcNow;
                    }
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in Vosk processing loop: {ex.Message}"));
        }
    }

    private VoskResult SimulateVoskProcessing(byte[] audioData, ref int partialCounter, ref string currentText)
    {
        partialCounter++;

        var sampleTexts = new[]
        {
            "こんにちは",
            "音声認識",
            "テストです",
            "スティファイ",
            "アプリケーション"
        };

        if (partialCounter <= _settings.TokensPerPartial)
        {
            var random = new Random();
            var newWord = sampleTexts[random.Next(sampleTexts.Length)];
            
            if (partialCounter == 1)
            {
                currentText = newWord;
            }
            else
            {
                currentText += newWord;
            }

            return new VoskResult
            {
                Text = currentText,
                IsPartial = true,
                IsFinal = false,
                Confidence = 0.7 + (Random.Shared.NextDouble() * 0.2)
            };
        }
        else
        {
            var finalText = currentText + (_settings.Punctuation ? "。" : "");
            
            return new VoskResult
            {
                Text = finalText,
                IsPartial = false,
                IsFinal = true,
                Confidence = 0.8 + (Random.Shared.NextDouble() * 0.15)
            };
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private class VoskResult
    {
        public string Text { get; set; } = "";
        public bool IsPartial { get; set; }
        public bool IsFinal { get; set; }
        public double Confidence { get; set; }
    }
}