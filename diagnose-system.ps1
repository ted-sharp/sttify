# Sttify システム診断スクリプト
Write-Host "🔍 System Diagnostic for SendInput Issues" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor White
Write-Host ""

# 1. Windows Version
Write-Host "📋 Windows Version:" -ForegroundColor Yellow
$winver = Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, WindowsBuildLabEx
Write-Host "   Product: $($winver.WindowsProductName)"
Write-Host "   Version: $($winver.WindowsVersion)"
Write-Host "   Build: $($winver.WindowsBuildLabEx)"
Write-Host ""

# 2. User Account Control Settings
Write-Host "🔐 User Account Control:" -ForegroundColor Yellow
try {
    $uacLevel = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name ConsentPromptBehaviorUser -ErrorAction SilentlyContinue
    $uacEnabled = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name EnableLUA -ErrorAction SilentlyContinue
    Write-Host "   UAC Enabled: $($uacEnabled.EnableLUA)"
    Write-Host "   Consent Prompt Level: $($uacLevel.ConsentPromptBehaviorUser)"
} catch {
    Write-Host "   Unable to check UAC settings" -ForegroundColor Red
}
Write-Host ""

# 3. Windows Defender Status
Write-Host "🛡️  Windows Defender:" -ForegroundColor Yellow
try {
    $defender = Get-MpComputerStatus -ErrorAction SilentlyContinue
    Write-Host "   Real-time Protection: $($defender.RealTimeProtectionEnabled)"
    Write-Host "   Behavior Monitoring: $($defender.BehaviorMonitorEnabled)"
    Write-Host "   Script Scanning: $($defender.ScriptScanningEnabled)"
} catch {
    Write-Host "   Unable to check Windows Defender status" -ForegroundColor Red
}
Write-Host ""

# 4. Developer Mode
Write-Host "👨‍💻 Developer Mode:" -ForegroundColor Yellow
try {
    $devMode = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
    Write-Host "   Developer Mode: $($devMode.AllowDevelopmentWithoutDevLicense -eq 1)"
} catch {
    Write-Host "   Unable to check Developer Mode" -ForegroundColor Red
}
Write-Host ""

# 5. Application Guard Status
Write-Host "🔒 Windows Defender Application Guard:" -ForegroundColor Yellow
try {
    $wdag = Get-WindowsOptionalFeature -Online -FeatureName Windows-Defender-ApplicationGuard -ErrorAction SilentlyContinue
    Write-Host "   Status: $($wdag.State)"
} catch {
    Write-Host "   Unable to check Application Guard" -ForegroundColor Red
}
Write-Host ""

# 6. Input Security Policies
Write-Host "⌨️  Input Security Policies:" -ForegroundColor Yellow
try {
    # Check for SendInput restrictions
    $inputPolicies = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Keyboard Layout" -ErrorAction SilentlyContinue
    Write-Host "   Keyboard Layout policies found: $($inputPolicies -ne $null)"
    
    # Check for UIPI settings
    $uipiSettings = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name FilterAdministratorToken -ErrorAction SilentlyContinue
    Write-Host "   UIPI Filter Admin Token: $($uipiSettings.FilterAdministratorToken)"
} catch {
    Write-Host "   Unable to check input policies" -ForegroundColor Red
}
Write-Host ""

# 7. Running Processes that might interfere
Write-Host "🖥️  Potentially Interfering Processes:" -ForegroundColor Yellow
$interferingProcesses = @("devenv", "msvsmon", "PerfWatson2", "ServiceHub", "Microsoft.ServiceHub", "VsDebugConsole")
foreach ($proc in $interferingProcesses) {
    $running = Get-Process -Name $proc -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "   ⚠️  $proc is running (PID: $($running.Id))" -ForegroundColor Orange
    }
}
Write-Host ""

# 8. .NET Debugging Settings
Write-Host "🐛 .NET Debugging Environment:" -ForegroundColor Yellow
Write-Host "   DOTNET_EnableWriteXorExecute: $($env:DOTNET_EnableWriteXorExecute)"
Write-Host "   DOTNET_JitDisableInlining: $($env:DOTNET_JitDisableInlining)"
Write-Host "   COMPLUS_JitDisableInlining: $($env:COMPLUS_JitDisableInlining)"
Write-Host ""

# 9. Security Software Detection
Write-Host "🛡️  Security Software:" -ForegroundColor Yellow
$securitySoftware = Get-WmiObject -Class Win32_Product | Where-Object { 
    $_.Name -like "*antivirus*" -or 
    $_.Name -like "*security*" -or 
    $_.Name -like "*defender*" -or
    $_.Name -like "*norton*" -or
    $_.Name -like "*mcafee*" -or
    $_.Name -like "*kaspersky*" -or
    $_.Name -like "*trend*"
} | Select-Object Name, Version
if ($securitySoftware) {
    foreach ($software in $securitySoftware) {
        Write-Host "   📦 $($software.Name) v$($software.Version)"
    }
} else {
    Write-Host "   No additional security software detected"
}
Write-Host ""

Write-Host "🎯 RECOMMENDATIONS:" -ForegroundColor Green
Write-Host "1. If Windows Defender Real-time Protection is ON, try temporarily disabling it"
Write-Host "2. If Developer Mode is OFF, try enabling it in Windows Settings"
Write-Host "3. If Visual Studio processes are running, close them completely"
Write-Host "4. Try running Sttify from a non-development environment"
Write-Host ""

Write-Host "Press any key to continue..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")