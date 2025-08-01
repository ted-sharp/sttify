using Sttify.Corelib.Config;
using Sttify.Corelib.Engine.Cloud;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Engine.Vibe;

namespace Sttify.Corelib.Engine;

public static class EngineFactory
{
    public static ISttEngine CreateEngine(EngineSettings settings)
    {
        return settings.Profile.ToLowerInvariant() switch
        {
            "vosk" => new RealVoskEngineAdapter(settings.Vosk),
            "vosk-multi" => new MultiLanguageVoskAdapter(settings.Vosk),
            "azure" => new AzureSpeechEngine(settings.Cloud),
            "cloud" => CreateCloudEngine(settings.Cloud),
            "vibe" => new VibeSttEngine(settings.Vibe),
            _ => throw new ArgumentException($"Unsupported engine profile: {settings.Profile}")
        };
    }

    private static ISttEngine CreateCloudEngine(CloudEngineSettings settings)
    {
        return settings.Provider.ToLowerInvariant() switch
        {
            "azure" => new AzureSpeechEngine(settings),
            _ => throw new ArgumentException($"Unsupported cloud provider: {settings.Provider}")
        };
    }

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
            "azure"
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