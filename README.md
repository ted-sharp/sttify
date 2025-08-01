# Sttify - Speech to Text Application

**Sttify** is a comprehensive speech-to-text application for Windows that provides real-time voice recognition with direct text insertion capabilities.

## Features

### 🎤 **Advanced Speech Recognition**
- **Vosk Engine Integration**: High-quality offline speech recognition
- **Japanese Language Support**: Optimized for Japanese speech recognition with large models
- **Multiple Recognition Modes**: PTT (Push-to-Talk), Single Utterance, Continuous, and Wake Word
- **Real-time Processing**: Low-latency audio capture and recognition

### 🖥️ **Smart Text Insertion**
- **TSF Text Input Processor**: Direct text insertion into any application via Windows Text Services Framework
- **SendInput Fallback**: Automatic fallback for applications that don't support TSF
- **IME Composition Awareness**: Intelligent suppression when other IMEs are active
- **RDP Support**: Optimized text insertion for Remote Desktop sessions

### 🎮 **Gaming Integration**
- **RTSS Integration**: On-screen display (OSD) overlay for gaming and full-screen applications
- **Real-time Subtitles**: Live speech-to-text display with customizable formatting
- **Performance Optimized**: Minimal impact on system performance

### ⚙️ **Flexible Configuration**
- **Hierarchical Settings**: Default → Engine-specific → Application-specific configuration
- **Hot Key Support**: Customizable global hotkeys (Win+Alt+H for UI, Win+Alt+M for microphone)
- **Audio Device Selection**: Support for multiple audio input devices
- **Privacy Controls**: Optional text masking in logs

### 🛠️ **Developer-Friendly**
- **Modular Architecture**: Pluggable engines and output sinks
- **Comprehensive Logging**: Structured JSON logging with Serilog
- **Test-Driven Development**: Extensive unit and integration tests
- **Modern Tech Stack**: C# 13, .NET 9, WPF, VC++/ATL

## System Requirements

- **OS**: Windows 10/11 (x64)
- **Runtime**: .NET 9.0 Runtime
- **Audio**: WASAPI-compatible audio input device
- **Memory**: 4GB RAM minimum, 8GB recommended
- **Storage**: 500MB for application + 1-3GB for Vosk models

## Installation

### Quick Install
1. Download the latest release from the releases page
2. Run `install.ps1` as Administrator
3. Download a Japanese Vosk model (recommended: `vosk-model-ja-0.22`)
4. Configure the model path in Sttify settings

### Manual Build
```powershell
# Clone the repository
git clone https://github.com/your-org/sttify.git
cd sttify

# Build the solution
.\build.ps1 -Configuration Release -Platform x64 -Test -Package

# Install
.\install.ps1
```

## Configuration

Sttify uses a hierarchical configuration system with settings stored in `%AppData%\sttify\config.json`.

### Basic Configuration
```json
{
  "engine": {
    "profile": "vosk",
    "vosk": {
      "modelPath": "C:\\path\\to\\vosk-model-ja-0.22",
      "language": "ja",
      "punctuation": true
    }
  },
  "session": {
    "mode": "ptt"
  },
  "output": {
    "primary": "tsf-tip",
    "fallbacks": ["sendinput"]
  },
  "hotkeys": {
    "toggleUi": "Win+Alt+H",
    "toggleMic": "Win+Alt+M"
  }
}
```

### Recognition Modes
- **PTT (Push-to-Talk)**: Manual activation via hotkey
- **Single Utterance**: Automatic start/stop for single phrases
- **Continuous**: Always-on recognition
- **Wake Word**: Voice activation with "スティファイ" (Sttify)

## Usage

### Getting Started
1. **Start Sttify**: Launch from Start Menu or system tray
2. **Configure Model**: Set the path to your downloaded Vosk model
3. **Test Recognition**: Use Win+Alt+M to toggle microphone
4. **Adjust Settings**: Fine-tune recognition and output preferences

### Hot Keys
| Key Combination | Action |
|----------------|--------|
| `Win+Alt+H` | Toggle Control Window |
| `Win+Alt+M` | Toggle Microphone |

### Control Window
- **Left Click**: Start/Stop recognition
- **Right Click**: Context menu with settings and options

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Sttify Application                     │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ WPF GUI App │  │ Corelib     │  │ TSF TIP (VC++)     │  │
│  │ (System     │  │ (Engine &   │  │ (Text Insertion)   │  │
│  │ Tray)       │  │ Processing) │  │                    │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Audio Pipeline                           │
│  WASAPI → Vosk Engine → Recognition Session → Output Sinks │
├─────────────────────────────────────────────────────────────┤
│                  External Integrations                     │
│              RTSS OSD       │       Target Applications    │
└─────────────────────────────────────────────────────────────┘
```

## Development

### Project Structure
```
sttify/
├── src/
│   ├── sttify.corelib/     # Core library (C#)
│   ├── sttify/             # WPF application
│   └── sttify.tip/         # TSF TIP (VC++)
├── tests/                  # Unit and integration tests
├── doc/                    # Documentation
├── build.ps1              # Build script
└── install.ps1            # Installation script
```

### Building
```powershell
# Debug build
.\build.ps1 -Configuration Debug

# Release build with tests and packaging
.\build.ps1 -Configuration Release -Test -Package

# Clean build
.\build.ps1 -Clean
```

### Testing
```powershell
# Run all tests
.\build.ps1 -Test

# Run specific test project
dotnet test tests/Sttify.Corelib.Tests/
```

## Troubleshooting

### Common Issues

**Voice not recognized**
- Check microphone permissions in Windows settings
- Verify Vosk model path is correct and model files exist
- Test audio input levels in Sttify settings

**Text not inserting**
- Ensure TSF TIP is properly registered (restart application as admin)
- Check if target application supports text input
- Try switching to SendInput mode in output settings

**High CPU usage**
- Reduce audio capture quality in settings
- Disable RTSS integration if not needed
- Check for model compatibility issues

### Logs
Application logs are stored in `%AppData%\sttify\logs\` in NDJSON format for structured analysis.

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Implement your changes with tests
4. Ensure all tests pass (`.\build.ps1 -Test`)
5. Submit a pull request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- **Vosk**: Open-source speech recognition toolkit
- **NAudio**: .NET audio library
- **RTSS**: RivaTuner Statistics Server for OSD integration
- **Text Services Framework**: Microsoft's text input architecture

---

**Made with ❤️ for the speech recognition community**