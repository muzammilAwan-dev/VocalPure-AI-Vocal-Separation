using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using Serilog;
using VocalPure.Models;

namespace VocalPure.Services
{
    public sealed class InferenceEngine : IDisposable
    {
        private readonly InferenceSettings _settings;
        private InferenceSession? _session;
        private string? _currentModelPath;
        private string _activeExecutionProvider;
        private bool _isDisposed;

        public string ActiveExecutionProvider => _activeExecutionProvider;

        public InferenceEngine(InferenceSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _activeExecutionProvider = "CPU";
        }

        public bool CheckGpuAvailability()
        {
            try
            {
                var availableProviders = OrtEnv.Instance().GetAvailableProviders();
                return availableProviders.Contains("Dml") ||
                       availableProviders.Contains("OpenVINO") ||
                       availableProviders.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private void LoadModel(string modelPath, CancellationToken cancellationToken)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            if (_session != null && _currentModelPath == modelPath) return;

            _session?.Dispose();
            _session = null;

            var providerPriority = new List<(string Name, Action<SessionOptions> Configure)>
            {
                ("DirectML", new Action<SessionOptions>(opts => opts.AppendExecutionProvider_DML(0))),
                ("OpenVINO", new Action<SessionOptions>(opts => opts.AppendExecutionProvider_OpenVINO("GPU_FP16"))),
                ("CPU", new Action<SessionOptions>(opts => {}))
            };

            foreach (var (providerName, configure) in providerPriority)
            {
                try
                {
                    var testOptions = new SessionOptions
                    {
                        LogSeverityLevel = (OrtLoggingLevel)_settings.LogVerbosityLevel,
                        GraphOptimizationLevel = (GraphOptimizationLevel)_settings.OptimizationLevel,
                        InterOpNumThreads = _settings.InterOpNumThreads,
                        IntraOpNumThreads = _settings.IntraOpNumThreads,
                        EnableCpuMemArena = false,
                        EnableMemoryPattern = false
                    };

                    testOptions.AddSessionConfigEntry("session.disable_prepacking", "1");
                    configure(testOptions);

                    _session = new InferenceSession(modelPath, testOptions);
                    _currentModelPath = modelPath;
                    _activeExecutionProvider = providerName;
                    return;
                }
                catch { continue; }
            }

            throw new InvalidOperationException("Failed to initialize any ONNX execution provider.");
        }

        /// <summary>
        /// THE ENTERPRISE PIPELINE:
        /// Orchestrates three concurrent workers (Producer, AI, Consumer) linked by Bounded Channels
        /// to process media continuously without intermediate disk writes or RAM bloating.
        /// </summary>
        public async Task SeparateVocalsAsync(
            MediaProcessor mediaProcessor,
            string inputFilePath,
            string outputFilePath,
            bool isVideoOutput,
            string modelPath,
            CancellationToken cancellationToken,
            IProgress<(double chunkProgress, int currentChunk, int totalChunks, string executionProvider)>? progress = null)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report((0, 0, 0, "Loading model..."));
            await Task.Run(() => LoadModel(modelPath, cancellationToken), cancellationToken);

            string inputName = _session!.InputMetadata.Keys.First();
            string outputName = _session.OutputMetadata.Keys.First();

            // Calculate estimated chunks for the UI progress bar safely
            long totalSamples;
            try {
                using var reader = new MediaFoundationReader(inputFilePath);
                totalSamples = (long)(reader.TotalTime.TotalSeconds * _settings.SampleRate * _settings.Channels);
            } catch {
                totalSamples = 300 * _settings.SampleRate * _settings.Channels; // Fallback to 5 mins
            }
            int totalChunks = (int)((totalSamples / _settings.Channels) / AudioChunker.GenSize) + 1;

            // Instantiate TWO chunkers to ensure Thread-Safety between Producer and AI!
            using var readChunker = new AudioChunker(_settings.SampleRate, _settings.Channels);
            using var writeChunker = new AudioChunker(_settings.SampleRate, _settings.Channels);

            // Open the Zero-Disk FFmpeg Pipes
            using var decodeProcess = mediaProcessor.StartFfmpegDecodeStream(inputFilePath);
            using var encodeProcess = mediaProcessor.StartFfmpegEncodeStream(inputFilePath, outputFilePath, isVideoOutput);

            var rawAudioStream = decodeProcess.StandardOutput.BaseStream;
            var finalAudioStream = encodeProcess.StandardInput.BaseStream;

            // Bounded Channels prevent RAM overflow by halting the Producer if the AI gets backed up
            var chunkChannel = Channel.CreateBounded<(float[] tensor, int validSamples)>(new BoundedChannelOptions(3) { SingleReader = true, SingleWriter = true });
            var processedChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3) { SingleReader = true, SingleWriter = true });

            int chunksProcessed = 0;

            // WORKER 1: The Producer (Streams from FFmpeg)
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        float[] tensorData = new float[4 * AudioChunker.FreqBins * AudioChunker.TimeFrames];
                        int validSamples = readChunker.ReadAndTransformStreamChunk(rawAudioStream, tensorData);
                        
                        if (validSamples == 0) break; // End of File Reached
                        await chunkChannel.Writer.WriteAsync((tensorData, validSamples), cancellationToken);
                    }
                }
                finally { chunkChannel.Writer.Complete(); }
            });

            // WORKER 2: The AI Engineer (Runs Neural Network Math)
            var aiTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var (tensorData, validSamples) in chunkChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        float[] outputTensorData = RunInference(tensorData, inputName, outputName);
                        float[] audioChunk = writeChunker.InverseTransformAndStitch(outputTensorData);
                        byte[] outBytes = writeChunker.ExtractValidAudioBytes(audioChunk, validSamples);

                        await processedChannel.Writer.WriteAsync(outBytes, cancellationToken);
                        
                        chunksProcessed++;
                        progress?.Report(((double)chunksProcessed / totalChunks, chunksProcessed, totalChunks, _activeExecutionProvider));
                        
                        // Force rapid cleanup of intermediate tensors to hold the 800MB RAM line
                        if (chunksProcessed % 2 == 0) GC.Collect(2, GCCollectionMode.Optimized, false);
                    }
                }
                finally { processedChannel.Writer.Complete(); }
            });

            // WORKER 3: The Consumer (Streams directly back to FFmpeg Encoder)
            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var bytes in processedChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        await finalAudioStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                    }
                }
                finally { finalAudioStream.Close(); } // Closing stream tells FFmpeg to finalize the MP3/MP4
            });

            // Wait for all three workers to cross the finish line
            await Task.WhenAll(producerTask, aiTask, consumerTask);

            // Give FFmpeg a moment to save the final file metadata
            await Task.Run(() => encodeProcess.WaitForExit(30000));
            progress?.Report((1.0, totalChunks, totalChunks, _activeExecutionProvider));
        }

        private float[] RunInference(float[] tensorData, string inputName, string outputName)
        {
            if (_session == null) throw new InvalidOperationException("Model not loaded.");

            var inputTensor = new DenseTensor<float>(tensorData, new[] { 1, 4, 3072, 256 });
            using var results = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) });
            return results.First(r => r.Name == outputName).AsTensor<float>().ToArray();
        }

        public void Dispose()
        {
            _isDisposed = true;
            _session?.Dispose();
        }
    }
}
