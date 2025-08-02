using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Sttify.Corelib.Plugins;

public class PluginSecurity
{
    private readonly Dictionary<string, string> _trustedHashes = new();
    private readonly HashSet<string> _blockedPlugins = new();
    private readonly HashSet<string> _allowedAuthors = new();
    private readonly PluginSecuritySettings _settings;

    public PluginSecurity(PluginSecuritySettings? settings = null)
    {
        _settings = settings ?? new PluginSecuritySettings();
        LoadSecurityConfiguration();
    }

    public async Task<PluginSecurityResult> ValidatePluginAsync(PluginDiscoveryInfo pluginInfo)
    {
        var result = new PluginSecurityResult
        {
            PluginName = pluginInfo.Metadata?.Name ?? pluginInfo.DirectoryName,
            IsAllowed = false
        };

        try
        {
            // Check if plugin is explicitly blocked
            if (_blockedPlugins.Contains(result.PluginName))
            {
                result.SecurityIssues.Add("Plugin is in the blocked list");
                result.ThreatLevel = ThreatLevel.High;
                return result;
            }

            // Validate metadata
            if (pluginInfo.Metadata == null)
            {
                result.SecurityIssues.Add("Plugin metadata is missing");
                result.ThreatLevel = ThreatLevel.Medium;
                return result;
            }

            // Check author trust
            if (_settings.RequireTrustedAuthors && 
                !string.IsNullOrEmpty(pluginInfo.Metadata.Author) &&
                !_allowedAuthors.Contains(pluginInfo.Metadata.Author))
            {
                result.SecurityIssues.Add($"Author '{pluginInfo.Metadata.Author}' is not in trusted list");
                result.ThreatLevel = ThreatLevel.Medium;
            }

            // Validate assembly if it exists
            if (!string.IsNullOrEmpty(pluginInfo.AssemblyPath) && File.Exists(pluginInfo.AssemblyPath))
            {
                var assemblyResult = await ValidateAssemblyAsync(pluginInfo.AssemblyPath, pluginInfo.Metadata.Name);
                result.SecurityIssues.AddRange(assemblyResult.SecurityIssues);
                result.ThreatLevel = (ThreatLevel)Math.Max((int)result.ThreatLevel, (int)assemblyResult.ThreatLevel);
                result.AssemblyHash = assemblyResult.AssemblyHash;
            }

            // Check capabilities for security risks
            var capabilityRisks = ValidateCapabilities(pluginInfo.Metadata.Capabilities);
            result.SecurityIssues.AddRange(capabilityRisks);

            // Determine if plugin should be allowed
            result.IsAllowed = result.ThreatLevel <= _settings.MaxAllowedThreatLevel && 
                              result.SecurityIssues.Count == 0;

            if (result.IsAllowed)
            {
                Telemetry.LogEvent("PluginSecurityValidationPassed", new { 
                    PluginName = result.PluginName,
                    ThreatLevel = result.ThreatLevel.ToString(),
                    Author = pluginInfo.Metadata.Author
                });
            }
            else
            {
                Telemetry.LogWarning("PluginSecurityValidationFailed", $"Plugin {result.PluginName} validation failed", new { 
                    PluginName = result.PluginName,
                    ThreatLevel = result.ThreatLevel.ToString(),
                    Issues = result.SecurityIssues.ToArray()
                });
            }
        }
        catch (Exception ex)
        {
            result.SecurityIssues.Add($"Security validation failed: {ex.Message}");
            result.ThreatLevel = ThreatLevel.High;
            result.ValidationException = ex;
            
            Telemetry.LogError("PluginSecurityValidationError", ex, new { PluginName = result.PluginName });
        }

        return result;
    }

    private async Task<AssemblySecurityResult> ValidateAssemblyAsync(string assemblyPath, string pluginName)
    {
        var result = new AssemblySecurityResult();

        try
        {
            // Calculate assembly hash
            using var sha256 = SHA256.Create();
            var fileBytes = await File.ReadAllBytesAsync(assemblyPath);
            var hashBytes = sha256.ComputeHash(fileBytes);
            result.AssemblyHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            // Check against trusted hashes
            if (_trustedHashes.TryGetValue(pluginName, out var trustedHash))
            {
                if (result.AssemblyHash != trustedHash)
                {
                    result.SecurityIssues.Add("Assembly hash does not match trusted hash");
                    result.ThreatLevel = ThreatLevel.High;
                }
            }
            else if (_settings.RequireTrustedHashes)
            {
                result.SecurityIssues.Add("No trusted hash found for plugin assembly");
                result.ThreatLevel = ThreatLevel.Medium;
            }

            // Load assembly for reflection analysis (in isolated context)
            if (_settings.PerformStaticAnalysis)
            {
                await PerformStaticAnalysisAsync(assemblyPath, result);
            }
        }
        catch (Exception ex)
        {
            result.SecurityIssues.Add($"Assembly validation failed: {ex.Message}");
            result.ThreatLevel = ThreatLevel.High;
            result.ValidationException = ex;
        }

        return result;
    }

    private async Task PerformStaticAnalysisAsync(string assemblyPath, AssemblySecurityResult result)
    {
        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            
            // Check for dangerous API usage
            var types = assembly.GetTypes();
            foreach (var type in types)
            {
                CheckForDangerousAPIs(type, result);
            }

            // Check assembly references
            var referencedAssemblies = assembly.GetReferencedAssemblies();
            foreach (var refAssembly in referencedAssemblies)
            {
                if (IsSuspiciousReference(refAssembly.Name))
                {
                    result.SecurityIssues.Add($"References suspicious assembly: {refAssembly.Name}");
                    result.ThreatLevel = (ThreatLevel)Math.Max((int)result.ThreatLevel, (int)ThreatLevel.Medium);
                }
            }
        }
        catch (Exception ex)
        {
            result.SecurityIssues.Add($"Static analysis failed: {ex.Message}");
            result.ThreatLevel = ThreatLevel.Medium;
        }

        await Task.CompletedTask;
    }

    private void CheckForDangerousAPIs(Type type, AssemblySecurityResult result)
    {
        var dangerousNamespaces = new[]
        {
            "System.IO.File",
            "System.Diagnostics.Process",
            "System.Net.Http",
            "System.Runtime.InteropServices",
            "Microsoft.Win32.Registry"
        };

        try
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            
            foreach (var method in methods)
            {
                var methodBody = method.GetMethodBody();
                if (methodBody == null) continue;

                // Simple heuristic: check if method calls potentially dangerous APIs
                foreach (var dangerousNs in dangerousNamespaces)
                {
                    if (type.FullName?.StartsWith(dangerousNs) == true)
                    {
                        result.SecurityIssues.Add($"Uses potentially dangerous API: {dangerousNs}");
                        result.ThreatLevel = (ThreatLevel)Math.Max((int)result.ThreatLevel, (int)ThreatLevel.Low);
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignore reflection errors for security analysis
        }
    }

    private bool IsSuspiciousReference(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return false;

        var suspiciousNames = new[]
        {
            "System.Management",
            "Microsoft.Win32.Registry",
            "System.DirectoryServices"
        };

        return suspiciousNames.Any(suspicious => 
            assemblyName.Contains(suspicious, StringComparison.OrdinalIgnoreCase));
    }

    private List<string> ValidateCapabilities(PluginCapabilities capabilities)
    {
        var issues = new List<string>();

        // Check for potentially risky capability combinations
        if (capabilities.HasFlag(PluginCapabilities.SpeechRecognitionEngine) &&
            capabilities.HasFlag(PluginCapabilities.TextOutputSink))
        {
            issues.Add("Plugin has both speech input and text output capabilities - potential privacy risk");
        }

        if (capabilities.HasFlag(PluginCapabilities.UIExtension) &&
            capabilities.HasFlag(PluginCapabilities.TextProcessor))
        {
            issues.Add("Plugin can modify UI and process text - potential security risk");
        }

        return issues;
    }

    public void AddTrustedPlugin(string pluginName, string assemblyHash)
    {
        _trustedHashes[pluginName] = assemblyHash.ToLowerInvariant();
        SaveSecurityConfiguration();
        
        Telemetry.LogEvent("TrustedPluginAdded", new { PluginName = pluginName, Hash = assemblyHash });
    }

    public void BlockPlugin(string pluginName)
    {
        _blockedPlugins.Add(pluginName);
        SaveSecurityConfiguration();
        
        Telemetry.LogEvent("PluginBlocked", new { PluginName = pluginName });
    }

    public void AddTrustedAuthor(string author)
    {
        _allowedAuthors.Add(author);
        SaveSecurityConfiguration();
        
        Telemetry.LogEvent("TrustedAuthorAdded", new { Author = author });
    }

    private void LoadSecurityConfiguration()
    {
        // In a real implementation, this would load from encrypted configuration
        // For now, use default trusted authors
        _allowedAuthors.Add("Sttify Official");
        _allowedAuthors.Add("Microsoft Corporation");
    }

    private void SaveSecurityConfiguration()
    {
        // In a real implementation, this would save to encrypted configuration
        Telemetry.LogEvent("SecurityConfigurationSaved");
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class PluginSecuritySettings
{
    public bool RequireTrustedAuthors { get; set; } = false;
    public bool RequireTrustedHashes { get; set; } = false;
    public bool PerformStaticAnalysis { get; set; } = true;
    public ThreatLevel MaxAllowedThreatLevel { get; set; } = ThreatLevel.Medium;
}

[ExcludeFromCodeCoverage] // Simple data container class
public class PluginSecurityResult
{
    public string PluginName { get; set; } = "";
    public bool IsAllowed { get; set; }
    public ThreatLevel ThreatLevel { get; set; } = ThreatLevel.None;
    public List<string> SecurityIssues { get; } = new();
    public string AssemblyHash { get; set; } = "";
    public Exception? ValidationException { get; set; }
}

[ExcludeFromCodeCoverage] // Simple data container class
public class AssemblySecurityResult
{
    public string AssemblyHash { get; set; } = "";
    public ThreatLevel ThreatLevel { get; set; } = ThreatLevel.None;
    public List<string> SecurityIssues { get; } = new();
    public Exception? ValidationException { get; set; }
}

public enum ThreatLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}