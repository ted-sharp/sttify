using Sttify.Services;
using Sttify.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Controls;
using System.Diagnostics;

namespace Sttify.Views;

public partial class ControlWindow : Window
{
    private readonly ApplicationService? _applicationService;
    private readonly MainViewModel? _viewModel;
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
        };
    }

    // Constructor for dependency injection
    public ControlWindow(ApplicationService applicationService, MainViewModel viewModel) : this()
    {
        Debug.WriteLine("ControlWindow: DI constructor called");
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        
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
        if (_applicationService == null) 
        {
            Debug.WriteLine("OnMicrophoneClick: ApplicationService is null, ignoring click");
            return;
        }
        
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

    private void OnMicrophoneRightClick(object sender, MouseButtonEventArgs e)
    {
        var contextMenu = new System.Windows.Controls.ContextMenu();
        
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
        var settingsWindow = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (settingsWindow == null)
        {
            // Note: In a real implementation, we would use DI to get the SettingsWindow
            // settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
            System.Windows.MessageBox.Show("Settings window not implemented yet.", "Sttify", MessageBoxButton.OK, MessageBoxImage.Information);
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
}