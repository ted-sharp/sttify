using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Sttify.Corelib.Caching;
using Sttify.Corelib.Collections;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Engine.Cloud;

public abstract class CloudSttEngine : ISttEngine
{
    protected readonly BoundedQueue<byte[]> _audioQueue;
    protected readonly HttpClient _httpClient;
    protected readonly object _lockObject = new();
    protected readonly ResponseCache<CloudRecognitionResult> _responseCache;

    protected readonly CloudEngineSettings _settings;
    protected bool _isRunning;
    protected CancellationTokenSource? _processingCancellation;
    protected Task? _processingTask;
    protected DateTime _recognitionStartTime;

    protected CloudSttEngine(CloudEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Use bounded queue to prevent memory bloat
        _audioQueue = new BoundedQueue<byte[]>(50); // Smaller queue for cloud processing

        // Cache responses to reduce API calls and improve latency
        _responseCache = new ResponseCache<CloudRecognitionResult>(maxEntries: 500, ttl: TimeSpan.FromMinutes(15));

        ConfigureHttpClient();
    }

    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    public virtual async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine is already running");

        try
        {
            await ValidateConnectionAsync(cancellationToken);

            lock (_lockObject)
            {
                _isRunning = true;
                _processingCancellation = new CancellationTokenSource();
                _recognitionStartTime = DateTime.UtcNow;
            }

            _processingTask = Task.Run(() => ProcessAudioLoop(_processingCancellation.Token), cancellationToken);

            Telemetry.LogEvent("CloudEngineStarted", new
            {
                Provider = GetProviderName(),
                Language = _settings.Language,
                Endpoint = _settings.Endpoint
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("CloudEngineStartFailed", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Failed to start cloud engine: {ex.Message}"));
            throw;
        }
    }

    public virtual async Task StopAsync(CancellationToken cancellationToken = default)
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

        Telemetry.LogEvent("CloudEngineStopped", new { Provider = GetProviderName() });
    }

    public void PushAudio(ReadOnlySpan<byte> audioData)
    {
        if (!_isRunning || audioData.IsEmpty)
            return;

        var buffer = audioData.ToArray();
        if (!_audioQueue.TryEnqueue(buffer))
        {
            // Queue full - drop oldest data
            Telemetry.LogWarning("CloudAudioQueueFull", "Audio queue full, dropping oldest data", new { QueueSize = _audioQueue.Count });
        }
    }

    public virtual void Dispose()
    {
        StopAsync().Wait();
        _httpClient?.Dispose();
        _responseCache?.Dispose();
    }

    protected abstract void ConfigureHttpClient();
    protected abstract Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken);

    protected virtual async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        var audioBuffer = new List<byte>();
        var lastProcessTime = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                byte[]? audioChunk = null;

                if (_audioQueue.TryDequeue(out audioChunk))
                {
                    // Got audio chunk
                }
                else
                {
                    audioChunk = null;
                }

                if (audioChunk != null)
                {
                    audioBuffer.AddRange(audioChunk);
                }

                // Process accumulated audio every few seconds or when buffer is large enough
                var timeSinceLastProcess = DateTime.UtcNow - lastProcessTime;
                if (audioBuffer.Count > 0 && (timeSinceLastProcess.TotalSeconds >= 3.0 || audioBuffer.Count > 32000))
                {
                    try
                    {
                        var audioArray = audioBuffer.ToArray();
                        var cacheKey = ResponseCache<CloudRecognitionResult>.GenerateKey(audioArray, GetProviderName());

                        CloudRecognitionResult result;
                        if (_responseCache.TryGet(cacheKey, out var cachedResult))
                        {
                            result = cachedResult;
                            Telemetry.LogEvent("CloudCacheHit", new { Provider = GetProviderName(), AudioSize = audioArray.Length });
                        }
                        else
                        {
                            result = await ProcessAudioChunkAsync(audioArray, cancellationToken);
                            if (result.Success)
                            {
                                _responseCache.Set(cacheKey, result);
                            }
                        }

                        ProcessCloudResult(result);

                        audioBuffer.Clear();
                        lastProcessTime = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        Telemetry.LogError("CloudAudioProcessingError", ex);
                        OnError?.Invoke(this, new SttErrorEventArgs(ex, "Error processing audio with cloud service"));

                        // Clear buffer on error to prevent infinite retry
                        audioBuffer.Clear();
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
            Telemetry.LogError("CloudProcessingLoopError", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in cloud processing loop: {ex.Message}"));
        }
    }

    protected virtual void ProcessCloudResult(CloudRecognitionResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.Text))
            return;

        if (result.IsFinal)
        {
            var duration = DateTime.UtcNow - _recognitionStartTime;
            OnFinal?.Invoke(this, new FinalRecognitionEventArgs(result.Text, result.Confidence, duration));
            _recognitionStartTime = DateTime.UtcNow;
        }
        else
        {
            OnPartial?.Invoke(this, new PartialRecognitionEventArgs(result.Text, result.Confidence));
        }
    }

    protected abstract Task ValidateConnectionAsync(CancellationToken cancellationToken);
    protected abstract string GetProviderName();

    protected virtual string GetAudioFormat()
    {
        return "audio/wav"; // Default format, override as needed
    }

    protected static byte[] WrapAsWav(byte[] pcmLittleEndian, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        // Build a minimal PCM WAV container around the provided PCM payload
        // RIFF header size = 44 bytes
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int dataSize = pcmLittleEndian?.Length ?? 0;
        int riffChunkSize = 36 + dataSize;

        var buffer = new byte[44 + dataSize];
        void WriteString(int offset, string s)
        {
            for (int i = 0; i < s.Length; i++)
                buffer[offset + i] = (byte)s[i];
        }
        void WriteInt32LE(int offset, int value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
        void WriteInt16LE(int offset, short value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        // RIFF chunk descriptor
        WriteString(0, "RIFF");
        WriteInt32LE(4, riffChunkSize);
        WriteString(8, "WAVE");

        // fmt sub-chunk
        WriteString(12, "fmt ");
        WriteInt32LE(16, 16);                 // Subchunk1Size for PCM
        WriteInt16LE(20, 1);                   // PCM format = 1
        WriteInt16LE(22, channels);
        WriteInt32LE(24, sampleRate);
        WriteInt32LE(28, byteRate);
        WriteInt16LE(32, blockAlign);
        WriteInt16LE(34, bitsPerSample);

        // data sub-chunk
        WriteString(36, "data");
        WriteInt32LE(40, dataSize);

        if (dataSize > 0 && pcmLittleEndian != null)
        {
            Buffer.BlockCopy(pcmLittleEndian, 0, buffer, 44, dataSize);
        }

        return buffer;
    }
}

[ExcludeFromCodeCoverage] // Simple data container class
public class CloudRecognitionResult
{
    public bool Success { get; set; } = true;
    public string ErrorMessage { get; set; } = "";
    public string Text { get; set; } = "";
    public double Confidence { get; set; }
    public bool IsFinal { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}


// Azure Speech Services implementation
public class AzureSpeechEngine : CloudSttEngine
{
    public AzureSpeechEngine(CloudEngineSettings settings) : base(settings)
    {
    }

    protected override void ConfigureHttpClient()
    {
        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _settings.ApiKey);
        }
    }

    protected override async Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{_settings.Endpoint}/speech/recognition/conversation/cognitiveservices/v1";
            var uri = $"{endpoint}?language={_settings.Language}&format=detailed";

            // Ensure we send a proper WAV container (16kHz, mono, 16-bit PCM by default)
            var wavBytes = WrapAsWav(audioData);
            using var content = new ByteArrayContent(wavBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

            var response = await _httpClient.PostAsync(uri, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var azureResult = JsonSerializer.Deserialize<AzureRecognitionResponse>(jsonResponse);

            if (azureResult?.NBest?.Length > 0)
            {
                var best = azureResult.NBest[0];
                return new CloudRecognitionResult
                {
                    Text = best.Display ?? "",
                    Confidence = best.Confidence,
                    IsFinal = true
                };
            }

            return new CloudRecognitionResult();
        }
        catch (Exception ex)
        {
            Telemetry.LogError("AzureSpeechProcessingFailed", ex);
            throw;
        }
    }

    protected override async Task ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        // Perform a lightweight POST with a short silent WAV to validate endpoint + key
        var endpoint = $"{_settings.Endpoint}/speech/recognition/conversation/cognitiveservices/v1";
        var uri = $"{endpoint}?language={_settings.Language}&format=detailed";

        // 100ms of silence @16kHz mono 16-bit
        var silentPcm = new byte[1600 * 2]; // 1600 samples * 2 bytes
        var probe = WrapAsWav(silentPcm);
        using var content = new ByteArrayContent(probe);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("Invalid Azure Speech Service API key");
        }

        // Accept 2xx as healthy; other codes indicate endpoint/config issues
        if (!response.IsSuccessStatusCode)
        {
            var msg = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Azure validation failed: {(int)response.StatusCode} {response.ReasonPhrase} - {msg}");
        }
    }

    protected override string GetProviderName() => "Azure Speech Services";

    private class AzureRecognitionResponse
    {
        public string? RecognitionStatus { get; set; }
        public AzureNBestResult[]? NBest { get; set; }
    }

    private class AzureNBestResult
    {
        public double Confidence { get; set; }
        public string? Display { get; set; }
    }
}
