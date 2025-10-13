using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Localization;
using Sttify.Services;
using Sttify.ViewModels;

namespace Sttify.Views;

public partial class ControlWindow
{
    private const double DragThreshold = 5.0;
    private readonly IServiceProvider? _serviceProvider;
    private readonly MainViewModel? _viewModel;
    private ApplicationService? _applicationService;
    private System.Threading.Timer? _audioLevelTimer;
    private Storyboard? _currentAudioLevelAnimation;
    private Storyboard? _currentProcessingAnimation;
    private Storyboard? _currentPulseAnimation;

    // Drag functionality fields
    private bool _isDragging;
    private bool _isEventRegistered;
    private bool _isHovering;
    private Corelib.Session.SessionState _lastState = Corelib.Session.SessionState.Idle;
    private System.Windows.Point _startPoint;

    // Parameterless constructor for XAML
    public ControlWindow()
    {
        Debug.WriteLine("ControlWindow: Parameterless constructor called");
        InitializeComponent();
        Debug.WriteLine("ControlWindow: InitializeComponent completed");

        // Add drag functionality after window is loaded (for XAML instantiation)
        Debug.WriteLine("ControlWindow: Adding Loaded event handler in parameterless constructor");
        Loaded += (_, _) =>
        {
            Debug.WriteLine("ControlWindow: Loaded event fired from parameterless constructor");
            SetupDragFunctionality();
            RestoreWindowPosition();

            // Try to get ApplicationService and register events if not already done
            TryRegisterApplicationService();
        };

        // Save position when window is moved or closed
        LocationChanged += (_, _) => SaveWindowPosition();
        Closing += (_, _) => SaveWindowPosition();
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
        _isEventRegistered = true;

        Debug.WriteLine("ControlWindow: Calling UpdateUI");
        UpdateUI();

        Debug.WriteLine("ControlWindow: DI constructor completed (drag setup already done in parameterless constructor)");

        // Elevation badge initial state
        UpdateElevationBadge();
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

    private void OnMicrophoneMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging && !_isHovering)
        {
            _isHovering = true;
            try
            {
                var hoverInAnimation = (Storyboard)FindResource("HoverInAnimation");
                hoverInAnimation.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start hover in animation: {ex.Message}");
            }
        }
    }

    private void OnMicrophoneMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isHovering)
        {
            _isHovering = false;
            try
            {
                var hoverOutAnimation = (Storyboard)FindResource("HoverOutAnimation");
                hoverOutAnimation.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start hover out animation: {ex.Message}");
            }
        }
    }

    private void OnMicrophoneClick(object _, MouseButtonEventArgs __)
    {
        Debug.WriteLine("OnMicrophoneClick: Click detected");

        // Play click animation
        try
        {
            var clickAnimation = (Storyboard)FindResource("ClickAnimation");
            clickAnimation.Begin();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start click animation: {ex.Message}");
        }

        // Ensure ApplicationService is available and events are registered
        TryRegisterApplicationService();

        var applicationService = _applicationService ?? App.ServiceProvider?.GetService<ApplicationService>();
        if (applicationService == null)
        {
            System.Windows.MessageBox.Show("ApplicationService is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AsyncHelper.FireAndForget(async () =>
        {
            try
            {
                var currentState = applicationService.GetCurrentState();
                Debug.WriteLine($"OnMicrophoneClick: Current state is {currentState}");

                if (currentState == Corelib.Session.SessionState.Listening)
                {
                    Debug.WriteLine("OnMicrophoneClick: Calling StopRecognitionAsync");
                    await applicationService.StopRecognitionAsync().ConfigureAwait(false);
                }
                else if (currentState == Corelib.Session.SessionState.Idle)
                {
                    Debug.WriteLine("OnMicrophoneClick: Calling StartRecognitionAsync");
                    await applicationService.StartRecognitionAsync().ConfigureAwait(false);
                    Debug.WriteLine("OnMicrophoneClick: StartRecognitionAsync completed");
                }
                else
                {
                    Debug.WriteLine($"OnMicrophoneClick: State {currentState} - no action taken");
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Failed to toggle recognition: {ex.Message}",
                    "Sttify", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }, nameof(OnMicrophoneClick));
    }

    private void OnMicrophoneRightClick(object sender, MouseButtonEventArgs e)
    {
        Debug.WriteLine("OnMicrophoneRightClick: Right-click detected");
        var contextMenu = new ContextMenu();

        // Add microphone control options
        var applicationService = _applicationService;

        if (applicationService == null)
        {
            Debug.WriteLine("OnMicrophoneRightClick: ApplicationService is not available");
            var noServiceItem = new MenuItem { Header = LocalizationManager.GetString("menu.service_unavailable"), IsEnabled = false };
            contextMenu.Items.Add(noServiceItem);
        }
        else
        {
            var currentState = applicationService.GetCurrentState();
            var micItem = new MenuItem();

            if (currentState == Corelib.Session.SessionState.Listening)
            {
                micItem.Header = LocalizationManager.GetString("menu.stop_recognition");
                micItem.Click += (_, _) => AsyncHelper.FireAndForget(() => applicationService.StopRecognitionAsync(), "MenuStopRecognition");
            }
            else
            {
                micItem.Header = LocalizationManager.GetString("menu.start_recognition");
                micItem.Click += (_, _) => AsyncHelper.FireAndForget(() => applicationService.StartRecognitionAsync(), "MenuStartRecognition");
            }

            contextMenu.Items.Add(micItem);
            contextMenu.Items.Add(new Separator());
        }

        var settingsItem = new MenuItem { Header = LocalizationManager.GetString("menu.settings") };
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new Separator());

        var hideItem = new MenuItem { Header = LocalizationManager.GetString("menu.hide") };
        hideItem.Click += (_, _) => Hide();
        contextMenu.Items.Add(hideItem);

        var exitItem = new MenuItem { Header = LocalizationManager.GetString("menu.exit") };
        exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();
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

    private void OnSessionStateChanged(object? sender, Corelib.Session.SessionStateChangedEventArgs e)
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

        // Update elevation badge as part of UI refresh
        UpdateElevationBadge();
    }

    private void UpdateElevationBadge()
    {
        try
        {
            var isElevated = App.IsElevated;
            var badge = FindName("ElevationBadge") as Border;
            if (badge != null)
            {
                badge.Visibility = isElevated ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"*** Failed to update elevation badge: {ex.Message} ***");
        }
    }

    private void AnimateStateChange(Corelib.Session.SessionState newState)
    {
        var (fillColor, iconText) = GetStateVisuals(newState);

        // Stop any running animations immediately
        try
        {
            if (_currentPulseAnimation != null)
            {
                _currentPulseAnimation.Stop();
                _currentPulseAnimation.Remove();
                _currentPulseAnimation = null;
            }

            if (_currentAudioLevelAnimation != null)
            {
                _currentAudioLevelAnimation.Stop();
                _currentAudioLevelAnimation.Remove();
                _currentAudioLevelAnimation = null;
            }

            if (_currentProcessingAnimation != null)
            {
                _currentProcessingAnimation.Stop();
                _currentProcessingAnimation.Remove();
                _currentProcessingAnimation = null;
            }

            // Force stop all storyboards on the elements
            var circleTransform = FindName("CircleScaleTransform") as ScaleTransform;
            if (circleTransform != null)
            {
                circleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                circleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            var iconRotateTransform = FindName("IconRotateTransform") as RotateTransform;
            if (iconRotateTransform != null)
            {
                iconRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            }

            var audioRingTransform = FindName("AudioRingScaleTransform") as ScaleTransform;
            if (audioRingTransform != null)
            {
                audioRingTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                audioRingTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            Debug.WriteLine("AnimateStateChange: All animations forcefully stopped");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AnimateStateChange: Error stopping animations: {ex.Message}");
        }

        // Stop audio level monitoring
        StopAudioLevelMonitoring();

        // Reset all transforms to default values
        ResetTransforms();

        // Hide audio ring by default
        var audioRing = FindName("AudioRing") as Ellipse;
        if (audioRing != null)
        {
            audioRing.Opacity = 0;
        }

        try
        {
            // Create flip animation with new color and icon
            var flipAnimation = (Storyboard)FindResource("FlipInAnimation");
            var colorAnimation = flipAnimation.Children.OfType<ColorAnimation>().FirstOrDefault();

            if (colorAnimation != null)
            {
                colorAnimation.To = fillColor;
            }

            // Set up completion handler to update icon and start state-specific animations
            flipAnimation.Completed += (_, _) =>
            {
                MicrophoneIcon.Text = iconText;

                // Start state-specific animations
                switch (newState)
                {
                    case Corelib.Session.SessionState.Listening:
                        try
                        {
                            _currentPulseAnimation = (Storyboard)FindResource("PulseAnimation");
                            _currentPulseAnimation.Begin();

                            // Show and animate audio ring
                            if (audioRing != null)
                            {
                                audioRing.Opacity = 0.8;
                                _currentAudioLevelAnimation = (Storyboard)FindResource("AudioLevelAnimation");
                                _currentAudioLevelAnimation.Begin();
                            }

                            StartAudioLevelMonitoring();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to start listening animations: {ex.Message}");
                        }
                        break;

                    case Corelib.Session.SessionState.Processing:
                        try
                        {
                            _currentProcessingAnimation = (Storyboard)FindResource("ProcessingAnimation");
                            _currentProcessingAnimation.Begin();
                            StopAudioLevelMonitoring();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to start processing animation: {ex.Message}");
                        }
                        break;

                    default:
                        StopAudioLevelMonitoring();
                        break;
                }
            };

            // Start the flip animation
            flipAnimation.Begin();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to animate state change: {ex.Message}");
            // Fallback: directly update without animation
            MicrophoneIcon.Text = iconText;
            var microphoneCircle = FindName("MicrophoneCircle") as Ellipse;
            if (microphoneCircle != null)
            {
                microphoneCircle.Fill = new SolidColorBrush(fillColor);
            }
        }
    }

    private string GetStateDisplayName(Corelib.Session.SessionState state) =>
        state switch
        {
            Corelib.Session.SessionState.Idle => "Ready",
            Corelib.Session.SessionState.Listening => "Listening",
            Corelib.Session.SessionState.Processing => "Processing",
            Corelib.Session.SessionState.Starting => "Starting",
            Corelib.Session.SessionState.Stopping => "Stopping",
            Corelib.Session.SessionState.Error => "Error",
            _ => "Unknown"
        };

    private (System.Windows.Media.Color fillColor, string iconText) GetStateVisuals(Corelib.Session.SessionState state) =>
        state switch
        {
            Corelib.Session.SessionState.Idle => (Colors.Gray, "🎤"),
            Corelib.Session.SessionState.Listening => (Colors.Green, "🎙️"),
            Corelib.Session.SessionState.Processing => (Colors.Orange, "🔄"),
            Corelib.Session.SessionState.Starting => (Colors.Yellow, "⏳"),
            Corelib.Session.SessionState.Stopping => (Colors.Yellow, "⏹️"),
            Corelib.Session.SessionState.Error => (Colors.Red, "❌"),
            _ => (Colors.Gray, "❓")
        };

    private async void SaveWindowPosition()
    {
        try
        {
            var settingsProvider = new SettingsProvider();
            var settings = await settingsProvider.GetSettingsAsync();

            // Only save position if remember window position is enabled
            if (settings.Application.RememberWindowPosition)
            {
                settings.Application.ControlWindow.Left = Left;
                settings.Application.ControlWindow.Top = Top;
                settings.Application.ControlWindow.DisplayConfiguration = GetDisplayConfiguration();

                await settingsProvider.SaveSettingsAsync(settings);
                Debug.WriteLine($"Window position saved: {Left}, {Top}");
            }
            else
            {
                Debug.WriteLine("Window position saving is disabled");
            }
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

            // Check if position should be remembered
            if (!settings.Application.RememberWindowPosition)
            {
                Debug.WriteLine("Remember window position is disabled, using default");
                return;
            }

            // Check if position is saved
            if (double.IsNaN(windowPos.Left) || double.IsNaN(windowPos.Top))
            {
                Debug.WriteLine("No saved window position, using default");
                return;
            }

            // If always on primary monitor is enabled, place on primary monitor
            if (settings.Application.AlwaysOnPrimaryMonitor)
            {
                var primaryScreen = Screen.PrimaryScreen;
                if (primaryScreen != null)
                {
                    Left = primaryScreen.WorkingArea.Left + 100;
                    Top = primaryScreen.WorkingArea.Top + 100;
                    Debug.WriteLine("Placed on primary monitor due to AlwaysOnPrimaryMonitor setting");
                    return;
                }
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

    private void TryRegisterApplicationService()
    {
        if (_applicationService != null || _isEventRegistered)
        {
            Debug.WriteLine("TryRegisterApplicationService: Already registered");
            return;
        }

        var serviceProvider = _serviceProvider ?? App.ServiceProvider;
        if (serviceProvider != null)
        {
            try
            {
                var applicationService = serviceProvider.GetRequiredService<ApplicationService>();
                Debug.WriteLine("TryRegisterApplicationService: ApplicationService obtained");

                _applicationService = applicationService;
                _applicationService.SessionStateChanged += OnSessionStateChanged;
                _isEventRegistered = true;
                Debug.WriteLine("TryRegisterApplicationService: Event registration completed");

                // Update UI to reflect current state
                UpdateUI();
                Debug.WriteLine("TryRegisterApplicationService: Initial UI update completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryRegisterApplicationService: Failed to get ApplicationService: {ex.Message}");
            }
        }
        else
        {
            Debug.WriteLine("TryRegisterApplicationService: ServiceProvider is null");
        }
    }

    private void ResetTransforms()
    {
        try
        {
            // Immediately reset all transform values
            var circleTransform = FindName("CircleScaleTransform") as ScaleTransform;
            if (circleTransform != null)
            {
                circleTransform.ScaleX = 1.0;
                circleTransform.ScaleY = 1.0;
            }

            var iconScaleTransform = FindName("IconScaleTransform") as ScaleTransform;
            if (iconScaleTransform != null)
            {
                iconScaleTransform.ScaleX = 1.0;
                iconScaleTransform.ScaleY = 1.0;
            }

            var iconRotateTransform = FindName("IconRotateTransform") as RotateTransform;
            if (iconRotateTransform != null)
            {
                iconRotateTransform.Angle = 0;
            }

            var audioRingScaleTransform = FindName("AudioRingScaleTransform") as ScaleTransform;
            if (audioRingScaleTransform != null)
            {
                audioRingScaleTransform.ScaleX = 1.0;
                audioRingScaleTransform.ScaleY = 1.0;
            }

            Debug.WriteLine("ResetTransforms: All transforms immediately reset to default values");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ResetTransforms: Failed to reset transforms: {ex.Message}");
        }
    }

    private void StartAudioLevelMonitoring()
    {
        StopAudioLevelMonitoring();
        _audioLevelTimer = new System.Threading.Timer(UpdateAudioLevelVisualization, null, 0, 100);
    }

    private void StopAudioLevelMonitoring()
    {
        _audioLevelTimer?.Dispose();
        _audioLevelTimer = null;
    }

    private void UpdateAudioLevelVisualization(object? state)
    {
        Dispatcher.Invoke(() =>
        {
            var random = new Random();
            var audioLevel = random.NextDouble();

            var audioRing = FindName("AudioRing") as Ellipse;
            if (audioRing != null && _applicationService?.GetCurrentState() == Corelib.Session.SessionState.Listening)
            {
                var scale = 1.0 + (audioLevel * 0.3);
                var opacity = 0.3 + (audioLevel * 0.5);

                var transform = audioRing.RenderTransform as ScaleTransform;
                if (transform != null)
                {
                    transform.ScaleX = scale;
                    transform.ScaleY = scale;
                }
                audioRing.Opacity = opacity;

                var green = (byte)(75 + (audioLevel * 180));
                audioRing.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, green, 80));
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAudioLevelMonitoring();

        // Unregister events to prevent memory leaks
        if (_applicationService != null && _isEventRegistered)
        {
            _applicationService.SessionStateChanged -= OnSessionStateChanged;
            _isEventRegistered = false;
            Debug.WriteLine("OnClosed: Event unregistration completed");
        }

        base.OnClosed(e);
    }
}
