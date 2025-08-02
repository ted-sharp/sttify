using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sttify.Corelib.Config;

public class SettingsProvider
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private SttifySettings? _cachedSettings;
    private DateTime _lastModified;

    public SettingsProvider()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sttifyDir = Path.Combine(appDataPath, "sttify");
        Directory.CreateDirectory(sttifyDir);
        
        _configPath = Path.Combine(sttifyDir, "config.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
    }

    public async Task<SttifySettings> GetSettingsAsync()
    {
        if (_cachedSettings == null || HasConfigChanged())
        {
            _cachedSettings = await LoadSettingsAsync();
            _lastModified = File.Exists(_configPath) ? File.GetLastWriteTime(_configPath) : DateTime.MinValue;
        }
        
        return _cachedSettings;
    }

    public async Task SaveSettingsAsync(SttifySettings settings)
    {
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        
        var backupPath = Path.ChangeExtension(_configPath, ".backup.json");
        if (File.Exists(_configPath))
        {
            File.Copy(_configPath, backupPath, true);
        }
        
        await File.WriteAllTextAsync(_configPath, json);
        
        _cachedSettings = settings;
        _lastModified = File.GetLastWriteTime(_configPath);
    }

    private async Task<SttifySettings> LoadSettingsAsync()
    {
        if (!File.Exists(_configPath))
        {
            var defaultSettings = CreateDefaultSettings();
            await SaveSettingsAsync(defaultSettings);
            return defaultSettings;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<SttifySettings>(json, _jsonOptions) ?? CreateDefaultSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            return CreateDefaultSettings();
        }
    }

    private bool HasConfigChanged()
    {
        if (!File.Exists(_configPath))
            return false;
            
        return File.GetLastWriteTime(_configPath) > _lastModified;
    }

    private static SttifySettings CreateDefaultSettings()
    {
        return new SttifySettings
        {
            Engine = new EngineSettings
            {
                Profile = "vosk",
                Vosk = new VoskEngineSettings
                {
                    ModelPath = "",
                    Language = "ja",
                    Punctuation = true,
                    EndpointSilenceMs = 800,
                    TokensPerPartial = 5
                }
            },
            Session = new SessionSettings
            {
                Mode = "ptt",
                Boundary = new BoundarySettings
                {
                    Delimiter = "。",
                    FinalizeTimeoutMs = 1500
                }
            },
            Output = new OutputSettings
            {
                Primary = "tsf-tip",
                Fallbacks = ["sendinput"],
                Tsf = new TsfOutputSettings
                {
                    CompositionMode = "final-only",
                    SuppressWhenImeComposing = true
                },
                SendInput = new SendInputOutputSettings
                {
                    RateLimitCps = 50,
                    CommitKey = null
                }
            },
            Rtss = new RtssSettings
            {
                Enabled = true,
                UpdatePerSec = 2,
                TruncateLength = 80
            },
            Hotkeys = new HotkeySettings
            {
                ToggleUi = "Win+Alt+H",
                ToggleMic = "Win+Alt+M"
            },
            Privacy = new PrivacySettings
            {
                MaskInLogs = false
            }
        };
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SttifySettings
{
    public ApplicationSettings Application { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public EngineSettings Engine { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public RtssSettings Rtss { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class ApplicationSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool ShowInTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public WindowPosition ControlWindow { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class WindowPosition
{
    public double Left { get; set; } = double.NaN; // NaN means not set
    public double Top { get; set; } = double.NaN;
    public string DisplayConfiguration { get; set; } = "";
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class AudioSettings
{
    public string DeviceId { get; set; } = "";
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class EngineSettings
{
    public string Profile { get; set; } = "vosk";
    public VoskEngineSettings Vosk { get; set; } = new();
    public CloudEngineSettings Cloud { get; set; } = new();
    public VibeEngineSettings Vibe { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class VoskEngineSettings
{
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "ja";
    public bool Punctuation { get; set; } = true;
    public int EndpointSilenceMs { get; set; } = 800;
    public int TokensPerPartial { get; set; } = 5;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class CloudEngineSettings
{
    public string Provider { get; set; } = "azure";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SecretKey { get; set; } = ""; // For AWS Access Secret Key
    public string Language { get; set; } = "ja-JP";
    public string Region { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public bool EnableProfanityFilter { get; set; } = false;
    public bool EnableAutomaticPunctuation { get; set; } = true;
    public Dictionary<string, object> AdditionalSettings { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SessionSettings
{
    public string Mode { get; set; } = "ptt";
    public BoundarySettings Boundary { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class BoundarySettings
{
    public string Delimiter { get; set; } = "。";
    public int FinalizeTimeoutMs { get; set; } = 1500;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class OutputSettings
{
    public string Primary { get; set; } = "tsf-tip";
    public string[] Fallbacks { get; set; } = ["sendinput"];
    public int PrimaryOutputIndex { get; set; } = 0; // 0=TSF, 1=SendInput, 2=External Process
    public TsfOutputSettings Tsf { get; set; } = new();
    public SendInputOutputSettings SendInput { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class TsfOutputSettings
{
    public string CompositionMode { get; set; } = "final-only";
    public bool SuppressWhenImeComposing { get; set; } = true;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SendInputOutputSettings
{
    public int RateLimitCps { get; set; } = 50;
    public int? CommitKey { get; set; }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class RtssSettings
{
    public bool Enabled { get; set; } = true;
    public int UpdatePerSec { get; set; } = 2;
    public int TruncateLength { get; set; } = 80;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class HotkeySettings
{
    public string ToggleUi { get; set; } = "Win+Alt+H";
    public string ToggleMic { get; set; } = "Win+Alt+M";
    public string PushToTalk { get; set; } = "Ctrl+Space";
    public string EmergencyStop { get; set; } = "Ctrl+Alt+X";
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class PrivacySettings
{
    public bool MaskInLogs { get; set; } = false;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class VibeEngineSettings
{
    public string Endpoint { get; set; } = "http://localhost:8080";
    public string ApiKey { get; set; } = "";
    public string Language { get; set; } = "ja";
    public string Model { get; set; } = "base";
    public string OutputFormat { get; set; } = "json";
    public bool EnableDiarization { get; set; } = false;
    public bool EnablePostProcessing { get; set; } = true;
    public bool AutoCapitalize { get; set; } = true;
    public bool AutoPunctuation { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public double ProcessingIntervalSeconds { get; set; } = 3.0;
    public int MaxBufferSize { get; set; } = 64000;
}