using Sttify.Services;
using Sttify.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Forms;
using Sttify.Corelib.Config;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Sttify.Views;

public partial class ControlWindow : Window
{
    private readonly ApplicationService? _applicationService;
    private readonly MainViewModel? _viewModel;
    private readonly IServiceProvider? _serviceProvider;
    private Storyboard? _currentPulseAnimation;
    private Sttify.Corelib.Session.SessionState _lastState = Sttify.Corelib.Session.SessionState.Idle;
    
    // Drag functionality fields
    private bool _isDragging;
    private System.Windows.Point _startPoint;
    private const double DragThreshold = 5.0;

    // Parameterless constructor for XAML
    public ControlWindow()
    {
        Debug.WriteLine("ControlWindow: Parameterless constructor called");
        InitializeComponent();
        Debug.WriteLine("ControlWindow: InitializeComponent completed");
        
        // Add drag functionality after window is loaded (for XAML instantiation)
        Debug.WriteLine("ControlWindow: Adding Loaded event handler in parameterless constructor");
        Loaded += (s, e) => {
            Debug.WriteLine("ControlWindow: Loaded event fired from parameterless constructor");
            SetupDragFunctionality();
            RestoreWindowPosition();
        };
        
        // Save position when window is moved or closed
        LocationChanged += (s, e) => SaveWindowPosition();
        Closing += (s, e) => SaveWindowPosition();
    }

    // Constructor for dependency injection
    public ControlWindow(ApplicationService applicationService, MainViewModel viewModel, IServiceProvider serviceProvider) : this()
    {
        Debug.WriteLine("ControlWindow: DI constructor called");
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        
        Debug.WriteLine("ControlWindow: Setting DataContext");
        DataContext = _viewModel;
        
        Debug.WriteLine("ControlWindow: Subscribing to events");
        _applicationService.SessionStateChanged += OnSessionStateChanged;
        
        Debug.WriteLine("ControlWindow: Calling UpdateUI");
        UpdateUI();
        
        Debug.WriteLine("ControlWindow: DI constructor completed (drag setup already done in parameterless constructor)");
    }

    private void SetupDragFunctionality()
    {
        Debug.WriteLine("SetupDragFunctionality: Setting up drag functionality");
        // Add drag functionality to the microphone circle
        var microphoneCircle = FindName("MicrophoneCircle") as Ellipse;
        if (microphoneCircle != null)
        {
            Debug.WriteLine("SetupDragFunctionality: MicrophoneCircle found, adding event handlers");
            microphoneCircle.MouseLeftButtonDown += OnMicrophoneMouseDown;
            microphoneCircle.MouseLeftButtonUp += OnMicrophoneMouseUp;
            microphoneCircle.MouseMove += OnMicrophoneMouseMove;
            
            // Make sure the circle can receive mouse events
            microphoneCircle.IsHitTestVisible = true;
        }
        else
        {
            Debug.WriteLine("SetupDragFunctionality: ERROR - MicrophoneCircle not found!");
        }
    }

    private void OnMicrophoneMouseDown(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("OnMicrophoneMouseDown: Mouse down detected");
        _startPoint = e.GetPosition(this);
        _isDragging = false;
        
        var ellipse = sender as Ellipse;
        ellipse?.CaptureMouse();
        Debug.WriteLine($"OnMicrophoneMouseDown: Mouse captured, start point: {_startPoint}");
    }

    private void OnMicrophoneMouseUp(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine($"OnMicrophoneMouseUp: Mouse up detected, was dragging: {_isDragging}");
        var ellipse = sender as Ellipse;
        ellipse?.ReleaseMouseCapture();
        
        if (!_isDragging)
        {
            Debug.WriteLine("OnMicrophoneMouseUp: Treating as click");
            // It was a click, not a drag
            OnMicrophoneClick(sender, e);
        }
        else
        {
            Debug.WriteLine("OnMicrophoneMouseUp: Was dragging, not clicking");
        }
        
        _isDragging = false;
    }

    private void OnMicrophoneMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var currentPoint = e.GetPosition(this);
            var distance = Math.Sqrt(Math.Pow(currentPoint.X - _startPoint.X, 2) + 
                                   Math.Pow(currentPoint.Y - _startPoint.Y, 2));
            
            if (distance > DragThreshold && !_isDragging)
            {
                Debug.WriteLine($"OnMicrophoneMouseMove: Starting drag (distance: {distance:F2})");
                _isDragging = true;
            }
            
            if (_isDragging)
            {
                Debug.WriteLine("OnMicrophoneMouseMove: Dragging window");
                try
                {
                    DragMove();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OnMicrophoneMouseMove: DragMove exception: {ex.Message}");
                }
            }
        }
    }

    private async void OnMicrophoneClick(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("OnMicrophoneClick: Click detected");
        
        // Try to use instance service first, then fall back to static service provider
        var applicationService = _applicationService;
        if (applicationService == null)
        {
            Debug.WriteLine("OnMicrophoneClick: ApplicationService is null - trying to get from static service provider");
            var serviceProvider = App.ServiceProvider;
            if (serviceProvider != null)
            {
                try
                {
                    applicationService = serviceProvider.GetRequiredService<ApplicationService>();
                    Debug.WriteLine("OnMicrophoneClick: ApplicationService obtained from static service provider");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OnMicrophoneClick: Failed to get ApplicationService: {ex.Message}");
                }
            }
        }
        
        if (applicationService == null)
        {
            System.Windows.MessageBox.Show("ApplicationService is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        try
        {
            var currentState = applicationService.GetCurrentState();
            
            if (currentState == Sttify.Corelib.Session.SessionState.Listening)
            {
                await applicationService.StopRecognitionAsync();
            }
            else if (currentState == Sttify.Corelib.Session.SessionState.Idle)
            {
                await applicationService.StartRecognitionAsync();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to toggle recognition: {ex.Message}", 
                "Sttify", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnMicrophoneRightClick(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("OnMicrophoneRightClick: Right-click detected");
        var contextMenu = new System.Windows.Controls.ContextMenu();
        
        // Add microphone control options
        var applicationService = _applicationService ?? App.ServiceProvider?.GetService<ApplicationService>();
        
        if (applicationService == null)
        {
            Debug.WriteLine("OnMicrophoneRightClick: ApplicationService is not available");
            var noServiceItem = new System.Windows.Controls.MenuItem { Header = "Service Not Available", IsEnabled = false };
            contextMenu.Items.Add(noServiceItem);
        }
        else
        {
            var currentState = applicationService.GetCurrentState();
            var micItem = new System.Windows.Controls.MenuItem();
            
            if (currentState == Sttify.Corelib.Session.SessionState.Listening)
            {
                micItem.Header = "Stop Recognition";
                micItem.Click += async (s, args) => await applicationService.StopRecognitionAsync();
            }
            else
            {
                micItem.Header = "Start Recognition";
                micItem.Click += async (s, args) => await applicationService.StartRecognitionAsync();
            }
            
            contextMenu.Items.Add(micItem);
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
        }
        
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings..." };
        settingsItem.Click += (s, args) => ShowSettingsWindow();
        contextMenu.Items.Add(settingsItem);
        
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        
        var hideItem = new System.Windows.Controls.MenuItem { Header = "Hide" };
        hideItem.Click += (s, args) => Hide();
        contextMenu.Items.Add(hideItem);
        
        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (s, args) => System.Windows.Application.Current.Shutdown();
        contextMenu.Items.Add(exitItem);
        
        contextMenu.IsOpen = true;
    }

    private void ShowSettingsWindow()
    {
        // Try to use instance service provider first, then fall back to static
        var serviceProvider = _serviceProvider ?? App.ServiceProvider;
        
        if (serviceProvider == null)
        {
            System.Windows.MessageBox.Show("Service provider not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settingsWindow = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (settingsWindow == null)
        {
            try
            {
                settingsWindow = serviceProvider.GetRequiredService<SettingsWindow>();
                settingsWindow.Show();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to create settings window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }
    }

    private void OnSessionStateChanged(object? sender, Sttify.Corelib.Session.SessionStateChangedEventArgs e)
    {
        Dispatcher.Invoke(() => UpdateUI());
    }

    private void UpdateUI()
    {
        if (_applicationService == null) 
        {
            StatusText.Text = "Not Connected";
            return;
        }
        
        var currentState = _applicationService.GetCurrentState();
        
        // Only animate if state actually changed
        if (currentState != _lastState)
        {
            AnimateStateChange(currentState);
            _lastState = currentState;
        }
        
        StatusText.Text = GetStateDisplayName(currentState);
    }

    private void AnimateStateChange(Sttify.Corelib.Session.SessionState newState)
    {
        var (fillColor, iconText) = GetStateVisuals(newState);
        
        // Stop any running pulse animation
        _currentPulseAnimation?.Stop();
        _currentPulseAnimation = null;
        
        // Create flip animation with new color and icon
        var flipAnimation = (Storyboard)FindResource("FlipInAnimation");
        var colorAnimation = flipAnimation.Children.OfType<ColorAnimation>().FirstOrDefault();
        
        if (colorAnimation != null)
        {
            colorAnimation.To = fillColor;
        }
        
        // Set up completion handler to update icon and start pulse if needed
        flipAnimation.Completed += (s, e) =>
        {
            MicrophoneIcon.Text = iconText;
            
            // Start pulse animation for listening state
            if (newState == Sttify.Corelib.Session.SessionState.Listening)
            {
                _currentPulseAnimation = (Storyboard)FindResource("PulseAnimation");
                _currentPulseAnimation.Begin();
            }
        };
        
        // Start the flip animation
        flipAnimation.Begin();
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

    private (System.Windows.Media.Color fillColor, string iconText) GetStateVisuals(Sttify.Corelib.Session.SessionState state)
    {
        return state switch
        {
            Sttify.Corelib.Session.SessionState.Idle => (Colors.Gray, "🎤"),
            Sttify.Corelib.Session.SessionState.Listening => (Colors.Green, "🎙️"),
            Sttify.Corelib.Session.SessionState.Processing => (Colors.Orange, "🔄"),
            Sttify.Corelib.Session.SessionState.Starting => (Colors.Yellow, "⏳"),
            Sttify.Corelib.Session.SessionState.Stopping => (Colors.Yellow, "⏹️"),
            Sttify.Corelib.Session.SessionState.Error => (Colors.Red, "❌"),
            _ => (Colors.Gray, "❓")
        };
    }

    private async void SaveWindowPosition()
    {
        try
        {
            var settingsProvider = new SettingsProvider();
            var settings = await settingsProvider.GetSettingsAsync();
            
            settings.Application.ControlWindow.Left = Left;
            settings.Application.ControlWindow.Top = Top;
            settings.Application.ControlWindow.DisplayConfiguration = GetDisplayConfiguration();
            
            await settingsProvider.SaveSettingsAsync(settings);
            Debug.WriteLine($"Window position saved: {Left}, {Top}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save window position: {ex.Message}");
        }
    }

    private async void RestoreWindowPosition()
    {
        try
        {
            var settingsProvider = new SettingsProvider();
            var settings = await settingsProvider.GetSettingsAsync();
            var windowPos = settings.Application.ControlWindow;
            
            // Check if position is saved
            if (double.IsNaN(windowPos.Left) || double.IsNaN(windowPos.Top))
            {
                Debug.WriteLine("No saved window position, using default");
                return;
            }
            
            // Check if display configuration changed
            var currentDisplayConfig = GetDisplayConfiguration();
            if (windowPos.DisplayConfiguration != currentDisplayConfig)
            {
                Debug.WriteLine("Display configuration changed, validating position");
                if (!IsPositionValid(windowPos.Left, windowPos.Top))
                {
                    Debug.WriteLine("Saved position is invalid, using default");
                    return;
                }
            }
            
            Left = windowPos.Left;
            Top = windowPos.Top;
            Debug.WriteLine($"Window position restored: {Left}, {Top}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restore window position: {ex.Message}");
        }
    }

    private string GetDisplayConfiguration()
    {
        var screens = Screen.AllScreens;
        var config = string.Join(";", screens.Select(s => 
            $"{s.Bounds.Width}x{s.Bounds.Height}@{s.Bounds.X},{s.Bounds.Y}"));
        return config;
    }

    private bool IsPositionValid(double left, double top)
    {
        var windowRect = new System.Drawing.Rectangle(
            (int)left, (int)top, (int)Width, (int)Height);
        
        // Check if window overlaps with any screen
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(windowRect))
            {
                return true;
            }
        }
        
        return false;
    }
}