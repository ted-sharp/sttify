using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Sttify.Corelib.Diagnostics;
using Vanara.PInvoke;
using static Vanara.PInvoke.Kernel32;
using static Vanara.PInvoke.User32;

namespace Sttify.Corelib.Hotkey;

[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage] // Win32 API integration, system dependent, difficult to mock effectively
public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private readonly Dictionary<string, int> _hotkeyNameToId = new();
    private readonly Random _random = new();

    private readonly Dictionary<int, HotkeyInfo> _registeredHotkeys = new();
    private readonly IntPtr _windowHandle;
    private bool _disposed;

    public HotkeyManager(IntPtr windowHandle = default)
    {
        _windowHandle = windowHandle;
        Telemetry.LogEvent("HotkeyManagerCreated", new { WindowHandle = _windowHandle.ToString("X") });
    }

    [SupportedOSPlatform("windows")]
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                UnregisterAllHotkeys();
            }
            catch (Exception ex)
            {
                Telemetry.LogError("HotkeyManagerDisposeFailed", ex);
            }
            finally
            {
                _disposed = true;
                Telemetry.LogEvent("HotkeyManagerDisposed");
            }
        }
    }

    public event EventHandler<HotkeyPressedEventArgs>? OnHotkeyPressed;
    public event EventHandler<HotkeyRegistrationEventArgs>? OnHotkeyRegistered;
    public event EventHandler<HotkeyRegistrationEventArgs>? OnHotkeyUnregistered;
    public event EventHandler<HotkeyRegistrationFailedEventArgs>? OnHotkeyRegistrationFailed;

    [SupportedOSPlatform("windows")]
    public bool RegisterHotkey(string hotkeyString, string name)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString) || string.IsNullOrWhiteSpace(name))
        {
            Telemetry.LogWarning("HotkeyRegistrationFailed", "Invalid hotkey string or name");
            return false;
        }

        // Check if name is already registered
        if (_hotkeyNameToId.ContainsKey(name))
        {
            Telemetry.LogWarning("HotkeyRegistrationFailed", $"Hotkey name already registered: {name}");
            return false;
        }

        if (!ParseHotkeyString(hotkeyString, out var modifiers, out var key))
        {
            Telemetry.LogWarning("HotkeyRegistrationFailed", $"Failed to parse hotkey string: {hotkeyString}");
            return false;
        }

        var id = GenerateUniqueId();

        try
        {
            if (RegisterHotKey(new HWND(_windowHandle), id, (HotKeyModifiers)modifiers, (uint)key))
            {
                var hotkeyInfo = new HotkeyInfo(name, hotkeyString, modifiers, key);
                _registeredHotkeys[id] = hotkeyInfo;
                _hotkeyNameToId[name] = id;

                OnHotkeyRegistered?.Invoke(this, new HotkeyRegistrationEventArgs(name, hotkeyString, true));
                Telemetry.LogEvent("HotkeyRegistered", new
                {
                    Name = name,
                    HotkeyString = hotkeyString,
                    Id = id,
                    Modifiers = modifiers.ToString(),
                    Key = key.ToString()
                });

                return true;
            }
            else
            {
                var lastError = GetLastError();
                var errorMessage = lastError == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? "Hotkey combination already in use by another application"
                    : $"Failed to register hotkey (Win32 Error: {lastError})";

                Telemetry.LogError("HotkeyRegistrationFailed", new Exception(errorMessage), new
                {
                    Name = name,
                    HotkeyString = hotkeyString,
                    Win32Error = lastError
                });

                OnHotkeyRegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(
                    name,
                    hotkeyString,
                    (uint)lastError,
                    Array.Empty<string>()
                ));

                return false;
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyRegistrationException", ex, new { Name = name, HotkeyString = hotkeyString });
            return false;
        }
    }

    // Alternative suggestions intentionally omitted per product policy

    private int GenerateUniqueId()
    {
        int id;
        do
        {
            id = _random.Next(1000, 9999);
        } while (_registeredHotkeys.ContainsKey(id));
        return id;
    }

    [SupportedOSPlatform("windows")]
    public void UnregisterHotkey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Telemetry.LogWarning("HotkeyUnregistrationFailed", "Invalid hotkey name");
            return;
        }

        if (!_hotkeyNameToId.TryGetValue(name, out var id))
        {
            Telemetry.LogWarning("HotkeyUnregistrationFailed", $"Hotkey not found: {name}");
            return;
        }

        try
        {
            if (UnregisterHotKey(new HWND(_windowHandle), id))
            {
                var hotkeyInfo = _registeredHotkeys[id];
                _registeredHotkeys.Remove(id);
                _hotkeyNameToId.Remove(name);

                OnHotkeyUnregistered?.Invoke(this, new HotkeyRegistrationEventArgs(name, hotkeyInfo.HotkeyString, false));
                Telemetry.LogEvent("HotkeyUnregistered", new { Name = name, Id = id });
            }
            else
            {
                var lastError = GetLastError();
                Telemetry.LogError("HotkeyUnregistrationFailed", new Exception($"Win32 Error: {lastError}"), new { Name = name, Id = id });
            }
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyUnregistrationException", ex, new { Name = name, Id = id });
        }
    }

    [SupportedOSPlatform("windows")]
    public void UnregisterAllHotkeys()
    {
        var hotkeysToRemove = _registeredHotkeys.Keys.ToList();
        var unregisteredCount = 0;

        foreach (var id in hotkeysToRemove)
        {
            try
            {
                if (UnregisterHotKey(new HWND(_windowHandle), id))
                {
                    unregisteredCount++;
                }
            }
            catch (Exception ex)
            {
                Telemetry.LogError("HotkeyUnregistrationException", ex, new { Id = id });
            }
        }

        _registeredHotkeys.Clear();
        _hotkeyNameToId.Clear();

        Telemetry.LogEvent("AllHotkeysUnregistered", new
        {
            TotalHotkeys = hotkeysToRemove.Count,
            SuccessfullyUnregistered = unregisteredCount
        });
    }

    public bool ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_registeredHotkeys.TryGetValue(id, out var hotkeyInfo))
            {
                OnHotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(hotkeyInfo.Name, hotkeyInfo.HotkeyString));
                return true;
            }
        }
        return false;
    }

    private static bool ParseHotkeyString(string hotkeyString, out ModifierKeys modifiers, out VirtualKey key)
    {
        modifiers = ModifierKeys.None;
        key = VirtualKey.None;

        if (string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLower())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    return false;
            }
        }

        var keyString = parts[^1].Trim().ToUpper();
        if (!Enum.TryParse<VirtualKey>(keyString, out key))
            return false;

        return true;
    }

    // Win32 APIs now provided by Vanara.PInvoke

    // Additional utility methods
    public bool IsHotkeyRegistered(string name)
    {
        return _hotkeyNameToId.ContainsKey(name);
    }

    public IReadOnlyDictionary<string, string> GetRegisteredHotkeys()
    {
        return _registeredHotkeys.Values.ToDictionary(h => h.Name, h => h.HotkeyString);
    }

    public bool ValidateHotkeyString(string hotkeyString)
    {
        return ParseHotkeyString(hotkeyString, out _, out _);
    }
}

[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

[ExcludeFromCodeCoverage] // Simple data container class
public class HotkeyInfo
{
    public HotkeyInfo(string name, string hotkeyString, ModifierKeys modifiers, VirtualKey key)
    {
        Name = name;
        HotkeyString = hotkeyString;
        Modifiers = modifiers;
        Key = key;
    }

    public string Name { get; }
    public string HotkeyString { get; }
    public ModifierKeys Modifiers { get; }
    public VirtualKey Key { get; }
}

public enum VirtualKey
{
    None = 0,
    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
    F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
    A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47,
    H = 0x48, I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E,
    O = 0x4F, P = 0x50, Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55,
    V = 0x56, W = 0x57, X = 0x58, Y = 0x59, Z = 0x5A,
    D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34,
    D5 = 0x35, D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39,
    Space = 0x20, Enter = 0x0D, Escape = 0x1B, Tab = 0x09,
    Back = 0x08, Delete = 0x2E, Insert = 0x2D,
    Home = 0x24, End = 0x23, PageUp = 0x21, PageDown = 0x22,
    Up = 0x26, Down = 0x28, Left = 0x25, Right = 0x27
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class HotkeyPressedEventArgs : EventArgs
{
    public HotkeyPressedEventArgs(string name, string hotkeyString)
    {
        Name = name;
        HotkeyString = hotkeyString;
        Timestamp = DateTime.UtcNow;
    }

    public string Name { get; }
    public string HotkeyString { get; }
    public DateTime Timestamp { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class HotkeyRegistrationEventArgs : EventArgs
{
    public HotkeyRegistrationEventArgs(string name, string hotkeyString, bool isRegistered)
    {
        Name = name;
        HotkeyString = hotkeyString;
        IsRegistered = isRegistered;
        Timestamp = DateTime.UtcNow;
    }

    public string Name { get; }
    public string HotkeyString { get; }
    public bool IsRegistered { get; }
    public DateTime Timestamp { get; }
}

[ExcludeFromCodeCoverage] // Simple data container EventArgs class
public class HotkeyRegistrationFailedEventArgs : EventArgs
{
    public HotkeyRegistrationFailedEventArgs(string name, string hotkeyString, uint win32Error, string[] suggestions)
    {
        Name = name;
        HotkeyString = hotkeyString;
        Win32Error = win32Error;
        Suggestions = suggestions;
    }

    public string Name { get; }
    public string HotkeyString { get; }
    public uint Win32Error { get; }
    public string[] Suggestions { get; }
}
