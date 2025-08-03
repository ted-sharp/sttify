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
        
        var testText = TestTextBox.Text;
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
}