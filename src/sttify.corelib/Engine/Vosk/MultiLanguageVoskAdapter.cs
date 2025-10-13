using System.Text.Json;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Vosk;

namespace Sttify.Corelib.Engine.Vosk;

public class MultiLanguageVoskAdapter : ISttEngine
{
    private readonly Queue<byte[]> _audioQueue = new();
    private readonly Dictionary<string, Model> _loadedModels = new();
    private readonly object _lockObject = new();
    private readonly Dictionary<string, VoskRecognizer> _recognizers = new();

    private readonly VoskEngineSettings _settings;
    private string _currentLanguage = "ja";
    private string _currentPartialText = "";
    private bool _isRunning;
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;
    private DateTime _recognitionStartTime;

    public MultiLanguageVoskAdapter(VoskEngineSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _currentLanguage = _settings.Language ?? "ja";
    }

    public event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    public event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    public event EventHandler<SttErrorEventArgs>? OnError;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine is already running");

        try
        {
            await Task.Run(() => InitializeModels(), cancellationToken);

            lock (_lockObject)
            {
                _isRunning = true;
                _processingCancellation = new CancellationTokenSource();
                _recognitionStartTime = DateTime.UtcNow;
            }

            _processingTask = Task.Run(() => ProcessAudioLoop(_processingCancellation.Token), cancellationToken);

            Telemetry.LogEvent("MultiLanguageVoskEngineStarted", new
            {
                Languages = _loadedModels.Keys.ToArray(),
                CurrentLanguage = _currentLanguage,
                ModelBasePath = _settings.ModelPath
            });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("MultiLanguageVoskEngineStartFailed", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Failed to start multi-language Vosk engine: {ex.Message}"));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _processingCancellation?.Cancel();
        }

        if (_processingTask != null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Finalize any remaining recognition
        if (_recognizers.TryGetValue(_currentLanguage, out var recognizer) && !string.IsNullOrEmpty(_currentPartialText))
        {
            try
            {
                var finalResult = recognizer.FinalResult();
                ProcessRecognitionResult(finalResult, true);
            }
            catch (Exception ex)
            {
                Telemetry.LogError("MultiLanguageVoskFinalizationFailed", ex);
            }
        }

        _processingCancellation?.Dispose();
        _processingCancellation = null;
        _processingTask = null;
        _currentPartialText = "";

        Telemetry.LogEvent("MultiLanguageVoskEngineStopped");
    }

    public void PushAudio(ReadOnlySpan<byte> audioData)
    {
        if (!_isRunning || audioData.IsEmpty)
            return;

        var buffer = audioData.ToArray();
        lock (_audioQueue)
        {
            _audioQueue.Enqueue(buffer);

            // Prevent queue from growing too large
            while (_audioQueue.Count > 100)
            {
                _audioQueue.Dequeue();
            }
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();

        foreach (var recognizer in _recognizers.Values)
        {
            recognizer?.Dispose();
        }
        _recognizers.Clear();

        foreach (var model in _loadedModels.Values)
        {
            model?.Dispose();
        }
        _loadedModels.Clear();
    }

    public async Task SwitchLanguageAsync(string languageCode)
    {
        if (_currentLanguage == languageCode)
            return;

        var wasRunning = _isRunning;
        if (wasRunning)
        {
            await StopAsync();
        }

        _currentLanguage = languageCode;
        _settings.Language = languageCode;

        if (wasRunning)
        {
            await StartAsync();
        }

        Telemetry.LogEvent("LanguageSwitched", new { NewLanguage = languageCode });
    }

    public string[] GetAvailableLanguages()
    {
        return _loadedModels.Keys.ToArray();
    }

    public string GetCurrentLanguage()
    {
        return _currentLanguage;
    }

    private void InitializeModels()
    {
        var availableModels = GetAvailableModelPaths();

        if (availableModels.Count == 0)
        {
            throw new DirectoryNotFoundException($"No Vosk models found in: {_settings.ModelPath}");
        }

        try
        {
            global::Vosk.Vosk.SetLogLevel(0);

            foreach (var modelInfo in availableModels)
            {
                try
                {
                    var model = new Model(modelInfo.Value);
                    var recognizer = new VoskRecognizer(model, 16000);

                    _loadedModels[modelInfo.Key] = model;
                    _recognizers[modelInfo.Key] = recognizer;

                    Telemetry.LogEvent("VoskModelLoaded", new
                    {
                        Language = modelInfo.Key,
                        ModelPath = modelInfo.Value,
                        ModelSize = GetDirectorySize(modelInfo.Value)
                    });
                }
                catch (Exception ex)
                {
                    Telemetry.LogWarning("VoskModelLoadFailed",
                        $"Failed to load model for language {modelInfo.Key}: {ex.Message}");
                }
            }

            if (_loadedModels.Count == 0)
            {
                throw new InvalidOperationException("No Vosk models could be loaded successfully");
            }

            // Ensure current language is available
            if (!_loadedModels.ContainsKey(_currentLanguage))
            {
                _currentLanguage = _loadedModels.Keys.First();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize Vosk models: {ex.Message}", ex);
        }
    }

    private Dictionary<string, string> GetAvailableModelPaths()
    {
        var modelPaths = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(_settings.ModelPath))
            return modelPaths;

        // Check if ModelPath is a direct model directory
        if (IsValidModelDirectory(_settings.ModelPath))
        {
            var language = DetectModelLanguage(_settings.ModelPath);
            modelPaths[language] = _settings.ModelPath;
            return modelPaths;
        }

        // Check if ModelPath is a parent directory containing multiple models
        if (Directory.Exists(_settings.ModelPath))
        {
            var subdirectories = Directory.GetDirectories(_settings.ModelPath);
            foreach (var subdir in subdirectories)
            {
                if (IsValidModelDirectory(subdir))
                {
                    var language = DetectModelLanguage(subdir);
                    modelPaths[language] = subdir;
                }
            }
        }

        return modelPaths;
    }

    private bool IsValidModelDirectory(string path)
    {
        if (!Directory.Exists(path))
            return false;

        var requiredFiles = new[]
        {
            "am/final.mdl",
            "graph/HCLG.fst",
            "graph/words.txt"
        };

        return requiredFiles.All(file => File.Exists(Path.Combine(path, file)));
    }

    private string DetectModelLanguage(string modelPath)
    {
        var modelName = Path.GetFileName(modelPath).ToLowerInvariant();

        // Try to detect language from model directory name
        if (modelName.Contains("-ja-") || modelName.Contains("japanese"))
            return "ja";
        if (modelName.Contains("-en-") || modelName.Contains("english"))
            return "en";
        if (modelName.Contains("-zh-") || modelName.Contains("chinese"))
            return "zh";
        if (modelName.Contains("-ko-") || modelName.Contains("korean"))
            return "ko";
        if (modelName.Contains("-es-") || modelName.Contains("spanish"))
            return "es";
        if (modelName.Contains("-fr-") || modelName.Contains("french"))
            return "fr";
        if (modelName.Contains("-de-") || modelName.Contains("german"))
            return "de";
        if (modelName.Contains("-ru-") || modelName.Contains("russian"))
            return "ru";

        // Default to the configured language or 'unknown'
        return _settings.Language ?? "unknown";
    }

    private async Task ProcessAudioLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                byte[]? audioChunk = null;

                lock (_audioQueue)
                {
                    if (_audioQueue.Count > 0)
                    {
                        audioChunk = _audioQueue.Dequeue();
                    }
                }

                if (audioChunk != null && _recognizers.TryGetValue(_currentLanguage, out var recognizer))
                {
                    try
                    {
                        bool hasResult = recognizer.AcceptWaveform(audioChunk, audioChunk.Length);

                        if (hasResult)
                        {
                            var result = recognizer.Result();
                            ProcessRecognitionResult(result, true);
                        }
                        else
                        {
                            var partialResult = recognizer.PartialResult();
                            ProcessRecognitionResult(partialResult, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Telemetry.LogError("MultiLanguageVoskProcessingError", ex);
                        OnError?.Invoke(this, new SttErrorEventArgs(ex, "Error processing audio with multi-language Vosk"));
                    }
                }

                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Telemetry.LogError("MultiLanguageVoskProcessingLoopError", ex);
            OnError?.Invoke(this, new SttErrorEventArgs(ex, $"Error in multi-language Vosk processing loop: {ex.Message}"));
        }
    }

    private void ProcessRecognitionResult(string jsonResult, bool isFinal)
    {
        try
        {
            if (string.IsNullOrEmpty(jsonResult))
                return;

            var result = JsonSerializer.Deserialize<VoskResult>(jsonResult);
            if (result == null)
                return;

            var text = result.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            // Apply language-specific post-processing
            text = ApplyLanguageSpecificProcessing(text, _currentLanguage);

            var confidence = result.Confidence ?? 0.0;

            if (isFinal)
            {
                var duration = DateTime.UtcNow - _recognitionStartTime;
                OnFinal?.Invoke(this, new FinalRecognitionEventArgs(text, confidence, duration));
                _currentPartialText = "";
                _recognitionStartTime = DateTime.UtcNow;
            }
            else
            {
                if (!string.Equals(_currentPartialText, text, StringComparison.OrdinalIgnoreCase))
                {
                    _currentPartialText = text;
                    OnPartial?.Invoke(this, new PartialRecognitionEventArgs(text, confidence));
                }
            }
        }
        catch (JsonException ex)
        {
            Telemetry.LogError("MultiLanguageVoskResultParsingError", ex, new { JsonResult = jsonResult });
        }
        catch (Exception ex)
        {
            Telemetry.LogError("MultiLanguageVoskResultProcessingError", ex);
        }
    }

    private string ApplyLanguageSpecificProcessing(string text, string language)
    {
        if (string.IsNullOrEmpty(text) || !_settings.Punctuation)
            return text;

        return language switch
        {
            "ja" => ApplyJapanesePunctuation(text),
            "zh" => ApplyChinesePunctuation(text),
            "en" => ApplyEnglishPunctuation(text),
            _ => ApplyDefaultPunctuation(text)
        };
    }

    private string ApplyJapanesePunctuation(string text)
    {
        if (!text.EndsWith("。") && !text.EndsWith("？") && !text.EndsWith("！"))
        {
            text += "。";
        }
        return text;
    }

    private string ApplyChinesePunctuation(string text)
    {
        if (!text.EndsWith("。") && !text.EndsWith("？") && !text.EndsWith("！"))
        {
            text += "。";
        }
        return text;
    }

    private string ApplyEnglishPunctuation(string text)
    {
        if (!text.EndsWith(".") && !text.EndsWith("?") && !text.EndsWith("!"))
        {
            text += ".";
        }
        return text;
    }

    private string ApplyDefaultPunctuation(string text)
    {
        if (!text.EndsWith(".") && !text.EndsWith("。"))
        {
            text += ".";
        }
        return text;
    }

    private long GetDirectorySize(string directoryPath)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            return directoryInfo.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    private class VoskResult
    {
        public string? Text { get; set; }
        public double? Confidence { get; set; }
    }
}
