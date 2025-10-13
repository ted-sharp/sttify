using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Hotkey;
using Sttify.Corelib.Output;
using Sttify.Corelib.Services;
using Sttify.Corelib.Session;
using Sttify.Services;
using Sttify.Tray;
using Sttify.ViewModels;
using Sttify.Views;

namespace Sttify;

public partial class App
{
    private IHost? _host;
    private NotifyIconHost? _notifyIconHost;
    private Mutex? _singleInstanceMutex;

    public static IServiceProvider? ServiceProvider { get; private set; }
    public static bool IsElevated { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!EnsureSingleInstance())
        {
            Shutdown();
            return;
        }

        try
        {
            // Required for Windows Forms components (NotifyIcon) in WPF
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            InitializeTelemetry();

            // Check for elevation and warn user about UIPI issues
            if (!CheckElevationAndWarnUser())
            {
                Shutdown();
                return;
            }

            BuildHost();
            Console.WriteLine("*** Starting InitializeServices ***");
            Debug.WriteLine("*** Starting InitializeServices ***");
            InitializeServices();
            Console.WriteLine("*** InitializeServices completed ***");
            Debug.WriteLine("*** InitializeServices completed ***");

            Telemetry.LogEvent("ApplicationStarted");

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ApplicationStartupFailed", ex);

            // Output detailed error information to console
            Debug.WriteLine("=== APPLICATION STARTUP ERROR ===");
            Debug.WriteLine($"Exception Type: {ex.GetType().Name}");
            Debug.WriteLine($"Message: {ex.Message}");
            Debug.WriteLine($"Stack Trace: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                Debug.WriteLine($"Inner Exception: {ex.InnerException.GetType().Name}");
                Debug.WriteLine($"Inner Message: {ex.InnerException.Message}");
                Debug.WriteLine($"Inner Stack Trace: {ex.InnerException.StackTrace}");
            }
            Debug.WriteLine("==================================");

            System.Windows.MessageBox.Show($"Failed to start application: {ex.Message}", "Sttify", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Telemetry.LogEvent("ApplicationShutdown");

        _notifyIconHost?.Dispose();
        _host?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Telemetry.Shutdown();

        base.OnExit(e);
    }

    private bool EnsureSingleInstance()
    {
        const string mutexName = "Global\\Sttify_Single_Instance_Mutex";

        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Sttify is already running.", "Sttify", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private void InitializeTelemetry()
    {
        // Allocate console for debugging if attached
        // if (Debugger.IsAttached)
        // {
        //     AllocConsole();
        // }

        bool maskLogs = false;
        try
        {
            // Load privacy setting early from user config WITHOUT instantiating SettingsProvider (avoids extra FileSystemWatcher)
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var sttifyDir = Path.Combine(appDataPath, "sttify");
            var configPath = Path.Combine(sttifyDir, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("privacy", out var privacy) &&
                    privacy.ValueKind == JsonValueKind.Object &&
                    privacy.TryGetProperty("maskInLogs", out var maskProp) &&
                    (maskProp.ValueKind == JsonValueKind.True || maskProp.ValueKind == JsonValueKind.False))
                {
                    maskLogs = maskProp.GetBoolean();
                }
            }
        }
        catch
        {
            // Ignore and keep default
        }

        var telemetrySettings = new TelemetrySettings
        {
            EnableConsoleLogging = false, // Debugger.IsAttached,
            MaskTextInLogs = maskLogs
        };

        Telemetry.Initialize(telemetrySettings);
    }

    private void BuildHost()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<SettingsProvider>();
                services.AddSingleton<AudioCapture>();
                services.AddSingleton<HotkeyManager>(_ =>
                {
                    // Create HotkeyManager with default IntPtr (0) for window handle
                    return new HotkeyManager(IntPtr.Zero);
                });
                services.AddSingleton<HotkeyService>();

                // Overlay service for transparent window display
                services.AddSingleton<OverlayService>();

                services.AddSingleton<ISttEngine>(provider =>
                {
                    // Create a placeholder engine that will be reconfigured when ApplicationService initializes
                    // This avoids DI container deadlocks while still allowing proper configuration
                    var settingsProvider = provider.GetService<SettingsProvider>();
                    EngineSettings engineSettings;

                    if (settingsProvider != null)
                    {
                        try
                        {
                            // Try to get settings synchronously first
                            var settings = settingsProvider.GetSettingsSync();
                            engineSettings = settings.Engine;
                            Debug.WriteLine($"*** STT Engine Settings LOADED - Profile: {engineSettings.Profile}, ModelPath: '{engineSettings.Vosk.ModelPath}' ***");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"*** Failed to load settings, using defaults: {ex.Message} ***");
                            // Fallback to safe defaults if settings loading fails
                            engineSettings = CreateDefaultEngineSettings();
                        }
                    }
                    else
                    {
                        Debug.WriteLine("*** SettingsProvider not available, using defaults ***");
                        engineSettings = CreateDefaultEngineSettings();
                    }

                    return SttEngineFactory.CreateEngine(engineSettings);
                });

                services.AddSingleton<IOutputSinkProvider, OutputSinkProvider>();

                services.AddSingleton<RecognitionSessionSettings>(_ =>
                {
                    return new RecognitionSessionSettings
                    {
                        FinalizeTimeoutMs = TimeSpan.FromMilliseconds(1500),
                        Delimiter = "。",
                        EndpointSilenceMs = 800,
                        SampleRate = 16000,
                        Channels = 1,
                        BufferSizeMs = 100,
                        WakeWords = ["スティファイ", "sttify"],
                        VoiceActivityThreshold = 0.01,
                        MinUtteranceLengthMs = 500
                    };
                });

                services.AddSingleton<RecognitionSession>(provider =>
                {
                    return new RecognitionSession(
                        provider.GetRequiredService<AudioCapture>(),
                        provider.GetRequiredService<SettingsProvider>(),
                        provider.GetRequiredService<IOutputSinkProvider>(),
                        provider.GetRequiredService<RecognitionSessionSettings>()
                    );
                });
                services.AddSingleton<ApplicationService>();

                services.AddTransient<MainViewModel>();
                services.AddTransient<SettingsViewModel>(provider =>
                {
                    return new SettingsViewModel(
                        provider.GetRequiredService<SettingsProvider>(),
                        provider.GetService<IOutputSinkProvider>(),
                        provider.GetService<ApplicationService>()
                    );
                });
                services.AddTransient<ControlWindow>();
                services.AddTransient<SettingsWindow>(provider =>
                {
                    return new SettingsWindow(
                        provider.GetRequiredService<SettingsViewModel>(),
                        provider.GetRequiredService<ApplicationService>()
                    );
                });
            })
            .Build();
    }

    private void InitializeServices()
    {
        try
        {
            // Console.WriteLine("InitializeServices: Starting service initialization...");

            // Console.WriteLine("InitializeServices: Getting ApplicationService...");
            var applicationService = _host!.Services.GetRequiredService<ApplicationService>();
            // Console.WriteLine("InitializeServices: ApplicationService obtained successfully");

            // Console.WriteLine("InitializeServices: Calling ApplicationService.Initialize()...");
            applicationService.Initialize();
            // Console.WriteLine("InitializeServices: ApplicationService initialized successfully");

            // Console.WriteLine("InitializeServices: Creating NotifyIconHost...");
            _notifyIconHost = new NotifyIconHost(_host.Services);
            // Console.WriteLine("InitializeServices: NotifyIconHost created");

            // Console.WriteLine("InitializeServices: Initializing NotifyIconHost...");
            _notifyIconHost.Initialize();
            // Console.WriteLine("InitializeServices: NotifyIconHost initialized successfully");

            // Make service provider globally accessible before showing any UI
            ServiceProvider = _host.Services;

            // Show control window via DI to ensure it uses injected services, not StartupUri
            try
            {
                var controlWindow = _host.Services.GetRequiredService<ControlWindow>();
                controlWindow.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show ControlWindow via DI: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"InitializeServices failed: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Debug.WriteLine($"InitializeServices failed: {ex.Message}");
            Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private bool CheckElevationAndWarnUser()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            bool isElevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            IsElevated = isElevated;

            if (isElevated)
            {
                Debug.WriteLine("*** WARNING: Sttify is running with administrator privileges ***");
                Debug.WriteLine("*** This will prevent input to non-elevated applications due to UIPI ***");
                Debug.WriteLine("*** Consider running Sttify without administrator privileges for better compatibility ***");

                // Show warning to user (force to foreground)
                var result = System.Windows.MessageBox.Show(
                    "⚠️ Administrator Privileges Detected\n\n" +
                    "Sttify is running with administrator privileges, which prevents text input to most applications due to Windows security (UIPI).\n\n" +
                    "🔧 SOLUTION:\n" +
                    "• Close Sttify\n" +
                    "• Run it again WITHOUT \"Run as administrator\"\n" +
                    "• Most features work better with normal privileges\n\n" +
                    "⚡ Quick Fix:\n" +
                    "Right-click Sttify → Properties → Compatibility → Uncheck \"Run as administrator\"\n\n" +
                    "Continue with administrator privileges anyway?\n" +
                    "(Text input will be blocked to most applications)",
                    "Privilege Warning - Input Will Be Blocked",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (result == MessageBoxResult.No)
                {
                    Debug.WriteLine("*** User chose to exit due to elevation warning ***");
                    return false;
                }

                Debug.WriteLine("*** User chose to continue with elevation despite warnings ***");
            }
            else
            {
                Debug.WriteLine("*** Sttify running with normal user privileges - optimal for text input ***");
                IsElevated = false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"*** Failed to check elevation: {ex.Message} ***");
            // If we can't check elevation, assume it's safe to continue
            return true;
        }
    }

    private static EngineSettings CreateDefaultEngineSettings()
    {
        return new EngineSettings
        {
            Profile = "vosk",
            Vosk = new VoskEngineSettings
            {
                ModelPath = "",
                Language = "ja",
                Punctuation = true,
                EndpointSilenceMs = 800,
                TokensPerPartial = 5
            }
        };
    }
}
