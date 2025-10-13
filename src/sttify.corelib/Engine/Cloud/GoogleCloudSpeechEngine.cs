using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sttify.Corelib.Config;

namespace Sttify.Corelib.Engine.Cloud;

public class GoogleCloudSpeechEngine : CloudSttEngine
{
    public GoogleCloudSpeechEngine(CloudEngineSettings settings) : base(settings)
    {
    }

    protected override void ConfigureHttpClient()
    {
        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.ApiKey}");
        }
        _httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");
    }

    protected override async Task<CloudRecognitionResult> ProcessAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{_settings.Endpoint}/v1/speech:recognize";

            var request = new GoogleSpeechRequest
            {
                Config = new GoogleSpeechConfig
                {
                    Encoding = "LINEAR16",
                    SampleRateHertz = 16000,
                    LanguageCode = _settings.Language ?? "ja-JP",
                    EnableWordTimeOffsets = true,
                    EnableAutomaticPunctuation = true,
                    Model = "latest_long"
                },
                Audio = new GoogleSpeechAudio
                {
                    Content = Convert.ToBase64String(audioData)
                }
            };

            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return new CloudRecognitionResult
                {
                    Success = false,
                    ErrorMessage = $"Google Cloud Speech API error: {response.StatusCode} - {errorContent}"
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var googleResponse = JsonSerializer.Deserialize<GoogleSpeechResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (googleResponse?.Results?.Any() == true)
            {
                var bestResult = googleResponse.Results[0];
                if (bestResult.Alternatives?.Any() == true)
                {
                    var transcript = bestResult.Alternatives[0].Transcript;
                    var confidence = bestResult.Alternatives[0].Confidence ?? 0.0f;

                    return new CloudRecognitionResult
                    {
                        Success = true,
                        Text = transcript ?? "",
                        Confidence = confidence,
                        IsFinal = true
                    };
                }
            }

            return new CloudRecognitionResult
            {
                Success = true,
                Text = "",
                Confidence = 0.0f,
                IsFinal = true
            };
        }
        catch (Exception ex)
        {
            return new CloudRecognitionResult
            {
                Success = false,
                ErrorMessage = $"Google Cloud Speech processing error: {ex.Message}"
            };
        }
    }

    protected override async Task ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Test connection with a minimal request
            var endpoint = $"{_settings.Endpoint}/v1/operations";
            var response = await _httpClient.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google Cloud Speech API validation failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to validate Google Cloud Speech connection: {ex.Message}", ex);
        }
    }

    protected override string GetProviderName()
    {
        return "Google Cloud Speech";
    }
}

// Google Cloud Speech API request/response models
internal class GoogleSpeechRequest
{
    [JsonPropertyName("config")]
    public GoogleSpeechConfig Config { get; set; } = new();

    [JsonPropertyName("audio")]
    public GoogleSpeechAudio Audio { get; set; } = new();
}

internal class GoogleSpeechConfig
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = "LINEAR16";

    [JsonPropertyName("sampleRateHertz")]
    public int SampleRateHertz { get; set; } = 16000;

    [JsonPropertyName("languageCode")]
    public string LanguageCode { get; set; } = "ja-JP";

    [JsonPropertyName("enableWordTimeOffsets")]
    public bool EnableWordTimeOffsets { get; set; } = true;

    [JsonPropertyName("enableAutomaticPunctuation")]
    public bool EnableAutomaticPunctuation { get; set; } = true;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "latest_long";
}

internal class GoogleSpeechAudio
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class GoogleSpeechResponse
{
    [JsonPropertyName("results")]
    public GoogleSpeechResult[]? Results { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class GoogleSpeechResult
{
    [JsonPropertyName("alternatives")]
    public GoogleSpeechAlternative[]? Alternatives { get; set; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
internal class GoogleSpeechAlternative
{
    [JsonPropertyName("transcript")]
    public string? Transcript { get; set; }

    [JsonPropertyName("confidence")]
    public float? Confidence { get; set; }
}
