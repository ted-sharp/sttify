using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Diagnostics;

/// <summary>
/// Utilities for safely running fire-and-forget tasks without surfacing unobserved exceptions.
/// </summary>
public static class AsyncHelper
{
    /// <summary>
    /// Executes the provided asynchronous operation on a background thread and logs any exceptions.
    /// </summary>
    /// <param name="taskFactory">Factory that creates the task to run.</param>
    /// <param name="operationName">Name used for telemetry when an error occurs.</param>
    /// <param name="context">Optional additional context for telemetry.</param>
    public static void FireAndForget(Func<Task> taskFactory, string operationName, object? context = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var task = taskFactory?.Invoke();
                if (task is not null)
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Treat cancellations as informational events
                Telemetry.LogEvent("OperationCanceled", new { Operation = operationName, Context = context });
            }
            catch (Exception ex)
            {
                Telemetry.LogError("FireAndForgetFailed", ex, new { Operation = operationName, Context = context });
            }
        });
    }

    /// <summary>
    /// Overload that accepts an existing Task and wraps it with exception logging.
    /// </summary>
    public static void FireAndForget(Task task, string operationName, object? context = null)
    {
        if (task == null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Telemetry.LogEvent("OperationCanceled", new { Operation = operationName, Context = context });
            }
            catch (Exception ex)
            {
                Telemetry.LogError("FireAndForgetFailed", ex, new { Operation = operationName, Context = context });
            }
        });
    }
}


