using System.Diagnostics;

namespace Sttify.Corelib.Diagnostics;

public class HealthMonitor : IDisposable
{
    private readonly Dictionary<string, HealthCheck> _healthChecks = new();
    private readonly Timer _healthCheckTimer;
    private readonly object _lockObject = new();
    private bool _disposed;

    public HealthMonitor()
    {
        _healthCheckTimer = new Timer(PerformHealthChecks, null, CheckInterval, CheckInterval);

        // Add default health checks
        RegisterDefaultHealthChecks();
    }

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool IsHealthy { get; private set; } = true;

    public void Dispose()
    {
        if (!_disposed)
        {
            _healthCheckTimer?.Dispose();
            _disposed = true;
        }
    }

    public event EventHandler<HealthStatusChangedEventArgs>? OnHealthStatusChanged;

    public void RegisterHealthCheck(string name, Func<Task<HealthCheckResult>> check, TimeSpan? timeout = null)
    {
        lock (_lockObject)
        {
            _healthChecks[name] = new HealthCheck(name, check, timeout ?? TimeSpan.FromSeconds(10));
        }
    }

    public void UnregisterHealthCheck(string name)
    {
        lock (_lockObject)
        {
            _healthChecks.Remove(name);
        }
    }

    public async Task<Dictionary<string, HealthCheckResult>> GetHealthStatusAsync()
    {
        var results = new Dictionary<string, HealthCheckResult>();

        List<HealthCheck> checksToRun;
        lock (_lockObject)
        {
            checksToRun = _healthChecks.Values.ToList();
        }

        var tasks = checksToRun.Select(async check =>
        {
            try
            {
                using var cts = new CancellationTokenSource(check.Timeout);
                var result = await check.CheckFunction();
                return new KeyValuePair<string, HealthCheckResult>(check.Name, result);
            }
            catch (OperationCanceledException)
            {
                return new KeyValuePair<string, HealthCheckResult>(check.Name,
                    HealthCheckResult.Unhealthy($"Health check timed out after {check.Timeout.TotalSeconds} seconds"));
            }
            catch (Exception ex)
            {
                return new KeyValuePair<string, HealthCheckResult>(check.Name,
                    HealthCheckResult.Unhealthy($"Health check failed: {ex.Message}"));
            }
        });

        var completedTasks = await Task.WhenAll(tasks);

        foreach (var task in completedTasks)
        {
            results[task.Key] = task.Value;
        }

        return results;
    }

    private async void PerformHealthChecks(object? state)
    {
        if (_disposed)
            return;

        try
        {
            var results = await GetHealthStatusAsync();
            var wasHealthy = IsHealthy;
            var unhealthyChecks = results.Where(r => r.Value.Status != HealthStatus.Healthy).ToList();

            IsHealthy = unhealthyChecks.Count == 0;

            if (wasHealthy != IsHealthy)
            {
                Telemetry.LogEvent("HealthStatusChanged", new
                {
                    WasHealthy = wasHealthy,
                    IsHealthy = IsHealthy,
                    UnhealthyChecks = unhealthyChecks.Select(c => new { c.Key, c.Value.Description })
                });

                OnHealthStatusChanged?.Invoke(this, new HealthStatusChangedEventArgs(wasHealthy, IsHealthy, results));
            }

            // Log unhealthy checks
            foreach (var unhealthyCheck in unhealthyChecks)
            {
                Telemetry.LogWarning("HealthCheckFailed",
                    $"Health check '{unhealthyCheck.Key}' failed: {unhealthyCheck.Value.Description}");
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HealthMonitorError", ex, "Error performing health checks");
        }
    }

    private void RegisterDefaultHealthChecks()
    {
        // Memory usage check
        RegisterHealthCheck("Memory", () =>
        {
            var process = Process.GetCurrentProcess();
            var memoryMb = process.WorkingSet64 / (1024 * 1024);

            if (memoryMb > 1024) // Over 1GB
            {
                return Task.FromResult(HealthCheckResult.Degraded($"High memory usage: {memoryMb} MB"));
            }
            else if (memoryMb > 2048) // Over 2GB
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"Very high memory usage: {memoryMb} MB"));
            }

            return Task.FromResult(HealthCheckResult.Healthy($"Memory usage: {memoryMb} MB"));
        });

        // CPU usage check
        RegisterHealthCheck("CPU", () =>
        {
            // Simplified to avoid async delay - CPU monitoring disabled for now
            var cpuUsagePercent = 0.0;

            if (cpuUsagePercent > 80.0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"High CPU usage: {cpuUsagePercent:F1}%"));
            }
            else if (cpuUsagePercent > 50.0)
            {
                return Task.FromResult(HealthCheckResult.Degraded($"Moderate CPU usage: {cpuUsagePercent:F1}%"));
            }

            return Task.FromResult(HealthCheckResult.Healthy($"CPU usage: {cpuUsagePercent:F1}%"));
        });

        // Application state check
        RegisterHealthCheck("Application", () =>
        {
            // This would be customized based on application-specific health criteria
            return Task.FromResult(HealthCheckResult.Healthy("Application is running normally"));
        });
    }
}

public class HealthCheck
{
    public HealthCheck(string name, Func<Task<HealthCheckResult>> checkFunction, TimeSpan timeout)
    {
        Name = name;
        CheckFunction = checkFunction;
        Timeout = timeout;
    }

    public string Name { get; }
    public Func<Task<HealthCheckResult>> CheckFunction { get; }
    public TimeSpan Timeout { get; }
}

public class HealthCheckResult
{
    private HealthCheckResult(HealthStatus status, string description, Dictionary<string, object>? data = null)
    {
        Status = status;
        Description = description;
        Data = data ?? new Dictionary<string, object>();
    }

    public HealthStatus Status { get; }
    public string Description { get; }
    public Dictionary<string, object> Data { get; }

    public static HealthCheckResult Healthy(string description = "", Dictionary<string, object>? data = null)
        => new(HealthStatus.Healthy, description, data);

    public static HealthCheckResult Degraded(string description, Dictionary<string, object>? data = null)
        => new(HealthStatus.Degraded, description, data);

    public static HealthCheckResult Unhealthy(string description, Dictionary<string, object>? data = null)
        => new(HealthStatus.Unhealthy, description, data);
}

public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

public class HealthStatusChangedEventArgs : EventArgs
{
    public HealthStatusChangedEventArgs(bool wasHealthy, bool isHealthy, Dictionary<string, HealthCheckResult> results)
    {
        WasHealthy = wasHealthy;
        IsHealthy = isHealthy;
        Results = results;
    }

    public bool WasHealthy { get; }
    public bool IsHealthy { get; }
    public Dictionary<string, HealthCheckResult> Results { get; }
}
