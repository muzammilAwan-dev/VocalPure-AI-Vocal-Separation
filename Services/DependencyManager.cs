using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Serilog;

namespace VocalPure.Services
{
    /// <summary>
    /// Manages external binary dependencies (FFmpeg, ONNX models) by ensuring they are deployed
    /// to %LOCALAPPDATA%\VocalPure\Engine. Handles first-run extraction from bundled resources
    /// and strips Mark of the Web from all binaries to prevent execution blocks.
    /// </summary>
    public sealed class DependencyManager
    {
        private readonly string _engineDirectory;
        private readonly string _appDirectory;
        private bool _isInitialized;

        /// <summary>
        /// Path to the FFmpeg executable in the engine directory.
        /// </summary>
        public string FFmpegPath => Path.Combine(_engineDirectory, "ffmpeg.exe");

        /// <summary>
        /// Path to the ONNX model file in the engine directory.
        /// </summary>
        public string ModelPath => Path.Combine(_engineDirectory, "Kim_Vocal_2.onnx");

        /// <summary>
        /// Path to the engine directory.
        /// </summary>
        public string EngineDirectory => _engineDirectory;

        public DependencyManager()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _engineDirectory = Path.Combine(localAppData, "VocalPure", "Engine");
            
            // FIX: Use AppContext.BaseDirectory instead of Assembly.Location for Single-File compatibility
            _appDirectory = AppContext.BaseDirectory;
            _isInitialized = false;
        }

        /// <summary>
        /// Initializes the engine directory: creates paths, copies bundled binaries, strips MotW.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                Log.Information("DependencyManager: Initializing engine at {EngineDir}", _engineDirectory);
                
                // Ensure engine directory exists
                Directory.CreateDirectory(_engineDirectory);

                // Copy and verify FFmpeg
                await DeployBinaryAsync("ffmpeg.exe", FFmpegPath);

                // Copy and verify ONNX model
                await DeployModelAsync("Kim_Vocal_2.onnx", ModelPath);

                Log.Information("DependencyManager: Stripping Mark of the Web from engine binaries.");
                // Strip MotW from all files in engine directory
                await Task.Run(() => MotWStripper.StripMotWInDirectory(_engineDirectory));

                _isInitialized = true;
                Log.Information("DependencyManager: Initialization completed successfully.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DependencyManager: Failed to initialize VocalPure engine.");
                throw new InvalidOperationException(
                    $"Failed to initialize VocalPure engine at {_engineDirectory}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies that all required dependencies are present and valid.
        /// </summary>
        public bool VerifyDependencies()
        {
            bool hasFfmpeg = File.Exists(FFmpegPath);
            bool hasModel = File.Exists(ModelPath);
            Log.Debug("Dependency verification: FFmpeg={HasFfmpeg}, Model={HasModel}", hasFfmpeg, hasModel);
            return hasFfmpeg && hasModel;
        }

        /// <summary>
        /// Deploys a binary file from the application directory to the engine directory.
        /// </summary>
        private async Task DeployBinaryAsync(string fileName, string destinationPath)
        {
            Log.Debug("Deploying binary {FileName}...", fileName);
            
            // Check if already exists and is valid
            if (File.Exists(destinationPath))
            {
                FileInfo destInfo = new FileInfo(destinationPath);
                string sourcePath = Path.Combine(_appDirectory, fileName);

                if (File.Exists(sourcePath))
                {
                    FileInfo srcInfo = new FileInfo(sourcePath);
                    // If bundled version is newer or different size, update
                    if (srcInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc ||
                        srcInfo.Length != destInfo.Length)
                    {
                        Log.Information("Updating {FileName} as bundled version is newer.", fileName);
                        await CopyFileWithRetryAsync(sourcePath, destinationPath);
                    }
                }

                return; // File exists, no need to deploy
            }

            // Try to copy from application directory
            string bundledPath = Path.Combine(_appDirectory, fileName);
            if (File.Exists(bundledPath))
            {
                Log.Information("Copying {FileName} from application directory.", fileName);
                await CopyFileWithRetryAsync(bundledPath, destinationPath);
                return;
            }

            // Try to copy from Engine subdirectory
            string engineBundledPath = Path.Combine(_appDirectory, "Engine", fileName);
            if (File.Exists(engineBundledPath))
            {
                Log.Information("Copying {FileName} from Engine subdirectory.", fileName);
                await CopyFileWithRetryAsync(engineBundledPath, destinationPath);
                return;
            }

            // If single-file publish, check temp extraction directory
            string tempPath = Path.Combine(Path.GetTempPath(), ".net", AppDomain.CurrentDomain.FriendlyName);
            if (Directory.Exists(tempPath))
            {
                var foundFiles = Directory.GetFiles(tempPath, fileName, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    Log.Information("Copying {FileName} from temporary extraction directory.", fileName);
                    await CopyFileWithRetryAsync(foundFiles[0], destinationPath);
                    return;
                }
            }

            Log.Error("Required binary {FileName} not found anywhere.", fileName);
            throw new FileNotFoundException(
                $"Required binary not found: {fileName}. Please ensure the engine components are properly installed.");
        }

        /// <summary>
        /// Deploys the ONNX model file to the engine directory.
        /// </summary>
        private async Task DeployModelAsync(string fileName, string destinationPath)
        {
            Log.Debug("Deploying model {FileName}...", fileName);
            
            // Check if model already exists
            if (File.Exists(destinationPath))
            {
                // Verify it's a valid ONNX file
                bool isValid = await Task.Run(() => ValidateOnnxFile(destinationPath));
                if (isValid)
                {
                    Log.Debug("Existing model {FileName} is valid.", fileName);
                    
                    // Check if there's a newer version bundled
                    string sourcePath = Path.Combine(_appDirectory, "Engine", fileName);
                    if (File.Exists(sourcePath))
                    {
                        FileInfo srcInfo = new FileInfo(sourcePath);
                        FileInfo destInfo = new FileInfo(destinationPath);
                        if (srcInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc ||
                            srcInfo.Length != destInfo.Length)
                        {
                            Log.Information("Updating model {FileName} from {SourcePath}", fileName, sourcePath);
                            await CopyFileWithRetryAsync(sourcePath, destinationPath);
                        }
                    }
                    return;
                }
                
                Log.Warning("Existing model {FileName} is invalid or corrupted. Attempting to redeploy.", fileName);
            }

            // Try to find the model file
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
                    Log.Information("Copying model {FileName} from {SourcePath}", fileName, searchPath);
                    await CopyFileWithRetryAsync(searchPath, destinationPath);
                    return;
                }
            }

            // Recursively search temp extraction directory
            string tempNetPath = Path.Combine(Path.GetTempPath(), ".net");
            if (Directory.Exists(tempNetPath))
            {
                foreach (string rootDir in Directory.GetDirectories(tempNetPath))
                {
                    try
                    {
                        var foundFiles = Directory.GetFiles(rootDir, fileName, SearchOption.AllDirectories);
                        if (foundFiles.Length > 0)
                        {
                            Log.Information("Copying model {FileName} from temp directory {SourcePath}", fileName, foundFiles[0]);
                            await CopyFileWithRetryAsync(foundFiles[0], destinationPath);
                            return;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                }
            }

            Log.Error("ONNX model {FileName} not found.", fileName);
            throw new FileNotFoundException(
                $"ONNX model not found: {fileName}. Please download the Kim_Vocal_2.onnx model and place it in the Engine folder.");
        }

        /// <summary>
        /// Copies a file with retry logic for file locking scenarios.
        /// </summary>
        private static async Task CopyFileWithRetryAsync(string source, string destination, int maxRetries = 3)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // Ensure directory exists
                    string? destDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);

                    // Copy with overwrite
                    File.Copy(source, destination, true);

                    // Strip MotW immediately after copy
                    MotWStripper.StripMotW(destination);
                    Log.Debug("Successfully copied to {Destination}", destination);

                    return;
                }
                catch (IOException) when (attempt < maxRetries - 1)
                {
                    Log.Warning("File lock when copying to {Destination}, retrying...", destination);
                    await Task.Delay(500 * (attempt + 1));
                }
            }

            throw new IOException($"Failed to copy file after {maxRetries} attempts: {source} -> {destination}");
        }

        /// <summary>
        /// Validates that the file exists and is reasonably sized for an ONNX model.
        /// Fixed: Relaxed the strict Protocol Buffer checks to allow raw downloads.
        /// </summary>
        private static bool ValidateOnnxFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                // Ensure file exists and is larger than 10MB (Kim_Vocal_2 is ~65MB)
                bool isValid = info.Exists && info.Length > 10 * 1024 * 1024;
                
                if (!isValid) 
                {
                    Log.Warning("Model validation failed. File '{Path}' is missing or too small ({Size} bytes).", path, info.Length);
                }
                
                return isValid;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception during model validation for {Path}", path);
                return false;
            }
        }
    }
}
