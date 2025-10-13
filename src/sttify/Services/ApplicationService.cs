using System.Windows;
using System.Windows.Interop;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Hotkey;
using Sttify.Corelib.Services;
using Sttify.Corelib.Session;

// duplicate using removed

namespace Sttify.Services;

public class ApplicationService : IDisposable
{
    private readonly ErrorRecovery _errorRecovery;
    private readonly HealthMonitor _healthMonitor;
    private readonly HotkeyService _hotkeyService;
    private readonly OverlayService _overlayService;
    private readonly RecognitionSession _recognitionSession;
    private readonly SettingsProvider _settingsProvider;

    private ThreadMessageEventHandler? _hotkeyThreadHandler;

    public ApplicationService(
        SettingsProvider settingsProvider,
        RecognitionSession recognitionSession,
        HotkeyService hotkeyService,
        OverlayService overlayService)
    {
        System.Diagnostics.Debug.WriteLine("*** ApplicationService Constructor Called - VERSION 2024-DEBUG ***");
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _recognitionSession = recognitionSession ?? throw new ArgumentNullException(nameof(recognitionSession));
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _overlayService = overlayService ?? throw new ArgumentNullException(nameof(overlayService));

        _errorRecovery = new ErrorRecovery();
        _healthMonitor = new HealthMonitor();

        _recognitionSession.OnStateChanged += OnSessionStateChanged;
        _recognitionSession.OnTextRecognized += OnTextRecognized;
        _hotkeyService.OnHotkeyTriggered += OnHotkeyTriggered;
        _hotkeyService.OnHotkeyRegistrationFailed += OnHotkeyRegistrationFailed;

        _errorRecovery.OnRecoveryFailed += OnRecoveryFailed;
        _healthMonitor.OnHealthStatusChanged += OnHealthStatusChanged;

        SetupHealthChecks();
    }

    public void Dispose()
    {
        _healthMonitor.Dispose();
        if (_hotkeyThreadHandler is not null)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= _hotkeyThreadHandler;
            _hotkeyThreadHandler = null;
        }
        if (_hotkeyService is not null)
        {
            _hotkeyService.OnHotkeyTriggered -= OnHotkeyTriggered;
            _hotkeyService.OnHotkeyRegistrationFailed -= OnHotkeyRegistrationFailed;
            _hotkeyService.Dispose();
        }
        _recognitionSession.Dispose();
        _overlayService.Dispose();
    }

    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
    public event EventHandler<TextRecognizedEventArgs>? TextRecognized;

    public void Initialize()
    {
        try
        {
            // Console.WriteLine("ApplicationService: Starting initialization...");
            Telemetry.LogEvent("ApplicationServiceInitializing");

            // Initialize hotkeys via HotkeyService
            AsyncHelper.FireAndForget(() => _hotkeyService.InitializeAsync(), nameof(HotkeyService.InitializeAsync));

            // Initialize overlay
            InitializeOverlay();

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
            _hotkeyThreadHandler = (ref MSG msg, ref bool _) =>
            {
                // WM_HOTKEY = 0x0312
                if (msg.message == 0x0312)
                {
                    _hotkeyService.ProcessWindowMessage(msg.hwnd, msg.message, msg.wParam, msg.lParam);
                }
            };
            ComponentDispatcher.ThreadPreprocessMessage += _hotkeyThreadHandler;
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

    // Hotkey registration is delegated to HotkeyService

    public async Task ReinitializeHotkeysAsync()
    {
        try
        {
            await _hotkeyService.RefreshHotkeysAsync();
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeysReinitFailed", ex);
        }
    }

    public async Task ReinitializeEngineAsync(bool restartIfRunning = true)
    {
        try
        {
            var wasListening = _recognitionSession.CurrentState == SessionState.Listening;
            if (wasListening && restartIfRunning)
            {
                await StopRecognitionAsync().ConfigureAwait(false);
            }

            // Next StartRecognitionAsync() will rebuild engine from latest settings via session
            if (wasListening && restartIfRunning)
            {
                await StartRecognitionAsync().ConfigureAwait(false);
            }

            Telemetry.LogEvent("EngineReinitialized", new { Restarted = wasListening && restartIfRunning });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("EngineReinitializeFailed", ex);
        }
    }

    public async Task ReinitializeOverlayAsync()
    {
        try
        {
            await _overlayService.ApplySettingsAsync();
            Telemetry.LogEvent("OverlayReinitialized");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("OverlayReinitializeFailed", ex);
        }
    }

    public async Task ShowOverlayTextAsync(string text)
    {
        try
        {
            await _overlayService.UpdateTextAsync(text);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("OverlayShowTextFailed", ex);
        }
    }

    public async Task HideOverlayAsync()
    {
        try
        {
            await _overlayService.HideAsync();
        }
        catch (Exception ex)
        {
            Telemetry.LogError("OverlayHideFailed", ex);
        }
    }

    private void InitializeOverlay()
    {
        try
        {
            _overlayService.Initialize();
            Telemetry.LogEvent("OverlayInitialized");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("OverlayInitializationFailed", ex);
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        // Avoid UI thread sync while holding potential locks upstream
        SessionStateChanged?.Invoke(this, e);

        Telemetry.LogEvent("SessionStateChanged", new
        {
            OldState = e.OldState.ToString(),
            NewState = e.NewState.ToString()
        });
    }

    private void OnTextRecognized(object? sender, TextRecognizedEventArgs e)
    {
        TextRecognized?.Invoke(this, e);

        AsyncHelper.FireAndForget(async () =>
        {
            var settings = await _settingsProvider.GetSettingsAsync().ConfigureAwait(false);
            await _overlayService.UpdateTextAsync(e.Text);

            Telemetry.LogRecognition(e.Text, e.IsFinal, e.Confidence, settings.Privacy.MaskInLogs);
        }, nameof(OnTextRecognized), new { e.Text, e.IsFinal });
    }

    private void OnHotkeyTriggered(object? sender, HotkeyTriggeredEventArgs e)
    {
        AsyncHelper.FireAndForget(async () =>
        {
            try
            {
                switch (e.Action)
                {
                    case HotkeyAction.ToggleUI:
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

                    case HotkeyAction.ToggleMicrophone:
                        var state = _recognitionSession.CurrentState;
                        if (state == SessionState.Listening)
                        {
                            await StopRecognitionAsync().ConfigureAwait(false);
                        }
                        else if (state == SessionState.Idle || state == SessionState.Error)
                        {
                            // Allow recovery from Error state via hotkey
                            await StartRecognitionAsync().ConfigureAwait(false);
                        }
                        break;

                    case HotkeyAction.StopMicrophone:
                        await StopRecognitionAsync().ConfigureAwait(false);
                        break;
                }

                Telemetry.LogEvent("HotkeyTriggeredHandled", new { e.Name, e.HotkeyString, Action = e.Action.ToString() });
            }
            catch (Exception ex)
            {
                Telemetry.LogError("HotkeyProcessingFailed", ex, new { e.Name, Action = e.Action.ToString() });
            }
        }, nameof(OnHotkeyTriggered), new { e.Name, e.HotkeyString, Action = e.Action.ToString() });
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

        // Overlay health check
        _healthMonitor.RegisterHealthCheck("Overlay", async () =>
        {
            var settings = await _settingsProvider.GetSettingsAsync();
            if (!settings.Overlay.Enabled)
            {
                return HealthCheckResult.Healthy("Overlay disabled");
            }
            return HealthCheckResult.Healthy("Overlay active");
        });
    }

    private void OnRecoveryFailed(object? sender, ErrorRecoveryEventArgs e)
    {
        Telemetry.LogError("RecoveryFailed", e.Exception, new
        {
            e.OperationName,
            e.AttemptNumber,
            e.MaxAttempts
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

    private void OnHotkeyRegistrationFailed(object? sender, HotkeyRegistrationFailedEventArgs e)
    {
        try
        {
            var userMessage = $"ホットキー登録に失敗しました: {e.HotkeyString}\nエラーコード: {e.Win32Error}";

            // Log the same user-facing message for traceability
            Telemetry.LogWarning("HotkeyRegistrationUserMessage", userMessage, new
            {
                e.Name,
                e.HotkeyString,
                e.Win32Error
            });

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                System.Windows.MessageBox.Show(
                    userMessage,
                    "Sttify - Hotkey Registration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyRegistrationUserNotifyFailed", ex);
        }
    }
}
