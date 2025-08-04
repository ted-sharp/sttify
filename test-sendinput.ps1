# SendInput テスト用スクリプト
Write-Host "🧪 SendInput Test - Outside Visual Studio Debugger" -ForegroundColor Cyan
Write-Host ""

# ビルドされた実行ファイルのパスを確認
$exePath = "C:\git\git-vo\sttify\src\sttify\bin\Release\net9.0-windows\sttify.exe"

if (Test-Path $exePath) {
    Write-Host "✅ Found executable: $exePath" -ForegroundColor Green
    
    Write-Host ""
    Write-Host "🚀 Starting Sttify WITHOUT debugger attachment..." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "📋 Test Steps:" -ForegroundColor White
    Write-Host "1. Open Notepad (standard Windows notepad.exe)"
    Write-Host "2. Click in the text area to focus"
    Write-Host "3. Press Win+Shift+F3 to test SendInput"
    Write-Host "4. Check if text appears in Notepad"
    Write-Host "5. Check console output for detailed logs"
    Write-Host ""
    Write-Host "🔍 What to look for in logs:" -ForegroundColor Cyan
    Write-Host "• INPUT structure size: should be ~28-32 bytes"
    Write-Host "• SendInput SUCCESS messages instead of FAILED"
    Write-Host "• Target window should be 'Untitled - Notepad' (not Notepad 2e)"
    Write-Host ""
    
    # 標準メモ帳を開く
    Write-Host "🗒️  Opening standard Notepad for testing..." -ForegroundColor Green
    Start-Process "notepad.exe"
    Start-Sleep 2
    
    Write-Host "▶️  Launching Sttify..." -ForegroundColor Magenta
    & $exePath
    
} else {
    Write-Host "❌ Executable not found at: $exePath" -ForegroundColor Red
    Write-Host ""
    Write-Host "🔧 Build the project first:" -ForegroundColor Yellow
    Write-Host "dotnet build src/sttify/ --configuration Release"
}

Write-Host ""
Write-Host "Press any key to continue..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")