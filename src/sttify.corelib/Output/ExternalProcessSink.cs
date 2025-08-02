using System.Diagnostics;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Output;

public class ExternalProcessSink : ITextOutputSink
{
    private readonly ExternalProcessSettings _settings;
    private DateTime _lastSent = DateTime.MinValue;

    public string Name => "External Process";
    public bool IsAvailable => !string.IsNullOrEmpty(_settings.ExecutablePath) && 
                              File.Exists(_settings.ExecutablePath);

    public ExternalProcessSink(ExternalProcessSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Task<bool> CanSendAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Task.FromResult(false);

        if (_settings.ThrottleMs > 0)
        {
            var elapsed = DateTime.UtcNow - _lastSent;
            if (elapsed.TotalMilliseconds < _settings.ThrottleMs)
                return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || !await CanSendAsync(cancellationToken))
            return;

        var startTime = DateTime.UtcNow;
        
        try
        {
            var arguments = ReplaceArgumentTemplate(_settings.ArgumentTemplate, text);
            
            Telemetry.LogEvent("ExternalProcessStarting", new 
            { 
                ExecutablePath = _settings.ExecutablePath,
                Arguments = _settings.LogArguments ? arguments : "[REDACTED]",
                TextLength = text.Length,
                WaitForExit = _settings.WaitForExit
            });

            using var process = new Process();
            process.StartInfo.FileName = _settings.ExecutablePath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            
            // Set working directory if specified
            if (!string.IsNullOrEmpty(_settings.WorkingDirectory) && Directory.Exists(_settings.WorkingDirectory))
            {
                process.StartInfo.WorkingDirectory = _settings.WorkingDirectory;
            }

            // Set environment variables if specified
            if (_settings.EnvironmentVariables?.Count > 0)
            {
                foreach (var kvp in _settings.EnvironmentVariables)
                {
                    process.StartInfo.Environment[kvp.Key] = kvp.Value;
                }
            }

            process.Start();

            if (_settings.WaitForExit)
            {
                var timeout = _settings.TimeoutMs > 0 ? _settings.TimeoutMs : 30000; // Default 30s timeout
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                try
                {
                    await process.WaitForExitAsync(combinedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    process.Kill(true);
                    throw new TextOutputFailedException($"External process timed out after {timeout}ms");
                }
                
                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                    
                    Telemetry.LogEvent("ExternalProcessFailed", new 
                    { 
                        ExitCode = process.ExitCode,
                        StandardError = error,
                        StandardOutput = output
                    });
                    
                    throw new TextOutputFailedException($"External process exited with code {process.ExitCode}: {error}");
                }
                
                if (_settings.LogOutput)
                {
                    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(output))
                    {
                        Telemetry.LogEvent("ExternalProcessOutput", new { Output = output });
                    }
                }
            }

            _lastSent = DateTime.UtcNow;
            var duration = _lastSent - startTime;
            
            Telemetry.LogEvent("ExternalProcessCompleted", new 
            { 
                DurationMs = duration.TotalMilliseconds,
                ProcessId = process.Id
            });
        }
        catch (Exception ex) when (ex is not TextOutputFailedException)
        {
            var duration = DateTime.UtcNow - startTime;
            Telemetry.LogError("ExternalProcessFailed", ex, new 
            { 
                DurationMs = duration.TotalMilliseconds,
                ExecutablePath = _settings.ExecutablePath
            });
            
            throw new TextOutputFailedException($"Failed to execute external process: {ex.Message}", ex);
        }
    }

    private static string ReplaceArgumentTemplate(string template, string text)
    {
        if (string.IsNullOrEmpty(template))
            return $"\"{text}\"";

        return template
            .Replace("{text}", text)
            .Replace("{text_quoted}", $"\"{text}\"")
            .Replace("{text_escaped}", text.Replace("\"", "\\\""))
            .Replace("{timestamp}", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }
}

public class ExternalProcessSettings
{
    public string ExecutablePath { get; set; } = "";
    public string ArgumentTemplate { get; set; } = "{text_quoted}";
    public bool WaitForExit { get; set; } = true;
    public int ThrottleMs { get; set; } = 0;
    public int TimeoutMs { get; set; } = 30000; // 30 seconds default timeout
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public bool LogArguments { get; set; } = false; // For security - don't log arguments by default
    public bool LogOutput { get; set; } = false; // Don't log process output by default
    public string WorkingDirectory { get; set; } = ""; // Working directory for the process
}