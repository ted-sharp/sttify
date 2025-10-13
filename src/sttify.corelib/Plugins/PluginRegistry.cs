using System.Collections.Concurrent;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Output;

namespace Sttify.Corelib.Plugins;

public class PluginRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, ISpeechEnginePlugin> _enginePlugins = new();
    private readonly ConcurrentDictionary<string, ITextOutputPlugin> _outputPlugins = new();
    private readonly PluginManager _pluginManager;
    private readonly ConcurrentDictionary<string, ITextProcessorPlugin> _processorPlugins = new();

    public PluginRegistry(PluginManager pluginManager)
    {
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));

        _pluginManager.OnPluginLoaded += OnPluginLoaded;
        _pluginManager.OnPluginUnloaded += OnPluginUnloaded;
    }

    public void Dispose()
    {
        _pluginManager.OnPluginLoaded -= OnPluginLoaded;
        _pluginManager.OnPluginUnloaded -= OnPluginUnloaded;

        _enginePlugins.Clear();
        _outputPlugins.Clear();
        _processorPlugins.Clear();
    }

    public event EventHandler<PluginRegisteredEventArgs>? OnPluginRegistered;
    public event EventHandler<PluginUnregisteredEventArgs>? OnPluginUnregistered;

    private void OnPluginLoaded(object? sender, PluginEventArgs e)
    {
        RegisterPlugin(e.Plugin);
    }

    private void OnPluginUnloaded(object? sender, PluginEventArgs e)
    {
        UnregisterPlugin(e.Plugin);
    }

    private void RegisterPlugin(IPlugin plugin)
    {
        try
        {
            var pluginType = plugin.GetType();
            var registered = false;

            if (plugin is ISpeechEnginePlugin enginePlugin)
            {
                _enginePlugins[plugin.Name] = enginePlugin;
                registered = true;
                Telemetry.LogEvent("SpeechEnginePluginRegistered", new { plugin.Name });
            }

            if (plugin is ITextOutputPlugin outputPlugin)
            {
                _outputPlugins[plugin.Name] = outputPlugin;
                registered = true;
                Telemetry.LogEvent("TextOutputPluginRegistered", new { plugin.Name });
            }

            if (plugin is ITextProcessorPlugin processorPlugin)
            {
                _processorPlugins[plugin.Name] = processorPlugin;
                registered = true;
                Telemetry.LogEvent("TextProcessorPluginRegistered", new
                {
                    plugin.Name,
                    processorPlugin.SupportedLanguages
                });
            }

            if (registered)
            {
                OnPluginRegistered?.Invoke(this, new PluginRegisteredEventArgs(plugin.Name, pluginType));
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginRegistrationFailed", ex, new { PluginName = plugin.Name });
        }
    }

    private void UnregisterPlugin(IPlugin plugin)
    {
        try
        {
            var pluginType = plugin.GetType();
            var unregistered = false;

            if (_enginePlugins.TryRemove(plugin.Name, out _))
            {
                unregistered = true;
                Telemetry.LogEvent("SpeechEnginePluginUnregistered", new { plugin.Name });
            }

            if (_outputPlugins.TryRemove(plugin.Name, out _))
            {
                unregistered = true;
                Telemetry.LogEvent("TextOutputPluginUnregistered", new { plugin.Name });
            }

            if (_processorPlugins.TryRemove(plugin.Name, out _))
            {
                unregistered = true;
                Telemetry.LogEvent("TextProcessorPluginUnregistered", new { plugin.Name });
            }

            if (unregistered)
            {
                OnPluginUnregistered?.Invoke(this, new PluginUnregisteredEventArgs(plugin.Name, pluginType));
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginUnregistrationFailed", ex, new { PluginName = plugin.Name });
        }
    }

    public ISpeechEnginePlugin[] GetAvailableEnginePlugins()
    {
        return _enginePlugins.Values.Where(p => p.IsEnabled).ToArray();
    }

    public ITextOutputPlugin[] GetAvailableOutputPlugins()
    {
        return _outputPlugins.Values.Where(p => p.IsEnabled).ToArray();
    }

    public ITextProcessorPlugin[] GetAvailableProcessorPlugins()
    {
        return _processorPlugins.Values.Where(p => p.IsEnabled).ToArray();
    }

    public ISpeechEnginePlugin? GetEnginePlugin(string name)
    {
        return _enginePlugins.TryGetValue(name, out var plugin) && plugin.IsEnabled ? plugin : null;
    }

    public ITextOutputPlugin? GetOutputPlugin(string name)
    {
        return _outputPlugins.TryGetValue(name, out var plugin) && plugin.IsEnabled ? plugin : null;
    }

    public ITextProcessorPlugin? GetProcessorPlugin(string name)
    {
        return _processorPlugins.TryGetValue(name, out var plugin) && plugin.IsEnabled ? plugin : null;
    }

    public ISttEngine? CreateEngine(string engineName, object configuration)
    {
        try
        {
            var plugin = GetEnginePlugin(engineName);
            if (plugin == null)
            {
                Telemetry.LogWarning("EnginePluginNotFound", $"Engine plugin '{engineName}' not found or disabled");
                return null;
            }

            var engine = plugin.CreateEngine(configuration);
            Telemetry.LogEvent("EngineCreatedFromPlugin", new
            {
                PluginName = engineName,
                EngineType = engine.GetType().Name
            });

            return engine;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("EngineCreationFromPluginFailed", ex, new
            {
                PluginName = engineName,
                ConfigurationType = configuration.GetType().Name
            });
            return null;
        }
    }

    public ITextOutputSink? CreateOutputSink(string outputName, object configuration)
    {
        try
        {
            var plugin = GetOutputPlugin(outputName);
            if (plugin == null)
            {
                Telemetry.LogWarning("OutputPluginNotFound", $"Output plugin '{outputName}' not found or disabled");
                return null;
            }

            var sink = plugin.CreateOutputSink(configuration);
            Telemetry.LogEvent("OutputSinkCreatedFromPlugin", new
            {
                PluginName = outputName,
                SinkType = sink.GetType().Name
            });

            return sink;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("OutputSinkCreationFromPluginFailed", ex, new
            {
                PluginName = outputName,
                ConfigurationType = configuration.GetType().Name
            });
            return null;
        }
    }

    public async Task<string?> ProcessTextAsync(string processorName, string text, string sourceLanguage, string targetLanguage)
    {
        try
        {
            var plugin = GetProcessorPlugin(processorName);
            if (plugin == null)
            {
                Telemetry.LogWarning("ProcessorPluginNotFound", $"Processor plugin '{processorName}' not found or disabled");
                return null;
            }

            if (!plugin.SupportedLanguages.Contains(sourceLanguage) ||
                !plugin.SupportedLanguages.Contains(targetLanguage))
            {
                Telemetry.LogWarning("ProcessorLanguageNotSupported", $"Language not supported by {processorName}", new
                {
                    PluginName = processorName,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    plugin.SupportedLanguages
                });
                return null;
            }

            var result = await plugin.ProcessTextAsync(text, sourceLanguage, targetLanguage);
            Telemetry.LogEvent("TextProcessedByPlugin", new
            {
                PluginName = processorName,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                InputLength = text.Length,
                OutputLength = result.Length
            });

            return result;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("TextProcessingByPluginFailed", ex, new
            {
                PluginName = processorName,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage
            });
            return null;
        }
    }

    public PluginRegistryInfo GetRegistryInfo()
    {
        return new PluginRegistryInfo
        {
            EnginePluginCount = _enginePlugins.Count,
            OutputPluginCount = _outputPlugins.Count,
            ProcessorPluginCount = _processorPlugins.Count,
            EnginePluginNames = _enginePlugins.Keys.ToArray(),
            OutputPluginNames = _outputPlugins.Keys.ToArray(),
            ProcessorPluginNames = _processorPlugins.Keys.ToArray()
        };
    }
}

public class PluginRegisteredEventArgs : EventArgs
{
    public PluginRegisteredEventArgs(string pluginName, Type pluginType)
    {
        PluginName = pluginName;
        PluginType = pluginType;
    }

    public string PluginName { get; }
    public Type PluginType { get; }
}

public class PluginUnregisteredEventArgs : EventArgs
{
    public PluginUnregisteredEventArgs(string pluginName, Type pluginType)
    {
        PluginName = pluginName;
        PluginType = pluginType;
    }

    public string PluginName { get; }
    public Type PluginType { get; }
}

public class PluginRegistryInfo
{
    public int EnginePluginCount { get; set; }
    public int OutputPluginCount { get; set; }
    public int ProcessorPluginCount { get; set; }
    public string[] EnginePluginNames { get; set; } = Array.Empty<string>();
    public string[] OutputPluginNames { get; set; } = Array.Empty<string>();
    public string[] ProcessorPluginNames { get; set; } = Array.Empty<string>();
}
