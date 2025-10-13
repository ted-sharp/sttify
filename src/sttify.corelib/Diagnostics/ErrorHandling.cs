namespace Sttify.Corelib.Diagnostics;

/// <summary>
/// Categorizes errors by their nature and recovery potential
/// </summary>
public enum ErrorCategory
{
    /// <summary>
    /// Temporary errors that may resolve themselves (network timeouts, temporary resource unavailability)
    /// </summary>
    Transient,

    /// <summary>
    /// Configuration-related errors (invalid settings, missing files, incorrect values)
    /// </summary>
    Configuration,

    /// <summary>
    /// Hardware or system-level errors (audio device failures, insufficient resources)
    /// </summary>
    Hardware,

    /// <summary>
    /// Integration errors with external systems (external process errors, API failures)
    /// </summary>
    Integration,

    /// <summary>
    /// Critical errors that may compromise application stability
    /// </summary>
    Critical
}

// ErrorSeverity enum is defined in ErrorRecoveryManager.cs

/// <summary>
/// Determines how users should be notified about errors
/// </summary>
public enum NotificationMode
{
    /// <summary>
    /// No user notification, log only
    /// </summary>
    None,

    /// <summary>
    /// Silent notification via system tray or status indicator
    /// </summary>
    Silent,

    /// <summary>
    /// Brief toast notification
    /// </summary>
    Toast,

    /// <summary>
    /// Modal dialog requiring user acknowledgment
    /// </summary>
    Dialog
}

/// <summary>
/// Comprehensive error information for structured error handling
/// </summary>
public class ErrorContext
{
    public ErrorContext(
        string componentName,
        string errorCode,
        string message,
        Exception? exception = null,
        ErrorCategory category = ErrorCategory.Critical,
        ErrorSeverity severity = ErrorSeverity.High,
        NotificationMode notificationMode = NotificationMode.Toast,
        Dictionary<string, object>? contextData = null)
    {
        ComponentName = componentName;
        ErrorCode = errorCode;
        Message = message;
        Exception = exception;
        Category = category;
        Severity = severity;
        NotificationMode = notificationMode;
        ContextData = contextData ?? new Dictionary<string, object>();
        Timestamp = DateTime.UtcNow;
    }

    public string ComponentName { get; }
    public string ErrorCode { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    public ErrorCategory Category { get; }
    public ErrorSeverity Severity { get; }
    public NotificationMode NotificationMode { get; }
    public Dictionary<string, object> ContextData { get; }
    public DateTime Timestamp { get; }
}

/// <summary>
/// Event arguments for error events that include comprehensive error context
/// </summary>
public class ErrorEventArgs : EventArgs
{
    public ErrorEventArgs(ErrorContext errorContext)
    {
        ErrorContext = errorContext;
    }

    public ErrorContext ErrorContext { get; }
}

// ErrorRecoveryEventArgs is defined in ErrorRecovery.cs

/// <summary>
/// Centralized error handling and notification system
/// </summary>
public static class ErrorHandler
{
    /// <summary>
    /// Global error event that components can subscribe to for error notifications
    /// </summary>
    public static event EventHandler<ErrorEventArgs>? OnError;

    // Error recovery events are handled through Telemetry logging

    /// <summary>
    /// Handles an error with comprehensive logging and notification
    /// </summary>
    public static void HandleError(ErrorContext errorContext)
    {
        // Log the error with structured data
        var logData = new Dictionary<string, object>
        {
            ["Component"] = errorContext.ComponentName,
            ["ErrorCode"] = errorContext.ErrorCode,
            ["Category"] = errorContext.Category.ToString(),
            ["Severity"] = errorContext.Severity.ToString(),
            ["NotificationMode"] = errorContext.NotificationMode.ToString(),
            ["Message"] = errorContext.Message
        };

        // Add context data
        foreach (var kvp in errorContext.ContextData)
        {
            logData[$"Context.{kvp.Key}"] = kvp.Value;
        }

        // Log based on severity
        if (errorContext.Exception != null)
        {
            Telemetry.LogError(errorContext.ErrorCode, errorContext.Exception, logData);
        }
        else
        {
            switch (errorContext.Severity)
            {
                case ErrorSeverity.Critical:
                    Telemetry.LogError(errorContext.ErrorCode, new InvalidOperationException(errorContext.Message), logData);
                    break;
                case ErrorSeverity.High:
                    Telemetry.LogWarning(errorContext.ErrorCode, errorContext.Message, logData);
                    break;
                case ErrorSeverity.Medium:
                    Telemetry.LogEvent(errorContext.ErrorCode, logData);
                    break;
                case ErrorSeverity.Low:
                    Telemetry.LogEvent(errorContext.ErrorCode, logData);
                    break;
            }
        }

        // Notify subscribers
        OnError?.Invoke(null, new ErrorEventArgs(errorContext));
    }
}

/// <summary>
/// Helper class for creating common error contexts
/// </summary>
public static class ErrorContextFactory
{
    public static ErrorContext AudioDeviceFailure(string deviceName, Exception? exception = null)
    {
        return new ErrorContext(
            "AudioCapture",
            "AUDIO_DEVICE_FAILURE",
            $"Failed to access audio device: {deviceName}",
            exception,
            ErrorCategory.Hardware,
            ErrorSeverity.High,
            NotificationMode.Toast,
            new Dictionary<string, object> { ["DeviceName"] = deviceName }
        );
    }

    public static ErrorContext ConfigurationError(string setting, string? value, Exception? exception = null)
    {
        return new ErrorContext(
            "Configuration",
            "CONFIG_INVALID_VALUE",
            $"Invalid configuration value for '{setting}': {value}",
            exception,
            ErrorCategory.Configuration,
            ErrorSeverity.Medium,
            NotificationMode.Silent,
            new Dictionary<string, object>
            {
                ["Setting"] = setting,
                ["Value"] = value ?? "null"
            }
        );
    }

    public static ErrorContext SttEngineFailure(string engineName, string operation, Exception? exception = null)
    {
        return new ErrorContext(
            "SttEngine",
            "STT_ENGINE_FAILURE",
            $"STT engine '{engineName}' failed during '{operation}'",
            exception,
            ErrorCategory.Integration,
            ErrorSeverity.High,
            NotificationMode.Toast,
            new Dictionary<string, object>
            {
                ["EngineName"] = engineName,
                ["Operation"] = operation
            }
        );
    }

    public static ErrorContext OutputSinkFailure(string sinkName, string operation, Exception? exception = null)
    {
        return new ErrorContext(
            "OutputSink",
            "OUTPUT_SINK_FAILURE",
            $"Output sink '{sinkName}' failed during '{operation}'",
            exception,
            ErrorCategory.Integration,
            ErrorSeverity.Medium,
            NotificationMode.Silent,
            new Dictionary<string, object>
            {
                ["SinkName"] = sinkName,
                ["Operation"] = operation
            }
        );
    }

    public static ErrorContext NetworkConnectionFailure(string service, Exception? exception = null)
    {
        return new ErrorContext(
            "NetworkService",
            "NETWORK_CONNECTION_FAILURE",
            $"Failed to connect to service: {service}",
            exception,
            ErrorCategory.Transient,
            ErrorSeverity.Medium,
            NotificationMode.Silent,
            new Dictionary<string, object> { ["Service"] = service }
        );
    }

    public static ErrorContext PermissionDenied(string resource, string operation, Exception? exception = null)
    {
        return new ErrorContext(
            "Security",
            "PERMISSION_DENIED",
            $"Permission denied for '{operation}' on '{resource}'",
            exception,
            ErrorCategory.Hardware,
            ErrorSeverity.High,
            NotificationMode.Dialog,
            new Dictionary<string, object>
            {
                ["Resource"] = resource,
                ["Operation"] = operation
            }
        );
    }
}
