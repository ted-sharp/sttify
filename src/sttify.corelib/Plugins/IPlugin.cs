using Sttify.Corelib.Engine;
using Sttify.Corelib.Output;
using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Plugins;

[ExcludeFromCodeCoverage] // Interface definition only
public interface IPlugin : IDisposable
{
    string Name { get; }
    string Version { get; }
    string Description { get; }
    string Author { get; }
    
    Task InitializeAsync(IPluginContext context);
    Task StartAsync();
    Task StopAsync();
    
    bool IsEnabled { get; set; }
    PluginCapabilities Capabilities { get; }
}

[ExcludeFromCodeCoverage] // Simple enum definition
[Flags]
public enum PluginCapabilities
{
    None = 0,
    SpeechRecognitionEngine = 1 << 0,
    TextOutputSink = 1 << 1,
    AudioProcessor = 1 << 2,
    TextProcessor = 1 << 3,
    UIExtension = 1 << 4,
    NotificationProvider = 1 << 5
}

[ExcludeFromCodeCoverage] // Interface definition only
public interface IPluginContext
{
    IServiceProvider ServiceProvider { get; }
    string PluginDataDirectory { get; }
    
    void RegisterService<T>(T service) where T : class;
    T? GetService<T>() where T : class;
    
    Task<string> GetConfigurationAsync(string key, string defaultValue = "");
    Task SetConfigurationAsync(string key, string value);
    
    void LogInfo(string message, object? data = null);
    void LogWarning(string message, object? data = null);
    void LogError(string message, Exception? exception = null, object? data = null);
}

[ExcludeFromCodeCoverage] // Interface definition only
public interface ISpeechEnginePlugin : IPlugin
{
    ISttEngine CreateEngine(object configuration);
    Type ConfigurationType { get; }
}

[ExcludeFromCodeCoverage] // Interface definition only
public interface ITextOutputPlugin : IPlugin
{
    ITextOutputSink CreateOutputSink(object configuration);
    Type ConfigurationType { get; }
}

[ExcludeFromCodeCoverage] // Interface definition only
public interface ITextProcessorPlugin : IPlugin
{
    Task<string> ProcessTextAsync(string text, string sourceLanguage, string targetLanguage);
    string[] SupportedLanguages { get; }
}

public static class PluginExtensions
{
    public static bool CanHandleTextProcessing(this IPlugin plugin)
    {
        return plugin.Capabilities.HasFlag(PluginCapabilities.TextProcessor) && plugin is ITextProcessorPlugin;
    }

    public static async Task<string> ProcessTextAsync(this IPlugin plugin, string text)
    {
        if (plugin is ITextProcessorPlugin textProcessor)
        {
            return await textProcessor.ProcessTextAsync(text, "auto", "auto");
        }
        return text;
    }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class PluginMetadata
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string MainClass { get; set; } = "";
    public PluginCapabilities Capabilities { get; set; } = PluginCapabilities.None;
    public string[] Dependencies { get; set; } = Array.Empty<string>();
    public Dictionary<string, object> Configuration { get; set; } = new();
}