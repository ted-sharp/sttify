namespace Sttify.Corelib.Diagnostics;

public static class TaskExtensions
{
    public static async Task WithTimeout(this Task task, TimeSpan timeout, string operationName)
    {
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
        if (completed == timeoutTask)
        {
            throw new TimeoutException($"Operation '{operationName}' timed out after {timeout.TotalSeconds:F1}s");
        }

        await task.ConfigureAwait(false);
    }

    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout, string operationName)
    {
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
        if (completed == timeoutTask)
        {
            throw new TimeoutException($"Operation '{operationName}' timed out after {timeout.TotalSeconds:F1}s");
        }

        return await task.ConfigureAwait(false);
    }
}


