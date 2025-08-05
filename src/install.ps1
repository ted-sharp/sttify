# Sttify Installation Script
# Installs Sttify application and registers TSF TIP

param(
    [Parameter(Mandatory=$false)]
    [switch]$Uninstall,
    
    [Parameter(Mandatory=$false)]
    [switch]$Force,
    
    [Parameter(Mandatory=$false)]
    [string]$InstallPath = "$env:LOCALAPPDATA\Sttify"
)

$ErrorActionPreference = "Stop"

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")


Write-Host "=== Sttify Installation Script ===" -ForegroundColor Green
Write-Host "Install Path: $InstallPath" -ForegroundColor Yellow

if ($Uninstall) {
    Write-Host "=== UNINSTALLING STTIFY ===" -ForegroundColor Red
    
    # Stop application if running
    $processes = Get-Process -Name "sttify" -ErrorAction SilentlyContinue
    if ($processes) {
        Write-Host "Stopping running Sttify processes..." -ForegroundColor Yellow
        $processes | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
    
    
    # Remove installation directory
    if (Test-Path $InstallPath) {
        Write-Host "Removing installation directory..." -ForegroundColor Yellow
        Remove-Item $InstallPath -Recurse -Force
        Write-Host "Installation directory removed" -ForegroundColor Green
    }
    
    # Remove from startup (if exists)
    $startupPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Sttify.lnk"
    if (Test-Path $startupPath) {
        Remove-Item $startupPath -Force
        Write-Host "Removed from startup" -ForegroundColor Green
    }
    
    Write-Host "=== UNINSTALLATION COMPLETED ===" -ForegroundColor Green
    exit
}

# Installation process
Write-Host "=== INSTALLING STTIFY ===" -ForegroundColor Green

# Check if already installed
if ((Test-Path $InstallPath) -and (-not $Force)) {
    Write-Host "Sttify is already installed at $InstallPath" -ForegroundColor Yellow
    $choice = Read-Host "Do you want to update the installation? (y/N)"
    if ($choice -ne "y" -and $choice -ne "Y") {
        Write-Host "Installation cancelled" -ForegroundColor Yellow
        exit
    }
}

# Create installation directory
Write-Host "Creating installation directory..." -ForegroundColor Yellow
if (Test-Path $InstallPath) {
    Remove-Item $InstallPath -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

# Copy files from package directory
$packagePath = Join-Path $PSScriptRoot "package"
if (-not (Test-Path $packagePath)) {
    Write-Error "Package directory not found. Please run build.ps1 with -Package parameter first."
    exit 1
}

Write-Host "Copying application files..." -ForegroundColor Yellow
Copy-Item "$packagePath\*" $InstallPath -Recurse -Force


# Create startup shortcut
Write-Host "Creating startup shortcut..." -ForegroundColor Yellow
$startupPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$shortcutPath = Join-Path $startupPath "Sttify.lnk"
$targetPath = Join-Path $InstallPath "sttify\sttify.exe"

if (Test-Path $targetPath) {
    $WshShell = New-Object -ComObject WScript.Shell
    $Shortcut = $WshShell.CreateShortcut($shortcutPath)
    $Shortcut.TargetPath = $targetPath
    $Shortcut.WorkingDirectory = Split-Path $targetPath
    $Shortcut.Description = "Sttify - Speech to Text Application"
    $Shortcut.Save()
    Write-Host "Startup shortcut created" -ForegroundColor Green
} else {
    Write-Warning "Main executable not found at $targetPath"
}

# Create desktop shortcut
Write-Host "Creating desktop shortcut..." -ForegroundColor Yellow
$desktopPath = [Environment]::GetFolderPath("Desktop")
$desktopShortcutPath = Join-Path $desktopPath "Sttify.lnk"

if (Test-Path $targetPath) {
    $WshShell = New-Object -ComObject WScript.Shell
    $Shortcut = $WshShell.CreateShortcut($desktopShortcutPath)
    $Shortcut.TargetPath = $targetPath
    $Shortcut.WorkingDirectory = Split-Path $targetPath
    $Shortcut.Description = "Sttify - Speech to Text Application"
    $Shortcut.Save()
    Write-Host "Desktop shortcut created" -ForegroundColor Green
}

# Set up initial configuration
Write-Host "Setting up initial configuration..." -ForegroundColor Yellow
$configDir = "$env:APPDATA\sttify"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

Write-Host "=== INSTALLATION COMPLETED ===" -ForegroundColor Green
Write-Host "Sttify has been installed to: $InstallPath" -ForegroundColor Cyan
Write-Host "You can start Sttify from the Start Menu or Desktop shortcut." -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Download and install a Vosk model for Japanese recognition" -ForegroundColor White
Write-Host "2. Configure the model path in Sttify settings" -ForegroundColor White
Write-Host "3. Start using voice recognition!" -ForegroundColor White