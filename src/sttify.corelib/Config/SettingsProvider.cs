using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Output;

namespace Sttify.Corelib.Config;

public class SettingsProvider
{
    private const int DebounceMs = 250;
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _lockObject = new();
    private SttifySettings? _cachedSettings;
    private volatile bool _configChanged;
    private Timer? _debounceTimer;
    private FileSystemWatcher? _fileWatcher;
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

        SetupFileWatcher();
    }

    public async Task<SttifySettings> GetSettingsAsync()
    {
        lock (_lockObject)
        {
            if (_cachedSettings != null && !_configChanged)
            {
                return _cachedSettings;
            }
        }

        var settings = await LoadSettingsAsync();

        lock (_lockObject)
        {
            _cachedSettings = settings;
            _configChanged = false;
            _lastModified = File.Exists(_configPath) ? File.GetLastWriteTime(_configPath) : DateTime.MinValue;
        }

        return settings;
    }

    public async Task SaveSettingsAsync(SttifySettings settings)
    {
        var json = JsonSerializer.Serialize(settings, _jsonOptions);

        var backupPath = Path.ChangeExtension(_configPath, ".backup.json");
        if (File.Exists(_configPath))
        {
            // Backup with best-effort retry to handle transient locks
            await TryWithRetryAsync(async () =>
            {
                File.Copy(_configPath, backupPath, true);
                await Task.CompletedTask;
            });
        }

        // Temporarily disable file watcher to avoid triggering change event for our own write
        _fileWatcher?.Dispose();

        try
        {
            await WriteAllTextWithRetryAsync(_configPath, json);

            lock (_lockObject)
            {
                _cachedSettings = settings;
                _configChanged = false;
                _lastModified = File.GetLastWriteTime(_configPath);
            }
        }
        finally
        {
            SetupFileWatcher();
        }
    }

    private static async Task TryWithRetryAsync(Func<Task> action, int maxAttempts = 5, int initialDelayMs = 50)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(initialDelayMs * attempt);
            }
        }
    }

    private static async Task WriteAllTextWithRetryAsync(string path, string contents, int maxAttempts = 5, int initialDelayMs = 50)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Explicit FileStream write to control sharing and atomic replace
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    await writer.WriteAsync(contents);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(initialDelayMs * attempt);
            }
        }
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
            var settings = JsonSerializer.Deserialize<SttifySettings>(json, _jsonOptions);

            if (settings == null)
            {
                Telemetry.LogError("SettingsDeserializationFailed",
                    new InvalidOperationException("Settings deserialization returned null"),
                    new { ConfigPath = _configPath });
                return CreateDefaultSettings();
            }

            return settings;
        }
        catch (JsonException ex)
        {
            Telemetry.LogError("SettingsJsonParsingFailed", ex,
                new { ConfigPath = _configPath });

            // Try to backup corrupted file and create new defaults
            await BackupCorruptedConfigAsync();
            return CreateDefaultSettings();
        }
        catch (Exception ex)
        {
            Telemetry.LogError("SettingsLoadFailed", ex,
                new { ConfigPath = _configPath });
            return CreateDefaultSettings();
        }
    }

    private void SetupFileWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            var fileName = Path.GetFileName(_configPath);

            if (directory != null && Directory.Exists(directory))
            {
                _fileWatcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnConfigFileChanged;
                _fileWatcher.Created += OnConfigFileChanged;

                _debounceTimer = new Timer(_ =>
                {
                    lock (_lockObject)
                    { _configChanged = true; }
                    Telemetry.LogEvent("ConfigFileChangedDebounced", new { Path = _configPath });
                }, null, Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("FileWatcherSetupFailed", ex, new { ConfigPath = _configPath });
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce noisy events
        try
        {
            _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
        }
        catch { }

        Telemetry.LogEvent("ConfigFileChanged", new { Path = e.FullPath, ChangeType = e.ChangeType.ToString() });
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
                    ModelPath = GetFirstAvailableModelPath(),
                    Language = "ja",
                    Punctuation = true,
                    EndpointSilenceMs = 800,
                    TokensPerPartial = 5
                },
                Vibe = new VibeEngineSettings
                {
                    Endpoint = "http://localhost:8080",
                    ApiKey = "",
                    Language = "ja",
                    Model = "base",
                    OutputFormat = "json",
                    EnableDiarization = false,
                    EnablePostProcessing = true,
                    AutoCapitalize = true,
                    AutoPunctuation = true,
                    TimeoutSeconds = 30,
                    ProcessingIntervalSeconds = 3.0,
                    MaxBufferSize = 64000
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
                Primary = "sendinput",
                Fallbacks = ["external-process", "stream"],
                SendInput = new SendInputOutputSettings
                {
                    RateLimitCps = 50,
                    CommitKey = null,
                    Ime = new ImeOutputSettings
                    {
                        EnableImeControl = true,
                        CloseImeWhenSending = true,
                        SetAlphanumericMode = true,
                        ClearCompositionString = true,
                        RestoreImeStateAfterSending = true,
                        RestoreDelayMs = 100,
                        SkipWhenImeComposing = true
                    }
                }
            },
            Overlay = new OverlaySettings
            {
                Enabled = true,
                UpdatePerSec = 2,
                MaxChars = 80,
                Topmost = true,
                IsClickThrough = true,
                Opacity = 0.9,
                FontFamily = "Segoe UI",
                FontSize = 28,
                Foreground = "#FFFFFFFF",
                Background = "#7F000000",
                HorizontalAlignment = "Center",
                VerticalAlignment = "Bottom",
                MarginX = 32,
                MarginY = 48
            },
            Hotkeys = new HotkeySettings
            {
                ToggleUi = "Win+Alt+H",
                ToggleMic = "Win+Alt+N",
                StopMic = "Win+Alt+X"
            },
            Privacy = new PrivacySettings
            {
                MaskInLogs = false
            }
        };
    }

    private async Task BackupCorruptedConfigAsync()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var backupPath = $"{_configPath}.corrupted.{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
                await File.WriteAllTextAsync(backupPath, await File.ReadAllTextAsync(_configPath));

                Telemetry.LogEvent("CorruptedConfigBackedUp", new
                {
                    OriginalPath = _configPath,
                    BackupPath = backupPath
                });
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ConfigBackupFailed", ex, new { ConfigPath = _configPath });
        }
    }

    /// <summary>
    /// Gets the first available Vosk model path from installed models, or empty string if none found
    /// </summary>
    private static string GetFirstAvailableModelPath()
    {
        try
        {
            var modelsDirectory = VoskModelManager.GetDefaultModelsDirectory();
            var installedModels = VoskModelManager.GetInstalledModels(modelsDirectory);

            if (installedModels.Length > 0)
            {
                // Prefer recommended models first, then any available model
                var availableModels = VoskModelManager.AvailableModels;
                var recommendedModel = installedModels.FirstOrDefault(installed =>
                    availableModels.Any(available =>
                        available.IsRecommended &&
                        installed.EndsWith(available.Name, StringComparison.OrdinalIgnoreCase)));

                return recommendedModel ?? installedModels[0];
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("GetFirstAvailableModelPathFailed", ex);
        }

        return ""; // Return empty string if no models found
    }

    /// <summary>
    /// Synchronously gets the current settings, primarily for DI container initialization
    /// </summary>
    public SttifySettings GetSettingsSync()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return CreateDefaultSettings();
            }

            var json = File.ReadAllText(_configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return CreateDefaultSettings();
            }

            var settings = JsonSerializer.Deserialize<SttifySettings>(json, _jsonOptions);
            return settings ?? CreateDefaultSettings();
        }
        catch (Exception ex)
        {
            Telemetry.LogError("GetSettingsSyncFailed", ex, new { ConfigPath = _configPath });
            return CreateDefaultSettings();
        }
    }

    public void Dispose()
    {
        _fileWatcher?.Dispose();
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
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class ApplicationSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool ShowInTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool AlwaysOnPrimaryMonitor { get; set; } = false;
    public bool RememberWindowPosition { get; set; } = true;
    public WindowPosition ControlWindow { get; set; } = new();
    public bool EnableDebugHotkeys { get; set; } = false;
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
    public int SampleRate { get; set; } = 16000;
    public string Grammar { get; set; } = "";
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
    public string Primary { get; set; } = "sendinput";
    public string[] Fallbacks { get; set; } = ["external-process", "stream"];
    public int PrimaryOutputIndex { get; set; } = 0; // 0=SendInput, 1=External Process, 2=Stream
    public SendInputOutputSettings SendInput { get; set; } = new();
    public ExternalProcessOutputSettings ExternalProcess { get; set; } = new();
    public StreamOutputSettings Stream { get; set; } = new();
}


[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SendInputOutputSettings
{
    public int RateLimitCps { get; set; } = 50;
    public int? CommitKey { get; set; }
    public ImeOutputSettings Ime { get; set; } = new ImeOutputSettings();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class ImeOutputSettings
{
    public bool EnableImeControl { get; set; } = true;
    public bool CloseImeWhenSending { get; set; } = true;
    public bool SetAlphanumericMode { get; set; } = true;
    public bool ClearCompositionString { get; set; } = true;
    public bool RestoreImeStateAfterSending { get; set; } = true;
    public int RestoreDelayMs { get; set; } = 100;
    public bool SkipWhenImeComposing { get; set; } = true;
}

[ExcludeFromCodeCoverage]
public class ExternalProcessOutputSettings
{
    public string ExecutablePath { get; set; } = "";
    public string ArgumentTemplate { get; set; } = "{text_quoted}";
    public bool WaitForExit { get; set; } = true;
    public int ThrottleMs { get; set; } = 0;
    public int TimeoutMs { get; set; } = 30000;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public bool LogArguments { get; set; } = false;
    public bool LogOutput { get; set; } = false;
    public string WorkingDirectory { get; set; } = "";
}

[ExcludeFromCodeCoverage]
public class StreamOutputSettings
{
    public StreamOutputType OutputType { get; set; } = StreamOutputType.Console;
    public string FilePath { get; set; } = "";
    public bool AppendToFile { get; set; } = true;
    public bool IncludeTimestamp { get; set; } = true;
    public bool ForceFlush { get; set; } = true;
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
    public string SharedMemoryName { get; set; } = "sttify_stream";
    public int SharedMemorySize { get; set; } = 4096;
    public string CustomPrefix { get; set; } = "";
    public string CustomSuffix { get; set; } = "";
}


[ExcludeFromCodeCoverage] // Simple configuration class for overlay UI
public class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public int UpdatePerSec { get; set; } = 2;
    public int MaxChars { get; set; } = 80;
    public int AutoHideMs { get; set; } = 3000;

    // Window behavior
    public bool Topmost { get; set; } = true;
    public bool IsClickThrough { get; set; } = true;
    public double Opacity { get; set; } = 0.9;

    // Animation
    public bool EnableFade { get; set; } = true;
    public int FadeInMs { get; set; } = 120;
    public int FadeOutMs { get; set; } = 120;
    public string FadeEasing { get; set; } = "Cubic"; // Cubic, Quadratic, Sine, Circle, Quartic, Quintic
    public string FadeEaseMode { get; set; } = "Out";  // In, Out, InOut

    // Appearance
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 28;
    public string Foreground { get; set; } = "#FFFFFFFF";
    public string Background { get; set; } = "#00000000"; // transparent by default
    public bool OutlineEnabled { get; set; } = true;
    public string OutlineColor { get; set; } = "#CC000000";
    public double OutlineThickness { get; set; } = 3.0;

    // Layout
    public string HorizontalAlignment { get; set; } = "Center"; // Left/Center/Right/Stretch
    public string VerticalAlignment { get; set; } = "Bottom";   // Top/Center/Bottom/Stretch
    public int MarginX { get; set; } = 32;
    public int MarginY { get; set; } = 48;
    public double MaxWidthRatio { get; set; } = 0.9; // relative to working area width
    public int TargetMonitorIndex { get; set; } = -1; // -1: cursor monitor, -2: primary, >=0: specific index
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class HotkeySettings
{
    public string ToggleUi { get; set; } = "Win+Alt+H";
    public string ToggleMic { get; set; } = "Win+Alt+N";
    public string StopMic { get; set; } = "Win+Alt+X";
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
