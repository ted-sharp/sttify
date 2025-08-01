using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace Sttify.Corelib.Localization;

public static class LocalizationManager
{
    private static readonly Dictionary<string, Dictionary<string, string>> _translations = new();
    private static string _currentLanguage = "en";
    private static readonly object _lockObject = new();

    static LocalizationManager()
    {
        LoadBuiltInTranslations();
        
        // Set default language based on system culture
        var systemLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (_translations.ContainsKey(systemLanguage))
        {
            _currentLanguage = systemLanguage;
        }
    }

    public static string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            lock (_lockObject)
            {
                if (_translations.ContainsKey(value))
                {
                    _currentLanguage = value;
                    OnLanguageChanged?.Invoke(null, new LanguageChangedEventArgs(value));
                }
            }
        }
    }

    public static event EventHandler<LanguageChangedEventArgs>? OnLanguageChanged;

    public static string GetString(string key, params object[] args)
    {
        lock (_lockObject)
        {
            var translation = GetTranslation(key);
            
            if (args.Length > 0)
            {
                try
                {
                    return string.Format(translation, args);
                }
                catch (FormatException)
                {
                    return translation;
                }
            }
            
            return translation;
        }
    }

    public static string GetString(string key, string defaultValue = "", params object[] args)
    {
        var result = GetString(key, args);
        return result == key ? defaultValue : result;
    }

    private static string GetTranslation(string key)
    {
        if (_translations.TryGetValue(_currentLanguage, out var languageDict))
        {
            if (languageDict.TryGetValue(key, out var translation))
            {
                return translation;
            }
        }

        // Fallback to English
        if (_currentLanguage != "en" && _translations.TryGetValue("en", out var englishDict))
        {
            if (englishDict.TryGetValue(key, out var englishTranslation))
            {
                return englishTranslation;
            }
        }

        // Return key if no translation found
        return key;
    }

    public static string[] GetAvailableLanguages()
    {
        lock (_lockObject)
        {
            return _translations.Keys.ToArray();
        }
    }

    public static string GetLanguageDisplayName(string languageCode)
    {
        return languageCode switch
        {
            "en" => "English",
            "ja" => "日本語",
            "zh" => "中文",
            "ko" => "한국어",
            "es" => "Español",
            "fr" => "Français",
            "de" => "Deutsch",
            "ru" => "Русский",
            _ => languageCode.ToUpper()
        };
    }

    public static async Task LoadTranslationsFromFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            var json = await File.ReadAllTextAsync(filePath);
            var translations = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            
            if (translations != null)
            {
                lock (_lockObject)
                {
                    foreach (var language in translations)
                    {
                        _translations[language.Key] = language.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load translations from {filePath}: {ex.Message}");
        }
    }

    private static void LoadBuiltInTranslations()
    {
        // English (default)
        _translations["en"] = new Dictionary<string, string>
        {
            // Application
            ["app.title"] = "Sttify - Speech to Text",
            ["app.ready"] = "Ready",
            ["app.listening"] = "Listening",
            ["app.processing"] = "Processing",
            ["app.error"] = "Error",
            
            // Settings Window
            ["settings.title"] = "Sttify Settings",
            ["settings.general"] = "General",
            ["settings.audio"] = "Audio",
            ["settings.engine"] = "Speech Engine",
            ["settings.output"] = "Text Output", 
            ["settings.modes"] = "Recognition Modes",
            ["settings.hotkeys"] = "Hotkeys",
            ["settings.gaming"] = "Gaming (RTSS)",
            
            // General Settings
            ["settings.start_with_windows"] = "Start with Windows",
            ["settings.show_in_tray"] = "Show in system tray",
            ["settings.start_minimized"] = "Start minimized",
            ["settings.mask_logs"] = "Mask text in logs",
            
            // Audio Settings
            ["settings.audio_device"] = "Audio Input Device",
            ["settings.sample_rate"] = "Sample Rate:",
            ["settings.channels"] = "Channels:",
            
            // Engine Settings
            ["settings.engine_type"] = "Engine Type:",
            ["settings.model_path"] = "Model Path:",
            ["settings.browse"] = "Browse...",
            ["settings.test_engine"] = "Test Engine",
            ["settings.download"] = "Download",
            ["settings.enable_punctuation"] = "Enable punctuation",
            
            // Vibe Settings
            ["settings.vibe_endpoint"] = "Vibe Endpoint:",
            ["settings.vibe_api_key"] = "API Key (Optional):",
            ["settings.vibe_model"] = "Model:",
            ["settings.vibe_enable_diarization"] = "Enable speaker diarization",
            ["settings.vibe_auto_capitalize"] = "Auto capitalize",
            ["settings.vibe_auto_punctuation"] = "Auto punctuation",
            ["settings.test_vibe"] = "Test Vibe Connection",
            ["msg.vibe_test_success"] = "Vibe connection test successful!",
            ["msg.vibe_test_failed"] = "Vibe connection test failed: {0}",
            
            // Recognition Modes
            ["mode.ptt"] = "Push-to-Talk (PTT)",
            ["mode.single"] = "Single Utterance", 
            ["mode.continuous"] = "Continuous Recognition",
            ["mode.wake_word"] = "Wake Word Activation",
            
            // Messages
            ["msg.engine_test_success"] = "Engine test successful!",
            ["msg.engine_test_failed"] = "Engine test failed: {0}",
            ["msg.download_complete"] = "Model '{0}' downloaded and configured successfully!",
            ["msg.download_failed"] = "Failed to download model: {0}",
            ["msg.invalid_model"] = "The selected directory does not contain a valid Vosk model.",
            
            // Buttons
            ["btn.ok"] = "OK",
            ["btn.cancel"] = "Cancel",
            ["btn.apply"] = "Apply & Close",
            ["btn.reset"] = "Reset to Defaults",
            
            // Status
            ["status.starting"] = "Starting download...",
            ["status.downloading"] = "Downloading",
            ["status.extracting"] = "Extracting model",
            ["status.complete"] = "Model ready"
        };

        // Japanese
        _translations["ja"] = new Dictionary<string, string>
        {
            ["app.title"] = "Sttify - 音声認識",
            ["app.ready"] = "準備完了",
            ["app.listening"] = "聞き取り中",
            ["app.processing"] = "処理中",
            ["app.error"] = "エラー",
            
            ["settings.title"] = "Sttify 設定",
            ["settings.general"] = "一般",
            ["settings.audio"] = "音声",
            ["settings.engine"] = "音声認識エンジン",
            ["settings.output"] = "テキスト出力",
            ["settings.modes"] = "認識モード",
            ["settings.hotkeys"] = "ホットキー",
            ["settings.gaming"] = "ゲーミング (RTSS)",
            
            ["settings.start_with_windows"] = "Windows起動時に開始",
            ["settings.show_in_tray"] = "システムトレイに表示",
            ["settings.start_minimized"] = "最小化して起動",
            ["settings.mask_logs"] = "ログでテキストをマスク",
            
            ["settings.audio_device"] = "音声入力デバイス",
            ["settings.sample_rate"] = "サンプルレート:",
            ["settings.channels"] = "チャンネル:",
            
            ["settings.engine_type"] = "エンジンタイプ:",
            ["settings.model_path"] = "モデルパス:",
            ["settings.browse"] = "参照...",
            ["settings.test_engine"] = "エンジンテスト",
            ["settings.download"] = "ダウンロード", 
            ["settings.enable_punctuation"] = "句読点を有効化",
            
            ["mode.ptt"] = "プッシュトゥトーク (PTT)",
            ["mode.single"] = "単発認識",
            ["mode.continuous"] = "連続認識",
            ["mode.wake_word"] = "ウェイクワード起動",
            
            ["msg.engine_test_success"] = "エンジンテストが成功しました！",
            ["msg.engine_test_failed"] = "エンジンテストが失敗しました: {0}",
            ["msg.download_complete"] = "モデル '{0}' のダウンロードと設定が完了しました！",
            ["msg.download_failed"] = "モデルのダウンロードに失敗しました: {0}",
            ["msg.invalid_model"] = "選択されたディレクトリには有効なVoskモデルが含まれていません。",
            
            ["btn.ok"] = "OK",
            ["btn.cancel"] = "キャンセル",
            ["btn.apply"] = "適用して閉じる",
            ["btn.reset"] = "デフォルトに戻す",
            
            ["status.starting"] = "ダウンロード開始中...",
            ["status.downloading"] = "ダウンロード中",
            ["status.extracting"] = "モデル展開中",
            ["status.complete"] = "モデル準備完了"
        };

        // Chinese Simplified
        _translations["zh"] = new Dictionary<string, string>
        {
            ["app.title"] = "Sttify - 语音识别",
            ["app.ready"] = "就绪",
            ["app.listening"] = "正在听取",
            ["app.processing"] = "处理中",
            ["app.error"] = "错误",
            
            ["settings.title"] = "Sttify 设置",
            ["settings.general"] = "常规",
            ["settings.audio"] = "音频",
            ["settings.engine"] = "语音引擎",
            ["settings.output"] = "文本输出",
            ["settings.modes"] = "识别模式",
            ["settings.hotkeys"] = "热键",
            ["settings.gaming"] = "游戏 (RTSS)",
            
            ["settings.start_with_windows"] = "随Windows启动",
            ["settings.show_in_tray"] = "显示在系统托盘",
            ["settings.start_minimized"] = "最小化启动",
            ["settings.mask_logs"] = "在日志中屏蔽文本",
            
            ["settings.audio_device"] = "音频输入设备",
            ["settings.sample_rate"] = "采样率:",
            ["settings.channels"] = "声道:",
            
            ["settings.engine_type"] = "引擎类型:",
            ["settings.model_path"] = "模型路径:",
            ["settings.browse"] = "浏览...",
            ["settings.test_engine"] = "测试引擎",
            ["settings.download"] = "下载",
            ["settings.enable_punctuation"] = "启用标点符号",
            
            ["mode.ptt"] = "按键通话 (PTT)",
            ["mode.single"] = "单次识别",
            ["mode.continuous"] = "连续识别",
            ["mode.wake_word"] = "唤醒词激活",
            
            ["msg.engine_test_success"] = "引擎测试成功！",
            ["msg.engine_test_failed"] = "引擎测试失败: {0}",
            ["msg.download_complete"] = "模型 '{0}' 下载和配置成功！",
            ["msg.download_failed"] = "模型下载失败: {0}",
            ["msg.invalid_model"] = "所选目录不包含有效的Vosk模型。",
            
            ["btn.ok"] = "确定",
            ["btn.cancel"] = "取消",
            ["btn.apply"] = "应用并关闭",
            ["btn.reset"] = "重置为默认值",
            
            ["status.starting"] = "开始下载...",
            ["status.downloading"] = "下载中",
            ["status.extracting"] = "正在提取模型",
            ["status.complete"] = "模型就绪"
        };
    }

    public static async Task SaveTranslationsAsync(string filePath, Dictionary<string, Dictionary<string, string>> translations)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var json = JsonSerializer.Serialize(translations, options);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save translations to {filePath}: {ex.Message}");
        }
    }
}

public class LanguageChangedEventArgs : EventArgs
{
    public string NewLanguage { get; }

    public LanguageChangedEventArgs(string newLanguage)
    {
        NewLanguage = newLanguage;
    }
}