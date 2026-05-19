using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace VocalPure.Services
{
    public sealed class DependencyManager
    {
        private readonly string _engineDirectory;
        private readonly string _appDirectory;
        private bool _isInitialized;

        public string FFmpegPath => Path.Combine(_engineDirectory, "ffmpeg.exe");
        public string ModelPath => Path.Combine(_engineDirectory, "Kim_Vocal_2.onnx");
        public string EngineDirectory => _engineDirectory;

        public DependencyManager()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _engineDirectory = Path.Combine(localAppData, "VocalPure", "Engine");
            _appDirectory = AppContext.BaseDirectory;
            _isInitialized = false;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                Log.Information("DependencyManager: Initializing engine at {EngineDir}", _engineDirectory);
                Directory.CreateDirectory(_engineDirectory);

                await DeployBinaryAsync("ffmpeg.exe", FFmpegPath);
                await DeployModelAsync("Kim_Vocal_2.onnx", ModelPath);

                Log.Information("DependencyManager: Stripping Mark of the Web from engine binaries.");
                await Task.Run(() => MotWStripper.StripMotWInDirectory(_engineDirectory));

                _isInitialized = true;
                Log.Information("DependencyManager: Initialization completed successfully.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DependencyManager: Failed to initialize VocalPure engine.");
                throw new InvalidOperationException($"Failed to initialize VocalPure engine at {_engineDirectory}: {ex.Message}", ex);
            }
        }

        public bool VerifyDependencies()
        {
            bool hasFfmpeg = File.Exists(FFmpegPath);
            bool hasModel = File.Exists(ModelPath);
            return hasFfmpeg && hasModel;
        }

        private async Task DeployBinaryAsync(string fileName, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                FileInfo destInfo = new FileInfo(destinationPath);
                string sourcePath = Path.Combine(_appDirectory, fileName);

                if (File.Exists(sourcePath))
                {
                    FileInfo srcInfo = new FileInfo(sourcePath);
                    if (srcInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc || srcInfo.Length != destInfo.Length)
                    {
                        await CopyFileWithRetryAsync(sourcePath, destinationPath);
                    }
                }
                return; 
            }

            string bundledPath = Path.Combine(_appDirectory, fileName);
            if (File.Exists(bundledPath))
            {
                await ValidateAndCopyFfmpegAsync(bundledPath, destinationPath);
                return;
            }

            string engineBundledPath = Path.Combine(_appDirectory, "Engine", fileName);
            if (File.Exists(engineBundledPath))
            {
                await ValidateAndCopyFfmpegAsync(engineBundledPath, destinationPath);
                return;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), ".net", AppDomain.CurrentDomain.FriendlyName);
            if (Directory.Exists(tempPath))
            {
                var foundFiles = Directory.GetFiles(tempPath, fileName, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    await ValidateAndCopyFfmpegAsync(foundFiles[0], destinationPath);
                    return;
                }
            }

            throw new FileNotFoundException($"Required binary not found: {fileName}. Please ensure the engine components are properly installed.");
        }

        // CRITICAL FIX: Validate FFmpeg integrity before blind execution
        private static async Task ValidateAndCopyFfmpegAsync(string sourcePath, string destinationPath)
        {
            FileInfo info = new FileInfo(sourcePath);
            if (info.Length < 30 * 1024 * 1024) // Usually >50MB
            {
                throw new InvalidDataException("FFmpeg executable is suspiciously small. Deployment aborted to prevent engine crash.");
            }
            await CopyFileWithRetryAsync(sourcePath, destinationPath);
        }

        private async Task DeployModelAsync(string fileName, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                bool isValid = await Task.Run(() => ValidateOnnxFile(destinationPath));
                if (isValid)
                {
                    string sourcePath = Path.Combine(_appDirectory, "Engine", fileName);
                    if (File.Exists(sourcePath))
                    {
                        FileInfo srcInfo = new FileInfo(sourcePath);
                        FileInfo destInfo = new FileInfo(destinationPath);
                        if (srcInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc || srcInfo.Length != destInfo.Length)
                        {
                            await CopyFileWithRetryAsync(sourcePath, destinationPath);
                        }
                    }
                    return;
                }
            }

            string[] searchPaths = new[]
            {
                Path.Combine(_appDirectory, "Engine", fileName),
                Path.Combine(_appDirectory, fileName),
                Path.Combine(Path.GetTempPath(), ".net", AppDomain.CurrentDomain.FriendlyName, fileName)
            };

            foreach (string searchPath in searchPaths)
            {
                if (File.Exists(searchPath))
                {
                    await CopyFileWithRetryAsync(searchPath, destinationPath);
                    return;
                }
            }

            throw new FileNotFoundException($"ONNX model not found: {fileName}. Please download the Kim_Vocal_2.onnx model and place it in the Engine folder.");
        }

        private static async Task CopyFileWithRetryAsync(string source, string destination, int maxRetries = 3)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    string? destDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                    File.Copy(source, destination, true);
                    MotWStripper.StripMotW(destination);
                    return;
                }
                catch (IOException) when (attempt < maxRetries - 1)
                {
                    await Task.Delay(500 * (attempt + 1));
                }
            }
            throw new IOException($"Failed to copy file after {maxRetries} attempts: {source} -> {destination}");
        }

        private static bool ValidateOnnxFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length > 10 * 1024 * 1024;
            }
            catch
            {
                return false;
            }
        }
    }
}
