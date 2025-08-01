using Sttify.ViewModels;
using System.Windows;

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

    private async void OnApplyAndClose(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveSettingsCommand.ExecuteAsync(null);
            DialogResult = true;
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
        DialogResult = false;
        Close();
    }
}