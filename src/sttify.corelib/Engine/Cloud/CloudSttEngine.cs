using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Sttify.Corelib.Engine.Cloud;

public abstract class CloudSttEngine : ISttEngine
{
    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    protected readonly CloudEngineSettings _settings;
    protected readonly HttpClient _httpClient;
    protected bool _isRunning;
    protected readonly Queue<byte[]> _audioQueue = new();
    protected readonly object _lockObject = new();
    protected CancellationTokenSource? _processingCancellation;
    protected Task? _processingTask;
    protected DateTime _recognitionStartTime;

    protected CloudSttEngine(CloudEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        
        ConfigureHttpClient();
    }

    protected abstract void ConfigureHttpClient();
    protected abstract Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken);
    
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
        lock (_audioQueue)
        {
            _audioQueue.Enqueue(buffer);
            
            // Prevent queue from growing too large
            while (_audioQueue.Count > 50) // Smaller queue for cloud processing
            {
                _audioQueue.Dequeue();
            }
        }
    }

    protected virtual async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        var audioBuffer = new List<byte>();
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
                    audioBuffer.AddRange(audioChunk);
                }

                // Process accumulated audio every few seconds or when buffer is large enough
                var timeSinceLastProcess = DateTime.UtcNow - lastProcessTime;
                if (audioBuffer.Count > 0 && (timeSinceLastProcess.TotalSeconds >= 3.0 || audioBuffer.Count > 32000))
                {
                    try
                    {
                        var result = await ProcessAudioChunkAsync(audioBuffer.ToArray(), cancellationToken);
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

    public virtual void Dispose()
    {
        StopAsync().Wait();
        _httpClient?.Dispose();
    }
}

public class CloudRecognitionResult
{
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

            using var content = new ByteArrayContent(audioData);
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
        // Simple validation - try to get a token or make a test request
        var testUri = $"{_settings.Endpoint}/speech/recognition/conversation/cognitiveservices/v1";
        var response = await _httpClient.GetAsync(testUri, cancellationToken);
        
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("Invalid Azure Speech Service API key");
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
        public string? Lexical { get; set; }
        public string? ITN { get; set; }
        public string? MaskedITN { get; set; }
        public string? Display { get; set; }
    }
}