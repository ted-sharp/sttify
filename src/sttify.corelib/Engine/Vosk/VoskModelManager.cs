using System.Diagnostics.CodeAnalysis;
using Sttify.Corelib.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Sttify.Corelib.Engine.Vosk;

public class VoskModelManager
{
    private static readonly HttpClient _httpClient = new();
    
    public static readonly VoskModelInfo[] AvailableModels = new[]
    {
        new VoskModelInfo
        {
            Name = "vosk-model-ja-0.22",
            Language = "ja",
            Size = "1.2 GB",
            Description = "Japanese large model, high accuracy",
            DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-ja-0.22.zip",
            IsRecommended = true
        },
        new VoskModelInfo
        {
            Name = "vosk-model-small-ja-0.22",
            Language = "ja", 
            Size = "40 MB",
            Description = "Japanese small model, faster but lower accuracy",
            DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ja-0.22.zip",
            IsRecommended = false
        },
        new VoskModelInfo
        {
            Name = "vosk-model-en-us-0.22",
            Language = "en",
            Size = "1.8 GB", 
            Description = "English US large model",
            DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip",
            IsRecommended = false
        },
        new VoskModelInfo
        {
            Name = "vosk-model-small-en-us-0.15",
            Language = "en",
            Size = "40 MB",
            Description = "English US small model",
            DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip",
            IsRecommended = false
        }
    };

    public static string GetDefaultModelsDirectory()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "sttify", "models");
    }

    public static async Task<string> DownloadModelAsync(VoskModelInfo modelInfo, 
        string targetDirectory, 
        Action<DownloadProgressEventArgs>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (modelInfo == null)
            throw new ArgumentNullException(nameof(modelInfo));

        Directory.CreateDirectory(targetDirectory);
        
        var zipPath = Path.Combine(targetDirectory, $"{modelInfo.Name}.zip");
        var extractPath = Path.Combine(targetDirectory, modelInfo.Name);

        try
        {
            Telemetry.LogEvent("VoskModelDownloadStarted", new { Model = modelInfo.Name, Size = modelInfo.Size });

            // Download the model
            using var response = await _httpClient.GetAsync(modelInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var downloadedBytes = 0L;

            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await downloadStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                downloadedBytes += bytesRead;

                if (totalBytes > 0)
                {
                    var progress = (double)downloadedBytes / totalBytes * 100;
                    onProgress?.Invoke(new DownloadProgressEventArgs(progress, downloadedBytes, totalBytes, "Downloading"));
                }
            }

            onProgress?.Invoke(new DownloadProgressEventArgs(100, downloadedBytes, totalBytes, "Download complete"));

            // Extract the model
            onProgress?.Invoke(new DownloadProgressEventArgs(100, downloadedBytes, totalBytes, "Extracting model"));
            
            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, true);
            }

            ZipFile.ExtractToDirectory(zipPath, targetDirectory);

            // Clean up zip file
            File.Delete(zipPath);

            // Find the actual model directory (sometimes nested)
            var extractedDirs = Directory.GetDirectories(targetDirectory, "*vosk*", SearchOption.TopDirectoryOnly);
            if (extractedDirs.Length == 1 && extractedDirs[0] != extractPath)
            {
                Directory.Move(extractedDirs[0], extractPath);
            }

            Telemetry.LogEvent("VoskModelDownloadCompleted", new { Model = modelInfo.Name, Path = extractPath });

            onProgress?.Invoke(new DownloadProgressEventArgs(100, downloadedBytes, totalBytes, "Model ready"));
            
            return extractPath;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("VoskModelDownloadFailed", ex, new { Model = modelInfo.Name });
            
            // Clean up on failure
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
            }
            catch { }

            throw new VoskModelDownloadException($"Failed to download model {modelInfo.Name}: {ex.Message}", ex);
        }
    }

    public static bool IsModelInstalled(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || !Directory.Exists(modelPath))
            return false;

        // Check for essential Vosk model files
        var requiredFiles = new[]
        {
            "am/final.mdl",
            "graph/HCLG.fst", 
            "graph/words.txt"
        };

        return requiredFiles.All(file => File.Exists(Path.Combine(modelPath, file)));
    }

    public static VoskModelInfo? GetModelInfo(string modelPath)
    {
        if (!IsModelInstalled(modelPath))
            return null;

        var modelName = Path.GetFileName(modelPath);
        return AvailableModels.FirstOrDefault(m => m.Name == modelName);
    }

    public static string[] GetInstalledModels(string modelsDirectory)
    {
        if (!Directory.Exists(modelsDirectory))
            return Array.Empty<string>();

        return Directory.GetDirectories(modelsDirectory)
            .Where(IsModelInstalled)
            .ToArray();
    }

    public static async Task<VoskModelInfo[]> GetAvailableModelsAsync()
    {
        // In a real implementation, this might fetch from a remote API
        return await Task.FromResult(AvailableModels);
    }

    public static long GetModelSize(string modelPath)
    {
        if (!Directory.Exists(modelPath))
            return 0;

        try
        {
            return new DirectoryInfo(modelPath)
                .GetFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }
}

public class VoskModelInfo
{
    public string Name { get; set; } = "";
    public string Language { get; set; } = "";
    public string Size { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public bool IsRecommended { get; set; }
}

public class DownloadProgressEventArgs : EventArgs
{
    public double ProgressPercentage { get; }
    public long BytesDownloaded { get; }
    public long TotalBytes { get; }
    public string Status { get; }

    public DownloadProgressEventArgs(double progressPercentage, long bytesDownloaded, long totalBytes, string status)
    {
        ProgressPercentage = progressPercentage;
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        Status = status;
    }
}

[ExcludeFromCodeCoverage] // Simple exception class with no business logic\npublic class VoskModelDownloadException : Exception
{
    public VoskModelDownloadException(string message) : base(message) { }
    public VoskModelDownloadException(string message, Exception innerException) : base(message, innerException) { }
}