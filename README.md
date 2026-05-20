# VocalPure - Professional AI Vocal Separation

VocalPure is a high-performance, memory-safe WPF desktop application built on .NET 8. It isolates vocals from audio and video files using the state-of-the-art **MDX-Net (Kim_Vocal_2)** ONNX AI model. Engineered for modern multi-core systems, it delivers production-grade audio separation with a professional, low-latency processing pipeline.

![VocalPure](VocalPure%20Setup%20maker/Assets/icon.png)

## ✨ Core Features

* **AI-Powered Separation**: Utilizes the MDX-Net neural network for industry-standard vocal and instrumental isolation.
* **GPU & CPU Acceleration**: Automatic DirectML detection for supported GPUs, with a highly optimized multi-threaded CPU fallback.
* **Dynamic CPU Scaling (Aggressive Mode)**: A UI toggle that lets users choose between normal background processing (physical cores only) or hijacking 100% of logical hyper-threads for maximum speed.
* **Lossless Video Support**: Extract vocals directly from MP4/MKV files. VocalPure preserves the original video stream losslessly and remuxes it with the newly separated audio.
* **Zero-Leak Memory Architecture**: Implements a zero-copy pipeline that processes media in 30-second chunks using an overlap-add algorithm. Bypasses ONNX memory arenas to keep RAM usage locked at ~350MB, regardless of the file length.
* **Hardware Telemetry**: Integrated real-time diagnostic logging allows for sub-millisecond tracking of Disk Read, Math, and Write operations, along with CPU and RAM utilization per chunk.
* **Security & Stability**: Features native Mark of the Web (MotW) stripping and validates FFmpeg and ONNX model integrity before execution.

---

## 🚀 Installation & Usage

**You do not need to build this application from source.** The complete, ready-to-use installer has already been published.

### Option 1: Automated Installer (Recommended)
1. Download the latest `VocalPure-Setup-v1.0.0.exe` from the [Releases](../../releases) page.
2. Run the installer. It will automatically detect your OS build, safely deploy the FFmpeg engine and ONNX models to your local AppData, and install required Visual C++ redistributables if missing.
3. Launch VocalPure from your Start Menu.

### Option 2: Portable Deployment
1. Download the portable `.zip` release.
2. Extract the folder and ensure `ffmpeg.exe` and `Kim_Vocal_2.onnx` are placed inside the `Engine/` folder.
3. Run `VocalPure.exe`.

### How to Use
1. **Configure Processing**: Check **Aggressive CPU Mode** in the app settings if you want the absolute fastest separation (Note: this will utilize maximum CPU resources).
2. **Add Media**: Drag and drop audio (MP3, WAV, FLAC) or video (MP4, MKV) files into the Drop Zone.
3. **Process**: Click **Start Processing**. The queue will automatically process jobs sequentially.
4. **Monitor**: Watch the real-time progress bar and the built-in hardware telemetry log.


## 🛠️ For Developers: Building from Source

If you wish to contribute to the codebase or modify the application, you can build the WPF executable locally. (Note: End-users should just download the installer from the Releases page).

### Prerequisites
- Visual Studio 2022 (v17.8+) or JetBrains Rider
- .NET 8 SDK
- Windows 10 Version 1809 (Build 17763) or later

### Build the Application
Clone the repository and publish the self-contained WPF executable:
```powershell
git clone [https://github.com/yourusername/vocalpure.git](https://github.com/yourusername/vocalpure.git)
cd VocalPure
dotnet restore -r win-x64
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish_output

```

*(Note: The repository also includes a `VocalPure Setup maker` directory containing Inno Setup scripts. This is only necessary if you are maintaining your own fork and wish to compile your own custom `.exe` installers).*

---

## 🧠 Technical Specifications

| Component | Technology |
| --- | --- |
| **Framework** | .NET 8 WPF (Self-contained, trimmed) |
| **Architecture** | MVVM via `CommunityToolkit.Mvvm` 8.3.2 |
| **AI Runtime** | `Microsoft.ML.OnnxRuntime.DirectML` 1.20.1 |
| **Audio I/O** | `NAudio` 2.2.1 |
| **Media Processing** | FFmpeg (Headless Inter-Process Communication) |
| **Logging** | `Serilog` (Daily Rolling File & UI binding) |

---

## 📄 License

This project is licensed under the [MIT License](https://www.google.com/search?q=LICENSE).

## 🙏 Acknowledgments

* **MDX-Net** - [kuielab/mdx-net](https://github.com/kuielab/mdx-net) for the Kim_Vocal_2 ONNX model architecture.
* **ONNX Runtime** - [microsoft/onnxruntime](https://github.com/microsoft/onnxruntime) for the inference engine.
* **NAudio** - [naudio/NAudio](https://github.com/naudio/NAudio) for the C# audio manipulation libraries.
* **FFmpeg** - [ffmpeg.org](https://ffmpeg.org/) for robust media extraction and remuxing.

```

```
