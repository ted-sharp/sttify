using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Serilog.Events;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Config;

public class AppConfiguration
{
    private static IConfiguration? _configuration;
    private static readonly object _lock = new();

    public static IConfiguration Configuration
    {
        get
        {
            if (_configuration == null)
            {
                lock (_lock)
                {
                    _configuration ??= BuildConfiguration();
                }
            }
            return _configuration;
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        var builder = new ConfigurationBuilder();
        
        // Add embedded appsettings.json from corelib
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
        
        if (assemblyDir != null)
        {
            var appsettingsPath = Path.Combine(assemblyDir, "appsettings.json");
            if (File.Exists(appsettingsPath))
            {
                builder.AddJsonFile(appsettingsPath, optional: true);
            }
        }
        
        // Add application-specific appsettings.json
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        builder.AddJsonFile(Path.Combine(appDir, "appsettings.json"), optional: true);
        
        // Add environment-specific configuration
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        builder.AddJsonFile($"appsettings.{environment}.json", optional: true);
        
        // Add environment variables
        builder.AddEnvironmentVariables("STTIFY_");
        
        return builder.Build();
    }

    public static LogEventLevel GetLogLevel()
    {
        var isDebug = IsDebugMode();
        var configLogLevel = Configuration["Application:DefaultLogLevel"];
        
        if (isDebug)
        {
            return LogEventLevel.Debug;
        }
        
        return configLogLevel?.ToLowerInvariant() switch
        {
            "debug" => LogEventLevel.Debug,
            "information" => LogEventLevel.Information,
            "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };
    }

    public static bool IsDebugMode()
    {
        #if DEBUG
            return true;
        #else
            return Configuration.GetValue<bool>("Application:EnableDetailedLogging");
        #endif
    }

    public static TelemetrySettings GetTelemetrySettings()
    {
        var settings = new TelemetrySettings();
        Configuration.GetSection("Telemetry").Bind(settings);
        
        // Override with computed log level
        settings.MinimumLevel = GetLogLevel();
        
        #if DEBUG
            settings.EnableConsoleLogging = true;
        #endif
        
        return settings;
    }
}

[ExcludeFromCodeCoverage] // Configuration model
public class EngineInfo
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string Language { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}

[ExcludeFromCodeCoverage] // Configuration model
public class VoskConfiguration
{
    public string ModelsUrl { get; set; } = "";
    public EngineInfo[] RecommendedModels { get; set; } = Array.Empty<EngineInfo>();
}