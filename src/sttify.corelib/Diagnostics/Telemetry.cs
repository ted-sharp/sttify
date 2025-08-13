using System.Diagnostics.CodeAnalysis;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Collections.Concurrent;
using System.Buffers;

namespace Sttify.Corelib.Diagnostics;

public static class Telemetry
{
    private static ILogger? _logger;
    private static bool _isInitialized;

    // Batching for better I/O performance
    private static readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private static readonly Timer _batchTimer;
    private static readonly object _batchLock = new();
    private static volatile bool _isShuttingDown;
    private const int BatchSize = 50;
    private const int BatchIntervalMs = 100;
    private const int MaxQueueSize = 5000; // backpressure upper bound

    static Telemetry()
    {
        _batchTimer = new Timer(FlushBatch, null, BatchIntervalMs, BatchIntervalMs);
    }

    public static void Initialize(TelemetrySettings? settings = null)
    {
        if (_isInitialized)
            return;

        // Use provided settings or load from configuration
        settings ??= Config.AppConfiguration.GetTelemetrySettings();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "sttify",
            "logs",
            "sttify-.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(settings.MinimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
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

        LogEvent("TelemetryInitialized", new {
            Settings = settings,
            LogLevel = settings.MinimumLevel.ToString(),
            IsDebugMode = Config.AppConfiguration.IsDebugMode()
        });
    }

    public static void LogEvent(string eventName, object? data = null)
    {
        if (!_isInitialized || _isShuttingDown)
            return;

        EnqueueLogEntry(new LogEntry
        {
            Level = LogEventLevel.Information,
            EventName = eventName,
            Data = data,
            Timestamp = DateTime.UtcNow
        });
    }

    public static void LogError(string eventName, Exception exception, object? data = null)
    {
        if (!_isInitialized || _isShuttingDown)
            return;

        EnqueueLogEntry(new LogEntry
        {
            Level = LogEventLevel.Error,
            EventName = eventName,
            Exception = exception,
            Data = data,
            Timestamp = DateTime.UtcNow
        });
    }

    public static void LogWarning(string eventName, string message, object? data = null)
    {
        if (!_isInitialized || _isShuttingDown)
            return;

        EnqueueLogEntry(new LogEntry
        {
            Level = LogEventLevel.Warning,
            EventName = eventName,
            Message = message,
            Data = data,
            Timestamp = DateTime.UtcNow
        });
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

    private static void EnqueueLogEntry(LogEntry entry)
    {
        // Backpressure: drop oldest when exceeding max size
        _logQueue.Enqueue(entry);
        if (_logQueue.Count > MaxQueueSize)
        {
            // Drain a small batch to keep memory bounded
            var drop = 0;
            while (_logQueue.Count > MaxQueueSize && drop < BatchSize && _logQueue.TryDequeue(out _))
            {
                drop++;
            }
        }
        // If queue is getting large, force flush
        if (_logQueue.Count >= BatchSize * 2)
        {
            Task.Run(() => FlushBatch(null));
        }
    }

    private static void FlushBatch(object? state)
    {
        if (_isShuttingDown || !_isInitialized || _logger == null)
            return;

        lock (_batchLock)
        {
            var entries = new List<LogEntry>();

            // Drain up to BatchSize entries
            while (entries.Count < BatchSize && _logQueue.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count == 0)
                return;

            // Write all entries in batch
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.Exception != null)
                    {
                        _logger.Error(entry.Exception, "[{EventName}] {Data}", entry.EventName, entry.Data ?? new { });
                    }
                    else if (!string.IsNullOrEmpty(entry.Message))
                    {
                        _logger.Warning("[{EventName}] {Message} {Data}", entry.EventName, entry.Message, entry.Data ?? new { });
                    }
                    else
                    {
                        _logger.Information("[{EventName}] {Data}", entry.EventName, entry.Data ?? new { });
                    }
                }
                catch (Exception ex)
                {
                    // Fallback logging - write to console if regular logging fails
                    Console.WriteLine($"Telemetry logging failed: {ex.Message}");
                }
            }
        }
    }

    public static void Shutdown()
    {
        _isShuttingDown = true;

        // Flush remaining entries until queue is empty
        for (int i = 0; i < 20; i++) // up to ~2s
        {
            FlushBatch(null);
            if (_logQueue.IsEmpty) break;
            Thread.Sleep(BatchIntervalMs);
        }

        if (_isInitialized && _logger is IDisposable disposableLogger)
        {
            // Log shutdown directly (bypass batching since we're shutting down)
            _logger?.Information("[TelemetryShutdown]");
            disposableLogger.Dispose();
            _isInitialized = false;
        }

        _batchTimer.Dispose();
    }
}

internal class LogEntry
{
    public LogEventLevel Level { get; set; }
    public string EventName { get; set; } = "";
    public string? Message { get; set; }
    public Exception? Exception { get; set; }
    public object? Data { get; set; }
    public DateTime Timestamp { get; set; }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class TelemetrySettings
{
    public bool EnableConsoleLogging { get; set; } = false;
    public bool MaskTextInLogs { get; set; } = false;
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;
}
