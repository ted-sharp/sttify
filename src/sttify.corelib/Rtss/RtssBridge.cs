using System.Runtime.InteropServices;
using System.Text;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Rtss;

[ExcludeFromCodeCoverage] // External RTSS shared memory integration, difficult to mock effectively
public class RtssBridge : IDisposable
{
    private const string RTSS_SHARED_MEMORY_NAME = "RTSSSharedMemoryV2";
    private const int RTSS_MAX_TEXT_LENGTH = 4096;
    private const int RTSS_HEADER_SIZE = 256;
    
    private bool _isInitialized;
    private readonly object _lockObject = new();
    private string _lastDisplayedText = "";
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly RtssSettings _settings;
    private IntPtr _sharedMemoryHandle = IntPtr.Zero;
    private IntPtr _sharedMemoryPtr = IntPtr.Zero;

    // Windows API imports for shared memory operations
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, 
        uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    private const uint FILE_MAP_WRITE = 0x0002;

    public RtssBridge(RtssSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool Initialize()
    {
        lock (_lockObject)
        {
            if (_isInitialized)
                return true;

            try
            {
                // Try to open RTSS shared memory
                _sharedMemoryHandle = OpenFileMapping(FILE_MAP_WRITE, false, RTSS_SHARED_MEMORY_NAME);
                if (_sharedMemoryHandle == IntPtr.Zero)
                {
                    Telemetry.LogEvent("RtssInitializationFailed", new { Reason = "SharedMemoryNotFound" });
                    return false;
                }

                _sharedMemoryPtr = MapViewOfFile(_sharedMemoryHandle, FILE_MAP_WRITE, 0, 0, UIntPtr.Zero);
                if (_sharedMemoryPtr == IntPtr.Zero)
                {
                    CloseHandle(_sharedMemoryHandle);
                    _sharedMemoryHandle = IntPtr.Zero;
                    Telemetry.LogEvent("RtssInitializationFailed", new { Reason = "MemoryMappingFailed" });
                    return false;
                }

                _isInitialized = true;
                Telemetry.LogEvent("RtssInitialized", new { SharedMemoryName = RTSS_SHARED_MEMORY_NAME });
                return true;
            }
            catch (Exception ex)
            {
                Telemetry.LogError("RtssInitializationException", ex);
                CleanupResources();
                return false;
            }
        }
    }

    public void UpdateOsd(string text)
    {
        if (!_settings.Enabled || !_isInitialized)
            return;

        var now = DateTime.UtcNow;
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.UpdatePerSec);

        if (now - _lastUpdate < minInterval && _lastDisplayedText == text)
            return;

        var displayText = text;
        if (displayText.Length > _settings.TruncateLength)
        {
            displayText = displayText.Substring(0, _settings.TruncateLength) + "...";
        }

        try
        {
            DisplayTextOnOsd(displayText);
            _lastDisplayedText = text;
            _lastUpdate = now;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to update RTSS OSD: {ex.Message}");
        }
    }

    public void ClearOsd()
    {
        if (!_isInitialized)
            return;

        try
        {
            DisplayTextOnOsd("");
            _lastDisplayedText = "";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to clear RTSS OSD: {ex.Message}");
        }
    }

    private void CleanupResources()
    {
        if (_sharedMemoryPtr != IntPtr.Zero)
        {
            UnmapViewOfFile(_sharedMemoryPtr);
            _sharedMemoryPtr = IntPtr.Zero;
        }

        if (_sharedMemoryHandle != IntPtr.Zero)
        {
            CloseHandle(_sharedMemoryHandle);
            _sharedMemoryHandle = IntPtr.Zero;
        }
    }

    private void DisplayTextOnOsd(string text)
    {
        if (_sharedMemoryPtr == IntPtr.Zero)
        {
            Telemetry.LogWarning("RtssDisplayFailed", "SharedMemoryNotMapped");
            return;
        }

        try
        {
            // RTSS shared memory format:
            // Header (256 bytes) + Text data
            // For simplicity, we'll write text directly to the mapped memory
            var textBytes = Encoding.UTF8.GetBytes(text);
            var totalLength = Math.Min(textBytes.Length, RTSS_MAX_TEXT_LENGTH - 1);

            // Write text length at the beginning (4 bytes)
            Marshal.WriteInt32(_sharedMemoryPtr, totalLength);
            
            // Write the text data after the header
            var textPtr = IntPtr.Add(_sharedMemoryPtr, RTSS_HEADER_SIZE);
            Marshal.Copy(textBytes, 0, textPtr, totalLength);
            
            // Null terminate
            Marshal.WriteByte(textPtr, totalLength, 0);

            Telemetry.LogEvent("RtssTextDisplayed", new { TextLength = totalLength });
        }  
        catch (Exception ex)
        {
            Telemetry.LogError("RtssDisplayTextFailed", ex, new { Text = text });
        }
    }

    private string GetRtssLibraryPath()
    {
        const string rtssLibraryName = "RTSSHooks.dll";
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var rtssPath = Path.Combine(programFiles, "RivaTuner Statistics Server", rtssLibraryName);
        
        if (File.Exists(rtssPath))
            return rtssPath;

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        rtssPath = Path.Combine(programFilesX86, "RivaTuner Statistics Server", rtssLibraryName);
        
        return rtssPath;
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            if (_isInitialized)
            {
                try
                {
                    ClearOsd();
                }
                catch (Exception ex)
                {
                    Telemetry.LogError("RtssDisposeClearFailed", ex);
                }

                CleanupResources();
                _isInitialized = false;
                Telemetry.LogEvent("RtssDisposed");
            }
        }
    }
}