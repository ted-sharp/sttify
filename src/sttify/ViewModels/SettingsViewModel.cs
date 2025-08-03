using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Output;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace Sttify.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsProvider _settingsProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVibeEngine))]
    [NotifyPropertyChangedFor(nameof(IsVoskSelected))]
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
    public bool IsVoskSelected => Settings?.Engine?.Profile?.ToLowerInvariant() == "vosk";

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

    [RelayCommand]
    private async Task ResetWindowPositionAsync()
    {
        try
        {
            Settings.Application.ControlWindow.Left = double.NaN;
            Settings.Application.ControlWindow.Top = double.NaN;
            Settings.Application.ControlWindow.DisplayConfiguration = "";
            
            await SaveSettingsAsync();
            
            System.Windows.MessageBox.Show(
                "Control window position has been reset. The window will appear at the default location on next startup.", 
                "Position Reset", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to reset window position: {ex.Message}", 
                "Reset Failed", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDirectory = Path.Combine(appDataPath, "sttify", "logs");
            
            // Create directory if it doesn't exist
            Directory.CreateDirectory(logDirectory);
            
            // Open directory in Windows Explorer using modern C# syntax
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to open log directory: {ex.Message}", 
                "Open Failed", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenEngineDocumentation()
    {
        try
        {
            var selectedEngine = Settings?.Engine?.Profile?.ToLowerInvariant();
            var url = GetEngineDocumentationUrl(selectedEngine);
            
            if (!string.IsNullOrEmpty(url))
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "No documentation URL available for the selected engine.", 
                    "Documentation", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to open documentation: {ex.Message}", 
                "Open Failed", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ShowVoskModelInfo()
    {
        try
        {
            var voskConfig = GetVoskConfiguration();
            if (voskConfig?.RecommendedModels?.Length > 0)
            {
                var modelInfo = string.Join("\n\n", voskConfig.RecommendedModels.Select(m => 
                    $"📦 {m.Name}\n" +
                    $"   Size: {m.Size}\n" +
                    $"   Language: {m.Language}\n" +
                    $"   {m.Description}\n" +
                    $"   Download: {m.DownloadUrl}"));

                System.Windows.MessageBox.Show(
                    $"Recommended Vosk Models:\n\n{modelInfo}\n\nFor more models, visit: {voskConfig.ModelsUrl}",
                    "Vosk Models",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://alphacep.com/vosk/models",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to show model information: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static Sttify.Corelib.Config.VoskConfiguration? GetVoskConfiguration()
    {
        try
        {
            var config = Sttify.Corelib.Config.AppConfiguration.Configuration;
            var voskConfig = new Sttify.Corelib.Config.VoskConfiguration();
            config.GetSection("Engines:Vosk").Bind(voskConfig);
            return voskConfig;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetEngineDocumentationUrl(string? engineProfile) =>
        engineProfile switch
        {
            "vosk" => "https://alphacep.com/vosk/models",
            "vibe" => "https://github.com/thewh1teagle/vibe",
            "azure" => "https://docs.microsoft.com/en-us/azure/cognitive-services/speech-service/",
            "aws" => "https://docs.aws.amazon.com/transcribe/",
            "google" => "https://cloud.google.com/speech-to-text/docs",
            _ => null
        };

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

    [RelayCommand]
    private async Task TestOutputAsync(string? testText)
    {
        if (string.IsNullOrEmpty(testText))
        {
            System.Diagnostics.Debug.WriteLine("*** Test Output: No test text provided ***");
            return;
        }

        try
        {
            // Create output sink based on current settings
            ITextOutputSink outputSink = Settings.Output.PrimaryOutputIndex switch
            {
                0 => new TsfTipSink(), // TSF TIP
                1 => new SendInputSink(), // SendInput
                _ => new SendInputSink() // Default to SendInput
            };

            System.Diagnostics.Debug.WriteLine($"*** Testing {outputSink.Name} with text: '{testText}' ***");

            bool canSend = await outputSink.CanSendAsync();
            if (canSend)
            {
                await outputSink.SendAsync(testText);
                System.Diagnostics.Debug.WriteLine($"*** Test completed successfully using {outputSink.Name} ***");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"*** {outputSink.Name} output method is not available ***");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Test failed: {ex.Message} ***");
        }
    }

    [RelayCommand]
    private async Task TestTsfAsync(string? testText)
    {
        if (string.IsNullOrEmpty(testText))
        {
            System.Diagnostics.Debug.WriteLine("*** Test TSF TIP: No test text provided ***");
            return;
        }

        try
        {
            var tsfSink = new TsfTipSink();
            System.Diagnostics.Debug.WriteLine($"*** Testing TSF TIP with text: '{testText}' ***");

            bool canSend = await tsfSink.CanSendAsync();
            if (canSend)
            {
                await tsfSink.SendAsync(testText);
                System.Diagnostics.Debug.WriteLine($"*** TSF TIP test completed successfully ***");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"*** TSF TIP is not available ***");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** TSF TIP test failed: {ex.Message} ***");
        }
    }

    [RelayCommand]
    private async Task TestSendInputAsync(string? testText)
    {
        if (string.IsNullOrEmpty(testText))
        {
            System.Diagnostics.Debug.WriteLine("*** Test SendInput: No test text provided ***");
            return;
        }

        try
        {
            var sendInputSink = new SendInputSink();
            System.Diagnostics.Debug.WriteLine($"*** Testing SendInput with text: '{testText}' ***");

            bool canSend = await sendInputSink.CanSendAsync();
            if (canSend)
            {
                await sendInputSink.SendAsync(testText);
                System.Diagnostics.Debug.WriteLine($"*** SendInput test completed successfully ***");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"*** SendInput is not available on this platform ***");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** SendInput test failed: {ex.Message} ***");
        }
    }
}