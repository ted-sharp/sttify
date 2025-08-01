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
            InitializeTelemetry();
            BuildHost();
            InitializeServices();
            
            Telemetry.LogEvent("ApplicationStarted");
            
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Telemetry.LogError("ApplicationStartupFailed", ex);
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
        var telemetrySettings = new TelemetrySettings
        {
            EnableConsoleLogging = Debugger.IsAttached,
            MaskTextInLogs = false
        };
        
        Telemetry.Initialize(telemetrySettings);
    }

    private void BuildHost()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<SettingsProvider>();
                services.AddSingleton<AudioCapture>();
                services.AddSingleton<HotkeyManager>();
                services.AddSingleton<RtssBridge>(provider =>
                {
                    var settingsProvider = provider.GetRequiredService<SettingsProvider>();
                    var rtssSettings = settingsProvider.GetSettingsAsync().Result.Rtss;
                    return new RtssBridge(rtssSettings);
                });
                
                services.AddSingleton<ISttEngine>(provider =>
                {
                    var settingsProvider = provider.GetRequiredService<SettingsProvider>();
                    var engineSettings = settingsProvider.GetSettingsAsync().Result.Engine;
                    return SttEngineFactory.CreateEngine(engineSettings);
                });
                
                services.AddSingleton<IEnumerable<ITextOutputSink>>(provider =>
                {
                    var settingsProvider = provider.GetRequiredService<SettingsProvider>();
                    var outputSettings = settingsProvider.GetSettingsAsync().Result.Output;
                    
                    var sinks = new List<ITextOutputSink>
                    {
                        new TsfTipSink(),
                        new SendInputSink(new Sttify.Corelib.Output.SendInputSettings 
                        { 
                            RateLimitCps = outputSettings.SendInput.RateLimitCps,
                            CommitKey = outputSettings.SendInput.CommitKey
                        })
                    };
                    
                    return sinks;
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
        var applicationService = _host!.Services.GetRequiredService<ApplicationService>();
        applicationService.Initialize();
        
        _notifyIconHost = new NotifyIconHost(_host.Services);
        _notifyIconHost.Initialize();
    }
}