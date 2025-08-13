using Microsoft.Extensions.DependencyInjection;
using Sttify.Services;
using Sttify.Views;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Tray;

public class NotifyIconHost : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private NotifyIcon? _notifyIcon;
    private ApplicationService? _applicationService;
    private ToolStripMenuItem? _menuItemControlWindow;
    private ToolStripMenuItem? _menuItemStartStop;

    public NotifyIconHost(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void Initialize()
    {
        try
        {
            Console.WriteLine("NotifyIconHost: Starting initialization...");

            _applicationService = _serviceProvider.GetRequiredService<ApplicationService>();
            Console.WriteLine("NotifyIconHost: ApplicationService obtained");

            CreateNotifyIcon();
            Console.WriteLine("NotifyIconHost: NotifyIcon created");

            SetupContextMenu();
            Console.WriteLine("NotifyIconHost: Context menu setup complete");

            // Initialize menu text/icon state based on current session
            UpdateContextMenu();

            _notifyIcon!.Visible = true;
            Console.WriteLine($"NotifyIconHost: Icon visibility set to true. Actually visible: {_notifyIcon.Visible}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NotifyIconHost initialization failed: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private void CreateNotifyIcon()
    {
        try
        {
            Console.WriteLine("Creating microphone icon...");
            var icon = CreateMicrophoneIcon();
            Console.WriteLine($"Icon created successfully: {icon != null}");

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "Sttify - Speech to Text",
                Visible = false
            };

            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
            Console.WriteLine("NotifyIcon object created with properties set");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create NotifyIcon: {ex.Message}");
            throw;
        }
    }

    private void SetupContextMenu()
    {
        if (_notifyIcon == null) return;

        var contextMenu = new ContextMenuStrip();

        _menuItemControlWindow = new ToolStripMenuItem("Show Control Window")
        {
            Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold)
        };
        _menuItemControlWindow.Click += OnToggleControlWindow;
        contextMenu.Items.Add(_menuItemControlWindow);

        contextMenu.Items.Add(new ToolStripSeparator());

        _menuItemStartStop = new ToolStripMenuItem("Start Recognition");
        _menuItemStartStop.Click += OnToggleRecognition;
        contextMenu.Items.Add(_menuItemStartStop);

        contextMenu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += OnShowSettings;
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExit;
        contextMenu.Items.Add(exitItem);

        contextMenu.Opening += (s, e) => UpdateContextMenu();
        _notifyIcon.ContextMenuStrip = contextMenu;

        if (_applicationService != null)
        {
            _applicationService.SessionStateChanged += (s, e) => UpdateContextMenu();
        }
    }

    private void UpdateContextMenu()
    {
        // Ensure we are on the WPF UI thread before touching Application.Current.Windows or UI-bound elements
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke((Action)UpdateContextMenu);
            return;
        }

        if (_applicationService == null || _notifyIcon == null || _notifyIcon.ContextMenuStrip == null)
            return;

        if (_menuItemStartStop != null)
        {
            var isListening = _applicationService.GetCurrentState() == Sttify.Corelib.Session.SessionState.Listening;
            _menuItemStartStop.Text = isListening ? "Stop Recognition" : "Start Recognition";
        }

        if (_menuItemControlWindow != null)
        {
            var cw = System.Windows.Application.Current?.Windows.OfType<ControlWindow>().FirstOrDefault();
            var isVisible = cw != null && cw.Visibility == Visibility.Visible;
            _menuItemControlWindow.Text = isVisible ? "Hide Control Window" : "Show Control Window";
        }

        var currentState = _applicationService.GetCurrentState();
        _notifyIcon.Icon = CreateMicrophoneIcon(currentState);
        var prefix = App.IsElevated ? "[Admin] " : string.Empty;
        _notifyIcon.Text = $"{prefix}Sttify - {GetStateDisplayName(currentState)}";
    }

    private Icon CreateMicrophoneIcon(Sttify.Corelib.Session.SessionState state = Sttify.Corelib.Session.SessionState.Idle)
    {
        try
        {
            using var bitmap = new Bitmap(16, 16);
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

            var hIcon = bitmap.GetHicon();
            var icon = Icon.FromHandle(hIcon);

            // Create a copy to avoid handle ownership issues
            var iconCopy = new Icon(icon, icon.Size);

            // Clean up the original handle
            icon.Dispose();
            Win32.DestroyIcon(hIcon);

            return iconCopy;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create microphone icon: {ex.Message}");
            // Return a simple default icon if creation fails
            return SystemIcons.Application;
        }
    }

    // Win32 API for proper icon cleanup
    private static class Win32
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
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
        ToggleControlWindow();
    }

    private void OnToggleControlWindow(object? sender, EventArgs e)
    {
        ToggleControlWindow();
    }

    private void ToggleControlWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var controlWindow = System.Windows.Application.Current.Windows.OfType<ControlWindow>().FirstOrDefault();
            if (controlWindow == null)
            {
                controlWindow = _serviceProvider.GetRequiredService<ControlWindow>();
                controlWindow.Show();
                controlWindow.Activate();
            }
            else
            {
                if (controlWindow.Visibility == Visibility.Visible)
                {
                    controlWindow.Hide();
                }
                else
                {
                    controlWindow.WindowState = WindowState.Normal;
                    controlWindow.Show();
                    controlWindow.Activate();
                }
            }

            UpdateContextMenu();
        });
    }

    private void OnShowControlWindow(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Console.WriteLine("NotifyIconHost: OnShowControlWindow called");
            var controlWindow = System.Windows.Application.Current.Windows.OfType<ControlWindow>().FirstOrDefault();
            if (controlWindow == null)
            {
                Console.WriteLine("NotifyIconHost: Creating new ControlWindow");
                controlWindow = _serviceProvider.GetRequiredService<ControlWindow>();
                Console.WriteLine("NotifyIconHost: ControlWindow created, calling Show()");
                controlWindow.Show();
                Console.WriteLine("NotifyIconHost: ControlWindow.Show() completed");
            }
            else
            {
                Console.WriteLine("NotifyIconHost: Using existing ControlWindow");
                controlWindow.WindowState = WindowState.Normal;
                controlWindow.Activate();
            }
        });
    }

    private void OnToggleRecognition(object? sender, EventArgs e)
    {
        if (_applicationService == null) return;

        AsyncHelper.FireAndForget(async () =>
        {
            try
            {
                var currentState = _applicationService.GetCurrentState();
                if (currentState == Sttify.Corelib.Session.SessionState.Listening)
                {
                    await _applicationService.StopRecognitionAsync().ConfigureAwait(false);
                }
                else if (currentState == Sttify.Corelib.Session.SessionState.Idle)
                {
                    await _applicationService.StartRecognitionAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    System.Windows.MessageBox.Show($"Failed to toggle recognition: {ex.Message}",
                        "Sttify", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }, nameof(OnToggleRecognition));
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
