using Sttify.Corelib.Config;
using Sttify.Corelib.Ime;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Output;

public interface IOutputSinkProvider
{
    IEnumerable<ITextOutputSink> GetSinks();
    Task RefreshAsync();
}

public class OutputSinkProvider : IOutputSinkProvider
{
    private readonly SettingsProvider _settingsProvider;
    private readonly object _lock = new();
    private List<ITextOutputSink> _current = new();

    public OutputSinkProvider(SettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _current = BuildSinks(_settingsProvider.GetSettingsSync());
    }

    public IEnumerable<ITextOutputSink> GetSinks()
    {
        lock (_lock)
        {
            return _current.ToArray();
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var settings = await _settingsProvider.GetSettingsAsync();
            var sinks = BuildSinks(settings);

            lock (_lock)
            {
                _current.ForEach(s => (s as IDisposable)?.Dispose());
                _current = sinks;
            }

            Telemetry.LogEvent("OutputSinksRefreshed", new { Count = _current.Count });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("OutputSinksRefreshFailed", ex);
        }
    }

    private static List<ITextOutputSink> BuildSinks(SttifySettings settings)
    {
        var sinks = new List<ITextOutputSink>();

        void AddSinkByName(string name)
        {
            switch (name)
            {
                case "sendinput":
                    sinks.Add(new SendInputSink(new SendInputSettings
                    {
                        RateLimitCps = settings.Output.SendInput.RateLimitCps,
                        CommitKey = settings.Output.SendInput.CommitKey,
                        Ime = new ImeSettings
                        {
                            EnableImeControl = settings.Output.SendInput.Ime.EnableImeControl,
                            CloseImeWhenSending = settings.Output.SendInput.Ime.CloseImeWhenSending,
                            SetAlphanumericMode = settings.Output.SendInput.Ime.SetAlphanumericMode,
                            ClearCompositionString = settings.Output.SendInput.Ime.ClearCompositionString,
                            RestoreImeStateAfterSending = settings.Output.SendInput.Ime.RestoreImeStateAfterSending,
                            RestoreDelayMs = settings.Output.SendInput.Ime.RestoreDelayMs,
                            SkipWhenImeComposing = settings.Output.SendInput.Ime.SkipWhenImeComposing
                        }
                    }));
                    break;
                case "external-process":
                    sinks.Add(new ExternalProcessSink(new ExternalProcessSettings
                    {
                        ExecutablePath = settings.Output.ExternalProcess.ExecutablePath,
                        ArgumentTemplate = settings.Output.ExternalProcess.ArgumentTemplate,
                        WaitForExit = settings.Output.ExternalProcess.WaitForExit,
                        TimeoutMs = settings.Output.ExternalProcess.TimeoutMs,
                        ThrottleMs = settings.Output.ExternalProcess.ThrottleMs,
                        LogArguments = settings.Output.ExternalProcess.LogArguments,
                        LogOutput = settings.Output.ExternalProcess.LogOutput,
                        WorkingDirectory = settings.Output.ExternalProcess.WorkingDirectory,
                        EnvironmentVariables = new Dictionary<string, string>(settings.Output.ExternalProcess.EnvironmentVariables ?? new())
                    }));
                    break;
                case "stream":
                    sinks.Add(new StreamSink(new StreamSinkSettings
                    {
                        OutputType = settings.Output.Stream.OutputType,
                        FilePath = settings.Output.Stream.FilePath,
                        AppendToFile = settings.Output.Stream.AppendToFile,
                        IncludeTimestamp = settings.Output.Stream.IncludeTimestamp,
                        ForceFlush = settings.Output.Stream.ForceFlush,
                        MaxFileSizeBytes = settings.Output.Stream.MaxFileSizeBytes,
                        SharedMemoryName = settings.Output.Stream.SharedMemoryName,
                        SharedMemorySize = settings.Output.Stream.SharedMemorySize,
                        CustomPrefix = settings.Output.Stream.CustomPrefix,
                        CustomSuffix = settings.Output.Stream.CustomSuffix
                    }));
                    break;
            }
        }

        var primary = settings.Output.Primary?.ToLowerInvariant() ?? "sendinput";
        AddSinkByName(primary);

        foreach (var fb in settings.Output.Fallbacks ?? Array.Empty<string>())
        {
            var lname = fb.ToLowerInvariant();
            if (!sinks.Any(s => string.Equals(s.Id, lname, StringComparison.OrdinalIgnoreCase)))
            {
                AddSinkByName(lname);
            }
        }

        if (sinks.Count == 0)
        {
            sinks.Add(new SendInputSink(new SendInputSettings { Ime = new ImeSettings() }));
        }

        return sinks;
    }
}


