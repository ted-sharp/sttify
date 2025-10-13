using System.Runtime.Versioning;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Hotkey;

namespace Sttify.Corelib.Services;

[SupportedOSPlatform("windows")]
public class HotkeyService : IDisposable
{
    private readonly HotkeyManager _hotkeyManager;
    private readonly SettingsProvider _settingsProvider;
    private SttifySettings? _currentSettings;
    private bool _disposed;

    [SupportedOSPlatform("windows")]
    public HotkeyService(HotkeyManager hotkeyManager, SettingsProvider settingsProvider)
    {
        _hotkeyManager = hotkeyManager ?? throw new ArgumentNullException(nameof(hotkeyManager));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));

        _hotkeyManager.OnHotkeyPressed += OnHotkeyPressed;
        _hotkeyManager.OnHotkeyRegistered += OnHotkeyRegistered;
        _hotkeyManager.OnHotkeyUnregistered += OnHotkeyUnregistered;
        _hotkeyManager.OnHotkeyRegistrationFailed += OnHotkeyRegistrationFailedInternal;
    }

    [SupportedOSPlatform("windows")]
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _hotkeyManager.OnHotkeyPressed -= OnHotkeyPressed;
                _hotkeyManager.OnHotkeyRegistered -= OnHotkeyRegistered;
                _hotkeyManager.OnHotkeyUnregistered -= OnHotkeyUnregistered;

                _hotkeyManager.Dispose();
            }
            catch (Exception ex)
            {
                Telemetry.LogError("HotkeyServiceDisposeFailed", ex);
            }
            finally
            {
                _disposed = true;
                Telemetry.LogEvent("HotkeyServiceDisposed");
            }
        }
    }

    public event EventHandler<HotkeyTriggeredEventArgs>? OnHotkeyTriggered;
    public event EventHandler<HotkeyRegistrationFailedEventArgs>? OnHotkeyRegistrationFailed;

    [SupportedOSPlatform("windows")]
    public async Task InitializeAsync()
    {
        try
        {
            _currentSettings = await _settingsProvider.GetSettingsAsync();
            await RegisterApplicationHotkeysAsync();

            Telemetry.LogEvent("HotkeyServiceInitialized");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyServiceInitializationFailed", ex);
            throw;
        }
    }

    private void OnHotkeyRegistrationFailedInternal(object? sender, HotkeyRegistrationFailedEventArgs e)
    {
        try
        {
            Telemetry.LogWarning("HotkeyRegistrationFailed", $"Failed to register hotkey {e.HotkeyString} (Win32={e.Win32Error})", new { e.Name, e.HotkeyString, e.Win32Error });
            OnHotkeyRegistrationFailed?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyService_OnRegistrationFailed_HandlerError", ex, new { e.Name, e.HotkeyString, e.Win32Error });
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task RefreshHotkeysAsync()
    {
        try
        {
            // Unregister all current hotkeys
            _hotkeyManager.UnregisterAllHotkeys();

            // Reload settings and register new hotkeys
            _currentSettings = await _settingsProvider.GetSettingsAsync();
            await RegisterApplicationHotkeysAsync();

            Telemetry.LogEvent("HotkeysRefreshed");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyRefreshFailed", ex);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private Task RegisterApplicationHotkeysAsync()
    {
        if (_currentSettings?.Hotkeys == null)
            return Task.CompletedTask;

        // Register UI toggle hotkey
        if (!string.IsNullOrEmpty(_currentSettings.Hotkeys.ToggleUi))
        {
            if (_hotkeyManager.RegisterHotkey(_currentSettings.Hotkeys.ToggleUi, "ToggleUI"))
            {
                Telemetry.LogEvent("ApplicationHotkeyRegistered", new { Type = "ToggleUI", Hotkey = _currentSettings.Hotkeys.ToggleUi });
            }
        }

        // Register microphone toggle hotkey
        if (!string.IsNullOrEmpty(_currentSettings.Hotkeys.ToggleMic))
        {
            if (_hotkeyManager.RegisterHotkey(_currentSettings.Hotkeys.ToggleMic, "ToggleMic"))
            {
                Telemetry.LogEvent("ApplicationHotkeyRegistered", new { Type = "ToggleMic", Hotkey = _currentSettings.Hotkeys.ToggleMic });
            }
        }

        // Register stop microphone hotkey
        if (!string.IsNullOrEmpty(_currentSettings.Hotkeys.StopMic))
        {
            if (_hotkeyManager.RegisterHotkey(_currentSettings.Hotkeys.StopMic, "StopMic"))
            {
                Telemetry.LogEvent("ApplicationHotkeyRegistered", new { Type = "StopMic", Hotkey = _currentSettings.Hotkeys.StopMic });
            }
        }

        return Task.CompletedTask;
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        try
        {
            var action = e.Name switch
            {
                "ToggleUI" => HotkeyAction.ToggleUI,
                "ToggleMic" => HotkeyAction.ToggleMicrophone,
                "StopMic" => HotkeyAction.StopMicrophone,
                _ => HotkeyAction.Unknown
            };

            OnHotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(e.Name, e.HotkeyString, action));

            Telemetry.LogEvent("HotkeyTriggered", new
            {
                Name = e.Name,
                HotkeyString = e.HotkeyString,
                Action = action.ToString(),
                Timestamp = e.Timestamp
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyHandlingFailed", ex, new { Name = e.Name, HotkeyString = e.HotkeyString });
        }
    }

    private void OnHotkeyRegistered(object? sender, HotkeyRegistrationEventArgs e)
    {
        Telemetry.LogEvent("HotkeyRegistrationChanged", new
        {
            Name = e.Name,
            HotkeyString = e.HotkeyString,
            IsRegistered = e.IsRegistered
        });
    }

    private void OnHotkeyUnregistered(object? sender, HotkeyRegistrationEventArgs e)
    {
        Telemetry.LogEvent("HotkeyRegistrationChanged", new
        {
            Name = e.Name,
            HotkeyString = e.HotkeyString,
            IsRegistered = e.IsRegistered
        });
    }

    [SupportedOSPlatform("windows")]
    public bool ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        return _hotkeyManager.ProcessWindowMessage(hwnd, msg, wParam, lParam);
    }

    [SupportedOSPlatform("windows")]
    public IReadOnlyDictionary<string, string> GetRegisteredHotkeys()
    {
        return _hotkeyManager.GetRegisteredHotkeys();
    }

    [SupportedOSPlatform("windows")]
    public bool ValidateHotkeyString(string hotkeyString)
    {
        return _hotkeyManager.ValidateHotkeyString(hotkeyString);
    }
}

public enum HotkeyAction
{
    Unknown,
    ToggleUI,
    ToggleMicrophone,
    StopMicrophone
}

public class HotkeyTriggeredEventArgs : EventArgs
{
    public HotkeyTriggeredEventArgs(string name, string hotkeyString, HotkeyAction action)
    {
        Name = name;
        HotkeyString = hotkeyString;
        Action = action;
        Timestamp = DateTime.UtcNow;
    }

    public string Name { get; }
    public string HotkeyString { get; }
    public HotkeyAction Action { get; }
    public DateTime Timestamp { get; }
}
