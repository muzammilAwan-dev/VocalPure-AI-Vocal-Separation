using System;
using System.Collections.Generic; // FIX: Added missing namespace for EqualityComparer
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace VocalPure.Models
{
    /// <summary>
    /// Represents a single vocal separation job with all tracking properties.
    /// Implements INotifyPropertyChanged for real-time UI updates.
    /// </summary>
    public sealed class SeparationJob : INotifyPropertyChanged, IDisposable
    {
        private string _id;
        private string _inputFilePath;
        private string? _outputFilePath;
        private string _fileName;
        private long _fileSize;
        private ProcessingPhase _phase;
        private double _overallProgress;
        private double _currentPhaseProgress;
        private string _statusMessage;
        private string _detailedLog;
        private bool _isActive;
        private DateTime _startTime;
        private TimeSpan _elapsed;
        private TimeSpan? _estimatedRemaining;
        private string _executionProvider;
        private string _errorMessage;
        private bool _isVideoOutput;

        public SeparationJob(string inputFilePath)
        {
            _id = Guid.NewGuid().ToString("N")[..8];
            _inputFilePath = inputFilePath;
            _fileName = Path.GetFileName(inputFilePath);
            _fileSize = new FileInfo(inputFilePath).Length;
            _phase = ProcessingPhase.Idle;
            _overallProgress = 0.0;
            _currentPhaseProgress = 0.0;
            _statusMessage = "Ready";
            _detailedLog = string.Empty;
            _isActive = false;
            _startTime = DateTime.MinValue;
            _elapsed = TimeSpan.Zero;
            _estimatedRemaining = null;
            _executionProvider = "Auto";
            _errorMessage = string.Empty;
            _isVideoOutput = false;
        }

        /// <summary>Unique job identifier.</summary>
        public string Id
        {
            get => _id;
            private set => SetProperty(ref _id, value);
        }

        /// <summary>Full path to the input media file.</summary>
        public string InputFilePath
        {
            get => _inputFilePath;
            set => SetProperty(ref _inputFilePath, value);
        }

        /// <summary>Full path to the output file (set after processing).</summary>
        public string? OutputFilePath
        {
            get => _outputFilePath;
            set => SetProperty(ref _outputFilePath, value);
        }

        /// <summary>Display name of the file.</summary>
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        /// <summary>File size in bytes.</summary>
        public long FileSize
        {
            get => _fileSize;
            set => SetProperty(ref _fileSize, value);
        }

        /// <summary>Formatted file size string (e.g., "15.2 MB").</summary>
        public string FormattedFileSize => FormatBytes(_fileSize);

        /// <summary>Current processing phase.</summary>
        public ProcessingPhase Phase
        {
            get => _phase;
            set
            {
                if (SetProperty(ref _phase, value))
                {
                    StatusMessage = value.GetDisplayName();
                    OnPropertyChanged(nameof(IsTerminal));
                    OnPropertyChanged(nameof(CanCancel));
                }
            }
        }

        /// <summary>Overall progress (0.0 to 1.0).</summary>
        public double OverallProgress
        {
            get => _overallProgress;
            set
            {
                if (SetProperty(ref _overallProgress, Math.Clamp(value, 0.0, 1.0)))
                {
                    UpdateEstimatedRemaining();
                }
            }
        }

        /// <summary>Progress of the current phase (0.0 to 1.0).</summary>
        public double CurrentPhaseProgress
        {
            get => _currentPhaseProgress;
            set => SetProperty(ref _currentPhaseProgress, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>Human-readable status message.</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>Detailed processing log for the status panel.</summary>
        public string DetailedLog
        {
            get => _detailedLog;
            set => SetProperty(ref _detailedLog, value);
        }

        /// <summary>Whether the job is currently active.</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        /// <summary>When the job started processing.</summary>
        public DateTime StartTime
        {
            get => _startTime;
            set => SetProperty(ref _startTime, value);
        }

        /// <summary>Elapsed processing time.</summary>
        public TimeSpan Elapsed
        {
            get => _elapsed;
            set
            {
                if (SetProperty(ref _elapsed, value))
                {
                    UpdateEstimatedRemaining();
                }
            }
        }

        /// <summary>Estimated time remaining based on current progress.</summary>
        public TimeSpan? EstimatedRemaining
        {
            get => _estimatedRemaining;
            private set => SetProperty(ref _estimatedRemaining, value);
        }

        /// <summary>ONNX execution provider being used (e.g., "DirectML", "CPU").</summary>
        public string ExecutionProvider
        {
            get => _executionProvider;
            set => SetProperty(ref _executionProvider, value);
        }

        /// <summary>Error message if the job failed.</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        /// <summary>Whether to output as video (muxed) or audio-only.</summary>
        public bool IsVideoOutput
        {
            get => _isVideoOutput;
            set => SetProperty(ref _isVideoOutput, value);
        }

        /// <summary>Whether the job has reached a terminal state.</summary>
        public bool IsTerminal => Phase.IsTerminal();

        /// <summary>Whether the job can be cancelled.</summary>
        public bool CanCancel => IsActive && !Phase.IsTerminal();

        /// <summary>Appends a line to the detailed processing log.</summary>
        public void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            Application.Current?.Dispatcher.Invoke(() =>
            {
                DetailedLog += $"[{timestamp}] {message}\n";
            });
        }

        /// <summary>Updates the estimated remaining time based on progress.</summary>
        private void UpdateEstimatedRemaining()
        {
            if (OverallProgress > 0.01 && OverallProgress < 1.0)
            {
                double totalSeconds = Elapsed.TotalSeconds / OverallProgress;
                double remainingSeconds = totalSeconds - Elapsed.TotalSeconds;
                EstimatedRemaining = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));
            }
            else
            {
                EstimatedRemaining = null;
            }
        }

        /// <summary>Formats bytes to human-readable string.</summary>
        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

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

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}
