using System.Windows;
using Sttify.Corelib.Diagnostics;
using Sttify.ViewModels;

namespace Sttify.Views;

public partial class EnhancedSettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public EnhancedSettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
    }

    private void OnApplyAndClose(object sender, RoutedEventArgs e)
    {
        AsyncHelper.FireAndForget(async () =>
        {
            try
            {
                await _viewModel.SaveSettingsCommand.ExecuteAsync(null).ConfigureAwait(false);
                Dispatcher.Invoke(() =>
                {
                    DialogResult = true;
                    Close();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Failed to save settings: {ex.Message}",
                    "Sttify", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }, nameof(OnApplyAndClose));
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
