using System.Globalization;
using System.Text.Json;

namespace Sttify.Corelib.Localization;

public static class LocalizationManager
{
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new();
    private static string _currentLanguage = "en";
    private static readonly object LockObject = new();

    static LocalizationManager()
    {
        LoadBuiltInTranslations();

        // Set default language based on system culture
        var systemLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (Translations.ContainsKey(systemLanguage))
        {
            _currentLanguage = systemLanguage;
        }
    }

    public static string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            lock (LockObject)
            {
                if (Translations.ContainsKey(value))
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
        lock (LockObject)
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
        if (Translations.TryGetValue(_currentLanguage, out var languageDict))
        {
            if (languageDict.TryGetValue(key, out var translation))
            {
                return translation;
            }
        }

        // Fallback to English
        if (_currentLanguage != "en" && Translations.TryGetValue("en", out var englishDict))
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
        lock (LockObject)
        {
            return Translations.Keys.ToArray();
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
                lock (LockObject)
                {
                    foreach (var language in translations)
                    {
                        Translations[language.Key] = language.Value;
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
        Translations["en"] = new Dictionary<string, string>
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

            // General Settings
            ["settings.start_with_windows"] = "Start with Windows",
            ["settings.show_in_tray"] = "Show in system tray",
            ["settings.start_minimized"] = "Start minimized",
            ["settings.mask_logs"] = "Mask text in logs",

            // Audio Settings
            ["settings.audio_device"] = "Audio Input Device",
            ["settings.sample_rate"] = "Sample Rate:",
            ["settings.channels"] = "Channels:",
            ["settings.audio_quality"] = "Audio Quality",
            ["settings.sample_rate_desc"] = "Higher sample rates provide better quality but use more processing power",
            ["settings.channels_desc"] = "Mono is recommended for speech recognition",
            ["settings.sample_rate_16k"] = "16000 Hz (Recommended for speech)",
            ["settings.sample_rate_22k"] = "22050 Hz",
            ["settings.sample_rate_44k"] = "44100 Hz (High quality)",
            ["settings.channels_mono"] = "Mono (1 channel - Recommended)",
            ["settings.channels_stereo"] = "Stereo (2 channels)",

            // Recognition Modes
            ["settings.recognition_mode"] = "Recognition Mode",
            ["settings.mode_ptt"] = "Push-to-Talk (PTT)",
            ["settings.mode_ptt_desc"] = "Press and hold hotkey to activate speech recognition",
            ["settings.mode_single"] = "Single Utterance",
            ["settings.mode_single_desc"] = "Recognize one phrase at a time, automatically stopping after silence",
            ["settings.mode_continuous"] = "Continuous Recognition",
            ["settings.mode_continuous_desc"] = "Always-on recognition, continuously processing speech",
            ["settings.mode_wakeword"] = "Wake Word Activation",
            ["settings.mode_wakeword_desc"] = "Start recognition when 'スティファイ' (Sttify) is detected",
            ["settings.silence_detection"] = "Silence Detection",
            ["settings.endpoint_silence"] = "Endpoint Silence (milliseconds):",

            // Context Menu
            ["menu.start_recognition"] = "Start Recognition",
            ["menu.stop_recognition"] = "Stop Recognition",
            ["menu.settings"] = "Settings...",
            ["menu.hide"] = "Hide",
            ["menu.exit"] = "Exit",
            ["menu.service_unavailable"] = "Service Not Available",

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
        Translations["ja"] = new Dictionary<string, string>
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

            ["settings.start_with_windows"] = "Windows起動時に開始",
            ["settings.show_in_tray"] = "システムトレイに表示",
            ["settings.start_minimized"] = "最小化して起動",
            ["settings.mask_logs"] = "ログでテキストをマスク",

            // Audio Settings (Japanese)
            ["settings.audio_device"] = "音声入力デバイス",
            ["settings.sample_rate"] = "サンプルレート:",
            ["settings.channels"] = "チャンネル:",
            ["settings.audio_quality"] = "音声品質",
            ["settings.sample_rate_desc"] = "高いサンプルレートは品質を向上させますが、より多くの処理能力を使用します",
            ["settings.channels_desc"] = "音声認識にはモノラルが推奨されます",
            ["settings.sample_rate_16k"] = "16000 Hz (音声認識推奨)",
            ["settings.sample_rate_22k"] = "22050 Hz",
            ["settings.sample_rate_44k"] = "44100 Hz (高品質)",
            ["settings.channels_mono"] = "モノラル (1チャンネル - 推奨)",
            ["settings.channels_stereo"] = "ステレオ (2チャンネル)",

            // Recognition Modes (Japanese)
            ["settings.recognition_mode"] = "認識モード",
            ["settings.mode_ptt"] = "プッシュ・トゥ・トーク (PTT)",
            ["settings.mode_ptt_desc"] = "ホットキーを押し続けて音声認識を有効にします",
            ["settings.mode_single"] = "単発認識",
            ["settings.mode_single_desc"] = "一度に一つのフレーズを認識し、無音後に自動停止",
            ["settings.mode_continuous"] = "連続認識",
            ["settings.mode_continuous_desc"] = "常時オンの認識、継続的に音声を処理",
            ["settings.mode_wakeword"] = "ウェイクワード起動",
            ["settings.mode_wakeword_desc"] = "'スティファイ' (Sttify) が検出されたときに認識を開始",
            ["settings.silence_detection"] = "無音検出",
            ["settings.endpoint_silence"] = "終了無音時間 (ミリ秒):",

            // Context Menu (Japanese)
            ["menu.start_recognition"] = "認識開始",
            ["menu.stop_recognition"] = "認識停止",
            ["menu.settings"] = "設定...",
            ["menu.hide"] = "非表示",
            ["menu.exit"] = "終了",
            ["menu.service_unavailable"] = "サービス利用不可",

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
        Translations["zh"] = new Dictionary<string, string>
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
    public LanguageChangedEventArgs(string newLanguage)
    {
        NewLanguage = newLanguage;
    }

    public string NewLanguage { get; }
}
