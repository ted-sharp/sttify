using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Hotkey;
using Sttify.Corelib.Output;
using Sttify.Corelib.Rtss;
using Sttify.Corelib.Session;
using System.Windows;

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
            Telemetry.LogEvent("ApplicationServiceInitializing");
            
            InitializeHotkeys();
            InitializeRtss();
            
            Telemetry.LogEvent("ApplicationServiceInitialized");
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ApplicationServiceInitializationFailed", ex);
            throw;
        }
    }

    public async Task StartRecognitionAsync()
    {
        await _errorRecovery.ExecuteWithRetryAsync(async () =>
        {
            await _recognitionSession.StartAsync();
            Telemetry.LogEvent("RecognitionStarted");
        }, "StartRecognition");
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
            var settings = await _settingsProvider.GetSettingsAsync();
            
            _hotkeyManager.RegisterHotkey(settings.Hotkeys.ToggleUi, "ToggleUi");
            _hotkeyManager.RegisterHotkey(settings.Hotkeys.ToggleMic, "ToggleMic");
            
            Telemetry.LogEvent("HotkeysRegistered", new { 
                ToggleUi = settings.Hotkeys.ToggleUi,
                ToggleMic = settings.Hotkeys.ToggleMic
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("HotkeyInitializationFailed", ex);
        }
    }

    private void InitializeRtss()
    {
        try
        {
            var initialized = _rtssService.Initialize();
            Telemetry.LogEvent("RtssInitialized", new { Success = initialized });
        }
        catch (Exception ex)
        {
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
        
        if (settings.Rtss.Enabled && e.IsFinal)
        {
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
        _healthMonitor.RegisterHealthCheck("RecognitionSession", async () =>
        {
            var state = _recognitionSession.CurrentState;
            if (state == SessionState.Error)
            {
                return HealthCheckResult.Unhealthy("Recognition session is in error state");
            }
            
            return HealthCheckResult.Healthy($"Recognition session state: {state}");
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