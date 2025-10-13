using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Diagnostics;

public class ErrorRecoveryManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ErrorState> _componentStates = new();
    private readonly object _lockObject = new();
    private readonly ConcurrentQueue<ErrorRecoveryAction> _recoveryQueue = new();
    private readonly Timer _recoveryTimer;
    private readonly ErrorRecoverySettings _settings;

    public ErrorRecoveryManager(ErrorRecoverySettings? settings = null)
    {
        _settings = settings ?? new ErrorRecoverySettings();
        _recoveryTimer = new Timer(ProcessRecoveryQueue, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(_settings.RecoveryIntervalSeconds));
    }

    public void Dispose()
    {
        _recoveryTimer?.Dispose();
        _componentStates.Clear();

        // Clear remaining recovery queue
        while (_recoveryQueue.TryDequeue(out _))
        { }
    }

    public event EventHandler<ErrorRecoveredEventArgs>? OnErrorRecovered;
    public event EventHandler<RecoveryFailedEventArgs>? OnRecoveryFailed;

    public void ReportError(string componentName, Exception exception, ErrorSeverity severity = ErrorSeverity.Medium)
    {
        lock (_lockObject)
        {
            var state = _componentStates.GetOrAdd(componentName, _ => new ErrorState(componentName));

            state.LastError = exception;
            state.LastErrorTime = DateTime.UtcNow;
            state.ErrorCount++;
            state.CurrentSeverity = severity;

            // Update consecutive failure count
            if (state.LastRecoveryTime < state.LastErrorTime)
            {
                state.ConsecutiveFailures++;
            }

            // Determine if component should be disabled
            if (ShouldDisableComponent(state))
            {
                state.IsDisabled = true;
                state.DisabledUntil = DateTime.UtcNow.AddMinutes(_settings.DisableDurationMinutes);

                Telemetry.LogError("ComponentDisabledDueToErrors", exception, new
                {
                    ComponentName = componentName,
                    state.ErrorCount,
                    state.ConsecutiveFailures,
                    state.DisabledUntil
                });
            }

            // Queue recovery action if appropriate
            if (ShouldAttemptRecovery(state))
            {
                var recoveryAction = DetermineRecoveryAction(state);
                if (recoveryAction != null)
                {
                    _recoveryQueue.Enqueue(recoveryAction);

                    Telemetry.LogEvent("RecoveryActionQueued", new
                    {
                        ComponentName = componentName,
                        ActionType = recoveryAction.ActionType.ToString(),
                        Priority = recoveryAction.Priority.ToString()
                    });
                }
            }

            Telemetry.LogError("ErrorReported", exception, new
            {
                ComponentName = componentName,
                Severity = severity.ToString(),
                state.ErrorCount,
                state.ConsecutiveFailures,
                state.IsDisabled
            });
        }
    }

    public bool IsComponentHealthy(string componentName)
    {
        if (!_componentStates.TryGetValue(componentName, out var state))
            return true; // No errors reported yet

        return !state.IsDisabled &&
               state.ConsecutiveFailures < _settings.MaxConsecutiveFailures &&
               (DateTime.UtcNow - state.LastErrorTime).TotalMinutes > _settings.HealthyThresholdMinutes;
    }

    public bool IsComponentDisabled(string componentName)
    {
        if (!_componentStates.TryGetValue(componentName, out var state))
            return false;

        if (state.IsDisabled && DateTime.UtcNow > state.DisabledUntil)
        {
            // Re-enable component
            state.IsDisabled = false;
            state.ConsecutiveFailures = 0;

            Telemetry.LogEvent("ComponentReEnabled", new { ComponentName = componentName });
        }

        return state.IsDisabled;
    }

    public ComponentHealthInfo GetComponentHealth(string componentName)
    {
        if (!_componentStates.TryGetValue(componentName, out var state))
        {
            return new ComponentHealthInfo
            {
                ComponentName = componentName,
                IsHealthy = true,
                Status = ComponentStatus.Healthy
            };
        }

        var status = DetermineComponentStatus(state);

        return new ComponentHealthInfo
        {
            ComponentName = componentName,
            IsHealthy = status == ComponentStatus.Healthy,
            Status = status,
            ErrorCount = state.ErrorCount,
            ConsecutiveFailures = state.ConsecutiveFailures,
            LastErrorTime = state.LastErrorTime,
            LastRecoveryTime = state.LastRecoveryTime,
            IsDisabled = state.IsDisabled,
            DisabledUntil = state.DisabledUntil,
            LastError = state.LastError?.Message
        };
    }

    public ComponentHealthInfo[] GetAllComponentHealth()
    {
        return _componentStates.Values.Select(state => GetComponentHealth(state.ComponentName)).ToArray();
    }

    public async Task<bool> ForceRecoveryAsync(string componentName)
    {
        if (!_componentStates.TryGetValue(componentName, out _))
            return true; // Component has no errors

        try
        {
            var recoveryAction = new ErrorRecoveryAction
            {
                ComponentName = componentName,
                ActionType = RecoveryActionType.ForceRestart,
                Priority = RecoveryPriority.High,
                QueuedTime = DateTime.UtcNow
            };

            var success = await ExecuteRecoveryActionAsync(recoveryAction);

            if (success)
            {
                ResetComponentState(componentName);
                Telemetry.LogEvent("ForcedRecoverySucceeded", new { ComponentName = componentName });
            }
            else
            {
                Telemetry.LogWarning("ForcedRecoveryFailed", $"Failed to recover component {componentName}");
            }

            return success;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ForcedRecoveryError", ex, new { ComponentName = componentName });
            return false;
        }
    }

    private void ProcessRecoveryQueue(object? state)
    {
        var actionsProcessed = 0;
        var maxActionsPerInterval = _settings.MaxRecoveryActionsPerInterval;

        while (_recoveryQueue.TryDequeue(out var action) && actionsProcessed < maxActionsPerInterval)
        {
            try
            {
                _ = Task.Run(() => ExecuteRecoveryActionAsync(action));
                actionsProcessed++;
            }
            catch (Exception ex)
            {
                Telemetry.LogError("RecoveryQueueProcessingError", ex, new
                {
                    ActionType = action.ActionType.ToString(),
                    action.ComponentName
                });
            }
        }
    }

    private async Task<bool> ExecuteRecoveryActionAsync(ErrorRecoveryAction action)
    {
        try
        {
            Telemetry.LogEvent("RecoveryActionStarted", new
            {
                action.ComponentName,
                ActionType = action.ActionType.ToString(),
                QueueTime = (DateTime.UtcNow - action.QueuedTime).TotalSeconds
            });

            bool success = action.ActionType switch
            {
                RecoveryActionType.Restart => await RestartComponentAsync(action.ComponentName),
                RecoveryActionType.Reset => await ResetComponentAsync(action.ComponentName),
                RecoveryActionType.Reinitialize => await ReinitializeComponentAsync(action.ComponentName),
                RecoveryActionType.ForceRestart => await ForceRestartComponentAsync(action.ComponentName),
                RecoveryActionType.Fallback => await EnableFallbackAsync(action.ComponentName),
                _ => false
            };

            if (_componentStates.TryGetValue(action.ComponentName, out var state))
            {
                if (success)
                {
                    state.LastRecoveryTime = DateTime.UtcNow;
                    state.ConsecutiveFailures = 0;
                    state.SuccessfulRecoveries++;

                    OnErrorRecovered?.Invoke(this, new ErrorRecoveredEventArgs(
                        action.ComponentName, action.ActionType, state.LastError));
                }
                else
                {
                    state.FailedRecoveries++;

                    OnRecoveryFailed?.Invoke(this, new RecoveryFailedEventArgs(
                        action.ComponentName, action.ActionType, state.LastError));
                }
            }

            Telemetry.LogEvent("RecoveryActionCompleted", new
            {
                action.ComponentName,
                ActionType = action.ActionType.ToString(),
                Success = success,
                ExecutionTime = (DateTime.UtcNow - action.QueuedTime).TotalSeconds
            });

            return success;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("RecoveryActionExecutionError", ex, new
            {
                action.ComponentName,
                ActionType = action.ActionType.ToString()
            });
            return false;
        }
    }

    private async Task<bool> RestartComponentAsync(string _)
    {
        // Implementation would depend on the specific component
        await Task.Delay(100); // Simulate restart time
        return true;
    }

    private async Task<bool> ResetComponentAsync(string _)
    {
        // Implementation would depend on the specific component
        await Task.Delay(50); // Simulate reset time
        return true;
    }

    private async Task<bool> ReinitializeComponentAsync(string _)
    {
        // Implementation would depend on the specific component
        await Task.Delay(200); // Simulate reinitialize time
        return true;
    }

    private async Task<bool> ForceRestartComponentAsync(string _)
    {
        // Implementation would depend on the specific component
        await Task.Delay(500); // Simulate force restart time
        return true;
    }

    private async Task<bool> EnableFallbackAsync(string _)
    {
        // Implementation would enable fallback mechanisms
        await Task.Delay(10); // Simulate fallback enablement
        return true;
    }

    private bool ShouldDisableComponent(ErrorState state)
    {
        return state.ConsecutiveFailures >= _settings.MaxConsecutiveFailures ||
               (state.CurrentSeverity == ErrorSeverity.Critical && state.ConsecutiveFailures >= 1);
    }

    private bool ShouldAttemptRecovery(ErrorState state)
    {
        if (state.IsDisabled)
            return false;

        var timeSinceLastRecovery = DateTime.UtcNow - state.LastRecoveryTime;
        return timeSinceLastRecovery.TotalMinutes >= _settings.MinRecoveryIntervalMinutes;
    }

    private ErrorRecoveryAction? DetermineRecoveryAction(ErrorState state)
    {
        var actionType = state.ConsecutiveFailures switch
        {
            1 => RecoveryActionType.Reset,
            2 => RecoveryActionType.Restart,
            3 => RecoveryActionType.Reinitialize,
            >= 4 => RecoveryActionType.Fallback,
            _ => RecoveryActionType.Reset
        };

        var priority = state.CurrentSeverity switch
        {
            ErrorSeverity.Critical => RecoveryPriority.High,
            ErrorSeverity.High => RecoveryPriority.Medium,
            _ => RecoveryPriority.Low
        };

        return new ErrorRecoveryAction
        {
            ComponentName = state.ComponentName,
            ActionType = actionType,
            Priority = priority,
            QueuedTime = DateTime.UtcNow
        };
    }

    private ComponentStatus DetermineComponentStatus(ErrorState state)
    {
        if (state.IsDisabled)
            return ComponentStatus.Disabled;

        if (state.ConsecutiveFailures >= _settings.MaxConsecutiveFailures)
            return ComponentStatus.Critical;

        if (state.ConsecutiveFailures >= 2)
            return ComponentStatus.Degraded;

        if ((DateTime.UtcNow - state.LastErrorTime).TotalMinutes < _settings.HealthyThresholdMinutes)
            return ComponentStatus.Warning;

        return ComponentStatus.Healthy;
    }

    private void ResetComponentState(string componentName)
    {
        if (_componentStates.TryGetValue(componentName, out var state))
        {
            state.ConsecutiveFailures = 0;
            state.IsDisabled = false;
            state.DisabledUntil = DateTime.MinValue;
            state.LastRecoveryTime = DateTime.UtcNow;
        }
    }
}

public class ErrorState
{
    public ErrorState(string componentName)
    {
        ComponentName = componentName;
        LastErrorTime = DateTime.MinValue;
        LastRecoveryTime = DateTime.MinValue;
        DisabledUntil = DateTime.MinValue;
    }

    public string ComponentName { get; }
    public int ErrorCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime LastErrorTime { get; set; }
    public DateTime LastRecoveryTime { get; set; }
    public Exception? LastError { get; set; }
    public ErrorSeverity CurrentSeverity { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime DisabledUntil { get; set; }
    public int SuccessfulRecoveries { get; set; }
    public int FailedRecoveries { get; set; }
}

public class ErrorRecoveryAction
{
    public string ComponentName { get; set; } = "";
    public RecoveryActionType ActionType { get; set; }
    public RecoveryPriority Priority { get; set; }
    public DateTime QueuedTime { get; set; }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class ErrorRecoverySettings
{
    public int MaxConsecutiveFailures { get; set; } = 3;
    public int DisableDurationMinutes { get; set; } = 5;
    public int HealthyThresholdMinutes { get; set; } = 2;
    public int MinRecoveryIntervalMinutes { get; set; } = 1;
    public int RecoveryIntervalSeconds { get; set; } = 5;
    public int MaxRecoveryActionsPerInterval { get; set; } = 3;
}

public class ComponentHealthInfo
{
    public string ComponentName { get; set; } = "";
    public bool IsHealthy { get; set; }
    public ComponentStatus Status { get; set; }
    public int ErrorCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime LastErrorTime { get; set; }
    public DateTime LastRecoveryTime { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime DisabledUntil { get; set; }
    public string? LastError { get; set; }
}

public enum ErrorSeverity
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum RecoveryActionType
{
    Reset,
    Restart,
    Reinitialize,
    ForceRestart,
    Fallback
}

public enum RecoveryPriority
{
    Low,
    Medium,
    High
}

public enum ComponentStatus
{
    Healthy,
    Warning,
    Degraded,
    Critical,
    Disabled
}

public class ErrorRecoveredEventArgs : EventArgs
{
    public ErrorRecoveredEventArgs(string componentName, RecoveryActionType actionType, Exception? previousError)
    {
        ComponentName = componentName;
        ActionType = actionType;
        PreviousError = previousError;
    }

    public string ComponentName { get; }
    public RecoveryActionType ActionType { get; }
    public Exception? PreviousError { get; }
}

public class RecoveryFailedEventArgs : EventArgs
{
    public RecoveryFailedEventArgs(string componentName, RecoveryActionType actionType, Exception? error)
    {
        ComponentName = componentName;
        ActionType = actionType;
        Error = error;
    }

    public string ComponentName { get; }
    public RecoveryActionType ActionType { get; }
    public Exception? Error { get; }
}
