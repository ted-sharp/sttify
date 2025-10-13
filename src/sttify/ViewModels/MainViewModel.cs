using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sttify.Corelib.Session;
using Sttify.Services;

namespace Sttify.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ApplicationService _applicationService;

    [ObservableProperty]
    private RecognitionMode _currentMode = RecognitionMode.Ptt;

    [ObservableProperty]
    private SessionState _currentState = SessionState.Idle;

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private string _recognizedText = "";

    public MainViewModel(ApplicationService applicationService)
    {
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));

        _applicationService.SessionStateChanged += OnSessionStateChanged;
        _applicationService.TextRecognized += OnTextRecognized;

        CurrentState = _applicationService.GetCurrentState();
        CurrentMode = _applicationService.GetCurrentMode();
        IsListening = CurrentState == SessionState.Listening;
    }

    [RelayCommand]
    private async Task ToggleRecognitionAsync()
    {
        try
        {
            if (IsListening)
            {
                await _applicationService.StopRecognitionAsync();
            }
            else
            {
                await _applicationService.StartRecognitionAsync();
            }
        }
        catch (Exception ex)
        {
            // In a real application, you would show this in the UI
            System.Diagnostics.Debug.WriteLine($"Failed to toggle recognition: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SetMode(string mode)
    {
        if (Enum.TryParse<RecognitionMode>(mode, out var recognitionMode))
        {
            _applicationService.SetRecognitionMode(recognitionMode);
            CurrentMode = recognitionMode;
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        CurrentState = e.NewState;
        IsListening = e.NewState == SessionState.Listening;
    }

    private void OnTextRecognized(object? sender, TextRecognizedEventArgs e)
    {
        if (e.IsFinal)
        {
            RecognizedText = e.Text;
        }
    }
}
