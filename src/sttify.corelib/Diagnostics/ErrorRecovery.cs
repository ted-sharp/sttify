using System.ComponentModel;

namespace Sttify.Corelib.Diagnostics;

public class ErrorRecovery
{
    private readonly Dictionary<Type, int> _errorCounts = new();
    private readonly Dictionary<Type, DateTime> _lastErrorTimes = new();
    private readonly object _lockObject = new();

    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ErrorResetInterval { get; set; } = TimeSpan.FromMinutes(5);

    public event EventHandler<ErrorRecoveryEventArgs>? OnRecoveryAttempt;
    public event EventHandler<ErrorRecoveryEventArgs>? OnRecoveryFailed;

    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName = "", CancellationToken cancellationToken = default)
    {
        var operationType = typeof(T);
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                var result = await operation();
                
                // Reset error count on successful execution
                ResetErrorCount(operationType);
                
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                Telemetry.LogError($"OperationFailed_{operationName}", ex, new
                {
                    Attempt = attempt,
                    MaxAttempts = MaxRetryAttempts,
                    OperationType = operationType.Name
                });

                if (attempt == MaxRetryAttempts)
                {
                    IncrementErrorCount(operationType);
                    OnRecoveryFailed?.Invoke(this, new ErrorRecoveryEventArgs(operationName, ex, attempt, MaxRetryAttempts));
                    break;
                }

                OnRecoveryAttempt?.Invoke(this, new ErrorRecoveryEventArgs(operationName, ex, attempt, MaxRetryAttempts));

                // Wait before retrying
                await Task.Delay(RetryInterval, cancellationToken);
            }
        }

        throw new ErrorRecoveryException($"Operation '{operationName}' failed after {MaxRetryAttempts} attempts", lastException!);
    }

    public async Task ExecuteWithRetryAsync(Func<Task> operation, string operationName = "", CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, operationName, cancellationToken);
    }

    public T ExecuteWithRetry<T>(Func<T> operation, string operationName = "")
    {
        return ExecuteWithRetryAsync(() => Task.FromResult(operation()), operationName).Result;
    }

    public void ExecuteWithRetry(Action operation, string operationName = "")
    {
        ExecuteWithRetryAsync(() =>
        {
            operation();
            return Task.CompletedTask;
        }, operationName).Wait();
    }

    public bool ShouldAttemptOperation(Type operationType)
    {
        lock (_lockObject)
        {
            if (!_errorCounts.TryGetValue(operationType, out var errorCount))
                return true;

            if (!_lastErrorTimes.TryGetValue(operationType, out var lastErrorTime))
                return true;

            // Reset error count if enough time has passed
            if (DateTime.UtcNow - lastErrorTime > ErrorResetInterval)
            {
                _errorCounts[operationType] = 0;
                return true;
            }

            // Check if we've exceeded the error threshold
            return errorCount < MaxRetryAttempts * 2; // Allow double the retry attempts before giving up
        }
    }

    public int GetErrorCount(Type operationType)
    {
        lock (_lockObject)
        {
            return _errorCounts.TryGetValue(operationType, out var count) ? count : 0;
        }
    }

    public DateTime? GetLastErrorTime(Type operationType)
    {
        lock (_lockObject)
        {
            return _lastErrorTimes.TryGetValue(operationType, out var time) ? time : null;
        }
    }

    private void IncrementErrorCount(Type operationType)
    {
        lock (_lockObject)
        {
            _errorCounts.TryGetValue(operationType, out var count);
            _errorCounts[operationType] = count + 1;
            _lastErrorTimes[operationType] = DateTime.UtcNow;
        }
    }

    private void ResetErrorCount(Type operationType)
    {
        lock (_lockObject)
        {
            _errorCounts[operationType] = 0;
            _lastErrorTimes.Remove(operationType);
        }
    }

    public void ResetAllErrorCounts()
    {
        lock (_lockObject)
        {
            _errorCounts.Clear();
            _lastErrorTimes.Clear();
        }
    }
}

public class ErrorRecoveryEventArgs : EventArgs
{
    public string OperationName { get; }
    public Exception Exception { get; }
    public int AttemptNumber { get; }
    public int MaxAttempts { get; }
    public bool IsFinalAttempt => AttemptNumber >= MaxAttempts;

    public ErrorRecoveryEventArgs(string operationName, Exception exception, int attemptNumber, int maxAttempts)
    {
        OperationName = operationName;
        Exception = exception;
        AttemptNumber = attemptNumber;
        MaxAttempts = maxAttempts;
    }
}

public class ErrorRecoveryException : Exception
{
    public ErrorRecoveryException(string message) : base(message) { }
    public ErrorRecoveryException(string message, Exception innerException) : base(message, innerException) { }
}