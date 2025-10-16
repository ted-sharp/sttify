using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Sttify.Corelib.Caching;
using Sttify.Corelib.Collections;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Engine.Cloud;

public abstract class CloudSttEngine : ISttEngine
{
    protected readonly BoundedQueue<byte[]> AudioQueue;
    protected readonly HttpClient HttpClient;
    protected readonly object LockObject = new();
    protected readonly ResponseCache<CloudRecognitionResult> ResponseCache;

    protected readonly CloudEngineSettings Settings;
    protected bool HttpClientConfigured;
    protected bool IsRunning;
    protected CancellationTokenSource? ProcessingCancellation;
    protected Task? ProcessingTask;
    protected DateTime RecognitionStartTime;

    protected CloudSttEngine(CloudEngineSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        HttpClient = new HttpClient();
        HttpClient.Timeout = TimeSpan.FromSeconds(30);

        // Use bounded queue to prevent memory bloat
        AudioQueue = new BoundedQueue<byte[]>(50); // Smaller queue for cloud processing

        // Cache responses to reduce API calls and improve latency
        ResponseCache = new ResponseCache<CloudRecognitionResult>(maxEntries: 500, ttl: TimeSpan.FromMinutes(15));
    }

    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    public virtual async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Engine is already running");

        // Configure HTTP client on first start (not in constructor to avoid calling virtual method)
        if (!HttpClientConfigured)
        {
            ConfigureHttpClient();
            HttpClientConfigured = true;
        }

        try
        {
            await ValidateConnectionAsync(cancellationToken);

            lock (LockObject)
            {
                IsRunning = true;
                ProcessingCancellation = new CancellationTokenSource();
                RecognitionStartTime = DateTime.UtcNow;
            }

            ProcessingTask = Task.Run(() => ProcessAudioLoop(ProcessingCancellation.Token), cancellationToken);

            Telemetry.LogEvent("CloudEngineStarted", new
            {
                Provider = GetProviderName(),
                Settings.Language,
                Settings.Endpoint
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
        CancellationTokenSource? cancellationToDispose;

        lock (LockObject)
        {
            if (!IsRunning)
                return;

            IsRunning = false;
            cancellationToDispose = ProcessingCancellation;
        }

        if (cancellationToDispose != null)
        {
            await cancellationToDispose.CancelAsync();
        }

        if (ProcessingTask != null)
        {
            try
            {
                await ProcessingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        cancellationToDispose?.Dispose();
        ProcessingCancellation = null;
        ProcessingTask = null;

        Telemetry.LogEvent("CloudEngineStopped", new { Provider = GetProviderName() });
    }

    public void PushAudio(ReadOnlySpan<byte> audioData)
    {
        if (!IsRunning || audioData.IsEmpty)
            return;

        var buffer = audioData.ToArray();
        if (!AudioQueue.TryEnqueue(buffer))
        {
            // Queue full - drop oldest data
            Telemetry.LogWarning("CloudAudioQueueFull", "Audio queue full, dropping oldest data", new { QueueSize = AudioQueue.Count });
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAsync().Wait();
            HttpClient.Dispose();
            ResponseCache.Dispose();
            AudioQueue.Dispose();
        }
    }

    protected abstract void ConfigureHttpClient();
    protected abstract Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken);

    protected virtual async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        var audioBuffer = new List<byte>();
        var lastProcessTime = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsRunning)
            {
                if (!AudioQueue.TryDequeue(out var audioChunk))
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
                        if (ResponseCache.TryGet(cacheKey, out var cachedResult))
                        {
                            result = cachedResult;
                            Telemetry.LogEvent("CloudCacheHit", new { Provider = GetProviderName(), AudioSize = audioArray.Length });
                        }
                        else
                        {
                            result = await ProcessAudioChunkAsync(audioArray, cancellationToken);
                            if (result.Success)
                            {
                                ResponseCache.Set(cacheKey, result);
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
            // Expected when processing is cancelled
        }
        catch (Exception ex)
        {
            Telemetry.LogError("CloudProcessingLoopError", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in cloud processing loop: {ex.Message}"));
        }
    }

    protected virtual void ProcessCloudResult(CloudRecognitionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
            return;

        if (result.IsFinal)
        {
            var duration = DateTime.UtcNow - RecognitionStartTime;
            OnFinal?.Invoke(this, new FinalRecognitionEventArgs(result.Text, result.Confidence, duration));
            RecognitionStartTime = DateTime.UtcNow;
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
        int dataSize = pcmLittleEndian.Length;
        int riffChunkSize = 36 + dataSize;

        var buffer = new byte[44 + dataSize];
        void WriteString(int offset, string s)
        {
            for (int i = 0; i < s.Length; i++)
                buffer[offset + i] = (byte)s[i];
        }
        void WriteInt32Le(int offset, int value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
        void WriteInt16Le(int offset, short value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        // RIFF chunk descriptor
        WriteString(0, "RIFF");
        WriteInt32Le(4, riffChunkSize);
        WriteString(8, "WAVE");

        // fmt sub-chunk
        WriteString(12, "fmt ");
        WriteInt32Le(16, 16);                 // Subchunk1Size for PCM
        WriteInt16Le(20, 1);                   // PCM format = 1
        WriteInt16Le(22, channels);
        WriteInt32Le(24, sampleRate);
        WriteInt32Le(28, byteRate);
        WriteInt16Le(32, blockAlign);
        WriteInt16Le(34, bitsPerSample);

        // data sub-chunk
        WriteString(36, "data");
        WriteInt32Le(40, dataSize);

        if (dataSize > 0)
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
        if (!string.IsNullOrEmpty(Settings.ApiKey))
        {
            HttpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", Settings.ApiKey);
        }
    }

    protected override async Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{Settings.Endpoint}/speech/recognition/conversation/cognitiveservices/v1";
            var uri = $"{endpoint}?language={Settings.Language}&format=detailed";

            // Ensure we send a proper WAV container (16kHz, mono, 16-bit PCM by default)
            var wavBytes = WrapAsWav(audioData);
            using var content = new ByteArrayContent(wavBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

            var response = await HttpClient.PostAsync(uri, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var azureResult = JsonSerializer.Deserialize<AzureRecognitionResponse>(jsonResponse);

            if (azureResult?.NBest is { Length: > 0 })
            {
                var best = azureResult.NBest[0];
                return new CloudRecognitionResult
                {
                    Text = best.Display,
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
        var endpoint = $"{Settings.Endpoint}/speech/recognition/conversation/cognitiveservices/v1";
        var uri = $"{endpoint}?language={Settings.Language}&format=detailed";

        // 100ms of silence @16kHz mono 16-bit
        var silentPcm = new byte[1600 * 2]; // 1600 samples * 2 bytes
        var probe = WrapAsWav(silentPcm);
        using var content = new ByteArrayContent(probe)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav") }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        var response = await HttpClient.SendAsync(request, cancellationToken);

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

    private sealed class AzureRecognitionResponse
    {
        public string RecognitionStatus { get; set; } = string.Empty;
        public AzureNBestResult[] NBest { get; set; } = [];
    }

    private sealed class AzureNBestResult
    {
        public double Confidence { get; set; }
        public string Display { get; set; } = string.Empty;
    }
}
