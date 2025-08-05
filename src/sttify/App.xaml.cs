using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Hotkey;
using Sttify.Corelib.Output;
using Sttify.Corelib.Rtss;
using Sttify.Corelib.Session;
using static Sttify.Corelib.Config.SettingsProvider;
using Sttify.Services;
using Sttify.Tray;
using Sttify.ViewModels;
using Sttify.Views;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Sttify;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private NotifyIconHost? _notifyIconHost;
    private Mutex? _singleInstanceMutex;
    
    public static IServiceProvider? ServiceProvider { get; private set; }

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
            InitializeServices();
            
            Telemetry.LogEvent("ApplicationStarted");
            
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ApplicationStartupFailed", ex);
            
            // Output detailed error information to console
            System.Diagnostics.Debug.WriteLine("=== APPLICATION STARTUP ERROR ===");
            System.Diagnostics.Debug.WriteLine($"Exception Type: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
            
            if (ex.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"Inner Exception: {ex.InnerException.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"Inner Message: {ex.InnerException.Message}");
                System.Diagnostics.Debug.WriteLine($"Inner Stack Trace: {ex.InnerException.StackTrace}");
            }
            System.Diagnostics.Debug.WriteLine("==================================");
            
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
        
        var telemetrySettings = new TelemetrySettings
        {
            EnableConsoleLogging = false, // Debugger.IsAttached,
            MaskTextInLogs = false
        };
        
        Telemetry.Initialize(telemetrySettings);
    }
    
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private void BuildHost()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<SettingsProvider>();
                services.AddSingleton<AudioCapture>();
                services.AddSingleton<HotkeyManager>(provider =>
                {
                    // Create HotkeyManager with default IntPtr (0) for window handle
                    return new HotkeyManager(IntPtr.Zero);
                });
                
                // Use default settings for now - will be configured later
                services.AddSingleton<RtssBridge>(provider =>
                {
                    var defaultRtssSettings = new RtssSettings();
                    return new RtssBridge(defaultRtssSettings);
                });
                
                services.AddSingleton<ISttEngine>(provider =>
                {
                    // Use safe default settings for initialization to avoid dependency issues
                    var defaultEngineSettings = new EngineSettings
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
                    System.Diagnostics.Debug.WriteLine($"*** STT Engine Settings - Profile: {defaultEngineSettings.Profile}, ModelPath: '{defaultEngineSettings.Vosk.ModelPath}' ***");
                    return SttEngineFactory.CreateEngine(defaultEngineSettings);
                });
                
                services.AddSingleton<IEnumerable<ITextOutputSink>>(provider =>
                {
                    var sinks = new List<ITextOutputSink>
                    {
                        new SendInputSink(new SendInputSettings()),
                        new ExternalProcessSink(new ExternalProcessSettings()),
                        new StreamSink(new StreamSinkSettings())
                    };
                    
                    return sinks;
                });
                
                services.AddSingleton<RecognitionSessionSettings>(provider =>
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
                
                services.AddSingleton<RecognitionSession>();
                services.AddSingleton<ApplicationService>();
                
                services.AddTransient<MainViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<ControlWindow>();
                services.AddTransient<SettingsWindow>();
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
            
            // Console.WriteLine("InitializeServices: All services initialized successfully");
            
            // Make service provider globally accessible
            ServiceProvider = _host.Services;
        }
        catch (Exception)
        {
            // Console.WriteLine($"InitializeServices failed: {ex.Message}");
            // Console.WriteLine($"Stack trace: {ex.StackTrace}");
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
            
            if (isElevated)
            {
                System.Diagnostics.Debug.WriteLine("*** WARNING: Sttify is running with administrator privileges ***");
                System.Diagnostics.Debug.WriteLine("*** This will prevent input to non-elevated applications due to UIPI ***");
                System.Diagnostics.Debug.WriteLine("*** Consider running Sttify without administrator privileges for better compatibility ***");
                
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
                    System.Diagnostics.Debug.WriteLine("*** User chose to exit due to elevation warning ***");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine("*** User chose to continue with elevation despite warnings ***");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("*** Sttify running with normal user privileges - optimal for text input ***");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Failed to check elevation: {ex.Message} ***");
            // If we can't check elevation, assume it's safe to continue
            return true;
        }
    }
}