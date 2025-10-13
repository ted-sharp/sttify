using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Sttify.Corelib.Diagnostics;

public class DiagnosticCollector : IDisposable
{
    private readonly Timer _collectionTimer;
    private readonly Process _currentProcess;
    private readonly ConcurrentDictionary<string, DiagnosticData> _diagnostics = new();
    private readonly DiagnosticSettings _settings;
    private DateTime _lastCpuTime;
    private TimeSpan _lastTotalProcessorTime;

    public DiagnosticCollector(DiagnosticSettings? settings = null)
    {
        _settings = settings ?? new DiagnosticSettings();
        _currentProcess = Process.GetCurrentProcess();
        _lastCpuTime = DateTime.UtcNow;
        _lastTotalProcessorTime = _currentProcess.TotalProcessorTime;

        _collectionTimer = new Timer(CollectDiagnostics, null,
            TimeSpan.Zero, TimeSpan.FromSeconds(_settings.CollectionIntervalSeconds));
    }

    public void Dispose()
    {
        _collectionTimer?.Dispose();
        _currentProcess?.Dispose();
        _diagnostics.Clear();
    }

    public event EventHandler<DiagnosticDataCollectedEventArgs>? OnDataCollected;
    public event EventHandler<DiagnosticThresholdExceededEventArgs>? OnThresholdExceeded;

    private void CollectDiagnostics(object? state)
    {
        try
        {
            var timestamp = DateTime.UtcNow;

            // Collect system diagnostics
            CollectSystemDiagnostics(timestamp);

            // Collect application diagnostics
            CollectApplicationDiagnostics(timestamp);

            // Collect custom diagnostics
            CollectCustomDiagnostics(timestamp);

            // Check thresholds
            CheckThresholds(timestamp);

            // Trigger event
            OnDataCollected?.Invoke(this, new DiagnosticDataCollectedEventArgs(timestamp, GetCurrentSnapshot()));

            Telemetry.LogEvent("DiagnosticsCollected", new
            {
                Timestamp = timestamp,
                DiagnosticCount = _diagnostics.Count
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("DiagnosticCollectionError", ex);
        }
    }

    private void CollectSystemDiagnostics(DateTime timestamp)
    {
        try
        {
            // CPU Usage (approximate calculation)
            var currentTime = DateTime.UtcNow;
            var currentTotalProcessorTime = _currentProcess.TotalProcessorTime;

            var timeDiff = (currentTime - _lastCpuTime).TotalMilliseconds;
            var cpuTimeDiff = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;

            var cpuUsage = timeDiff > 0 ? Math.Min(100.0, (cpuTimeDiff / timeDiff) * 100.0) : 0.0;

            _lastCpuTime = currentTime;
            _lastTotalProcessorTime = currentTotalProcessorTime;

            UpdateDiagnostic("System.CPU.UsagePercent", cpuUsage, timestamp);

            // Memory Usage
            var totalMemory = GC.GetTotalMemory(false);
            var workingSet = _currentProcess.WorkingSet64;
            var privateMemory = _currentProcess.PrivateMemorySize64;

            UpdateDiagnostic("System.Memory.TotalBytes", totalMemory, timestamp);
            UpdateDiagnostic("System.Memory.WorkingSetBytes", workingSet, timestamp);
            UpdateDiagnostic("System.Memory.PrivateBytes", privateMemory, timestamp);

            // GC Information
            var gen0Collections = GC.CollectionCount(0);
            var gen1Collections = GC.CollectionCount(1);
            var gen2Collections = GC.CollectionCount(2);

            UpdateDiagnostic("System.GC.Gen0Collections", gen0Collections, timestamp);
            UpdateDiagnostic("System.GC.Gen1Collections", gen1Collections, timestamp);
            UpdateDiagnostic("System.GC.Gen2Collections", gen2Collections, timestamp);

            // Thread Count
            var threadCount = _currentProcess.Threads.Count;
            UpdateDiagnostic("System.Threads.Count", threadCount, timestamp);

            // Handle Count
            var handleCount = _currentProcess.HandleCount;
            UpdateDiagnostic("System.Handles.Count", handleCount, timestamp);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("SystemDiagnosticsCollectionError", ex);
        }
    }

    private void CollectApplicationDiagnostics(DateTime timestamp)
    {
        try
        {
            // Application uptime
            var uptime = DateTime.UtcNow - _currentProcess.StartTime.ToUniversalTime();
            UpdateDiagnostic("App.UptimeSeconds", uptime.TotalSeconds, timestamp);

            // JIT compilation
            var jitTime = _currentProcess.TotalProcessorTime.TotalMilliseconds;
            UpdateDiagnostic("App.JIT.TotalMs", jitTime, timestamp);

            // Assembly count
            var assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            UpdateDiagnostic("App.Assemblies.Count", assemblyCount, timestamp);

            // Exception count (from global handler if available)
            // This would need to be tracked separately in the application

        }
        catch (Exception ex)
        {
            Telemetry.LogError("ApplicationDiagnosticsCollectionError", ex);
        }
    }

    private void CollectCustomDiagnostics(DateTime _)
    {
        // This method can be extended by specific components to add their own diagnostics
        // For now, we'll add some basic counters that can be incremented by other parts of the system
    }

    public void UpdateCustomDiagnostic(string key, double value, DateTime? timestamp = null)
    {
        UpdateDiagnostic($"Custom.{key}", value, timestamp ?? DateTime.UtcNow);
    }

    public void IncrementCounter(string counterName, double increment = 1.0)
    {
        var key = $"Counter.{counterName}";
        var current = GetCurrentValue(key);
        UpdateDiagnostic(key, current + increment, DateTime.UtcNow);
    }

    public void RecordLatency(string operationName, TimeSpan latency)
    {
        var key = $"Latency.{operationName}";
        UpdateDiagnostic($"{key}.LastMs", latency.TotalMilliseconds, DateTime.UtcNow);

        // Also track average
        var avgKey = $"{key}.AvgMs";
        var currentAvg = GetCurrentValue(avgKey);
        var count = GetCurrentValue($"{key}.Count") + 1;
        var newAvg = (currentAvg * (count - 1) + latency.TotalMilliseconds) / count;

        UpdateDiagnostic(avgKey, newAvg, DateTime.UtcNow);
        UpdateDiagnostic($"{key}.Count", count, DateTime.UtcNow);
    }

    public void RecordThroughput(string operationName, double itemsProcessed)
    {
        var key = $"Throughput.{operationName}";
        var timestamp = DateTime.UtcNow;

        UpdateDiagnostic($"{key}.TotalItems", GetCurrentValue($"{key}.TotalItems") + itemsProcessed, timestamp);
        UpdateDiagnostic($"{key}.LastBatch", itemsProcessed, timestamp);

        // Calculate items per second over last minute
        var windowKey = $"{key}.PerSecond";
        if (_diagnostics.TryGetValue(windowKey, out var existing))
        {
            var timeDiff = (timestamp - existing.LastUpdated).TotalSeconds;
            if (timeDiff > 0)
            {
                var rate = itemsProcessed / timeDiff;
                UpdateDiagnostic(windowKey, rate, timestamp);
            }
        }
        else
        {
            UpdateDiagnostic(windowKey, itemsProcessed, timestamp);
        }
    }

    private void UpdateDiagnostic(string key, double value, DateTime timestamp)
    {
        _diagnostics.AddOrUpdate(key,
            new DiagnosticData(key, value, timestamp),
            (_, existing) =>
            {
                existing.PreviousValue = existing.CurrentValue;
                existing.CurrentValue = value;
                existing.LastUpdated = timestamp;
                existing.UpdateCount++;

                // Update min/max
                if (value < existing.MinValue)
                    existing.MinValue = value;
                if (value > existing.MaxValue)
                    existing.MaxValue = value;

                // Update moving average (simple exponential)
                existing.MovingAverage = existing.MovingAverage * 0.9 + value * 0.1;

                return existing;
            });
    }

    private double GetCurrentValue(string key)
    {
        return _diagnostics.TryGetValue(key, out var data) ? data.CurrentValue : 0.0;
    }

    private void CheckThresholds(DateTime timestamp)
    {
        foreach (var threshold in _settings.Thresholds)
        {
            if (_diagnostics.TryGetValue(threshold.Key, out var data))
            {
                bool exceeded = threshold.ComparisonType switch
                {
                    ThresholdComparison.GreaterThan => data.CurrentValue > threshold.Value,
                    ThresholdComparison.LessThan => data.CurrentValue < threshold.Value,
                    ThresholdComparison.Equals => Math.Abs(data.CurrentValue - threshold.Value) < 0.001,
                    _ => false
                };

                if (exceeded && (timestamp - data.LastThresholdAlert).TotalMinutes >= threshold.AlertCooldownMinutes)
                {
                    data.LastThresholdAlert = timestamp;

                    OnThresholdExceeded?.Invoke(this, new DiagnosticThresholdExceededEventArgs(
                        threshold.Key, data.CurrentValue, threshold.Value, threshold.ComparisonType));

                    Telemetry.LogWarning("DiagnosticThresholdExceeded", $"Threshold exceeded for {threshold.Key}", new
                    {
                        threshold.Key,
                        data.CurrentValue,
                        ThresholdValue = threshold.Value,
                        ComparisonType = threshold.ComparisonType.ToString()
                    });
                }
            }
        }
    }

    public DiagnosticSnapshot GetCurrentSnapshot()
    {
        var snapshot = new DiagnosticSnapshot
        {
            Timestamp = DateTime.UtcNow,
            Data = _diagnostics.Values.ToDictionary(d => d.Key, d => d.Clone())
        };

        return snapshot;
    }

    public DiagnosticData? GetDiagnostic(string key)
    {
        return _diagnostics.TryGetValue(key, out var data) ? data.Clone() : null;
    }

    public string[] GetAvailableKeys()
    {
        return _diagnostics.Keys.ToArray();
    }

    public string ExportToJson()
    {
        var snapshot = GetCurrentSnapshot();
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
        Telemetry.LogEvent("DiagnosticsCleared");
    }

    public void RemoveDiagnostic(string key)
    {
        if (_diagnostics.TryRemove(key, out _))
        {
            Telemetry.LogEvent("DiagnosticRemoved", new { Key = key });
        }
    }
}

public class DiagnosticData
{
    public DiagnosticData(string key, double value, DateTime timestamp)
    {
        Key = key;
        CurrentValue = value;
        PreviousValue = value;
        MinValue = value;
        MaxValue = value;
        MovingAverage = value;
        LastUpdated = timestamp;
        LastThresholdAlert = DateTime.MinValue;
        UpdateCount = 1;
    }

    public string Key { get; }
    public double CurrentValue { get; set; }
    public double PreviousValue { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public double MovingAverage { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime LastThresholdAlert { get; set; }
    public int UpdateCount { get; set; }

    public DiagnosticData Clone()
    {
        return new DiagnosticData(Key, CurrentValue, LastUpdated)
        {
            PreviousValue = PreviousValue,
            MinValue = MinValue,
            MaxValue = MaxValue,
            MovingAverage = MovingAverage,
            LastThresholdAlert = LastThresholdAlert,
            UpdateCount = UpdateCount
        };
    }
}

public class DiagnosticSnapshot
{
    public DateTime Timestamp { get; set; }
    public Dictionary<string, DiagnosticData> Data { get; set; } = new();
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class DiagnosticSettings
{
    public int CollectionIntervalSeconds { get; set; } = 10;
    public DiagnosticThreshold[] Thresholds { get; set; } = Array.Empty<DiagnosticThreshold>();
    public bool EnableSystemDiagnostics { get; set; } = true;
    public bool EnableApplicationDiagnostics { get; set; } = true;
    public bool EnableCustomDiagnostics { get; set; } = true;
}

public class DiagnosticThreshold
{
    public string Key { get; set; } = "";
    public double Value { get; set; }
    public ThresholdComparison ComparisonType { get; set; }
    public int AlertCooldownMinutes { get; set; } = 5;
}

public enum ThresholdComparison
{
    GreaterThan,
    LessThan,
    Equals
}

public class DiagnosticDataCollectedEventArgs : EventArgs
{
    public DiagnosticDataCollectedEventArgs(DateTime timestamp, DiagnosticSnapshot snapshot)
    {
        Timestamp = timestamp;
        Snapshot = snapshot;
    }

    public DateTime Timestamp { get; }
    public DiagnosticSnapshot Snapshot { get; }
}

public class DiagnosticThresholdExceededEventArgs : EventArgs
{
    public DiagnosticThresholdExceededEventArgs(string key, double currentValue, double thresholdValue, ThresholdComparison comparisonType)
    {
        Key = key;
        CurrentValue = currentValue;
        ThresholdValue = thresholdValue;
        ComparisonType = comparisonType;
    }

    public string Key { get; }
    public double CurrentValue { get; }
    public double ThresholdValue { get; }
    public ThresholdComparison ComparisonType { get; }
}
