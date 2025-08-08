using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Sttify.Corelib.Engine.Vibe;

[ExcludeFromCodeCoverage] // External Vibe API integration, network dependent, difficult to mock effectively
public class VibeSttEngine : ISttEngine, IDisposable
{
    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    private readonly Config.VibeEngineSettings _settings;
    private readonly HttpClient _httpClient;
    private bool _isRunning;
    private readonly Queue<byte[]> _audioQueue = new();
    private readonly object _lockObject = new();
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;
    private DateTime _recognitionStartTime;
    private readonly MemoryStream _audioBuffer = new();

    public VibeSttEngine(Config.VibeEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);

        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.ApiKey}");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Vibe engine is already running");

        try
        {
            // Skip health check for now since /health endpoint may not exist
            // await ValidateConnectionAsync(cancellationToken);
            System.Diagnostics.Debug.WriteLine("*** VibeSttEngine.StartAsync - Skipping health check, proceeding with startup ***");

            lock (_lockObject)
            {
                _isRunning = true;
                _processingCancellation = new CancellationTokenSource();
                _recognitionStartTime = DateTime.UtcNow;
            }

            _processingTask = Task.Run(() => ProcessAudioLoop(_processingCancellation.Token), cancellationToken);

            Telemetry.LogEvent("VibeEngineStarted", new
            {
                Endpoint = _settings.Endpoint,
                Language = _settings.Language,
                Model = _settings.Model
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VibeEngineStartFailed", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Failed to start Vibe engine: {ex.Message}"));
            throw;
        }

        return Task.CompletedTask;
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

        // Process any remaining audio in buffer
        if (_audioBuffer.Length > 0)
        {
            try
            {
                await ProcessFinalAudioChunk();
            }
            catch (Exception ex)
            {
                Telemetry.LogError("VibeFinalizationFailed", ex);
            }
        }

        _processingCancellation?.Dispose();
        _processingCancellation = null;
        _processingTask = null;
        _audioBuffer.SetLength(0);

        Telemetry.LogEvent("VibeEngineStopped");
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
            while (_audioQueue.Count > 30) // Smaller queue for API-based processing
            {
                _audioQueue.Dequeue();
            }
        }
    }

    private async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        var lastProcessTime = DateTime.UtcNow;

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

                if (audioChunk != null)
                {
                    _audioBuffer.Write(audioChunk, 0, audioChunk.Length);
                }

                // Process accumulated audio every few seconds or when buffer is large enough
                var timeSinceLastProcess = DateTime.UtcNow - lastProcessTime;
                var shouldProcess = _audioBuffer.Length > 0 &&
                    (timeSinceLastProcess.TotalSeconds >= _settings.ProcessingIntervalSeconds ||
                     _audioBuffer.Length > _settings.MaxBufferSize);

                if (shouldProcess)
                {
                    try
                    {
                        var audioBytes = _audioBuffer.ToArray();
                        var result = await TranscribeAudioAsync(audioBytes, cancellationToken);

                        if (!string.IsNullOrEmpty(result.Text))
                        {
                            ProcessVibeResult(result);
                        }

                        _audioBuffer.SetLength(0);
                        lastProcessTime = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        Telemetry.LogError("VibeAudioProcessingError", ex);
                        OnError?.Invoke(this, new SttErrorEventArgs(ex, "Error processing audio with Vibe"));

                        // Clear buffer on error to prevent infinite retry
                        _audioBuffer.SetLength(0);
                    }
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VibeProcessingLoopError", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in Vibe processing loop: {ex.Message}"));
        }
    }

    private async Task<VibeTranscriptionResult> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            // Vibe expects JSON with file path, so we need to save audio to temp file
            var tempAudioFile = Path.Combine(Path.GetTempPath(), $"sttify_audio_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.wav");

            try
            {
                // Save audio data to temporary WAV file
                await File.WriteAllBytesAsync(tempAudioFile, CreateWavFile(audioData), cancellationToken);

                // Create JSON request matching Vibe API
                var requestBody = new
                {
                    path = tempAudioFile,
                    language = !string.IsNullOrEmpty(_settings.Language) ? _settings.Language : (object?)null,
                    model = !string.IsNullOrEmpty(_settings.Model) ? _settings.Model : (object?)null,
                    diarization = _settings.EnableDiarization,
                    output_format = _settings.OutputFormat
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var endpoint = $"{_settings.Endpoint.TrimEnd('/')}/transcribe";
                System.Diagnostics.Debug.WriteLine($"*** Sending Vibe request to: {endpoint} with file: {tempAudioFile} ***");
                var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                System.Diagnostics.Debug.WriteLine($"*** Vibe response: {jsonResponse} ***");

                var result = JsonSerializer.Deserialize<VibeApiResponse>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                return new VibeTranscriptionResult
                {
                    Text = result?.Text ?? "",
                    Confidence = result?.Confidence ?? 0.0,
                    Language = result?.Language,
                    Duration = result?.Duration ?? 0.0,
                    Segments = result?.Segments ?? Array.Empty<VibeSegment>()
                };
            }
            finally
            {
                // Clean up temporary file
                try
                {
                    if (File.Exists(tempAudioFile))
                    {
                        File.Delete(tempAudioFile);
                        System.Diagnostics.Debug.WriteLine($"*** Deleted temp audio file: {tempAudioFile} ***");
                    }
                }
                catch (Exception cleanupEx)
                {
                    System.Diagnostics.Debug.WriteLine($"*** Failed to delete temp file {tempAudioFile}: {cleanupEx.Message} ***");
                }
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VibeTranscriptionFailed", ex);
            System.Diagnostics.Debug.WriteLine($"*** Vibe transcription error: {ex.Message} ***");
            throw;
        }
    }

    private async Task ProcessFinalAudioChunk()
    {
        if (_audioBuffer.Length == 0)
            return;

        var audioBytes = _audioBuffer.ToArray();
        var result = await TranscribeAudioAsync(audioBytes, CancellationToken.None);

        if (!string.IsNullOrEmpty(result.Text))
        {
            ProcessVibeResult(result, true);
        }
    }

    private void ProcessVibeResult(VibeTranscriptionResult result, bool isFinal = false)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
            return;

        var text = result.Text.Trim();
        var confidence = result.Confidence;

        // Apply post-processing if enabled
        if (_settings.EnablePostProcessing)
        {
            text = ApplyPostProcessing(text);
        }

        if (isFinal || result.Segments.Length > 0)
        {
            var duration = DateTime.UtcNow - _recognitionStartTime;
            OnFinal?.Invoke(this, new FinalRecognitionEventArgs(text, confidence, duration));
            _recognitionStartTime = DateTime.UtcNow;
        }
        else
        {
            OnPartial?.Invoke(this, new PartialRecognitionEventArgs(text, confidence));
        }

        Telemetry.LogEvent("VibeTranscriptionCompleted", new
        {
            TextLength = text.Length,
            Confidence = confidence,
            Language = result.Language,
            Duration = result.Duration,
            SegmentCount = result.Segments.Length
        });
    }

    private string ApplyPostProcessing(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Basic post-processing
        text = text.Trim();

        // Capitalize first letter if enabled
        if (_settings.AutoCapitalize && text.Length > 0)
        {
            text = char.ToUpper(text[0]) + text.Substring(1);
        }

        // Add punctuation if missing and enabled
        if (_settings.AutoPunctuation && !text.EndsWith(".") && !text.EndsWith("?") && !text.EndsWith("!"))
        {
            text += ".";
        }

        return text;
    }

    private async Task ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{_settings.Endpoint.TrimEnd('/')}/health";
            var response = await _httpClient.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Vibe service is not available. Status: {response.StatusCode}");
            }
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to connect to Vibe service: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a simple WAV file from raw PCM audio data
    /// </summary>
    private static byte[] CreateWavFile(byte[] pcmData, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        using var wavStream = new MemoryStream();
        using var writer = new BinaryWriter(wavStream);

        // WAV Header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length); // File size - 8 bytes
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // Format chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Format chunk size
        writer.Write((short)1); // PCM format
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8); // Byte rate
        writer.Write((short)(channels * bitsPerSample / 8)); // Block align
        writer.Write(bitsPerSample);

        // Data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        return wavStream.ToArray();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _httpClient?.Dispose();
        _audioBuffer?.Dispose();
    }
}

public class VibeTranscriptionResult
{
    public string Text { get; set; } = "";
    public double Confidence { get; set; }
    public string? Language { get; set; }
    public double Duration { get; set; }
    public VibeSegment[] Segments { get; set; } = Array.Empty<VibeSegment>();
}

public class VibeApiResponse
{
    public string? Text { get; set; }
    public double Confidence { get; set; }
    public string? Language { get; set; }
    public double Duration { get; set; }
    public VibeSegment[]? Segments { get; set; }
}

public class VibeSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string? Text { get; set; }
    public string? Speaker { get; set; }
    public double Confidence { get; set; }
}

