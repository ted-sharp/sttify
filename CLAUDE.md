# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

**Build the solution:**
```powershell
.\src\build.ps1 -Configuration Release -Platform x64
```

**Build with tests:**
```powershell
.\src\build.ps1 -Configuration Release -Platform x64 -Test
```

**Build, test, and package:**
```powershell
.\src\build.ps1 -Configuration Release -Platform x64 -Test -Package
```

**Clean build:**
```powershell
.\src\build.ps1 -Clean
```

**Run tests individually:**
```powershell
dotnet test src\tests\Sttify.Corelib.Tests\
dotnet test src\tests\Sttify.Integration.Tests\
```

**Install after build:**
```powershell
.\src\install.ps1
```

## Architecture Overview

Sttify is a Windows speech-to-text application with a modular three-project architecture:

### Core Projects
- **`sttify.corelib`** (C# .NET 9): Core speech recognition, audio processing, and output handling
- **`sttify`** (WPF): GUI application with system tray integration
- **`sttify.tip`** (VC++/ATL x64): Text Services Framework Text Input Processor for direct text insertion

### Key Architectural Patterns
- **Engine abstraction**: `ISttEngine` interface allows pluggable STT engines (currently Vosk)
- **Output sink abstraction**: `ITextOutputSink` interface supports multiple text output methods
- **Hierarchical configuration**: Default → Engine-specific → Application-specific settings merging
- **IPC communication**: Named pipes between C# corelib and VC++ TSF TIP

### Audio Pipeline
```
WASAPI Audio Input → AudioCapture → STT Engine (Vosk) → RecognitionSession → Output Sinks
```

### Output Methods (Priority Order)
1. **TSF TIP** (primary): Direct text insertion via Windows Text Services Framework
2. **SendInput** (fallback): Virtual keyboard input when TSF unavailable
3. **External Process**: Launch external applications with recognized text
4. **Stream Output**: File, stdout, or shared memory output
5. **RTSS Integration**: On-screen display overlay for gaming

### Recognition Modes
- **PTT (Push-to-Talk)**: Manual activation via hotkey
- **Single Utterance**: Auto start/stop for single phrases
- **Continuous**: Always-on recognition
- **Wake Word**: Voice activation with "スティファイ" (Sttify)

## Technology Stack

- **C# 13** with **.NET 9** and nullable reference types
- **WPF** for GUI with **CommunityToolkit.MVVM**
- **VC++/ATL** for TSF TIP implementation (x64 only)
- **Vosk** for offline speech recognition (Japanese models)
- **NAudio** for WASAPI audio capture
- **Serilog** for structured JSON logging (NDJSON format)
- **xUnit** with **Moq** for testing

## Key Configuration

- **Settings location**: `%AppData%\sttify\config.json`
- **Default hotkeys**: Win+Alt+H (UI), Win+Alt+M (microphone)
- **Platform**: Windows 10/11 x64 only
- **TSF TIP registration**: Per-user (HKCU) registration, requires admin for install

## Development Notes

- The build script requires Visual Studio Build Tools or Visual Studio 2022
- TSF TIP component must be built as x64 and registered for text insertion to work
- Japanese Vosk models must be manually downloaded and configured
- The application supports RDP scenarios with automatic fallback to SendInput
- RTSS integration provides real-time subtitle overlay for gaming applications