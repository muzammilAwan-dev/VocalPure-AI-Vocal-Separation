using System;
using System.IO;

namespace VocalPure.Models
{
    public sealed class InferenceSettings
    {
        public int ChunkDurationSeconds { get; set; } = 30;
        public int OverlapSeconds { get; set; } = 1;
        public int SampleRate { get; set; } = 44100;
        public int Channels { get; set; } = 2;
        public string SampleFormat { get; set; } = "f32le";
        public string ModelPath { get; set; } = string.Empty;
        public int ParallelWorkers { get; set; } = 1;
        
        // THE UNLOCK: Set to 0 to let ONNX auto-scale to 100% of available CPU cores.
        // We can safely do this now because EnableCpuMemArena=false prevents the RAM explosion.
        public int IntraOpNumThreads { get; set; } = 0; 
        
        // InterOp remains 1. This prevents multiple chunks from processing at the exact same time.
        public int InterOpNumThreads { get; set; } = 1; 

        public bool UseGpuBuffer { get; set; } = false;
        public int OptimizationLevel { get; set; } = 99;
        public int LogVerbosityLevel { get; set; } = 2;

        public int ChunkSize => ChunkDurationSeconds * SampleRate * Channels;
        public int OverlapSize => OverlapSeconds * SampleRate * Channels;
        public int HopSize => ChunkSize - (OverlapSize * 2);

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
                IntraOpNumThreads = 0,  // 0 = Max Cores
                InterOpNumThreads = 1,
                ParallelWorkers = 1,    
                UseGpuBuffer = false
            };
        }
    }
}
