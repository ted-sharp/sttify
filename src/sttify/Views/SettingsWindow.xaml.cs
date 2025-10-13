using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Sttify.Corelib.Diagnostics;
using Sttify.Services;
using Sttify.ViewModels;

namespace Sttify.Views;

public partial class SettingsWindow
{
    private readonly ApplicationService _applicationService;
    private readonly Corelib.Config.SettingsProvider _settingsProvider;

    private readonly SettingsViewModel _viewModel;
    // Debug hotkey manager removed

    public SettingsWindow(SettingsViewModel viewModel, ApplicationService applicationService, Corelib.Config.SettingsProvider settingsProvider)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        DataContext = _viewModel;

        // Debug hotkey feature removed
        Closed += OnWindowClosed;
    }

    // Debug hotkey OnWindowLoaded removed

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Debug.WriteLine("*** SettingsWindow Closed ***");
    }

    // WndProc and debug hotkey handlers removed

    // Global hotkey test handlers removed

    private void OnOK(object sender, RoutedEventArgs e)
    {
        AsyncHelper.FireAndForget(async () =>
        {
            try
            {
                await _viewModel.SaveSettingsCommand.ExecuteAsync(null).ConfigureAwait(false);

                try
                {
                    await _applicationService.ReinitializeHotkeysAsync().ConfigureAwait(false);
                }
                catch { }

                Dispatcher.Invoke(Close);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Failed to save settings: {ex.Message}",
                    "Sttify", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }, nameof(OnOK));
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // RTSS integration has been removed


    private void OnVoskModelsUrlClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var url = "https://alphacephei.com/vosk/models";
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            Debug.WriteLine($"*** Opened Vosk models URL: {url} ***");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"*** Failed to open Vosk models URL: {ex.Message} ***");
            System.Windows.MessageBox.Show(
                $"Failed to open URL: {ex.Message}",
                "Open URL Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
