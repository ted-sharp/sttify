using System.Runtime.Loader;
using System.Text.Json;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Plugins;

public class PluginManager : IDisposable
{
    private readonly Dictionary<string, AssemblyLoadContext> _loadContexts = new();
    private readonly Dictionary<string, IPlugin> _loadedPlugins = new();
    private readonly object _lockObject = new();
    private readonly Dictionary<string, PluginMetadata> _pluginMetadata = new();
    private readonly string _pluginsDirectory;
    private readonly PluginSecurity _pluginSecurity = new();
    private readonly IServiceProvider _serviceProvider;

    public PluginManager(IServiceProvider serviceProvider, string? pluginsDirectory = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _pluginsDirectory = pluginsDirectory ?? GetDefaultPluginsDirectory();

        Directory.CreateDirectory(_pluginsDirectory);
    }

    public void Dispose()
    {
        StopAllPluginsAsync().Wait();

        foreach (var plugin in _loadedPlugins.Values)
        {
            try
            {
                plugin.Dispose();
            }
            catch (Exception ex)
            {
                Telemetry.LogError("PluginDisposeFailed", ex, new { PluginName = plugin.Name });
            }
        }

        foreach (var loadContext in _loadContexts.Values)
        {
            try
            {
                loadContext.Unload();
            }
            catch (Exception ex)
            {
                Telemetry.LogError("PluginLoadContextUnloadFailed", ex);
            }
        }

        _loadedPlugins.Clear();
        _pluginMetadata.Clear();
        _loadContexts.Clear();
    }

    public event EventHandler<PluginEventArgs>? OnPluginLoaded;
    public event EventHandler<PluginEventArgs>? OnPluginUnloaded;
    public event EventHandler<PluginErrorEventArgs>? OnPluginError;

    public async Task LoadAllPluginsAsync()
    {
        try
        {
            var pluginDirs = Directory.GetDirectories(_pluginsDirectory);

            foreach (var pluginDir in pluginDirs)
            {
                await LoadPluginFromDirectoryAsync(pluginDir);
            }

            int loadedCount;
            lock (_lockObject)
            {
                loadedCount = _loadedPlugins.Count;
            }

            Telemetry.LogEvent("AllPluginsLoaded", new
            {
                PluginsDirectory = _pluginsDirectory,
                LoadedCount = loadedCount
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginLoadAllFailed", ex);
            throw;
        }
    }

    public async Task<bool> LoadPluginFromDirectoryAsync(string pluginDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                Telemetry.LogWarning("PluginManifestNotFound", $"No manifest found in {pluginDirectory}");
                return false;
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var metadata = JsonSerializer.Deserialize<PluginMetadata>(manifestJson);

            if (metadata == null)
            {
                Telemetry.LogWarning("PluginManifestInvalid", $"Invalid manifest in {pluginDirectory}");
                return false;
            }

            lock (_lockObject)
            {
                if (_loadedPlugins.ContainsKey(metadata.Name))
                {
                    Telemetry.LogWarning("PluginAlreadyLoaded", $"Plugin {metadata.Name} is already loaded");
                    return false;
                }
            }

            var assemblyPath = Path.Combine(pluginDirectory, metadata.AssemblyPath);
            if (!File.Exists(assemblyPath))
            {
                Telemetry.LogWarning("PluginAssemblyNotFound", $"Assembly not found: {assemblyPath}");
                return false;
            }

            // Security validation before loading
            try
            {
                var discovery = new PluginDiscoveryInfo
                {
                    Directory = pluginDirectory,
                    DirectoryName = Path.GetFileName(pluginDirectory),
                    ManifestPath = manifestPath,
                    AssemblyPath = assemblyPath,
                    Metadata = metadata
                };

                var validation = await _pluginSecurity.ValidatePluginAsync(discovery);
                if (!validation.IsAllowed)
                {
                    Telemetry.LogWarning(
                        "PluginSecurityValidationFailed",
                        $"Plugin '{metadata.Name}' rejected by security policy",
                        new { Plugin = metadata.Name, Issues = validation.SecurityIssues.ToArray(), Threat = validation.ThreatLevel.ToString() }
                    );
                    return false;
                }
            }
            catch (Exception ex)
            {
                Telemetry.LogError("PluginSecurityValidationException", ex, new { Plugin = metadata.Name });
                return false;
            }

            // Create isolated load context for the plugin
            var loadContext = new AssemblyLoadContext($"Plugin_{metadata.Name}", isCollectible: true);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            var pluginType = assembly.GetType(metadata.MainClass);
            if (pluginType == null)
            {
                Telemetry.LogWarning("PluginMainClassNotFound", $"Main class {metadata.MainClass} not found");
                loadContext.Unload();
                return false;
            }

            var plugin = Activator.CreateInstance(pluginType) as IPlugin;
            if (plugin == null)
            {
                Telemetry.LogWarning("PluginInstantiationFailed", $"Failed to create instance of {pluginType.Name}");
                loadContext.Unload();
                return false;
            }

            // Create plugin context
            var pluginDataDir = Path.Combine(_pluginsDirectory, ".data", metadata.Name);
            Directory.CreateDirectory(pluginDataDir);

            var context = new PluginContext(_serviceProvider, pluginDataDir, metadata.Name);

            // Initialize the plugin
            await plugin.InitializeAsync(context);

            lock (_lockObject)
            {
                _loadedPlugins[metadata.Name] = plugin;
                _pluginMetadata[metadata.Name] = metadata;
                _loadContexts[metadata.Name] = loadContext;
            }

            OnPluginLoaded?.Invoke(this, new PluginEventArgs(metadata.Name, plugin));

            Telemetry.LogEvent("PluginLoaded", new
            {
                metadata.Name,
                metadata.Version,
                Capabilities = metadata.Capabilities.ToString()
            });

            return true;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginLoadFailed", ex, new { Directory = pluginDirectory });
            OnPluginError?.Invoke(this, new PluginErrorEventArgs(Path.GetFileName(pluginDirectory), ex));
            return false;
        }
    }

    public async Task<bool> UnloadPluginAsync(string pluginName)
    {
        try
        {
            IPlugin? plugin;
            lock (_lockObject)
            {
                if (!_loadedPlugins.TryGetValue(pluginName, out plugin))
                    return false;

                _loadedPlugins.Remove(pluginName);
                _pluginMetadata.Remove(pluginName);
            }

            await plugin.StopAsync();
            plugin.Dispose();

            if (_loadContexts.TryGetValue(pluginName, out var loadContext))
            {
                _loadContexts.Remove(pluginName);
                loadContext.Unload();
            }

            OnPluginUnloaded?.Invoke(this, new PluginEventArgs(pluginName, plugin));

            Telemetry.LogEvent("PluginUnloaded", new { Name = pluginName });

            return true;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginUnloadFailed", ex, new { PluginName = pluginName });
            OnPluginError?.Invoke(this, new PluginErrorEventArgs(pluginName, ex));
            return false;
        }
    }

    public T[] GetPlugins<T>() where T : class, IPlugin
    {
        lock (_lockObject)
        {
            return _loadedPlugins.Values.OfType<T>().ToArray();
        }
    }

    public IPlugin? GetPlugin(string name)
    {
        lock (_lockObject)
        {
            return _loadedPlugins.TryGetValue(name, out var plugin) ? plugin : null;
        }
    }

    public IPlugin[] GetLoadedPlugins()
    {
        lock (_lockObject)
        {
            return _loadedPlugins.Values.ToArray();
        }
    }

    public PluginMetadata[] GetLoadedPluginsMetadata()
    {
        lock (_lockObject)
        {
            return _pluginMetadata.Values.ToArray();
        }
    }

    public async Task StartAllPluginsAsync()
    {
        IPlugin[] plugins;
        lock (_lockObject)
        {
            plugins = _loadedPlugins.Values.ToArray();
        }

        foreach (var plugin in plugins)
        {
            try
            {
                if (plugin.IsEnabled)
                {
                    await plugin.StartAsync();
                }
            }
            catch (Exception ex)
            {
                Telemetry.LogError("PluginStartFailed", ex, new { PluginName = plugin.Name });
                OnPluginError?.Invoke(this, new PluginErrorEventArgs(plugin.Name, ex));
            }
        }
    }

    public async Task StopAllPluginsAsync()
    {
        IPlugin[] plugins;
        lock (_lockObject)
        {
            plugins = _loadedPlugins.Values.ToArray();
        }

        foreach (var plugin in plugins)
        {
            try
            {
                await plugin.StopAsync();
            }
            catch (Exception ex)
            {
                Telemetry.LogError("PluginStopFailed", ex, new { PluginName = plugin.Name });
            }
        }
    }

    private static string GetDefaultPluginsDirectory()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "sttify", "plugins");
    }
}

public class PluginContext : IPluginContext
{
    private readonly Dictionary<string, string> _configuration = new();
    private readonly string _pluginName;
    private readonly Dictionary<Type, object> _services = new();

    public PluginContext(IServiceProvider serviceProvider, string pluginDataDirectory, string pluginName)
    {
        ServiceProvider = serviceProvider;
        PluginDataDirectory = pluginDataDirectory;
        _pluginName = pluginName;

        LoadConfiguration();
    }

    public IServiceProvider ServiceProvider { get; }
    public string PluginDataDirectory { get; }

    public void RegisterService<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    public T? GetService<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out var service))
            return service as T;

        return ServiceProvider.GetService(typeof(T)) as T;
    }

    public async Task<string> GetConfigurationAsync(string key, string defaultValue = "")
    {
        return await Task.FromResult(_configuration.TryGetValue(key, out var value) ? value : defaultValue);
    }

    public async Task SetConfigurationAsync(string key, string value)
    {
        _configuration[key] = value;
        await SaveConfiguration();
    }

    public void LogInfo(string message, object? data = null)
    {
        Telemetry.LogEvent($"Plugin_{_pluginName}", new { Message = message, Data = data });
    }

    public void LogWarning(string message, object? data = null)
    {
        Telemetry.LogWarning($"Plugin_{_pluginName}", message, data);
    }

    public void LogError(string message, Exception? exception = null, object? data = null)
    {
        if (exception != null)
            Telemetry.LogError($"Plugin_{_pluginName}", exception, new { Message = message, Data = data });
        else
            Telemetry.LogWarning($"Plugin_{_pluginName}", message, data);
    }

    private void LoadConfiguration()
    {
        try
        {
            var configPath = Path.Combine(PluginDataDirectory, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (config != null)
                {
                    foreach (var kvp in config)
                    {
                        _configuration[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginConfigLoadFailed", ex, new { PluginName = _pluginName });
        }
    }

    private async Task SaveConfiguration()
    {
        try
        {
            var configPath = Path.Combine(PluginDataDirectory, "config.json");
            var json = JsonSerializer.Serialize(_configuration, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, json);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginConfigSaveFailed", ex, new { PluginName = _pluginName });
        }
    }
}

public class PluginEventArgs : EventArgs
{
    public PluginEventArgs(string pluginName, IPlugin plugin)
    {
        PluginName = pluginName;
        Plugin = plugin;
    }

    public string PluginName { get; }
    public IPlugin Plugin { get; }
}

public class PluginErrorEventArgs : EventArgs
{
    public PluginErrorEventArgs(string pluginName, Exception exception)
    {
        PluginName = pluginName;
        Exception = exception;
    }

    public string PluginName { get; }
    public Exception Exception { get; }
}
