using System;

namespace VocalPure.Models
{
    /// <summary>
    /// Represents the current processing phase of a separation job.
    /// </summary>
    public enum ProcessingPhase
    {
        Idle,
        Initializing,
        ExtractingAudio,
        LoadingModel,
        ProcessingInference,
        MergingOutput,
        CleaningUp,
        Completed,
        Cancelled,
        Error
    }

    /// <summary>
    /// Extension methods for ProcessingPhase to provide human-readable descriptions.
    /// </summary>
    public static class ProcessingPhaseExtensions
    {
        public static string GetDisplayName(this ProcessingPhase phase) => phase switch
        {
            ProcessingPhase.Idle => "Ready",
            ProcessingPhase.Initializing => "Initializing engine...",
            ProcessingPhase.ExtractingAudio => "Extracting audio from media...",
            ProcessingPhase.LoadingModel => "Loading ONNX model...",
            ProcessingPhase.ProcessingInference => "Running AI vocal separation...",
            ProcessingPhase.MergingOutput => "Merging output media...",
            ProcessingPhase.CleaningUp => "Cleaning up temporary files...",
            ProcessingPhase.Completed => "Separation complete!",
            ProcessingPhase.Cancelled => "Operation cancelled by user.",
            ProcessingPhase.Error => "An error occurred during processing.",
            _ => "Unknown status"
        };

        public static bool IsTerminal(this ProcessingPhase phase) =>
            phase == ProcessingPhase.Completed ||
            phase == ProcessingPhase.Cancelled ||
            phase == ProcessingPhase.Error;
    }
}
