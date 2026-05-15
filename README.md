# VocalPure - AI Vocal Separation

A high-performance WPF desktop application that isolates vocals from audio and video files using the **MDX-Net** (Kim_Vocal_2) ONNX AI model. Built on .NET 8 with GPU acceleration support via DirectML.

## Features

- **AI-Powered Separation** - Uses state-of-the-art MDX-Net neural network for high-quality vocal/instrumental separation
- **GPU Acceleration** - Automatic DirectML detection with fallback to OpenVINO and CPU execution
- **Video Support** - Extract vocals from video files while preserving original video quality losslessly
- **Chunked Processing** - Memory-safe 30-second chunking prevents OutOfMemoryException on long files
- **Modern Dark UI** - Clean, high-contrast interface with drag-and-drop support and real-time progress
- **Professional Deployment** - Self-contained single-file executable with automated CI/CD via GitHub Actions

## Requirements

- **OS**: Windows 10 version 1809 (17763) or later, Windows 11
- **Runtime**: Self-contained (no .NET runtime required)
- **GPU** (Optional): DirectX 12 compatible GPU for acceleration (NVIDIA, AMD, or Intel)
- **RAM**: 4GB minimum, 8GB recommended for large files

## Installation

### Option 1: Installer (Recommended)

1. Download `VocalPure-Setup-v1.0.0.exe` from [Releases](../../releases)
2. Run the installer with administrator privileges
3. Engine components (FFmpeg, ONNX model) are automatically deployed to `%LOCALAPPDATA%\VocalPure\Engine`
4. Launch VocalPure from the Start Menu or Desktop

### Option 2: Portable

1. Download `VocalPure-v1.0.0-win-x64.zip` from [Releases](../../releases)
2. Extract to any directory
3. Run `VocalPure.exe`
4. On first launch, the application will extract engine components to `%LOCALAPPDATA%\VocalPure\Engine`

## Usage

### Basic Workflow

1. **Open VocalPure** - The app checks for engine dependencies on startup
2. **Add Media** - Drag and drop an audio/video file, or click "Browse Files"
3. **Processing** - The app automatically:
   - Extracts audio to Float32 PCM WAV (44100Hz, Stereo)
   - Loads the MDX-Net ONNX model (with GPU auto-detection)
   - Processes audio in 30-second chunks with overlap-add crossfading
   - Merges output (video copy for video inputs, WAV/MP3 for audio inputs)
4. **Output** - Processed file is saved alongside the original

### Output Formats

| Input Type | Output Format | Video Quality |
|------------|---------------|---------------|
| Audio (MP3, WAV, etc.) | WAV or MP3 (320kbps) | N/A |
| Video (MP4, MKV, etc.) | MP4 with AAC audio | Lossless copy |

### Cancellation

Click **"Cancel Processing"** at any time. The app will:
- Kill the active FFmpeg process immediately
- Stop the ONNX inference loop
- Clean up all temporary files in `%TEMP%\VocalPure`

## Architecture

### Project Structure

```
VocalPure/
├── Models/
│   ├── ProcessingPhase.cs          # Processing phase enum
│   ├── SeparationJob.cs            # Job model with INPC
│   └── InferenceSettings.cs        # ONNX engine configuration
├── ViewModels/
│   ├── MainViewModel.cs            # Main UI logic & orchestration
│   └── RelayCommand.cs             # ICommand implementations
├── Converters/
│   └── BooleanToVisibilityConverter.cs  # XAML value converters
├── Services/
│   ├── DependencyManager.cs        # Binary extraction & verification
│   ├── MotWStripper.cs             # Zone.Identifier ADS removal
│   ├── MediaProcessor.cs           # Headless FFmpeg wrapper
│   ├── AudioChunker.cs             # WAV chunking with overlap-add
│   └── InferenceEngine.cs          # ONNX DirectML/CPU inference
├── MainWindow.xaml                 # Dark theme UI
└── App.xaml                        # Global styles & resources
```

### Processing Pipeline

```
Input File
    |
    v
[Phase 1: FFmpeg Extraction] --> Float32 PCM WAV (44.1kHz Stereo)
    |
    v
[Phase 2: ONNX Inference] --> Chunks -> MDX-Net Model -> Vocal Stems
    |                            (30s overlap-add)
    v
[Phase 3: FFmpeg Merge] --> Output File (Video/Audio)
    |
    v
[Phase 4: Cleanup] --> Delete temp files
```

### Chunked Inference

To prevent memory overflow on long audio files, VocalPure implements overlap-add processing:

- **Chunk Size**: 30 seconds (1,323,000 stereo samples)
- **Overlap**: 1 second on each side (44,100 samples)
- **Windowing**: Hann window crossfade at chunk boundaries
- **Memory**: ~100MB per chunk regardless of file length

## Building from Source

### Prerequisites

- Visual Studio 2022 17.8+ or JetBrains Rider
- .NET 8 SDK
- Windows SDK

### Build Steps

```powershell
# Clone repository
git clone https://github.com/yourusername/vocalpure.git
cd VocalPure

# Restore packages
dotnet restore --runtime win-x64

# Build release
dotnet publish -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true

# Output: bin/Release/net8.0-windows/win-x64/publish/VocalPure.exe
```

### Adding the ONNX Model

Place `Kim_Vocal_2.onnx` in the `Engine/` directory before building:

```
VocalPure/
├── Engine/
│   ├── ffmpeg.exe          # Download from https://ffmpeg.org/download.html
│   └── Kim_Vocal_2.onnx    # Download from MDX-Net model repository
```

**FFmpeg**: Download the Windows builds from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) or [BtbN](https://github.com/BtbN/FFmpeg-Builds/releases) and extract `ffmpeg.exe`.

**Model**: The Kim_Vocal_2 model is available from the [MDX-Net](https://github.com/kuielab/mdx-net) project. Place the `.onnx` file in the Engine directory.

## CI/CD

The included GitHub Actions workflow (`build.yml`) automates:

1. **Restore** - NuGet package caching for fast builds
2. **Build** - Self-contained single-file x64 executable
3. **Test** - Runs unit tests (if present)
4. **Analyze** - `dotnet format` and vulnerability audit
5. **Release** - Automatic GitHub Release creation with ZIP archive (on tag push)

### Triggering a Release

```bash
# Create and push a version tag
git tag -a v1.0.0 -m "Release v1.0.0"
git push origin v1.0.0
```

The workflow will automatically build and create a GitHub Release.

## Technical Specifications

| Component | Technology |
|-----------|------------|
| Framework | .NET 8 WPF |
| Pattern | MVVM |
| AI Runtime | Microsoft.ML.OnnxRuntime.DirectML 1.16.3 |
| Audio I/O | NAudio 2.2.1 |
| Media Processing | FFmpeg (headless) |
| MVVM Toolkit | Microsoft.Toolkit.Mvvm 7.1.2 |
| Deployment | Self-contained, single-file, trimmed |

### Security Features

- **Mark of the Web (MotW) stripping** via `Zone.Identifier` Alternate Data Stream removal
- All binaries extracted to user-local `%LOCALAPPDATA%` (no admin required after install)
- FFmpeg runs headless with no shell access (`CreateNoWindow`, `UseShellExecute=false`)
- Full stream redirection for process isolation

## License

[MIT License](LICENSE.txt)

## Acknowledgments

- **MDX-Net** - [kuielab/mdx-net](https://github.com/kuielab/mdx-net) for the Kim_Vocal_2 model
- **ONNX Runtime** - [microsoft/onnxruntime](https://github.com/microsoft/onnxruntime) for inference
- **NAudio** - [naudio/NAudio](https://github.com/naudio/NAudio) for audio manipulation
- **FFmpeg** - [ffmpeg.org](https://ffmpeg.org/) for media processing
