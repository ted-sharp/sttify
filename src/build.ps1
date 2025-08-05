# Sttify Build Script
# Builds all projects in the solution with proper configuration

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    
    [Parameter(Mandatory=$false)]
    [ValidateSet("x64", "AnyCPU")]
    [string]$Platform = "x64",
    
    [Parameter(Mandatory=$false)]
    [switch]$Clean,
    
    [Parameter(Mandatory=$false)]
    [switch]$Test,
    
    [Parameter(Mandatory=$false)]
    [switch]$Package
)

$ErrorActionPreference = "Stop"

$SolutionPath = Join-Path $PSScriptRoot "sttify.sln"
$OutputPath = Join-Path $PSScriptRoot "build\$Configuration\$Platform"

Write-Host "=== Sttify Build Script ===" -ForegroundColor Green
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Platform: $Platform" -ForegroundColor Yellow
Write-Host "Output Path: $OutputPath" -ForegroundColor Yellow

# Check if Visual Studio Build Tools are available
try {
    $msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\*\Bin\MSBuild.exe | Select-Object -First 1
    if (-not $msbuildPath) {
        throw "MSBuild not found"
    }
    Write-Host "Using MSBuild: $msbuildPath" -ForegroundColor Green
} catch {
    Write-Error "Visual Studio Build Tools not found. Please install Visual Studio 2022 or Build Tools."
    exit 1
}

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning solution..." -ForegroundColor Yellow
    & $msbuildPath $SolutionPath /p:Configuration=$Configuration /p:Platform=$Platform /t:Clean /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Clean failed"
        exit 1
    }
}

# Restore NuGet packages
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore $SolutionPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "NuGet restore failed"
    exit 1
}

# Build solution
Write-Host "Building solution..." -ForegroundColor Yellow
& $msbuildPath $SolutionPath /p:Configuration=$Configuration /p:Platform=$Platform /p:OutputPath=$OutputPath /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed"
    exit 1
}

Write-Host "Build completed successfully!" -ForegroundColor Green

# Run tests if requested
if ($Test) {
    Write-Host "Running tests..." -ForegroundColor Yellow
    
    $testProjects = @(
        "tests\Sttify.Corelib.Tests\Sttify.Corelib.Tests.csproj",
        "tests\Sttify.Integration.Tests\Sttify.Integration.Tests.csproj"
    )
    
    foreach ($testProject in $testProjects) {
        $testProjectPath = Join-Path $PSScriptRoot $testProject
        if (Test-Path $testProjectPath) {
            Write-Host "Running tests for $testProject..." -ForegroundColor Cyan
            dotnet test $testProjectPath --configuration $Configuration --no-build --verbosity normal
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Tests failed for $testProject"
                exit 1
            }
        }
    }
    
    Write-Host "All tests passed!" -ForegroundColor Green
}

# Package if requested
if ($Package) {
    Write-Host "Creating package..." -ForegroundColor Yellow
    
    $PackagePath = Join-Path $PSScriptRoot "package"
    if (Test-Path $PackagePath) {
        Remove-Item $PackagePath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PackagePath -Force | Out-Null
    
    # Copy main application
    $AppSource = Join-Path $OutputPath "sttify"
    $AppDest = Join-Path $PackagePath "sttify"
    if (Test-Path $AppSource) {
        Copy-Item $AppSource $AppDest -Recurse -Force
        Write-Host "Copied main application to package" -ForegroundColor Cyan
    }
    
    
    # Copy documentation
    $DocSource = Join-Path $PSScriptRoot "doc"
    $DocDest = Join-Path $PackagePath "doc"
    if (Test-Path $DocSource) {
        Copy-Item $DocSource $DocDest -Recurse -Force
        Write-Host "Copied documentation to package" -ForegroundColor Cyan
    }
    
    Write-Host "Package created at: $PackagePath" -ForegroundColor Green
}

Write-Host "=== Build Script Completed ===" -ForegroundColor Green