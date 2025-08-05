# sttify 要件定義・最終ドキュメント（FINAL_DOC）
> C# 13 / .NET 9 / WPF(+Forms Tray) / CommunityToolkit.MVVM / TDD 前提  
> 本ドキュメントは「要件定義 → 詳細設計 ― 方針作成 → レビュー＆ブラッシュアップ → ドキュメントアウトプット」の最終成果物です。

---

## 1. プロジェクトルール抜粋
- **アーキテクチャ**
  - プロジェクト分割：`sttify.corelib`（コア） / `sttify`（GUI常駐）。
  - STT エンジン差し替え：`ISttEngine` 抽象。初期は **Vosk**（**日本語“大”モデル**、公式から手動DL・手動配置）。
  - 出力先差し替え：`ITextOutputSink` 抽象。**既定＝SendInput**、その他 **External Process**、**Stream Output** に対応。
- **モード & 操作**
  - モード：**PTT**／**一文**／**常時**／（ウェイクワード＝「スティファイ」→一文送出）。
  - 既定ホットキー：`Win+Alt+H`（UI表示）／`Win+Alt+M`（マイク）。
- **RTSS**
  - **直接連携**。既定は**文単位更新**（テキスト長 60–80 文字目安、設定で変更可）。
- **設定・ログ**
  - 設定：`%AppData%\sttify\config.json`（ユーザー単位、階層マージ：既定→エンジン別→アプリ別）。
  - ログ：**Named Pipe 集約** → C# 側 **Serilog**（NDJSON ローリング）。  
    - 既定は**マスクなし（全文出力）**。C++ 側は **spdlog 等**で Pipe 出力。
- **対象 HW/OS**
  - Windows 10/11（x64）。  
  - HW 目標：**Ryzen AI MAX+ 935 / 128GB**（将来 Whisper/NPU は別トラックで検証）。

---

## 2. 要件ジャッジ結果
- **コア分離（corelib/GUI/TIP）**：**Yes / 高**
- **STT プラガブル（初期 Vosk）**：**Yes / 高**
- **出力：SendInput（既定）**：**Yes / 高**（仮想キーボード入力）
- **出力：外部 exe / ストリーム**：**Yes / 中**（初期は枠のみ）
- **RTSS 直接連携**：**Yes / 中**（文単位推奨）
- **ホットキー**：**Yes / 高**
- **ウェイクワード（「スティファイ」）**：**Yes / 中**（一文送出）
- **Whisper/NPU（ONNX/Vitis AI 等）**：**要検討 / 低〜中**（別トラック）

---

## 3. 変更対象ファイル・関数リスト
- **`src/sttify.corelib/`**
  - `Audio/AudioCapture.cs`：WASAPI、低遅延、`StartAsync/StopAsync/OnFrame`
  - `Engine/ISttEngine.cs`：`StartAsync/PushAudio/OnPartial/OnFinal/StopAsync`
  - `Engine/Vosk/VoskEngineAdapter.cs`：モデルロード・言語・句読点
  - `Output/ITextOutputSink.cs`：`CanSend/SendAsync`
  - `Output/SendInputSink.cs`：仮想キー送出・レート制御
  - `Output/ExternalProcessSink.cs`：引数テンプレート・スロットリング
  - `Output/StreamSink.cs`：ファイル/標準出力/共有メモリ
  - `Rtss/RtssBridge.cs`：直接 OSD（更新間引き）
  - `Session/RecognitionSession.cs`：PTT/一文/常時/ウェイク・区切り/確定
  - `Hotkey/HotkeyManager.cs`：RegisterHotKey（衝突再登録）
  - `Wake/WakeWordDetector.cs`：ウェイクワード最小
  - `Config/SettingsProvider.cs`：JSON 設定・階層マージ
  - `Diagnostics/Telemetry.cs`：最小ログ（NDJSON イベント）
- **`src/sttify/`（WPF + Forms Tray）**
  - `App.xaml/.cs`：常駐・単一インスタンス・初期化
  - `Tray/NotifyIconHost.cs`：通知領域・メニュー
  - `Views/ControlWindow.xaml`：非矩形・TopMost・フリップアニメ
  - `Views/SettingsWindow.xaml`：一般/音声/エンジン/出力/ホットキー/高度
  - `ViewModels/*`：MVVM（CommunityToolkit）
- **`src/sttify.tip/`（VC++/ATL, x64, HKCU）**
  - `Tip/TextService.cpp`：TextService/ThreadMgr/Compartment
  - `Tip/CompositionController.cpp`：composition 制御・抑制
  - `Tip/LanguageProfile.cpp`：日本語プロファイル登録
  - `Ipc/TipIpcServer.cpp`：`SendText/CanInsert/SetMode`（Named Pipe）
- **`tests/*`**（TDD）
  - `Sttify.Corelib.Tests`：区切り・確定・Vosk 単体・出力モック
  - `Sttify.Integration.Tests`：オーディオ→エンジン→出力（合成音源）

---

## 4. データ設計方針
- **保存先**：`%AppData%\sttify\config.json`（バックアップ `config.backup.json`）
- **設定スキーマ（抜粋）**
  - `engine.profile`：`"vosk"`（初期）
  - `engine.vosk`：`modelPath`, `language`, `punctuation`, `endpointSilenceMs`, `tokensPerPartial`
  - `session.mode`：`"ptt"|"single-utterance"|"continuous"|"wake"`
  - `session.boundary`：`delimiter`（句点/改行/無音ms）, `finalizeTimeoutMs`
  - `output.primary`：`"sendinput"`（初期）／`"external"`／`"stream"`
  - `output.fallbacks`：例 `["external", "stream"]`
  - `output.sendInput`：`rateLimitCps`, `commitKey`
  - `rtss`：`enabled`, `updatePerSec`, `truncateLength`
  - `hotkeys`：`toggleUi`, `toggleMic`
  - `privacy`：`maskInLogs=false`（**既定で全文出力**）
- **プロファイル**
  - 既定 → エンジン別 → アプリ別上書きの**階層マージ**。

---

## 5. 画面設計方針
- **コントロールウィンドウ**
  - 非矩形・TopMost・マイクアイコン。クリックで**フリップ**開始/停止。右クリックでメニュー。
  - 状態表示：待機／認識中／ミュート／エラー。
- **設定ウィンドウ**
  - **一般**（起動時常駐／ホットキー）
  - **音声**（デバイス選択・レベル・VAD）
  - **エンジン**（Vosk モデルパス・言語・句読点）
  - **出力**（優先＝SendInput／フォールバック＝External Process、Stream）
  - **モード**（PTT/一文/常時/ウェイク）
  - **RTSS**（有効／頻度／長さ）
  - **高度**（RDP時の自動ポリシー、ログ詳細化）

---

## 6. 処理フロー（mermaid）
```mermaid
flowchart LR
  Mic[Audio Input\n(WASAPI)] --> AC[AudioCapture]
  AC --> ENG[ISttEngine\n(Vosk)]
  ENG --> SES[RecognitionSession\n区切り/確定/モード]
  SES --> OUT[Output Sink\n(SendInput/External/Stream)]
  OUT --> APP[ターゲットアプリ]

  SES -- 文/確定ごと --> RTSS[RTSS Bridge\n(直接OSD)]
  RTSS --> OSD[OSD Overlay]

  HK[ホットキー\nWin+Alt+H/M] --> SES
  UI[Tray/Control Window] --> SES
