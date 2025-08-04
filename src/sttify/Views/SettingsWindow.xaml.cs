using Sttify.ViewModels;
using System.Windows;
using System.Windows.Input;
using Sttify.Corelib.Hotkey;
using System.Windows.Interop;

namespace Sttify.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private HotkeyManager? _hotkeyManager;
    private HwndSource? _hwndSource;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        
        // Setup global hotkeys when window is loaded
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }
    
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("*** SettingsWindow Loaded - Setting up global hotkeys ***");
        
        try
        {
            // Update privilege information
            UpdatePrivilegeInformation();
            
            // Get window handle
            var windowInteropHelper = new WindowInteropHelper(this);
            var windowHandle = windowInteropHelper.Handle;
            
            // Create HwndSource to handle window messages
            _hwndSource = HwndSource.FromHwnd(windowHandle);
            _hwndSource.AddHook(WndProc);
            
            // Create hotkey manager
            _hotkeyManager = new HotkeyManager(windowHandle);
            _hotkeyManager.OnHotkeyPressed += OnGlobalHotkeyPressed;
            
            // Register global hotkeys (using Win+Shift+F1/F2/F3 to avoid conflicts)
            bool success1 = _hotkeyManager.RegisterHotkey("Win+Shift+F1", "TestCurrentOutput");
            bool success2 = _hotkeyManager.RegisterHotkey("Win+Shift+F2", "TestTsfTip");
            bool success3 = _hotkeyManager.RegisterHotkey("Win+Shift+F3", "TestSendInput");
            
            System.Diagnostics.Debug.WriteLine($"*** Global hotkey registration results: F1={success1}, F2={success2}, F3={success3} ***");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Failed to setup global hotkeys: {ex.Message} ***");
        }
    }
    
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("*** SettingsWindow Closed - Cleaning up global hotkeys ***");
        
        try
        {
            _hotkeyManager?.Dispose();
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Failed to cleanup global hotkeys: {ex.Message} ***");
        }
    }
    
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkeyManager?.ProcessWindowMessage(hwnd, msg, wParam, lParam) == true)
        {
            handled = true;
        }
        return IntPtr.Zero;
    }
    
    private async void OnGlobalHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"*** Global hotkey pressed: {e.Name} ({e.HotkeyString}) ***");
        
        var testText = SystemTestTextBox.Text;
        if (string.IsNullOrEmpty(testText))
        {
            testText = "Hello, this is a test from Sttify.";
        }
        
        try
        {
            switch (e.Name)
            {
                case "TestCurrentOutput":
                    System.Diagnostics.Debug.WriteLine("*** Executing TestCurrentOutput ***");
                    await _viewModel.TestOutputCommand.ExecuteAsync(testText);
                    break;
                case "TestTsfTip":
                    System.Diagnostics.Debug.WriteLine("*** Executing TestTsfTip ***");
                    await _viewModel.TestTsfCommand.ExecuteAsync(testText);
                    break;
                case "TestSendInput":
                    System.Diagnostics.Debug.WriteLine("*** Executing TestSendInput ***");
                    await _viewModel.TestSendInputCommand.ExecuteAsync(testText);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Error executing test command: {ex.Message} ***");
        }
    }

    private async void OnOK(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveSettingsCommand.ExecuteAsync(null);
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to save settings: {ex.Message}", 
                "Sttify", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdatePrivilegeInformation()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            bool isElevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            
            if (isElevated)
            {
                PrivilegeStatusText.Text = "⚠️ Running with Administrator Privileges";
                PrivilegeStatusText.Foreground = System.Windows.Media.Brushes.Red;
                
                PrivilegeDescriptionText.Text = 
                    "PROBLEM: Sttify is running with administrator privileges, which prevents text input to most applications due to Windows security (UIPI - User Interface Privilege Isolation).\n\n" +
                    "SOLUTION: Restart Sttify without administrator privileges for optimal text input functionality.\n\n" +
                    "IMPACT: Text input will be blocked to applications running with normal user privileges (most apps including Notepad, browsers, etc.)";
                
                RestartNormalButton.Visibility = Visibility.Visible;
            }
            else
            {
                PrivilegeStatusText.Text = "✅ Running with Normal User Privileges";
                PrivilegeStatusText.Foreground = System.Windows.Media.Brushes.Green;
                
                PrivilegeDescriptionText.Text = 
                    "OPTIMAL: Sttify is running with normal user privileges, which provides the best compatibility for text input to all applications.\n\n" +
                    "TEXT INPUT: Should work correctly with most applications including text editors, browsers, and other programs.\n\n" +
                    "SECURITY: This is the recommended and most secure way to run Sttify.";
                
                RestartNormalButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            PrivilegeStatusText.Text = "❓ Unable to determine privilege level";
            PrivilegeStatusText.Foreground = System.Windows.Media.Brushes.Orange;
            PrivilegeDescriptionText.Text = $"Failed to check privileges: {ex.Message}";
            RestartNormalButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRestartNormal(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = System.Windows.MessageBox.Show(
                "This will close Sttify and restart it with normal user privileges.\n\n" +
                "The application will restart automatically without administrator privileges, " +
                "which will enable text input to work with all applications.\n\n" +
                "Continue?",
                "Restart Without Administrator",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
                
            if (result == MessageBoxResult.Yes)
            {
                // Get current executable path
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                var exePath = currentProcess.MainModule?.FileName ?? "";
                
                if (!string.IsNullOrEmpty(exePath))
                {
                    // Start new instance without elevation
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        Verb = "", // No "runas" verb = normal privileges
                        WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? ""
                    };
                    
                    System.Diagnostics.Process.Start(startInfo);
                    
                    // Close current instance
                    System.Windows.Application.Current.Shutdown();
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to restart application: {ex.Message}",
                "Restart Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnOpenNotepad(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start("notepad.exe");
            System.Diagnostics.Debug.WriteLine("*** Opened Notepad for testing ***");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Failed to open Notepad: {ex.Message} ***");
            System.Windows.MessageBox.Show(
                $"Failed to open Notepad: {ex.Message}",
                "Open Notepad Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}