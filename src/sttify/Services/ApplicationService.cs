using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Hotkey;
using Sttify.Corelib.Output;
using Sttify.Corelib.Rtss;
using Sttify.Corelib.Session;
using System.Windows;
using System.Windows.Interop;

namespace Sttify.Services;

public class ApplicationService : IDisposable
{
    private readonly SettingsProvider _settingsProvider;
    private readonly RecognitionSession _recognitionSession;
    private readonly HotkeyManager _hotkeyManager;
    private readonly RtssBridge _rtssService;
    private readonly ErrorRecovery _errorRecovery;
    private readonly HealthMonitor _healthMonitor;

    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
    public event EventHandler<TextRecognizedEventArgs>? TextRecognized;

    public ApplicationService(
        SettingsProvider settingsProvider,
        RecognitionSession recognitionSession,
        HotkeyManager hotkeyManager,
        RtssBridge rtssService)
    {
        System.Diagnostics.Debug.WriteLine("*** ApplicationService Constructor Called - VERSION 2024-DEBUG ***");
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _recognitionSession = recognitionSession ?? throw new ArgumentNullException(nameof(recognitionSession));
        _hotkeyManager = hotkeyManager ?? throw new ArgumentNullException(nameof(hotkeyManager));
        _rtssService = rtssService ?? throw new ArgumentNullException(nameof(rtssService));

        _errorRecovery = new ErrorRecovery();
        _healthMonitor = new HealthMonitor();

        _recognitionSession.OnStateChanged += OnSessionStateChanged;
        _recognitionSession.OnTextRecognized += OnTextRecognized;
        _hotkeyManager.OnHotkeyPressed += OnHotkeyPressed;

        _errorRecovery.OnRecoveryFailed += OnRecoveryFailed;
        _healthMonitor.OnHealthStatusChanged += OnHealthStatusChanged;

        SetupHealthChecks();
    }

    public void Initialize()
    {
        try
        {
            // Console.WriteLine("ApplicationService: Starting initialization...");
            Telemetry.LogEvent("ApplicationServiceInitializing");

            // Console.WriteLine("ApplicationService: Initializing hotkeys...");
            InitializeHotkeys();
            // Console.WriteLine("ApplicationService: Hotkeys initialized");

            // Console.WriteLine("ApplicationService: Initializing RTSS...");
            InitializeRtss();
            // Console.WriteLine("ApplicationService: RTSS initialized");

            // Hook WM_HOTKEY for global hotkeys when no explicit window handle is used
            RegisterHotkeyMessageHook();

            Telemetry.LogEvent("ApplicationServiceInitialized");
            // Console.WriteLine("ApplicationService: Initialization completed successfully");
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"ApplicationService initialization failed: {ex.Message}");
            // Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Telemetry.LogError("ApplicationServiceInitializationFailed", ex);
            throw;
        }
    }

    private void RegisterHotkeyMessageHook()
    {
        try
        {
            ComponentDispatcher.ThreadPreprocessMessage += (ref MSG msg, ref bool handled) =>
            {
                // WM_HOTKEY = 0x0312
                if (msg.message == 0x0312)
                {
                    _hotkeyManager.ProcessWindowMessage(msg.hwnd, (int)msg.message, msg.wParam, msg.lParam);
                }
            };
            Telemetry.LogEvent("HotkeyMessageHookRegistered");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyMessageHookRegisterFailed", ex);
        }
    }

    public async Task StartRecognitionAsync()
    {
        System.Diagnostics.Debug.WriteLine("*** ApplicationService.StartRecognitionAsync CALLED - VERSION 2024-DEBUG ***");
        System.Diagnostics.Debug.WriteLine($"*** RecognitionSession Instance ID: {_recognitionSession.GetHashCode()} ***");
        Telemetry.LogEvent("ApplicationService_StartRecognitionRequested");

        try
        {
            await _errorRecovery.ExecuteWithRetryAsync(async () =>
            {
                System.Diagnostics.Debug.WriteLine("*** About to call RecognitionSession.StartAsync ***");
                Telemetry.LogEvent("ApplicationService_CallingRecognitionSessionStart");
                await _recognitionSession.StartAsync();
                Telemetry.LogEvent("RecognitionStarted");
                System.Diagnostics.Debug.WriteLine("*** RecognitionSession.StartAsync completed ***");
            }, "StartRecognition");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** ApplicationService.StartRecognitionAsync FAILED: {ex.Message} ***");
            Telemetry.LogError("ApplicationService_StartRecognitionFailed", ex);
            throw;
        }
    }

    public async Task StopRecognitionAsync()
    {
        await _errorRecovery.ExecuteWithRetryAsync(async () =>
        {
            await _recognitionSession.StopAsync();
            Telemetry.LogEvent("RecognitionStopped");
        }, "StopRecognition");
    }

    public RecognitionMode GetCurrentMode()
    {
        return _recognitionSession.CurrentMode;
    }

    public void SetRecognitionMode(RecognitionMode mode)
    {
        _recognitionSession.CurrentMode = mode;
        Telemetry.LogEvent("RecognitionModeChanged", new { Mode = mode.ToString() });
    }

    public SessionState GetCurrentState()
    {
        return _recognitionSession.CurrentState;
    }

    private async void InitializeHotkeys()
    {
        try
        {
            // Console.WriteLine("ApplicationService: Getting settings for hotkeys...");
            var settings = await _settingsProvider.GetSettingsAsync();
            // Console.WriteLine($"ApplicationService: Settings loaded - ToggleUi: {settings.Hotkeys.ToggleUi}, ToggleMic: {settings.Hotkeys.ToggleMic}");

            // Re-register from clean state
            _hotkeyManager.UnregisterAllHotkeys();

            var uiOk = _hotkeyManager.RegisterHotkey(settings.Hotkeys.ToggleUi, "ToggleUi");
            var micOk = _hotkeyManager.RegisterHotkey(settings.Hotkeys.ToggleMic, "ToggleMic");

            Telemetry.LogEvent("HotkeysRegistered", new {
                ToggleUi = settings.Hotkeys.ToggleUi,
                ToggleMic = settings.Hotkeys.ToggleMic,
                ToggleUiRegistered = uiOk,
                ToggleMicRegistered = micOk
            });
            if (!uiOk || !micOk)
            {
                Telemetry.LogWarning("HotkeyRegistrationIssue", $"Some hotkeys failed to register. UI={uiOk}, MIC={micOk}");
            }
            // Console.WriteLine("ApplicationService: Hotkeys registered successfully");
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"ApplicationService: Hotkey initialization failed: {ex.Message}");
            Telemetry.LogError("HotkeyInitializationFailed", ex);
        }
    }

    public async Task ReinitializeHotkeysAsync()
    {
        InitializeHotkeys();
        await Task.CompletedTask;
    }

    private void InitializeRtss()
    {
        try
        {
            // Console.WriteLine("ApplicationService: Initializing RTSS service...");
            var initialized = _rtssService.Initialize();
            // Console.WriteLine($"ApplicationService: RTSS initialization result: {initialized}");
            Telemetry.LogEvent("RtssInitialized", new { Success = initialized });
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"ApplicationService: RTSS initialization failed: {ex.Message}");
            Telemetry.LogError("RtssInitializationFailed", ex);
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        SessionStateChanged?.Invoke(this, e);

        Telemetry.LogEvent("SessionStateChanged", new {
            OldState = e.OldState.ToString(),
            NewState = e.NewState.ToString()
        });
    }

    private async void OnTextRecognized(object? sender, TextRecognizedEventArgs e)
    {
        TextRecognized?.Invoke(this, e);

        var settings = await _settingsProvider.GetSettingsAsync();

        if (settings.Rtss.Enabled)
        {
            // Update OSD for both partial and final to achieve streaming-like display
            _rtssService.UpdateOsd(e.Text);
        }

        Telemetry.LogRecognition(e.Text, e.IsFinal, e.Confidence, settings.Privacy.MaskInLogs);
    }

    private async void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        try
        {
            switch (e.Name)
            {
                case "ToggleUi":
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        var controlWindow = System.Windows.Application.Current.Windows.OfType<Views.ControlWindow>().FirstOrDefault();
                        if (controlWindow != null)
                        {
                            if (controlWindow.Visibility == Visibility.Visible)
                                controlWindow.Hide();
                            else
                                controlWindow.Show();
                        }
                    });
                    break;

                case "ToggleMic":
                    if (_recognitionSession.CurrentState == SessionState.Listening)
                    {
                        await StopRecognitionAsync();
                    }
                    else if (_recognitionSession.CurrentState == SessionState.Idle)
                    {
                        await StartRecognitionAsync();
                    }
                    break;
            }

            Telemetry.LogEvent("HotkeyPressed", new { Name = e.Name, Hotkey = e.HotkeyString });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyProcessingFailed", ex, new { Name = e.Name });
        }
    }

    private void SetupHealthChecks()
    {
        // Recognition session health check
        _healthMonitor.RegisterHealthCheck("RecognitionSession", () =>
        {
            var state = _recognitionSession.CurrentState;
            if (state == SessionState.Error)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Recognition session is in error state"));
            }

            return Task.FromResult(HealthCheckResult.Healthy($"Recognition session state: {state}"));
        });

        // Settings provider health check
        _healthMonitor.RegisterHealthCheck("Settings", async () =>
        {
            try
            {
                var settings = await _settingsProvider.GetSettingsAsync();
                if (string.IsNullOrEmpty(settings.Engine.Vosk.ModelPath))
                {
                    return HealthCheckResult.Degraded("Vosk model path not configured");
                }

                return HealthCheckResult.Healthy("Settings loaded successfully");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy($"Failed to load settings: {ex.Message}");
            }
        });

        // RTSS service health check
        _healthMonitor.RegisterHealthCheck("RTSS", async () =>
        {
            var settings = await _settingsProvider.GetSettingsAsync();
            if (!settings.Rtss.Enabled)
            {
                return HealthCheckResult.Healthy("RTSS integration disabled");
            }

            // Note: In a real implementation, we would check if RTSS is actually working
            return HealthCheckResult.Healthy("RTSS integration active");
        });
    }

    private void OnRecoveryFailed(object? sender, ErrorRecoveryEventArgs e)
    {
        Telemetry.LogError("RecoveryFailed", e.Exception, new
        {
            OperationName = e.OperationName,
            AttemptNumber = e.AttemptNumber,
            MaxAttempts = e.MaxAttempts
        });

        // Show user notification for critical failures
        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                System.Windows.MessageBox.Show(
                    $"Operation '{e.OperationName}' failed after {e.MaxAttempts} attempts.\n\n" +
                    $"Error: {e.Exception.Message}",
                    "Sttify - Operation Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        }
    }

    private void OnHealthStatusChanged(object? sender, HealthStatusChangedEventArgs e)
    {
        if (!e.IsHealthy)
        {
            var unhealthyChecks = e.Results.Where(r => r.Value.Status != HealthStatus.Healthy);
            var message = "Application health degraded:\n" +
                         string.Join("\n", unhealthyChecks.Select(c => $"- {c.Key}: {c.Value.Description}"));

            Telemetry.LogWarning("ApplicationHealthDegraded", message);
        }
        else if (e.WasHealthy != e.IsHealthy)
        {
            Telemetry.LogEvent("ApplicationHealthRestored");
        }
    }

    public async Task<Dictionary<string, HealthCheckResult>> GetHealthStatusAsync()
    {
        return await _healthMonitor.GetHealthStatusAsync();
    }

    public void Dispose()
    {
        _healthMonitor?.Dispose();
        _hotkeyManager?.Dispose();
        _recognitionSession?.Dispose();
        _rtssService?.Dispose();
    }
}
