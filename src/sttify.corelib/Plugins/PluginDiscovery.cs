using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;
using System.Text.Json;

namespace Sttify.Corelib.Plugins;

public class PluginDiscovery
{
    private readonly string _pluginsDirectory;
    private readonly string _manifestFileName;

    public PluginDiscovery(string pluginsDirectory, string manifestFileName = "plugin.json")
    {
        _pluginsDirectory = pluginsDirectory ?? throw new ArgumentNullException(nameof(pluginsDirectory));
        _manifestFileName = manifestFileName ?? throw new ArgumentNullException(nameof(manifestFileName));
    }

    public async Task<PluginDiscoveryResult> DiscoverPluginsAsync()
    {
        var result = new PluginDiscoveryResult();

        try
        {
            if (!Directory.Exists(_pluginsDirectory))
            {
                Directory.CreateDirectory(_pluginsDirectory);
                Telemetry.LogEvent("PluginDirectoryCreated", new { Directory = _pluginsDirectory });
                return result;
            }

            var pluginDirs = Directory.GetDirectories(_pluginsDirectory);
            
            foreach (var pluginDir in pluginDirs)
            {
                var discoveryInfo = await DiscoverPluginAsync(pluginDir);
                
                if (discoveryInfo.IsValid)
                {
                    result.ValidPlugins.Add(discoveryInfo);
                }
                else
                {
                    result.InvalidPlugins.Add(discoveryInfo);
                }
            }

            Telemetry.LogEvent("PluginDiscoveryCompleted", new { 
                Directory = _pluginsDirectory,
                ValidCount = result.ValidPlugins.Count,
                InvalidCount = result.InvalidPlugins.Count,
                TotalDirectories = pluginDirs.Length
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("PluginDiscoveryFailed", ex, new { Directory = _pluginsDirectory });
            result.DiscoveryError = ex;
        }

        return result;
    }

    public async Task<PluginDiscoveryInfo> DiscoverPluginAsync(string pluginDirectory)
    {
        var info = new PluginDiscoveryInfo
        {
            Directory = pluginDirectory,
            DirectoryName = Path.GetFileName(pluginDirectory)
        };

        try
        {
            var manifestPath = Path.Combine(pluginDirectory, _manifestFileName);
            
            if (!File.Exists(manifestPath))
            {
                info.ValidationErrors.Add($"Manifest file '{_manifestFileName}' not found");
                return info;
            }

            info.ManifestPath = manifestPath;

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var metadata = JsonSerializer.Deserialize<PluginMetadata>(manifestJson);
            
            if (metadata == null)
            {
                info.ValidationErrors.Add("Failed to deserialize manifest JSON");
                return info;
            }

            info.Metadata = metadata;

            // Validate required fields
            if (string.IsNullOrEmpty(metadata.Name))
                info.ValidationErrors.Add("Plugin name is required");
            
            if (string.IsNullOrEmpty(metadata.Version))
                info.ValidationErrors.Add("Plugin version is required");
            
            if (string.IsNullOrEmpty(metadata.AssemblyPath))
                info.ValidationErrors.Add("Assembly path is required");
            
            if (string.IsNullOrEmpty(metadata.MainClass))
                info.ValidationErrors.Add("Main class is required");

            // Validate assembly file exists
            if (!string.IsNullOrEmpty(metadata.AssemblyPath))
            {
                var assemblyPath = Path.Combine(pluginDirectory, metadata.AssemblyPath);
                if (!File.Exists(assemblyPath))
                {
                    info.ValidationErrors.Add($"Assembly file not found: {metadata.AssemblyPath}");
                }
                else
                {
                    info.AssemblyPath = assemblyPath;
                    info.AssemblySize = new FileInfo(assemblyPath).Length;
                }
            }

            // Check for optional readme
            var readmePath = Path.Combine(pluginDirectory, "README.md");
            if (File.Exists(readmePath))
            {
                info.ReadmePath = readmePath;
            }

            // Validate dependencies
            foreach (var dependency in metadata.Dependencies)
            {
                if (string.IsNullOrEmpty(dependency))
                {
                    info.ValidationErrors.Add("Empty dependency name found");
                }
            }

            // Check for duplicate capabilities
            var capabilities = metadata.Capabilities;
            if (capabilities == PluginCapabilities.None)
            {
                info.ValidationWarnings.Add("Plugin has no declared capabilities");
            }

            info.IsValid = info.ValidationErrors.Count == 0;

            if (info.IsValid)
            {
                Telemetry.LogEvent("PluginDiscovered", new { 
                    Name = metadata.Name,
                    Version = metadata.Version,
                    Capabilities = capabilities.ToString(),
                    Directory = pluginDirectory
                });
            }
            else
            {
                Telemetry.LogWarning("InvalidPluginDiscovered", $"Invalid plugin in {pluginDirectory}", new { 
                    Directory = pluginDirectory,
                    Errors = info.ValidationErrors.ToArray()
                });
            }
        }
        catch (Exception ex)
        {
            info.ValidationErrors.Add($"Discovery failed: {ex.Message}");
            info.DiscoveryException = ex;
            
            Telemetry.LogError("PluginDiscoveryError", ex, new { Directory = pluginDirectory });
        }

        return info;
    }

    public async Task<bool> ValidatePluginAsync(string pluginDirectory)
    {
        var info = await DiscoverPluginAsync(pluginDirectory);
        return info.IsValid;
    }

    public async Task<PluginMetadata[]> GetAllValidPluginMetadataAsync()
    {
        var result = await DiscoverPluginsAsync();
        return result.ValidPlugins.Where(p => p.Metadata != null).Select(p => p.Metadata!).ToArray();
    }

    public async Task<string[]> GetPluginNamesAsync()
    {
        var result = await DiscoverPluginsAsync();
        return result.ValidPlugins
            .Where(p => p.Metadata != null && !string.IsNullOrEmpty(p.Metadata.Name))
            .Select(p => p.Metadata!.Name)
            .ToArray();
    }
}

[ExcludeFromCodeCoverage] // Simple data container class
public class PluginDiscoveryResult
{
    public List<PluginDiscoveryInfo> ValidPlugins { get; } = new();
    public List<PluginDiscoveryInfo> InvalidPlugins { get; } = new();
    public Exception? DiscoveryError { get; set; }
    
    public int TotalPlugins => ValidPlugins.Count + InvalidPlugins.Count;
    public bool HasErrors => DiscoveryError != null || InvalidPlugins.Any();
}

public class PluginDiscoveryInfo
{
    public string Directory { get; set; } = "";
    public string DirectoryName { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string ReadmePath { get; set; } = "";
    public long AssemblySize { get; set; }
    
    public PluginMetadata? Metadata { get; set; }
    public bool IsValid { get; set; }
    
    public List<string> ValidationErrors { get; } = new();
    public List<string> ValidationWarnings { get; } = new();
    public Exception? DiscoveryException { get; set; }
    
    public DateTime CreatedTime => System.IO.Directory.Exists(Directory) 
        ? System.IO.Directory.GetCreationTime(Directory) 
        : DateTime.MinValue;
        
    public DateTime ModifiedTime => System.IO.Directory.Exists(Directory) 
        ? System.IO.Directory.GetLastWriteTime(Directory) 
        : DateTime.MinValue;
}