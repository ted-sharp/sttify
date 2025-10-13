using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Text;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Output;

public class StreamSink : ITextOutputSink, IDisposable
{
    private readonly bool _disposed = false;
    private readonly object _lock = new();
    private readonly StreamSinkSettings _settings;
    private StreamWriter? _fileWriter;
    private MemoryMappedViewAccessor? _memoryMappedAccessor;
    private MemoryMappedFile? _memoryMappedFile;
    private long _totalBytesWritten;
    private long _totalWrites;

    public StreamSink(StreamSinkSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Initialize();
    }

    public void Dispose()
    {
        _fileWriter?.Dispose();
        _memoryMappedAccessor?.Dispose();
        _memoryMappedFile?.Dispose();
    }

    public string Id => "stream";
    public string Name => "Stream";
    public bool IsAvailable => GetAvailability();

    public Task<bool> CanSendAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(IsAvailable && !_disposed);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || _disposed)
            return;

        var startTime = DateTime.UtcNow;
        var timestamp = startTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");

        var line = text;

        // Apply custom prefix
        if (!string.IsNullOrEmpty(_settings.CustomPrefix))
        {
            line = _settings.CustomPrefix + line;
        }

        // Apply timestamp if enabled
        if (_settings.IncludeTimestamp)
        {
            line = $"[{timestamp}] {line}";
        }

        // Apply custom suffix
        if (!string.IsNullOrEmpty(_settings.CustomSuffix))
        {
            line = line + _settings.CustomSuffix;
        }

        try
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                switch (_settings.OutputType)
                {
                    case StreamOutputType.Console:
                        Console.WriteLine(line);
                        break;

                    case StreamOutputType.File:
                        // file write is handled outside lock to avoid blocking
                        break;

                    case StreamOutputType.SharedMemory:
                        WriteToSharedMemory(line);
                        break;

                    case StreamOutputType.StandardOutput:
                        Console.Out.WriteLine(line);
                        break;

                    default:
                        throw new TextOutputFailedException($"Unsupported output type: {_settings.OutputType}");
                }

                _totalWrites++;
                _totalBytesWritten += Encoding.UTF8.GetByteCount(line);
            }

            // Perform file write outside lock for async I/O
            if (_settings.OutputType == StreamOutputType.File)
            {
                await WriteToFileAsync(line, cancellationToken);
            }

            var duration = DateTime.UtcNow - startTime;

            Telemetry.LogEvent("StreamSinkWrite", new
            {
                OutputType = _settings.OutputType.ToString(),
                TextLength = text.Length,
                DurationMs = duration.TotalMilliseconds,
                TotalWrites = _totalWrites,
                TotalBytesWritten = _totalBytesWritten
            });
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            Telemetry.LogError("StreamSinkWriteFailed", ex, new
            {
                OutputType = _settings.OutputType.ToString(),
                TextLength = text.Length,
                DurationMs = duration.TotalMilliseconds
            });

            throw new TextOutputFailedException($"Failed to write to {_settings.OutputType}: {ex.Message}", ex);
        }
    }

    private bool GetAvailability()
    {
        if (_disposed)
            return false;

        return _settings.OutputType switch
        {
            StreamOutputType.Console or StreamOutputType.StandardOutput => true,
            StreamOutputType.File => !string.IsNullOrEmpty(_settings.FilePath) &&
                                   (File.Exists(_settings.FilePath) || CanCreateFile(_settings.FilePath)),
            StreamOutputType.SharedMemory => !string.IsNullOrEmpty(_settings.SharedMemoryName),
            _ => false
        };
    }

    private bool CanCreateFile(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return !string.IsNullOrEmpty(directory) && (Directory.Exists(directory) || CanCreateDirectory(directory));
        }
        catch
        {
            return false;
        }
    }

    private bool CanCreateDirectory(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WriteToFileAsync(string line, CancellationToken cancellationToken)
    {
        if (_fileWriter == null)
        {
            // Try to reinitialize file writer if it's null
            InitializeFileOutput();
            if (_fileWriter == null)
                throw new TextOutputFailedException("File writer is not initialized");
        }

        await _fileWriter.WriteLineAsync(line.AsMemory(), cancellationToken);

        if (_settings.ForceFlush)
        {
            await _fileWriter.FlushAsync();
        }

        // Rotate file if size limit is reached
        if (_settings.MaxFileSizeBytes > 0 && _fileWriter.BaseStream.Length > _settings.MaxFileSizeBytes)
        {
            await RotateFileAsync();
        }
    }

    private void Initialize()
    {
        switch (_settings.OutputType)
        {
            case StreamOutputType.File:
                InitializeFileOutput();
                break;

            case StreamOutputType.SharedMemory:
                InitializeSharedMemory();
                break;
        }
    }

    private void InitializeFileOutput()
    {
        if (string.IsNullOrEmpty(_settings.FilePath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(_settings.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileWriter = new StreamWriter(_settings.FilePath, _settings.AppendToFile, Encoding.UTF8)
            {
                AutoFlush = _settings.ForceFlush
            };

            Telemetry.LogEvent("StreamSinkFileInitialized", new
            {
                _settings.FilePath,
                AppendMode = _settings.AppendToFile,
                MaxFileSize = _settings.MaxFileSizeBytes
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("StreamSinkFileInitializationFailed", ex, new { _settings.FilePath });
            throw;
        }
    }

    private Task RotateFileAsync()
    {
        if (_fileWriter == null || string.IsNullOrEmpty(_settings.FilePath))
            return Task.CompletedTask;

        try
        {
            // Close current file
            _fileWriter.Dispose();

            // Generate rotated filename
            var directory = Path.GetDirectoryName(_settings.FilePath)!;
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(_settings.FilePath);
            var extension = Path.GetExtension(_settings.FilePath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var rotatedPath = Path.Combine(directory, $"{fileNameWithoutExt}_{timestamp}{extension}");

            // Rename current file
            File.Move(_settings.FilePath, rotatedPath);

            // Create new file writer
            _fileWriter = new StreamWriter(_settings.FilePath, false, Encoding.UTF8)
            {
                AutoFlush = _settings.ForceFlush
            };

            Telemetry.LogEvent("StreamSinkFileRotated", new
            {
                OriginalPath = _settings.FilePath,
                RotatedPath = rotatedPath,
                FileSize = new FileInfo(rotatedPath).Length
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("StreamSinkFileRotationFailed", ex, new { _settings.FilePath });
            // Try to reinitialize the file writer
            InitializeFileOutput();
        }

        return Task.CompletedTask;
    }

    private void InitializeSharedMemory()
    {
        if (string.IsNullOrEmpty(_settings.SharedMemoryName))
            return;

        try
        {
            // Windows-specific memory mapped file functionality
            if (OperatingSystem.IsWindows())
            {
                _memoryMappedFile = MemoryMappedFile.CreateOrOpen(_settings.SharedMemoryName, _settings.SharedMemorySize);
                _memoryMappedAccessor = _memoryMappedFile.CreateViewAccessor();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize shared memory: {ex.Message}");
        }
    }

    private void WriteToSharedMemory(string text)
    {
        if (_memoryMappedAccessor == null)
            return;

        try
        {
            // Prefix with 4-byte little-endian length (payload only), then UTF-8 bytes
            var payload = Encoding.UTF8.GetBytes(text);
            int maxPayload = Math.Max(0, _settings.SharedMemorySize - 4);
            if (payload.Length > maxPayload)
            {
                Array.Resize(ref payload, maxPayload);
            }
            int len = payload.Length;
            _memoryMappedAccessor.Write(0, (byte)(len & 0xFF));
            _memoryMappedAccessor.Write(1, (byte)((len >> 8) & 0xFF));
            _memoryMappedAccessor.Write(2, (byte)((len >> 16) & 0xFF));
            _memoryMappedAccessor.Write(3, (byte)((len >> 24) & 0xFF));
            _memoryMappedAccessor.WriteArray(4, payload, 0, payload.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write to shared memory: {ex.Message}");
        }
    }
}

public enum StreamOutputType
{
    Console,
    File,
    SharedMemory,
    StandardOutput
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class StreamSinkSettings
{
    public StreamOutputType OutputType { get; set; } = StreamOutputType.Console;
    public string FilePath { get; set; } = "";
    public bool AppendToFile { get; set; } = true;
    public bool IncludeTimestamp { get; set; } = true;
    public bool ForceFlush { get; set; } = true; // Force flush after each write
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB default
    public string SharedMemoryName { get; set; } = "sttify_stream";
    public int SharedMemorySize { get; set; } = 4096;
    public string CustomPrefix { get; set; } = ""; // Custom prefix for each line
    public string CustomSuffix { get; set; } = ""; // Custom suffix for each line
}
