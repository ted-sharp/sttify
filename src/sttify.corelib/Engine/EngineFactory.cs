using Sttify.Corelib.Config;
using Sttify.Corelib.Engine.Cloud;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Engine.Vibe;

namespace Sttify.Corelib.Engine;

// Deprecated: Use SttEngineFactory instead
public static class EngineFactory
{
    public static ISttEngine CreateEngine(EngineSettings settings)
    {
        // Forward to unified factory for backward compatibility
        return SttEngineFactory.CreateEngine(settings);
    }

    // GetAvailableProfiles/GetAvailableCloudProviders/ValidateEngineSettings remain for existing callers

    public static string[] GetAvailableProfiles()
    {
        return new[]
        {
            "vosk",
            "vosk-multi",
            "azure",
            "cloud",
            "vibe"
        };
    }

    public static string[] GetAvailableCloudProviders()
    {
        return new[]
        {
            "azure",
            "google",
            "aws"
        };
    }

    public static bool ValidateEngineSettings(EngineSettings settings)
    {
        return settings.Profile.ToLowerInvariant() switch
        {
            "vosk" or "vosk-multi" => ValidateVoskSettings(settings.Vosk),
            "azure" or "cloud" => ValidateCloudSettings(settings.Cloud),
            "vibe" => ValidateVibeSettings(settings.Vibe),
            _ => false
        };
    }

    private static bool ValidateVoskSettings(VoskEngineSettings settings)
    {
        return !string.IsNullOrEmpty(settings.ModelPath) &&
               Directory.Exists(settings.ModelPath);
    }

    private static bool ValidateCloudSettings(CloudEngineSettings settings)
    {
        return !string.IsNullOrEmpty(settings.Endpoint) &&
               !string.IsNullOrEmpty(settings.ApiKey);
    }

    private static bool ValidateVibeSettings(Config.VibeEngineSettings settings)
    {
        return !string.IsNullOrEmpty(settings.Endpoint) &&
               Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out _);
    }
}
