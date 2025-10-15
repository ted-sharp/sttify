using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine.Cloud;
using Sttify.Corelib.Engine.Vibe;
using Sttify.Corelib.Engine.Vosk;

namespace Sttify.Corelib.Engine;

public static class SttEngineFactory
{
    private const string AzureProvider = "azure";
    private const string GoogleProvider = "google";
    private const string AwsProvider = "aws";
    private const string VoskProvider = "vosk";

    public static ISttEngine CreateEngine(EngineSettings engineSettings)
    {
        var profile = engineSettings.Profile?.ToLowerInvariant() ?? VoskProvider;
        System.Diagnostics.Debug.WriteLine($"*** SttEngineFactory.CreateEngine - Profile: {profile} ***");
        var engine = profile switch
        {
            VoskProvider => CreateVoskEngine(engineSettings.Vosk),
            "vosk-multi" => new MultiLanguageVoskAdapter(engineSettings.Vosk),
            "vosk-real" => new RealVoskEngineAdapter(engineSettings.Vosk),
            "vosk-mock" => new VoskEngineAdapter(engineSettings.Vosk),
            "vibe" => new VibeSttEngine(engineSettings.Vibe),
            AzureProvider => new AzureSpeechEngine(engineSettings.Cloud),
            "cloud" => CreateCloudEngine(engineSettings.Cloud),
            GoogleProvider or AwsProvider => CreateCloudEngine(engineSettings.Cloud),
            _ => FallbackToDefault(engineSettings, profile)
        };
        System.Diagnostics.Debug.WriteLine($"*** SttEngineFactory.CreateEngine - Created: {engine.GetType().Name} ***");
        return engine;
    }

    private static ISttEngine CreateCloudEngine(CloudEngineSettings settings)
    {
        var provider = settings.Provider?.ToLowerInvariant();
        return provider switch
        {
            AzureProvider => new AzureSpeechEngine(settings),
            GoogleProvider => new GoogleCloudSpeechEngine(settings),
            AwsProvider => new AwsTranscribeEngine(settings),
            _ => throw new ArgumentException($"Unsupported cloud provider: {settings.Provider}")
        };
    }

    private static ISttEngine CreateVoskEngine(VoskEngineSettings voskSettings)
    {
        System.Diagnostics.Debug.WriteLine($"*** CreateVoskEngine - ModelPath: '{voskSettings.ModelPath}' ***");
        // Check if Vosk model exists and is valid, then decide which implementation to use
        bool isValidModel = IsValidVoskModel(voskSettings.ModelPath);
        System.Diagnostics.Debug.WriteLine($"*** CreateVoskEngine - IsValidModel: {isValidModel} ***");

        if (isValidModel)
        {
            System.Diagnostics.Debug.WriteLine("*** CreateVoskEngine - Using RealVoskEngineAdapter ***");
            return new RealVoskEngineAdapter(voskSettings);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("*** CreateVoskEngine - Using VoskEngineAdapter (Mock) ***");
            // Fall back to mock implementation for development/testing
            return new VoskEngineAdapter(voskSettings);
        }
    }

    private static bool IsValidVoskModel(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || !Directory.Exists(modelPath))
            return false;

        // Check for essential Vosk model files
        var requiredFiles = new[]
        {
            "am/final.mdl",          // Acoustic model
            "graph/HCLG.fst",        // Grammar/language model
            "graph/words.txt",       // Word list
            "ivector/final.ie"       // ivector extractor (optional but common)
        };

        // Check if at least the core files exist
        int foundRequiredFiles = 0;
        foreach (var file in requiredFiles)
        {
            if (File.Exists(Path.Combine(modelPath, file)))
                foundRequiredFiles++;
        }

        // We need at least the acoustic model and some language model files
        return foundRequiredFiles >= 2;
    }

    public static string[] GetAvailableEngines()
    {
        return
        [
            VoskProvider,
            "vosk-multi",
            "vosk-real",
            "vosk-mock",
            AzureProvider,
            "cloud",
            "vibe"
        ];
    }

    public static string GetEngineDescription(string engineProfile)
    {
        return engineProfile.ToLowerInvariant() switch
        {
            VoskProvider => "Vosk (Auto-detect real/mock)",
            "vosk-multi" => "Vosk (Multi-language adapter)",
            "vosk-real" => "Vosk (Real implementation)",
            "vosk-mock" => "Vosk (Mock implementation for testing)",
            GoogleProvider => "Google Cloud Speech (via Cloud settings)",
            AwsProvider => "AWS Transcribe (via Cloud settings)",
            AzureProvider => "Azure Cognitive Services (Cloud)",
            "cloud" => "Cloud (Azure/Google/AWS via settings)",
            "vibe" => "Vibe (HTTP API-based speech recognition)",
            _ => "Unknown engine"
        };
    }

    private static ISttEngine FallbackToDefault(EngineSettings engineSettings, string invalidProfile)
    {
        Telemetry.LogWarning("EngineProfileUnsupported", $"Unsupported engine profile '{invalidProfile}', falling back to 'vosk'", new { Profile = invalidProfile });
        // Do not mutate incoming settings; just use Vosk-safe creation based on current Vosk settings
        return CreateVoskEngine(engineSettings.Vosk);
    }

    public static bool IsEngineAvailable(string engineProfile)
    {
        try
        {
            var dummySettings = new EngineSettings
            {
                Profile = engineProfile,
                Vosk = new VoskEngineSettings
                {
                    ModelPath = "",
                    Language = "ja"
                },
                Vibe = new VibeEngineSettings
                {
                    Endpoint = "http://localhost:3022",
                    Language = "ja"
                }
            };

            // Try to create the engine - if it throws NotSupportedException, it's not available
            var engine = CreateEngine(dummySettings);
            engine.Dispose();
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch
        {
            // Other exceptions might indicate the engine is available but misconfigured
            return true;
        }
    }
}
