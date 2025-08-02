using Sttify.Services;
using Sttify.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sttify.Views;

public partial class ControlWindow : Window
{
    private readonly ApplicationService? _applicationService;
    private readonly MainViewModel? _viewModel;
    private Storyboard? _currentPulseAnimation;
    private Sttify.Corelib.Session.SessionState _lastState = Sttify.Corelib.Session.SessionState.Idle;

    // Parameterless constructor for XAML
    public ControlWindow()
    {
        InitializeComponent();
    }

    // Constructor for dependency injection
    public ControlWindow(ApplicationService applicationService, MainViewModel viewModel) : this()
    {
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        
        DataContext = _viewModel;
        
        _applicationService.SessionStateChanged += OnSessionStateChanged;
        
        UpdateUI();
    }

    private async void OnMicrophoneClick(object sender, MouseButtonEventArgs e)
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