using System.Diagnostics.CodeAnalysis;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Sttify.Corelib.Diagnostics;

public static class Telemetry
{
    private static ILogger? _logger;
    private static bool _isInitialized;

    public static void Initialize(TelemetrySettings settings)
    {
        if (_isInitialized)
            return;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "sttify",
            "logs",
            "sttify-.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                new CompactJsonFormatter(),
                logPath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 7);

        if (settings.EnableConsoleLogging)
        {
            logConfig.WriteTo.Console();
        }

        _logger = logConfig.CreateLogger();
        _isInitialized = true;

        LogEvent("TelemetryInitialized", new { Settings = settings });
    }

    public static void LogEvent(string eventName, object? data = null)
    {
        if (!_isInitialized || _logger == null)
            return;

        _logger.Information("[{EventName}] {Data}", eventName, data ?? new { });
    }

    public static void LogError(string eventName, Exception exception, object? data = null)
    {
        if (!_isInitialized || _logger == null)
            return;

        _logger.Error(exception, "[{EventName}] {Data}", eventName, data ?? new { });
    }

    public static void LogWarning(string eventName, string message, object? data = null)
    {
        if (!_isInitialized || _logger == null)
            return;

        _logger.Warning("[{EventName}] {Message} {Data}", eventName, message, data ?? new { });
    }

    public static void LogRecognition(string text, bool isFinal, double confidence, bool maskText = false)
    {
        var logData = new
        {
            Text = maskText ? MaskText(text) : text,
            IsFinal = isFinal,
            Confidence = confidence,
            Timestamp = DateTime.UtcNow
        };

        LogEvent("Recognition", logData);
    }

    public static void LogAudioCapture(int frameSize, double level)
    {
        var logData = new
        {
            FrameSize = frameSize,
            Level = level,
            Timestamp = DateTime.UtcNow
        };

        LogEvent("AudioCapture", logData);
    }

    public static void LogOutputSent(string sinkName, string text, bool success, bool maskText = false)
    {
        var logData = new
        {
            SinkName = sinkName,
            Text = maskText ? MaskText(text) : text,
            Success = success,
            Timestamp = DateTime.UtcNow
        };

        LogEvent("OutputSent", logData);
    }

    private static string MaskText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        if (text.Length <= 4)
            return new string('*', text.Length);

        return text[..2] + new string('*', text.Length - 4) + text[^2..];
    }

    public static void Shutdown()
    {
        if (_isInitialized && _logger is IDisposable disposableLogger)
        {
            LogEvent("TelemetryShutdown");
            disposableLogger.Dispose();
            _isInitialized = false;
        }
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class TelemetrySettings
{
    public bool EnableConsoleLogging { get; set; } = false;
    public bool MaskTextInLogs { get; set; } = false;
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;
}