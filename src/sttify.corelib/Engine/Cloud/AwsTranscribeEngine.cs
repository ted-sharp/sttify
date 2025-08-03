using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Sttify.Corelib.Engine.Cloud;

[ExcludeFromCodeCoverage] // External AWS API integration, network dependent, difficult to mock effectively
public class AwsTranscribeEngine : CloudSttEngine
{
    private readonly string _region;
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;

    public AwsTranscribeEngine(CloudEngineSettings settings) : base(settings)
    {
        // Parse AWS-specific settings from the endpoint or separate config
        _region = ExtractRegionFromEndpoint(settings.Endpoint) ?? "us-east-1";
        _accessKeyId = settings.ApiKey ?? throw new ArgumentException("Access Key ID is required for AWS Transcribe");
        _secretAccessKey = settings.SecretKey ?? throw new ArgumentException("Secret Access Key is required for AWS Transcribe");
    }

    protected override void ConfigureHttpClient()
    {
        // AWS Transcribe uses signature-based authentication
        // Headers will be added per request in ProcessAudioChunkAsync
    }

    protected override async Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            // AWS Transcribe requires starting a transcription job and polling for results
            // For real-time transcription, we'd use AWS Transcribe Streaming
            // This is a simplified implementation for batch transcription
            
            var jobName = $"sttify-job-{Guid.NewGuid():N}";
            var bucketName = "sttify-transcribe-temp"; // Would need to be configurable
            
            // Step 1: Upload audio to S3 (simplified - would need actual S3 client)
            var s3Uri = await UploadToS3Async(audioData, bucketName, jobName, cancellationToken);
            
            // Step 2: Start transcription job
            var startJobResult = await StartTranscriptionJobAsync(jobName, s3Uri, cancellationToken);
            if (!startJobResult.Success)
            {
                return startJobResult;
            }
            
            // Step 3: Poll for completion
            var result = await PollTranscriptionJobAsync(jobName, cancellationToken);
            
            // Step 4: Cleanup S3 object (optional)
            await CleanupS3ObjectAsync(bucketName, jobName, cancellationToken);
            
            return result;
        }
        catch (Exception ex)
        {
            return new CloudRecognitionResult
            {
                Success = false,
                ErrorMessage = $"AWS Transcribe processing error: {ex.Message}"
            };
        }
    }

    private async Task<string> UploadToS3Async(byte[] audioData, string bucketName, string objectKey, CancellationToken cancellationToken)
    {
        // This is a simplified placeholder
        // In a real implementation, you would use AWS SDK to upload to S3
        // For now, we'll simulate with a direct API call
        
        var endpoint = $"https://{bucketName}.s3.{_region}.amazonaws.com/{objectKey}.wav";
        var timestamp = DateTimeOffset.UtcNow;
        var headers = CreateAwsHeaders("PUT", endpoint, audioData, timestamp);
        
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = new ByteArrayContent(audioData);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        return $"s3://{bucketName}/{objectKey}.wav";
    }

    private async Task<CloudRecognitionResult> StartTranscriptionJobAsync(string jobName, string s3Uri, CancellationToken cancellationToken)
    {
        var endpoint = $"https://transcribe.{_region}.amazonaws.com/";
        var timestamp = DateTimeOffset.UtcNow;
        
        var requestBody = new AwsTranscribeJobRequest
        {
            TranscriptionJobName = jobName,
            LanguageCode = _settings.Language ?? "ja-JP",
            MediaFormat = "wav",
            Media = new AwsTranscribeMedia { MediaFileUri = s3Uri },
            Settings = new AwsTranscribeSettings
            {
                ShowSpeakerLabels = false,
                MaxSpeakerLabels = 2
            }
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var headers = CreateAwsHeaders("POST", endpoint, Encoding.UTF8.GetBytes(json), timestamp);
        headers["X-Amz-Target"] = "Transcribe.StartTranscriptionJob";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return new CloudRecognitionResult
            {
                Success = false,
                ErrorMessage = $"Failed to start transcription job: {response.StatusCode} - {errorContent}"
            };
        }

        return new CloudRecognitionResult { Success = true };
    }

    private async Task<CloudRecognitionResult> PollTranscriptionJobAsync(string jobName, CancellationToken cancellationToken)
    {
        var endpoint = $"https://transcribe.{_region}.amazonaws.com/";
        var maxAttempts = 30; // 30 seconds max wait
        var attempt = 0;

        while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var requestBody = new { TranscriptionJobName = jobName };
            var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var headers = CreateAwsHeaders("POST", endpoint, Encoding.UTF8.GetBytes(json), timestamp);
            headers["X-Amz-Target"] = "Transcribe.GetTranscriptionJob";

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            request.Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var jobResponse = JsonSerializer.Deserialize<AwsTranscribeJobResponse>(content, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                if (jobResponse?.TranscriptionJob?.TranscriptionJobStatus == "COMPLETED")
                {
                    // Download and parse transcript
                    var transcript = await DownloadTranscriptAsync(jobResponse.TranscriptionJob.Transcript?.TranscriptFileUri, cancellationToken);
                    return new CloudRecognitionResult
                    {
                        Success = true,
                        Text = transcript,
                        Confidence = 0.9f, // AWS doesn't provide word-level confidence in simple format
                        IsFinal = true
                    };
                }
                else if (jobResponse?.TranscriptionJob?.TranscriptionJobStatus == "FAILED")
                {
                    return new CloudRecognitionResult
                    {
                        Success = false,
                        ErrorMessage = $"Transcription job failed: {jobResponse.TranscriptionJob.FailureReason}"
                    };
                }
            }

            attempt++;
            await Task.Delay(1000, cancellationToken); // Wait 1 second between polls
        }

        return new CloudRecognitionResult
        {
            Success = false,
            ErrorMessage = "Transcription job timed out"
        };
    }

    private async Task<string> DownloadTranscriptAsync(string? transcriptUri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(transcriptUri))
            return "";

        var response = await _httpClient.GetAsync(transcriptUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        
        // Parse AWS transcript JSON format
        var transcript = JsonSerializer.Deserialize<AwsTranscriptResult>(content);
        return transcript?.Results?.Transcripts?.FirstOrDefault()?.Transcript ?? "";
    }

    private async Task CleanupS3ObjectAsync(string bucketName, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"https://{bucketName}.s3.{_region}.amazonaws.com/{objectKey}.wav";
            var timestamp = DateTimeOffset.UtcNow;
            var headers = CreateAwsHeaders("DELETE", endpoint, Array.Empty<byte>(), timestamp);

            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            await _httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private Dictionary<string, string> CreateAwsHeaders(string httpMethod, string endpoint, byte[] payload, DateTimeOffset timestamp)
    {
        // Simplified AWS signature v4 implementation
        // In production, use AWS SDK which handles this automatically
        var headers = new Dictionary<string, string>
        {
            ["Host"] = new Uri(endpoint).Host,
            ["X-Amz-Date"] = timestamp.ToString("yyyyMMddTHHmmssZ"),
            ["Authorization"] = $"AWS4-HMAC-SHA256 Credential={_accessKeyId}/{timestamp:yyyyMMdd}/{_region}/transcribe/aws4_request, SignedHeaders=host;x-amz-date, Signature=placeholder"
        };

        return headers;
    }

    private static string? ExtractRegionFromEndpoint(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            return null;

        // Extract region from endpoints like "https://transcribe.us-west-2.amazonaws.com/"
        var match = System.Text.RegularExpressions.Regex.Match(endpoint, @"transcribe\.([^.]+)\.amazonaws\.com");
        return match.Success ? match.Groups[1].Value : null;
    }

    protected override async Task ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Test connection by listing transcription jobs (should return 200 even if empty)
            var endpoint = $"https://transcribe.{_region}.amazonaws.com/";
            var timestamp = DateTimeOffset.UtcNow;
            var requestBody = new { MaxResults = 1 };
            var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var headers = CreateAwsHeaders("POST", endpoint, Encoding.UTF8.GetBytes(json), timestamp);
            headers["X-Amz-Target"] = "Transcribe.ListTranscriptionJobs";

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            request.Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"AWS Transcribe API validation failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to validate AWS Transcribe connection: {ex.Message}", ex);
        }
    }

    protected override string GetProviderName()
    {
        return "AWS Transcribe";
    }
}

// AWS Transcribe API models
[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscribeJobRequest
{
    public string TranscriptionJobName { get; set; } = "";
    public string LanguageCode { get; set; } = "";
    public string MediaFormat { get; set; } = "";
    public AwsTranscribeMedia Media { get; set; } = new();
    public AwsTranscribeSettings? Settings { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscribeMedia
{
    public string MediaFileUri { get; set; } = "";
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
internal class AwsTranscribeSettings
{
    public bool ShowSpeakerLabels { get; set; }
    public int MaxSpeakerLabels { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscribeJobResponse
{
    public AwsTranscriptionJob? TranscriptionJob { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscriptionJob
{
    public string? TranscriptionJobStatus { get; set; }
    public string? FailureReason { get; set; }
    public AwsTranscript? Transcript { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscript
{
    public string? TranscriptFileUri { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscriptResult
{
    public AwsTranscriptResults? Results { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscriptResults
{
    public AwsTranscriptItem[]? Transcripts { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class AwsTranscriptItem
{
    public string? Transcript { get; set; }
}