using Sttify.Corelib.Engine.Vosk;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Sttify.Views;

public partial class VoskModelInfoDialog : Window
{
    public VoskModelInfoDialog()
    {
        InitializeComponent();
        LoadModels();
    }

    private void LoadModels()
    {
        ModelsListControl.ItemsSource = VoskModelManager.AvailableModels;
    }

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to open URL: {ex.Message}", 
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }

    private void OnMoreModelsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://alphacephei.com/vosk/models")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to open models page: {ex.Message}", 
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}