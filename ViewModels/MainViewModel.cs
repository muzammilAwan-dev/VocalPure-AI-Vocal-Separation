using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using VocalPure.Models;
using VocalPure.Services;

namespace VocalPure.ViewModels
{
    /// <summary>
    /// Main ViewModel for the VocalPure application.
    /// Manages the separation queue, handles drag-and-drop, and orchestrates processing.
    /// </summary>
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly DependencyManager _dependencyManager;
        private readonly MediaProcessor _mediaProcessor;
        private readonly InferenceEngine _inferenceEngine;
        private readonly InferenceSettings _inferenceSettings;
        private readonly DispatcherTimer _elapsedTimer;
        private CancellationTokenSource? _currentCts;
        private SeparationJob? _activeJob;
        private bool _isProcessing;
        private bool _isDragging;
        private string _dropZoneText;
        private string _versionInfo;
        private string _engineStatus;
        private bool _gpuAvailable;

        public MainViewModel()
        {
            _dependencyManager = new DependencyManager();
            _mediaProcessor = new MediaProcessor();
            _inferenceSettings = InferenceSettings.CreateDefault();
            _inferenceEngine = new InferenceEngine(_inferenceSettings);
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += OnElapsedTimerTick;
            _isProcessing = false;
            _isDragging = false;
            _dropZoneText = "Drag & Drop Audio/Video Here\nor Click to Browse";
            _versionInfo = $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";
            _engineStatus = "Checking engine...";
            _gpuAvailable = false;
            JobQueue = new ObservableCollection<SeparationJob>();

            BrowseCommand = new RelayCommand(OnBrowse);
            StartProcessingCommand = new RelayCommand(OnStartProcessing, () => !IsProcessing && JobQueue.Any(j => j.Phase == ProcessingPhase.Idle));
            CancelCommand = new RelayCommand(OnCancel, () => IsProcessing);
            ClearCompletedCommand = new RelayCommand(OnClearCompleted, () => JobQueue.Any(j => j.IsTerminal));
            OpenOutputFolderCommand = new RelayCommand<string>(OnOpenOutputFolder);
            InitializeCommand = new RelayCommand(() => _ = InitializeAsync());

            _ = InitializeAsync();
        }

        #region Properties

        /// <summary>Queue of separation jobs.</summary>
        public ObservableCollection<SeparationJob> JobQueue { get; }

        /// <summary>Whether a job is currently being processed.</summary>
        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    CancelCommand.RaiseCanExecuteChanged();
                    StartProcessingCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Whether a file is being dragged over the drop zone.</summary>
        public bool IsDragging
        {
            get => _isDragging;
            set => SetProperty(ref _isDragging, value);
        }

        /// <summary>Text displayed in the drop zone.</summary>
        public string DropZoneText
        {
            get => _dropZoneText;
            set => SetProperty(ref _dropZoneText, value);
        }

        /// <summary>Application version string.</summary>
        public string VersionInfo
        {
            get => _versionInfo;
            set => SetProperty(ref _versionInfo, value);
        }

        /// <summary>Engine dependency status string.</summary>
        public string EngineStatus
        {
            get => _engineStatus;
            set => SetProperty(ref _engineStatus, value);
        }

        /// <summary>Whether GPU acceleration is available.</summary>
        public bool GpuAvailable
        {
            get => _gpuAvailable;
            set => SetProperty(ref _gpuAvailable, value);
        }

        /// <summary>Currently active job being processed.</summary>
        public SeparationJob? ActiveJob
        {
            get => _activeJob;
            private set => SetProperty(ref _activeJob, value);
        }

        #endregion

        #region Commands

        public RelayCommand BrowseCommand { get; }
        public RelayCommand StartProcessingCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ClearCompletedCommand { get; }
        public RelayCommand<string> OpenOutputFolderCommand { get; }
        public RelayCommand InitializeCommand { get; }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the application engine: verifies dependencies and checks GPU availability.
        /// </summary>
        private async Task InitializeAsync()
        {
            try
            {
                EngineStatus = "Initializing engine dependencies...";
                Log.Information("Starting engine dependency initialization...");
                
                await _dependencyManager.InitializeAsync();

                // Check GPU availability
                bool hasGpu = await Task.Run(() => _inferenceEngine.CheckGpuAvailability());
                GpuAvailable = hasGpu;
                EngineStatus = hasGpu
                    ? "Engine ready - GPU acceleration available"
                    : "Engine ready - CPU mode";
                    
                Log.Information("Engine initialization complete. GPU Available: {GpuAvailable}", hasGpu);
            }
            catch (Exception ex)
            {
                EngineStatus = $"Engine initialization failed: {ex.Message}";
                Log.Error(ex, "Failed to initialize the dependency engine.");
                MessageBox.Show($"Failed to initialize engine: {ex.Message}",
                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region File Handling

        /// <summary>
        /// Handles files dropped onto the application.
        /// </summary>
        public async Task HandleDroppedFilesAsync(string[] filePaths)
        {
            var validExtensions = new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
                                          ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm" };

            foreach (string path in filePaths)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (validExtensions.Contains(ext) && File.Exists(path))
                {
                    Log.Information("Adding file to queue: {FilePath}", path);
                    var job = new SeparationJob(path)
                    {
                        IsVideoOutput = IsVideoFile(path)
                    };
                    JobQueue.Add(job);
                }
                else
                {
                    Log.Warning("Skipped invalid or missing file: {FilePath}", path);
                }
            }

            // Enable the Start Processing button now that items are queued
            StartProcessingCommand.RaiseCanExecuteChanged();
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Opens file browser dialog to select media files.
        /// </summary>
        private async void OnBrowse()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Audio or Video File",
                Filter = "Media Files|*.mp3;*.wav;*.flac;*.aac;*.ogg;*.m4a;*.wma;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv;*.webm|All Files|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                await HandleDroppedFilesAsync(dialog.FileNames);
            }
        }

        private bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm";
        }

        #endregion

        #region Processing Pipeline

        /// <summary>
        /// Starts processing the queue when the user clicks the Start button.
        /// </summary>
        private void OnStartProcessing()
        {
            if (!IsProcessing && JobQueue.Any(j => j.Phase == ProcessingPhase.Idle))
            {
                _ = ProcessNextJobAsync();
            }
        }

        /// <summary>
        /// Processes the next queued job with full cancellation support.
        /// </summary>
        private async Task ProcessNextJobAsync()
        {
            var job = JobQueue.FirstOrDefault(j => j.Phase == ProcessingPhase.Idle);
            if (job == null) return;

            ActiveJob = job;
            IsProcessing = true;
            _currentCts = new CancellationTokenSource();
            CancellationToken token = _currentCts.Token;
            string tempDir = Path.Combine(Path.GetTempPath(), "VocalPure", job.Id);
            
            Log.Information("Starting job ID: {JobId} for file: {FileName}", job.Id, job.FileName);

            try
            {
                job.StartTime = DateTime.Now;
                job.IsActive = true;
                job.Phase = ProcessingPhase.Initializing;
                job.AppendLog($"Starting processing: {job.FileName}");
                _elapsedTimer.Start();

                // Progress reporter
                var progress = new Progress<(ProcessingPhase phase, double phaseProgress, string message)>(update =>
                {
                    job.Phase = update.phase;
                    job.CurrentPhaseProgress = update.phaseProgress;
                    if (!string.IsNullOrEmpty(update.message))
                        job.AppendLog(update.message);

                    // Calculate overall progress
                    job.OverallProgress = CalculateOverallProgress(update.phase, update.phaseProgress);
                });

                // Phase 1: Extract audio to Float32 PCM WAV
                job.Phase = ProcessingPhase.ExtractingAudio;
                string wavPath = Path.Combine(tempDir, "input.wav");
                Directory.CreateDirectory(tempDir);
                Log.Debug("Phase 1: Extracting to {WavPath}", wavPath);

                job.AppendLog("Phase 1: Extracting audio with FFmpeg...");
                var extractReporter = new Progress<double>(p =>
                    ((IProgress<(ProcessingPhase, double, string)>)progress).Report((
                        ProcessingPhase.ExtractingAudio, p, $"FFmpeg extraction: {p:P0}")));

                await _mediaProcessor.ExtractAudioToWavAsync(
                    job.InputFilePath, wavPath, token, extractReporter);

                if (token.IsCancellationRequested) throw new OperationCanceledException();

                // Phase 2: Load model and run inference
                job.Phase = ProcessingPhase.LoadingModel;
                job.AppendLog("Phase 2: Loading ONNX model...");

                string vocalsWavPath = Path.Combine(tempDir, "vocals.wav");
                string modelPath = _inferenceSettings.ModelPath;
                Log.Debug("Phase 2: Loading model from {ModelPath}", modelPath);

                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"ONNX model not found: {modelPath}");

                var inferenceReporter = new Progress<(double chunkProgress, int currentChunk, int totalChunks, string executionProvider)>(update =>
                {
                    job.ExecutionProvider = update.executionProvider;
                    double phaseProgress = update.totalChunks > 0 ? (double)update.currentChunk / update.totalChunks : 0;
                    ((IProgress<(ProcessingPhase, double, string)>)progress).Report((
                        ProcessingPhase.ProcessingInference, phaseProgress,
                        $"Processing chunk {update.currentChunk}/{update.totalChunks} [{update.executionProvider}]"));
                });

                await _inferenceEngine.SeparateVocalsAsync(
                    wavPath, vocalsWavPath, modelPath, token, inferenceReporter);

                if (token.IsCancellationRequested) throw new OperationCanceledException();

                // Phase 3: Merge output
                job.Phase = ProcessingPhase.MergingOutput;
                job.AppendLog("Phase 3: Encoding final output...");

                string outputPath = GenerateOutputPath(job);
                Log.Debug("Phase 3: Output targeting {OutputPath}", outputPath);
                
                var mergeReporter = new Progress<double>(p =>
                    ((IProgress<(ProcessingPhase, double, string)>)progress).Report((
                        ProcessingPhase.MergingOutput, p, $"FFmpeg encode: {p:P0}")));

                if (job.IsVideoOutput)
                {
                    await _mediaProcessor.MergeVideoWithAudioAsync(
                        job.InputFilePath, vocalsWavPath, outputPath, token, mergeReporter);
                }
                else
                {
                    await _mediaProcessor.EncodeAudioAsync(
                        vocalsWavPath, outputPath, token, mergeReporter);
                }

                job.OutputFilePath = outputPath;
                job.AppendLog($"Output saved: {outputPath}");

                // Phase 4: Cleanup
                job.Phase = ProcessingPhase.CleaningUp;
                job.AppendLog("Phase 4: Cleaning up temporary files...");
                CleanupTempDirectory(tempDir);

                job.Phase = ProcessingPhase.Completed;
                job.AppendLog("Processing completed successfully!");
                Log.Information("Job completed successfully: {JobId}", job.Id);
            }
            catch (OperationCanceledException)
            {
                job.Phase = ProcessingPhase.Cancelled;
                job.AppendLog("Processing cancelled.");
                Log.Warning("Job cancelled: {JobId}", job.Id);
                CleanupTempDirectory(tempDir);
            }
            catch (Exception ex)
            {
                job.Phase = ProcessingPhase.Error;
                job.ErrorMessage = ex.Message;
                job.AppendLog($"ERROR: {ex.Message}");
                Log.Error(ex, "Job failed: {JobId}", job.Id);
                if (ex.InnerException != null)
                    job.AppendLog($"Inner: {ex.InnerException.Message}");
                CleanupTempDirectory(tempDir);
            }
            finally
            {
                _elapsedTimer.Stop();
                job.IsActive = false;
                IsProcessing = false;
                ActiveJob = null;
                _currentCts?.Dispose();
                _currentCts = null;

                ClearCompletedCommand.RaiseCanExecuteChanged();
                StartProcessingCommand.RaiseCanExecuteChanged();

                // Process next job if available
                if (JobQueue.Any(j => j.Phase == ProcessingPhase.Idle))
                {
                    await ProcessNextJobAsync();
                }
            }
        }

        /// <summary>
        /// Calculates overall progress percentage based on current phase.
        /// </summary>
        private static double CalculateOverallProgress(ProcessingPhase phase, double phaseProgress)
        {
            return phase switch
            {
                ProcessingPhase.Initializing => 0.0,
                ProcessingPhase.ExtractingAudio => 0.05 + (phaseProgress * 0.15),
                ProcessingPhase.LoadingModel => 0.20,
                ProcessingPhase.ProcessingInference => 0.25 + (phaseProgress * 0.55),
                ProcessingPhase.MergingOutput => 0.80 + (phaseProgress * 0.15),
                ProcessingPhase.CleaningUp => 0.96,
                ProcessingPhase.Completed => 1.0,
                _ => 0.0
            };
        }

        /// <summary>
        /// Generates the output file path based on input and settings.
        /// </summary>
        private static string GenerateOutputPath(SeparationJob job)
        {
            string dir = Path.GetDirectoryName(job.InputFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string baseName = Path.GetFileNameWithoutExtension(job.FileName);
            string ext = job.IsVideoOutput ? ".mp4" : ".wav";
            string suffix = "_vocals";
            string outputPath = Path.Combine(dir, $"{baseName}{suffix}{ext}");

            int counter = 1;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(dir, $"{baseName}{suffix}_{counter}{ext}");
                counter++;
            }

            return outputPath;
        }

        /// <summary>
        /// Safely cleans up the temporary processing directory.
        /// </summary>
        private static void CleanupTempDirectory(string tempDir)
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    foreach (string file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { /* best effort */ }
                    }
                    Directory.Delete(tempDir, true);
                    Log.Debug("Cleaned up temporary directory: {TempDir}", tempDir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to fully clean temporary directory: {TempDir}", tempDir);
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Cancels the currently active processing job.
        /// </summary>
        private void OnCancel()
        {
            try
            {
                _currentCts?.Cancel();
                ActiveJob?.AppendLog("Cancellation requested...");
                Log.Information("Cancellation requested by user for active job.");
            }
            catch (ObjectDisposedException)
            {
                // CTS already disposed
            }
        }

        /// <summary>
        /// Removes all completed/failed/cancelled jobs from the queue.
        /// </summary>
        private void OnClearCompleted()
        {
            var completed = JobQueue.Where(j => j.IsTerminal).ToList();
            foreach (var job in completed)
            {
                JobQueue.Remove(job);
                job.Dispose();
            }
            Log.Information("Cleared {Count} completed/terminal jobs from queue.", completed.Count);
        }

        /// <summary>
        /// Opens the output folder in Windows Explorer.
        /// </summary>
        private static void OnOpenOutputFolder(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }

        /// <summary>
        /// Updates elapsed time for the active job.
        /// </summary>
        private void OnElapsedTimerTick(object? sender, EventArgs e)
        {
            if (ActiveJob != null && ActiveJob.StartTime != DateTime.MinValue)
            {
                ActiveJob.Elapsed = DateTime.Now - ActiveJob.StartTime;
            }
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        #endregion

        public void Dispose()
        {
            _elapsedTimer.Stop();
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _inferenceEngine.Dispose();

            foreach (var job in JobQueue)
                job.Dispose();
        }
    }
}
