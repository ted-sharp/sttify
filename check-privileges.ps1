# Sttify Privilege Checker Script
Write-Host "🔍 Checking Current User Privileges..." -ForegroundColor Cyan

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ""
if ($isAdmin) {
    Write-Host "❌ STATUS: ADMINISTRATOR PRIVILEGES DETECTED" -ForegroundColor Red
    Write-Host ""
    Write-Host "⚠️  PROBLEM:" -ForegroundColor Yellow
    Write-Host "   • Text input will be BLOCKED to most applications"
    Write-Host "   • Windows UIPI security prevents elevated → normal input"
    Write-Host "   • SendInput, Ctrl+V, and keyboard shortcuts will fail"
    Write-Host ""
    Write-Host "✅ SOLUTION:" -ForegroundColor Green
    Write-Host "   1. Close this PowerShell/CMD window"
    Write-Host "   2. Close Visual Studio if running as admin"
    Write-Host "   3. Open normal (non-admin) PowerShell/CMD"
    Write-Host "   4. Run Sttify from there, OR"
    Write-Host "   5. Use 'Restart Without Administrator' in Sttify Settings"
    Write-Host ""
    Write-Host "🎯 RECOMMENDATION: Run Sttify with NORMAL user privileges" -ForegroundColor Magenta
} else {
    Write-Host "✅ STATUS: NORMAL USER PRIVILEGES" -ForegroundColor Green
    Write-Host ""
    Write-Host "🎉 EXCELLENT:" -ForegroundColor Green
    Write-Host "   • Text input will work with ALL applications"
    Write-Host "   • No UIPI blocking issues"
    Write-Host "   • Optimal security and compatibility"
    Write-Host ""
    Write-Host "🚀 You're good to go!" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "📋 Current Process Info:" -ForegroundColor White
Write-Host "   User: $($env:USERNAME)"
Write-Host "   Domain: $($env:USERDOMAIN)"
Write-Host "   Elevated: $isAdmin"
Write-Host ""

# Check for Visual Studio processes
$vsProcesses = Get-Process | Where-Object { $_.ProcessName -match "devenv|Visual Studio" } | Select-Object ProcessName, Id
if ($vsProcesses) {
    Write-Host "🔧 Visual Studio Processes Found:" -ForegroundColor Yellow
    $vsProcesses | Format-Table -AutoSize
    Write-Host "   If VS is running as admin, Sttify will inherit elevation." -ForegroundColor Yellow
}

Write-Host "Press any key to continue..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")