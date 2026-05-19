using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace VocalPure.Models
{
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

        // CRITICAL FIX: StringBuilder prevents O(N^2) memory allocation UI locks
        private readonly StringBuilder _logBuilder;

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
            _logBuilder = new StringBuilder();
            _isActive = false;
            _startTime = DateTime.MinValue;
            _elapsed = TimeSpan.Zero;
            _estimatedRemaining = null;
            _executionProvider = "Auto";
            _errorMessage = string.Empty;
            _isVideoOutput = false;
        }

        public string Id { get => _id; private set => SetProperty(ref _id, value); }
        public string InputFilePath { get => _inputFilePath; set => SetProperty(ref _inputFilePath, value); }
        
        public string? OutputFilePath
        {
            get => _outputFilePath;
            set { if (SetProperty(ref _outputFilePath, value)) OnPropertyChanged(nameof(HasOutput)); }
        }

        public bool HasOutput => !string.IsNullOrWhiteSpace(OutputFilePath);
        public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
        public long FileSize { get => _fileSize; set => SetProperty(ref _fileSize, value); }
        public string FormattedFileSize => FormatBytes(_fileSize);

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

        public double OverallProgress
        {
            get => _overallProgress;
            set { if (SetProperty(ref _overallProgress, Math.Clamp(value, 0.0, 1.0))) UpdateEstimatedRemaining(); }
        }

        public double CurrentPhaseProgress { get => _currentPhaseProgress; set => SetProperty(ref _currentPhaseProgress, Math.Clamp(value, 0.0, 1.0)); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        
        public string DetailedLog { get => _detailedLog; set => SetProperty(ref _detailedLog, value); }
        
        public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
        public DateTime StartTime { get => _startTime; set => SetProperty(ref _startTime, value); }
        public TimeSpan Elapsed { get => _elapsed; set { if (SetProperty(ref _elapsed, value)) UpdateEstimatedRemaining(); } }
        public TimeSpan? EstimatedRemaining { get => _estimatedRemaining; private set => SetProperty(ref _estimatedRemaining, value); }
        public string ExecutionProvider { get => _executionProvider; set => SetProperty(ref _executionProvider, value); }
        public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
        public bool IsVideoOutput { get => _isVideoOutput; set => SetProperty(ref _isVideoOutput, value); }
        public bool IsTerminal => Phase.IsTerminal();
        public bool CanCancel => IsActive && !Phase.IsTerminal();

        public void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            
            lock (_logBuilder)
            {
                _logBuilder.AppendLine($"[{timestamp}] {message}");
            }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                lock (_logBuilder) { DetailedLog = _logBuilder.ToString(); }
            });
        }

        private void UpdateEstimatedRemaining()
        {
            if (OverallProgress > 0.01 && OverallProgress < 1.0)
            {
                double totalSeconds = Elapsed.TotalSeconds / OverallProgress;
                double remainingSeconds = totalSeconds - Elapsed.TotalSeconds;
                EstimatedRemaining = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));
            }
            else EstimatedRemaining = null;
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:0.##} {sizes[order]}";
        }

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        #endregion

        // CRITICAL FIX: Properly suppress finalize
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
