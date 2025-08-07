using Sttify.Corelib.Config;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Engine.Vibe;

namespace Sttify.Corelib.Engine;

public static class SttEngineFactory
{
    public static ISttEngine CreateEngine(EngineSettings engineSettings)
    {
        System.Diagnostics.Debug.WriteLine($"*** SttEngineFactory.CreateEngine - Profile: {engineSettings.Profile} ***");
        var engine = engineSettings.Profile.ToLowerInvariant() switch
        {
            "vosk" => CreateVoskEngine(engineSettings.Vosk),
            "vosk-real" => new RealVoskEngineAdapter(engineSettings.Vosk),
            "vosk-mock" => new VoskEngineAdapter(engineSettings.Vosk),
            "vibe" => new VibeSttEngine(engineSettings.Vibe),
            _ => throw new NotSupportedException($"Engine profile '{engineSettings.Profile}' is not supported")
        };
        System.Diagnostics.Debug.WriteLine($"*** SttEngineFactory.CreateEngine - Created: {engine.GetType().Name} ***");
        return engine;
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

        var optionalFiles = new[]
        {
            "conf/model.conf",       // Model configuration
            "graph/phones.txt"       // Phone list
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
        return new[]
        {
            "vosk",
            "vosk-real", 
            "vosk-mock",
            "vibe"
        };
    }

    public static string GetEngineDescription(string engineProfile)
    {
        return engineProfile.ToLowerInvariant() switch
        {
            "vosk" => "Vosk (Auto-detect real/mock)",
            "vosk-real" => "Vosk (Real implementation)",
            "vosk-mock" => "Vosk (Mock implementation for testing)",
            "vibe" => "Vibe (HTTP API-based speech recognition)",
            _ => "Unknown engine"
        };
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