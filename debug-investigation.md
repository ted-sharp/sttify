# Sttify 音声認識不具合調査ガイド

## 音声認識パイプライン
```
マイク → WasapiAudioCapture → AudioCapture → RecognitionSession → STTEngine → 認識結果
```

## 調査すべきデバッグメッセージの確認順序

### 1. アプリケーション開始時
以下のメッセージがVisual Studioの出力ウィンドウまたはDebugViewで確認できるか：

```
*** Starting InitializeServices ***
*** InitializeServices completed ***
*** ApplicationService Constructor Called - VERSION 2024-DEBUG ***
*** RecognitionSession Constructor - Instance ID: [番号] ***
```

### 2. 認識開始時（マイクボタンクリックまたはホットキー時）
```
*** ApplicationService.StartRecognitionAsync CALLED - VERSION 2024-DEBUG ***
*** About to call RecognitionSession.StartAsync ***
*** RecognitionSession.StartAsync ENTRY - Current State: Idle ***
*** RecognitionSession proceeding with startup ***
*** About to call _sttEngine.StartAsync() on [エンジン名] ***
*** _sttEngine.StartAsync() completed successfully ***
*** STATE CHANGE: Starting → Listening ***
```

### 3. 音声データ処理時（話している時）
```
*** PARTIAL RECOGNITION: '[認識テキスト]' (Confidence: [信頼度]) ***
*** FINAL RECOGNITION: '[最終テキスト]' (Confidence: [信頼度]) ***
```

## 問題の切り分け

### Case 1: InitializeServices でエラー
- DI Container の設定問題
- STTEngine の初期化失敗
- 設定ファイルの問題

### Case 2: StartRecognitionAsync でエラー
- RecognitionSession の状態問題
- STTEngine の開始失敗
- AudioCapture の開始失敗

### Case 3: 音声データが認識されない
- WasapiAudioCapture の OnFrame イベント未発火
- AudioLevel が 0 または NaN
- STTEngine の PushAudio 失敗

### Case 4: 認識は動作するが結果が返らない
- Voskモデルの問題（不正なパスや破損したモデル）
- Vibeサーバーの接続問題
- 言語設定の不一致

## 即座に確認すべき項目

1. **現在の設定確認**:
   ```
   %AppData%\sttify\config.json
   ```
   
2. **Voskモデルの存在確認**:
   - ModelPathに指定されたフォルダが存在するか
   - 必要ファイルが揃っているか（am/final.mdl, graph/HCLG.fst等）

3. **Vibeの場合**:
   - Vibeサーバーが起動しているか
   - エンドポイントが正しいか

4. **オーディオデバイス**:
   - 正しいマイクが選択されているか
   - デバイスが他のアプリケーションで専有されていないか

## デバッグ実行方法

Visual Studioでデバッグ実行し、出力ウィンドウの「デバッグ」タブで上記メッセージを確認してください。