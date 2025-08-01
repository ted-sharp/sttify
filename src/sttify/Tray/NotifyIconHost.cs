using Microsoft.Extensions.DependencyInjection;
using Sttify.Services;
using Sttify.Views;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Sttify.Tray;

public class NotifyIconHost : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private NotifyIcon? _notifyIcon;
    private ApplicationService? _applicationService;

    public NotifyIconHost(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void Initialize()
    {
        _applicationService = _serviceProvider.GetRequiredService<ApplicationService>();
        
        CreateNotifyIcon();
        SetupContextMenu();
        
        _notifyIcon!.Visible = true;
    }

    private void CreateNotifyIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateMicrophoneIcon(),
            Text = "Sttify - Speech to Text",
            Visible = false
        };

        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
    }

    private void SetupContextMenu()
    {
        if (_notifyIcon == null) return;

        var contextMenu = new ContextMenuStrip();

        var controlWindowItem = new ToolStripMenuItem("Control Window")
        {
            Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold)
        };
        controlWindowItem.Click += OnShowControlWindow;
        contextMenu.Items.Add(controlWindowItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var startStopItem = new ToolStripMenuItem("Start Recognition");
        startStopItem.Click += OnToggleRecognition;
        contextMenu.Items.Add(startStopItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += OnShowSettings;
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExit;
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;

        if (_applicationService != null)
        {
            _applicationService.SessionStateChanged += (s, e) => UpdateContextMenu();
        }
    }

    private void UpdateContextMenu()
    {
        if (_notifyIcon?.ContextMenuStrip == null || _applicationService == null)
            return;

        var startStopItem = _notifyIcon.ContextMenuStrip.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(item => item.Text.Contains("Recognition"));

        if (startStopItem != null)
        {
            var isListening = _applicationService.GetCurrentState() == Sttify.Corelib.Session.SessionState.Listening;
            startStopItem.Text = isListening ? "Stop Recognition" : "Start Recognition";
        }

        var currentState = _applicationService.GetCurrentState();
        _notifyIcon.Icon = CreateMicrophoneIcon(currentState);
        _notifyIcon.Text = $"Sttify - {GetStateDisplayName(currentState)}";
    }

    private Icon CreateMicrophoneIcon(Sttify.Corelib.Session.SessionState state = Sttify.Corelib.Session.SessionState.Idle)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);

        var color = state switch
        {
            Sttify.Corelib.Session.SessionState.Listening => Color.Green,
            Sttify.Corelib.Session.SessionState.Processing => Color.Orange,
            Sttify.Corelib.Session.SessionState.Error => Color.Red,
            _ => Color.Gray
        };

        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 4, 2, 8, 10);
        graphics.FillRectangle(brush, 7, 12, 2, 2);
        graphics.FillRectangle(brush, 5, 14, 6, 1);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private string GetStateDisplayName(Sttify.Corelib.Session.SessionState state)
    {
        return state switch
        {
            Sttify.Corelib.Session.SessionState.Idle => "Ready",
            Sttify.Corelib.Session.SessionState.Listening => "Listening",
            Sttify.Corelib.Session.SessionState.Processing => "Processing",
            Sttify.Corelib.Session.SessionState.Starting => "Starting",
            Sttify.Corelib.Session.SessionState.Stopping => "Stopping",
            Sttify.Corelib.Session.SessionState.Error => "Error",
            _ => "Unknown"
        };
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        OnShowControlWindow(sender, e);
    }

    private void OnShowControlWindow(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var controlWindow = System.Windows.Application.Current.Windows.OfType<ControlWindow>().FirstOrDefault();
            if (controlWindow == null)
            {
                controlWindow = _serviceProvider.GetRequiredService<ControlWindow>();
                controlWindow.Show();
            }
            else
            {
                controlWindow.WindowState = WindowState.Normal;
                controlWindow.Activate();
            }
        });
    }

    private async void OnToggleRecognition(object? sender, EventArgs e)
    {
        if (_applicationService == null) return;

        try
        {
            var currentState = _applicationService.GetCurrentState();
            
            if (currentState == Sttify.Corelib.Session.SessionState.Listening)
            {
                await _applicationService.StopRecognitionAsync();
            }
            else if (currentState == Sttify.Corelib.Session.SessionState.Idle)
            {
                await _applicationService.StartRecognitionAsync();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to toggle recognition: {ex.Message}", 
                "Sttify", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnShowSettings(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var settingsWindow = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
            if (settingsWindow == null)
            {
                settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
                settingsWindow.Show();
            }
            else
            {
                settingsWindow.WindowState = WindowState.Normal;
                settingsWindow.Activate();
            }
        });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}