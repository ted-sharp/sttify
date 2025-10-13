using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sttify.Corelib.Audio;
using Sttify.Corelib.Config;
using Sttify.Corelib.Engine;
using Sttify.Corelib.Engine.Vosk;
using Sttify.Corelib.Ime;
using Sttify.Corelib.Output;
using Sttify.Services;

namespace Sttify.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ApplicationService? _applicationService;
    private readonly SettingsProvider _settingsProvider;
    private readonly IOutputSinkProvider? _sinkProvider;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _audioDevices = new();

    [ObservableProperty]
    private string[] _availableEngines = Array.Empty<string>();

    [ObservableProperty]
    private ObservableCollection<VoskModelInfo> _availableModels = new();

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = "";

    [ObservableProperty]
    private ObservableCollection<string> _installedModels = new();

    [ObservableProperty]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVibeEngine))]
    [NotifyPropertyChangedFor(nameof(IsVoskSelected))]
    [NotifyPropertyChangedFor(nameof(IsCloudEngine))]
    [NotifyPropertyChangedFor(nameof(IsSendInputSelected))]
    private SttifySettings _settings = new();

    public SettingsViewModel(SettingsProvider settingsProvider, IOutputSinkProvider? sinkProvider = null, ApplicationService? applicationService = null)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _sinkProvider = sinkProvider;
        _applicationService = applicationService;

        _ = InitializeAsync();
    }

    public bool IsVibeEngine => Settings.Engine.Profile.ToLowerInvariant() == "vibe";
    public bool IsVoskSelected => Settings.Engine.Profile.ToLowerInvariant() == "vosk";

    public bool IsCloudEngine => Settings.Engine.Profile.ToLowerInvariant().Contains("cloud") ||
                                 Settings.Engine.Profile.ToLowerInvariant() == "azure" ||
                                 Settings.Engine.Profile.ToLowerInvariant() == "google" ||
                                 Settings.Engine.Profile.ToLowerInvariant() == "aws";

    public bool IsSendInputSelected => Settings.Output.PrimaryOutputIndex == 0;

    public string EngineProfile
    {
        get => Settings.Engine.Profile;
        set
        {
            if (Settings.Engine.Profile != value)
            {
                Settings.Engine.Profile = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsVibeEngine));
                OnPropertyChanged(nameof(IsVoskSelected));
                OnPropertyChanged(nameof(IsCloudEngine));
                _ = SaveSettingsAsync();
            }
        }
    }

    public int PrimaryOutputIndex
    {
        get => Settings.Output.PrimaryOutputIndex;
        set
        {
            if (Settings.Output.PrimaryOutputIndex != value)
            {
                Settings.Output.PrimaryOutputIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSendInputSelected));
                _ = SaveSettingsAsync();
            }
        }
    }

    private async Task InitializeAsync()
    {
        // Parallelize startup loads for faster UI readiness
        var loadSettingsTask = LoadSettingsAsync();
        var loadDevicesTask = LoadAudioDevicesAsync();
        var loadEngineTask = LoadEngineInfoAsync();
        var loadModelsTask = LoadVoskModelsAsync();

        await Task.WhenAll(loadSettingsTask, loadDevicesTask, loadEngineTask, loadModelsTask);
    }

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        try
        {
            IsLoading = true;
            Settings = await _settingsProvider.GetSettingsAsync();

            // Explicitly notify dependent properties after settings load
            OnPropertyChanged(nameof(EngineProfile));
            OnPropertyChanged(nameof(IsVoskSelected));
            OnPropertyChanged(nameof(IsVibeEngine));
            OnPropertyChanged(nameof(IsCloudEngine));
            OnPropertyChanged(nameof(PrimaryOutputIndex));
            OnPropertyChanged(nameof(IsSendInputSelected));
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

            // Apply settings immediately: refresh output sinks and hotkeys
            try
            {
                if (_sinkProvider != null)
                    await _sinkProvider.RefreshAsync();
                if (_applicationService != null)
                {
                    await _applicationService.ReinitializeHotkeysAsync();
                    await _applicationService.ReinitializeEngineAsync(restartIfRunning: true);
                    await _applicationService.ReinitializeOverlayAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply settings immediately: {ex.Message}");
            }
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
    private async Task PreviewOverlayAsync()
    {
        try
        {
            if (_applicationService != null)
            {
                await _applicationService.ShowOverlayTextAsync("Overlay Preview: The quick brown fox jumps over the lazy dog.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Overlay preview failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task HideOverlayAsync()
    {
        try
        {
            if (_applicationService != null)
            {
                await _applicationService.HideOverlayAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Overlay hide failed: {ex.Message}");
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
            var selectedEngine = Settings.Engine.Profile.ToLowerInvariant();
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
            var dialog = new Views.VoskModelInfoDialog
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            dialog.ShowDialog();
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
            // Prefer unified factory's list if available; keep EngineFactory for backward compatibility
            AvailableEngines = await Task.Run(() => SttEngineFactory.GetAvailableEngines());
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
    private async Task BrowseForModelFolderAsync()
    {
        try
        {
            var openFolderDialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Vosk Model Directory"
            };

            var folderResult = openFolderDialog.ShowDialog();
            if (folderResult == true)
            {
                await ProcessSelectedModelAsync(openFolderDialog.FolderName, false);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to browse for model folder: {ex.Message}",
                "Browse Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task BrowseForModelZipAsync()
    {
        try
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Vosk Model ZIP file",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            var fileResult = openFileDialog.ShowDialog();
            if (fileResult == true)
            {
                // Show status message for ZIP processing
                System.Diagnostics.Debug.WriteLine($"*** Starting ZIP extraction for: {openFileDialog.FileName} ***");
                await ProcessSelectedModelAsync(openFileDialog.FileName, true);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to browse for ZIP file: {ex.Message}",
                "Browse Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ProcessSelectedModelAsync(string selectedPath, bool isZipFile)
    {
        // Show wait cursor during processing
        var originalCursor = System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

        try
        {
            string finalModelPath;

            if (isZipFile)
            {
                // Extract ZIP to cache directory
                var modelsDir = VoskModelManager.GetDefaultModelsDirectory();
                Directory.CreateDirectory(modelsDir);

                var fileName = Path.GetFileNameWithoutExtension(selectedPath);
                var tempExtractionPath = Path.Combine(modelsDir, "temp_" + Guid.NewGuid().ToString());
                var finalExtractionPath = Path.Combine(modelsDir, fileName);

                if (Directory.Exists(finalExtractionPath))
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Model '{fileName}' already exists. Do you want to overwrite it?",
                        "Model Exists",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.No)
                        return;

                    Directory.Delete(finalExtractionPath, true);
                }

                try
                {
                    // Extract to temporary directory first
                    System.Diagnostics.Debug.WriteLine($"*** Extracting ZIP to temporary directory: {tempExtractionPath} ***");
                    System.IO.Compression.ZipFile.ExtractToDirectory(selectedPath, tempExtractionPath);
                    System.Diagnostics.Debug.WriteLine("*** ZIP extraction completed ***");

                    // Find the actual model directory within the extracted content
                    var extractedItems = Directory.GetDirectories(tempExtractionPath);

                    string actualModelPath;

                    // Check if files were extracted directly to temp directory
                    if (VoskModelManager.IsModelInstalled(tempExtractionPath))
                    {
                        actualModelPath = tempExtractionPath;
                    }
                    // Check if there's a single subdirectory containing the model
                    else if (extractedItems.Length == 1 && VoskModelManager.IsModelInstalled(extractedItems[0]))
                    {
                        actualModelPath = extractedItems[0];
                    }
                    // Look for any directory that contains a valid model
                    else
                    {
                        actualModelPath = extractedItems.FirstOrDefault(VoskModelManager.IsModelInstalled) ?? tempExtractionPath;
                    }

                    // Move the actual model directory to the final location
                    if (actualModelPath == tempExtractionPath)
                    {
                        Directory.Move(tempExtractionPath, finalExtractionPath);
                    }
                    else
                    {
                        Directory.Move(actualModelPath, finalExtractionPath);
                        // Clean up remaining temp directory
                        if (Directory.Exists(tempExtractionPath))
                            Directory.Delete(tempExtractionPath, true);
                    }

                    finalModelPath = finalExtractionPath;

                    System.Diagnostics.Debug.WriteLine($"*** Model extracted successfully to: {finalExtractionPath} ***");
                }
                catch (Exception ex)
                {
                    // Clean up temp directory on failure
                    if (Directory.Exists(tempExtractionPath))
                        Directory.Delete(tempExtractionPath, true);
                    throw new Exception($"Failed to extract ZIP file: {ex.Message}", ex);
                }
            }
            else
            {
                finalModelPath = selectedPath;
            }

            // Validate the final model path
            if (VoskModelManager.IsModelInstalled(finalModelPath))
            {
                System.Diagnostics.Debug.WriteLine($"*** Model validation successful, updating path to: {finalModelPath} ***");
                Settings.Engine.Vosk.ModelPath = finalModelPath;

                // Force multiple property change notifications to ensure UI updates
                OnPropertyChanged(nameof(Settings));

                await SaveSettingsAsync();

                // Trigger additional refresh after save
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OnPropertyChanged(nameof(Settings));
                });

                System.Diagnostics.Debug.WriteLine("*** Path update completed successfully ***");
            }
            else
            {
                var errorMessage = $"The selected path does not contain a valid Vosk model.\n\nPath: {finalModelPath}\n\n" +
                    "A valid Vosk model should contain these essential files:\n" +
                    "• am/final.mdl (acoustic model)\n" +
                    "• graph/HCLG.fst (large models) OR graph/HCLR.fst (small models)\n" +
                    "• graph/words.txt (vocabulary)\n\n" +
                    "Optional files:\n" +
                    "• ivector/final.ie (i-vector extractor)\n" +
                    "• conf/model.conf (configuration)";

                System.Windows.MessageBox.Show(errorMessage,
                    "Invalid Model",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                // If this was from a ZIP extraction and it failed, clean up
                if (isZipFile && Directory.Exists(finalModelPath) && finalModelPath != selectedPath)
                {
                    try
                    { Directory.Delete(finalModelPath, true); }
                    catch (Exception cleanupEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"*** Failed to cleanup invalid model directory: {cleanupEx.Message} ***");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to process model: {ex.Message}",
                "Processing Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Restore original cursor
            System.Windows.Input.Mouse.OverrideCursor = originalCursor;
        }
    }

    [RelayCommand]
    private async Task TestEngineAsync()
    {
        try
        {
            var engine = SttEngineFactory.CreateEngine(Settings.Engine);

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
                0 => new SendInputSink(ConvertToSendInputSettings(Settings.Output.SendInput)), // SendInput
                1 => new ExternalProcessSink(new ExternalProcessSettings
                {
                    ExecutablePath = Settings.Output.ExternalProcess.ExecutablePath,
                    ArgumentTemplate = Settings.Output.ExternalProcess.ArgumentTemplate,
                    WaitForExit = Settings.Output.ExternalProcess.WaitForExit,
                    TimeoutMs = Settings.Output.ExternalProcess.TimeoutMs,
                    ThrottleMs = Settings.Output.ExternalProcess.ThrottleMs,
                    LogArguments = Settings.Output.ExternalProcess.LogArguments,
                    LogOutput = Settings.Output.ExternalProcess.LogOutput,
                    WorkingDirectory = Settings.Output.ExternalProcess.WorkingDirectory,
                    EnvironmentVariables = new Dictionary<string, string>(Settings.Output.ExternalProcess.EnvironmentVariables ?? new())
                }), // External Process
                2 => new StreamSink(new StreamSinkSettings
                {
                    OutputType = Settings.Output.Stream.OutputType,
                    FilePath = Settings.Output.Stream.FilePath,
                    AppendToFile = Settings.Output.Stream.AppendToFile,
                    IncludeTimestamp = Settings.Output.Stream.IncludeTimestamp,
                    ForceFlush = Settings.Output.Stream.ForceFlush,
                    MaxFileSizeBytes = Settings.Output.Stream.MaxFileSizeBytes,
                    SharedMemoryName = Settings.Output.Stream.SharedMemoryName,
                    SharedMemorySize = Settings.Output.Stream.SharedMemorySize,
                    CustomPrefix = Settings.Output.Stream.CustomPrefix,
                    CustomSuffix = Settings.Output.Stream.CustomSuffix
                }), // Stream
                _ => new SendInputSink(ConvertToSendInputSettings(Settings.Output.SendInput)) // Default to SendInput
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
    private async Task TestSendInputAsync(string? testText)
    {
        if (string.IsNullOrEmpty(testText))
        {
            System.Diagnostics.Debug.WriteLine("*** Test SendInput: No test text provided ***");
            return;
        }

        try
        {
            var sendInputSink = new SendInputSink(ConvertToSendInputSettings(Settings.Output.SendInput));
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

    [RelayCommand]
    private async Task TestImeControlAsync(string? testText)
    {
        if (string.IsNullOrEmpty(testText))
        {
            testText = "IME制御テスト：これはテスト用のテキストです。This is test text.";
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"*** Starting comprehensive IME Control test with text: '{testText}' ***");

            // Get current IME status report first
            var statusReport = ImeTestHelper.GetImeStatusReport();
            System.Diagnostics.Debug.WriteLine($"*** Pre-test IME Status:\n{statusReport} ***");

            // Run comprehensive IME test
            var imeSettings = ConvertToSendInputSettings(Settings.Output.SendInput).Ime;
            var testResult = await ImeTestHelper.TestImeControlAsync(testText, imeSettings);

            // Log all test steps
            System.Diagnostics.Debug.WriteLine("*** IME Control Test Steps: ***");
            foreach (var step in testResult.Steps)
            {
                System.Diagnostics.Debug.WriteLine($"  {step}");
            }

            // Now test with actual text sending during suppression
            System.Diagnostics.Debug.WriteLine("*** Testing text input during IME suppression... ***");
            var sendInputSink = new SendInputSink(ConvertToSendInputSettings(Settings.Output.SendInput));
            bool canSend = await sendInputSink.CanSendAsync();
            if (canSend)
            {
                await sendInputSink.SendAsync(testText);
                System.Diagnostics.Debug.WriteLine("*** Text sent successfully with IME control integration ***");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("*** Cannot send text (SendInput not available or IME composing) ***");
            }

            // Final status
            if (testResult.Success)
            {
                System.Diagnostics.Debug.WriteLine("*** IME Control test completed successfully ***");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"*** IME Control test failed: {testResult.ErrorMessage} ***");
            }

            // Show detailed report
            System.Diagnostics.Debug.WriteLine("*** Full IME Test Report: ***");
            System.Diagnostics.Debug.WriteLine(testResult.GetReport());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** IME Control test failed with exception: {ex.Message} ***");
        }
    }

    private static SendInputSettings ConvertToSendInputSettings(SendInputOutputSettings outputSettings)
    {
        return new SendInputSettings
        {
            RateLimitCps = outputSettings.RateLimitCps,
            CommitKey = outputSettings.CommitKey,
            Ime = new ImeSettings
            {
                EnableImeControl = outputSettings.Ime.EnableImeControl,
                CloseImeWhenSending = outputSettings.Ime.CloseImeWhenSending,
                SetAlphanumericMode = outputSettings.Ime.SetAlphanumericMode,
                ClearCompositionString = outputSettings.Ime.ClearCompositionString,
                RestoreImeStateAfterSending = outputSettings.Ime.RestoreImeStateAfterSending,
                RestoreDelayMs = outputSettings.Ime.RestoreDelayMs,
                SkipWhenImeComposing = outputSettings.Ime.SkipWhenImeComposing
            }
        };
    }
}
