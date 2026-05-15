using System;
using System.IO;

namespace VocalPure.Models
{
    /// <summary>
    /// Configuration settings for the ONNX inference engine.
    /// </summary>
    public sealed class InferenceSettings
    {
        /// <summary>Duration of each audio chunk in seconds (default: 30).</summary>
        public int ChunkDurationSeconds { get; set; } = 30;

        /// <summary>Overlap between chunks in seconds for smooth crossfading (default: 1).</summary>
        public int OverlapSeconds { get; set; } = 1;

        /// <summary>Target sample rate for processing (default: 44100).</summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>Number of audio channels (default: 2 for stereo).</summary>
        public int Channels { get; set; } = 2;

        /// <summary>Sample format: f32le for float32.</summary>
        public string SampleFormat { get; set; } = "f32le";

        /// <summary>Path to the ONNX model file.</summary>
        public string ModelPath { get; set; } = string.Empty;

        /// <summary>Number of parallel inference workers (default: 1 for memory safety).</summary>
        public int ParallelWorkers { get; set; } = 1;

        /// <summary>Thread count for ONNX intra-op parallelism (0 = auto).</summary>
        public int IntraOpNumThreads { get; set; } = 0;

        /// <summary>Whether to use direct GPU memory buffers for input/output.</summary>
        public bool UseGpuBuffer { get; set; } = false;

        /// <summary>ONNX graph optimization level (0=None, 1=Basic, 2=Extended, 3=All).</summary>
        public int OptimizationLevel { get; set; } = 99; // 99 = All (ORT_ENABLE_ALL)

        /// <summary>Log verbosity level (0=Verbose, 1=Info, 2=Warning, 3=Error, 4=Fatal).</summary>
        public int LogVerbosityLevel { get; set; } = 2;

        /// <summary>Number of inter-op threads (0 = default).</summary>
        public int InterOpNumThreads { get; set; } = 0;

        /// <summary>Validated chunk size in samples.</summary>
        public int ChunkSize => ChunkDurationSeconds * SampleRate * Channels;

        /// <summary>Validated overlap size in samples.</summary>
        public int OverlapSize => OverlapSeconds * SampleRate * Channels;

        /// <summary>Hop size between chunks in samples.</summary>
        public int HopSize => ChunkSize - (OverlapSize * 2);

        /// <summary>Creates a default settings instance with the standard engine path.</summary>
        public static InferenceSettings CreateDefault()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string enginePath = Path.Combine(localAppData, "VocalPure", "Engine");

            return new InferenceSettings
            {
                ModelPath = Path.Combine(enginePath, "Kim_Vocal_2.onnx"),
                ChunkDurationSeconds = 30,
                OverlapSeconds = 1,
                SampleRate = 44100,
                Channels = 2,
                IntraOpNumThreads = 0,  // Auto
                ParallelWorkers = 1,     // Conservative for memory
                UseGpuBuffer = false
            };
        }
    }
}
