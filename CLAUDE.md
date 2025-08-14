# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

**Build the solution:**
```powershell
dotnet build src\sttify.sln -c Release -p:Platform=x64
```

**Build with tests:**
```powershell
dotnet build src\sttify.sln -c Release -p:Platform=x64
dotnet test src\tests\Sttify.Corelib.Tests\ -c Release
dotnet test src\tests\Sttify.Integration.Tests\ -c Release
```

**Publish application:**
```powershell
.\src\_publish.cmd
```

**Run tests individually:**
```powershell
dotnet test src\tests\Sttify.Corelib.Tests\
dotnet test src\tests\Sttify.Integration.Tests\
```

**Build individual projects:**
```powershell
dotnet build src\sttify.corelib\
dotnet build src\sttify\
```

**Clean build:**
```powershell
dotnet clean src\sttify.sln
```

## Architecture Overview

Sttify is a Windows speech-to-text application with a modular three-project architecture:

### Core Projects
- **`sttify.corelib`** (C# .NET 9): Core speech recognition, audio processing, and output handling
- **`sttify`** (WPF): GUI application with system tray integration

### Key Architectural Patterns
- **Engine abstraction**: `ISttEngine` interface allows pluggable STT engines (currently Vosk, cloud engines)
- **Output sink abstraction**: `ITextOutputSink` interface supports multiple text output methods
- **Hierarchical configuration**: Default → Engine-specific → Application-specific settings merging
- **Performance optimization**: ArrayPool, bounded queues, FFT caching, response caching throughout

### Audio Pipeline
```
WASAPI Audio Input → AudioCapture (ArrayPool) → STT Engine (Vosk) → RecognitionSession → Output Sinks
```

### Output Methods (Priority Order)
1. **SendInput** (primary): Virtual keyboard input for text insertion
2. **External Process**: Launch external applications with recognized text
3. **Stream Output**: File, stdout, or shared memory output

### Recognition Modes
- **PTT (Push-to-Talk)**: Manual activation via hotkey
- **Single Utterance**: Auto start/stop for single phrases
- **Continuous**: Always-on recognition
- **Wake Word**: Voice activation with "スティファイ" (Sttify)

## Performance Architecture

The codebase includes comprehensive optimizations implemented throughout:

### Memory Management
- **ArrayPool<T>**: Used in `AudioCapture`, `WasapiAudioCapture`, `VoiceActivityDetector` for zero-allocation audio processing
- **BoundedQueue<T>**: Custom collection in `Collections/BoundedQueue.cs` for memory-bounded audio queues
- **Object Pooling**: Complex number arrays cached in FFT operations
- **Response Caching**: `Caching/ResponseCache.cs` provides LRU cache for cloud API responses

### CPU Optimizations
- **FFT Caching**: `VoiceActivityDetector` caches twiddle factors and reduces computation frequency
- **Spectral Analysis Caching**: 50ms cache duration for voice activity detection
- **Async Processing**: Non-blocking operations throughout with proper async/await patterns
- **Batched Operations**: Telemetry I/O batching in `Diagnostics/Telemetry.cs`

### Key Optimization Classes
- `Collections/BoundedQueue<T>`: Thread-safe bounded queue with oldest-item eviction
- `Caching/ResponseCache<T>`: Generic LRU cache with TTL and content-based hashing
- `Audio/VoiceActivityDetector`: Optimized FFT with caching and ArrayPool usage
- `Diagnostics/Telemetry`: Batched logging system with 100ms intervals

## Technology Stack

- **C# 13** with **.NET 9** and nullable reference types
- **WPF** for GUI with **CommunityToolkit.MVVM**
- **Vosk** for offline speech recognition (Japanese models)
- **NAudio** for WASAPI audio capture
- **Serilog** for structured JSON logging (NDJSON format)
- **xUnit** with **Moq** for testing

## Code Quality Patterns

### Test Coverage Strategy
- Use `[ExcludeFromCodeCoverage]` attribute for:
  - Simple data classes (DTOs, settings, EventArgs)
  - System integration code (WASAPI, Win32 APIs)
  - External API wrappers (cloud engines)
- Focus testing on business logic in core processing classes
- Integration tests for end-to-end workflows

### Error Handling
- Structured error handling with `Diagnostics/ErrorHandling.cs`
- Comprehensive telemetry logging for all error scenarios
- Automatic recovery mechanisms for transient errors (audio devices, cloud APIs)
- Error categories: Transient, Configuration, Hardware, Integration, Critical

### Performance Guidelines
- Always use ArrayPool<T> for audio buffer management
- Implement bounded queues for any continuous data streams
- Cache expensive computations (FFT, configuration, API responses)
- Use batched I/O operations for telemetry and configuration
- Prefer async/await over Thread.Sleep for delays

## Key Configuration

- **Settings location**: `%AppData%\sttify\config.json`
- **Log location**: `%AppData%\sttify\logs\`
- **Default hotkeys**: Win+Alt+H (UI), Win+Alt+M (microphone)
- **Platform**: Windows 10/11 x64 only

## Development Notes

- The build script requires Visual Studio Build Tools or Visual Studio 2022
- Japanese Vosk models must be manually downloaded and configured
- The application supports RDP scenarios with automatic fallback to SendInput

- All audio processing uses ArrayPool to avoid allocations
- Cloud engines include response caching with SHA256-based keys
- Configuration system includes file watching for real-time updates without polling

## Important Code Patterns

### Audio Processing
- Always use `ArrayPool<byte>.Shared` for audio buffers
- Audio data is passed as `ReadOnlySpan<byte>` for efficiency
- Voice activity detection uses cached FFT with 50ms update intervals

### Output Sinks
- Implement `ITextOutputSink` for new output methods
- Use async/await patterns throughout
- SendInput sink uses async delays instead of Thread.Sleep

### Engine Integration
- Implement `ISttEngine` for new recognition engines
- Use bounded queues for audio data management
- Cloud engines include automatic response caching
- Handle both streaming and batch recognition patterns

### Configuration Management
- Settings use hierarchical merging (default → engine → application)
- FileSystemWatcher enables real-time configuration updates
- Settings classes marked with `[ExcludeFromCodeCoverage]`
- JSON serialization with camelCase naming policy
