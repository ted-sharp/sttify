using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Engine.Vosk;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;

namespace Sttify.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsProvider _settingsProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVibeEngine))]
    private SttifySettings _settings = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _audioDevices = new();

    [ObservableProperty]
    private ObservableCollection<VoskModelInfo> _availableModels = new();

    [ObservableProperty]
    private ObservableCollection<string> _installedModels = new();

    [ObservableProperty]
    private string[] _availableEngines = Array.Empty<string>();

    [ObservableProperty]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = "";

    public bool IsVibeEngine => Settings?.Engine?.Profile?.ToLowerInvariant() == "vibe";

    public SettingsViewModel(SettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadSettingsAsync();
        await LoadAudioDevicesAsync();
        await LoadEngineInfoAsync();
        await LoadVoskModelsAsync();
    }

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        try
        {
            IsLoading = true;
            Settings = await _settingsProvider.GetSettingsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            IsLoading = true;
            await _settingsProvider.SaveSettingsAsync(Settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        Settings = new SttifySettings();
    }

    private async Task LoadAudioDevicesAsync()
    {
        try
        {
            var devices = await Task.Run(() => AudioCapture.GetAvailableDevices());
            AudioDevices.Clear();
            foreach (var device in devices)
            {
                AudioDevices.Add(device);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load audio devices: {ex.Message}");
        }
    }

    private async Task LoadEngineInfoAsync()
    {
        try
        {
            AvailableEngines = await Task.Run(() => EngineFactory.GetAvailableProfiles());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load engine info: {ex.Message}");
        }
    }

    private async Task LoadVoskModelsAsync()
    {
        try
        {
            // Load available models for download
            var availableModels = await VoskModelManager.GetAvailableModelsAsync();
            AvailableModels.Clear();
            foreach (var model in availableModels)
            {
                AvailableModels.Add(model);
            }

            // Load installed models
            var modelsDir = VoskModelManager.GetDefaultModelsDirectory();
            var installedModels = await Task.Run(() => VoskModelManager.GetInstalledModels(modelsDir));
            InstalledModels.Clear();
            foreach (var model in installedModels)
            {
                InstalledModels.Add(model);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load Vosk models: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DownloadModelAsync(VoskModelInfo? modelInfo)
    {
        if (modelInfo == null || IsDownloadingModel)
            return;

        try
        {
            IsDownloadingModel = true;
            DownloadProgress = 0;
            DownloadStatus = "Starting download...";

            var modelsDir = VoskModelManager.GetDefaultModelsDirectory();
            
            var modelPath = await VoskModelManager.DownloadModelAsync(
                modelInfo, 
                modelsDir,
                OnDownloadProgress);

            // Update settings to use the new model
            Settings.Engine.Vosk.ModelPath = modelPath;
            await SaveSettingsAsync();
            await LoadVoskModelsAsync();

            System.Windows.MessageBox.Show($"Model '{modelInfo.Name}' downloaded and configured successfully!", 
                "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to download model: {ex.Message}", 
                "Download Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsDownloadingModel = false;
            DownloadProgress = 0;
            DownloadStatus = "";
        }
    }

    [RelayCommand]
    private async Task BrowseForModelAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Vosk Model Directory"
        };

        if (dialog.ShowDialog() == true)
        {
            var selectedPath = dialog.FolderName;
            if (VoskModelManager.IsModelInstalled(selectedPath))
            {
                Settings.Engine.Vosk.ModelPath = selectedPath;
                await SaveSettingsAsync();
            }
            else
            {
                System.Windows.MessageBox.Show("The selected directory does not contain a valid Vosk model.", 
                    "Invalid Model", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    private async Task TestEngineAsync()
    {
        try
        {
            var engine = EngineFactory.CreateEngine(Settings.Engine);
            
            // Basic test - try to initialize
            await engine.StartAsync();
            await Task.Delay(100);
            await engine.StopAsync();
            
            engine.Dispose();

            System.Windows.MessageBox.Show("Engine test successful!", "Test Result", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Engine test failed: {ex.Message}", "Test Result", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task TestVibeAsync()
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            if (!string.IsNullOrEmpty(Settings.Engine.Vibe.ApiKey))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {Settings.Engine.Vibe.ApiKey}");
            }

            var endpoint = $"{Settings.Engine.Vibe.Endpoint.TrimEnd('/')}/health";
            var response = await httpClient.GetAsync(endpoint);
            
            if (response.IsSuccessStatusCode)
            {
                System.Windows.MessageBox.Show("Vibe connection test successful!", "Test Result", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show($"Vibe connection test failed: HTTP {response.StatusCode}", "Test Result", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Vibe connection test failed: {ex.Message}", "Test Result", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDownloadProgress(DownloadProgressEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            DownloadProgress = e.ProgressPercentage;
            DownloadStatus = e.Status;
        });
    }
}