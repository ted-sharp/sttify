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
            BuildHost();
            InitializeServices();
            
            Telemetry.LogEvent("ApplicationStarted");
            
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ApplicationStartupFailed", ex);
            
            // Output detailed error information to console
            Console.WriteLine("=== APPLICATION STARTUP ERROR ===");
            Console.WriteLine($"Exception Type: {ex.GetType().Name}");
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner Exception: {ex.InnerException.GetType().Name}");
                Console.WriteLine($"Inner Message: {ex.InnerException.Message}");
                Console.WriteLine($"Inner Stack Trace: {ex.InnerException.StackTrace}");
            }
            Console.WriteLine("==================================");
            
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
        if (Debugger.IsAttached)
        {
            AllocConsole();
        }
        
        var telemetrySettings = new TelemetrySettings
        {
            EnableConsoleLogging = Debugger.IsAttached,
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
                    var defaultEngineSettings = new EngineSettings();
                    return SttEngineFactory.CreateEngine(defaultEngineSettings);
                });
                
                services.AddSingleton<IEnumerable<ITextOutputSink>>(provider =>
                {
                    var sinks = new List<ITextOutputSink>
                    {
                        new TsfTipSink(),
                        new SendInputSink(new SendInputSettings())
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
            Console.WriteLine("InitializeServices: Starting service initialization...");
            
            Console.WriteLine("InitializeServices: Getting ApplicationService...");
            var applicationService = _host!.Services.GetRequiredService<ApplicationService>();
            Console.WriteLine("InitializeServices: ApplicationService obtained successfully");
            
            Console.WriteLine("InitializeServices: Calling ApplicationService.Initialize()...");
            applicationService.Initialize();
            Console.WriteLine("InitializeServices: ApplicationService initialized successfully");
            
            Console.WriteLine("InitializeServices: Creating NotifyIconHost...");
            _notifyIconHost = new NotifyIconHost(_host.Services);
            Console.WriteLine("InitializeServices: NotifyIconHost created");
            
            Console.WriteLine("InitializeServices: Initializing NotifyIconHost...");
            _notifyIconHost.Initialize();
            Console.WriteLine("InitializeServices: NotifyIconHost initialized successfully");
            
            Console.WriteLine("InitializeServices: All services initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"InitializeServices failed: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }
}