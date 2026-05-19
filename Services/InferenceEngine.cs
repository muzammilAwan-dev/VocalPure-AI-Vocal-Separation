using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
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
        private int _currentIntraOpThreads = -1; // Tracks thread count to force reload if toggled

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
            catch (Exception ex)
            {
                Log.Debug(ex, "GPU availability check failed. Defaulting to CPU.");
                return false; 
            }
        }

        private void LoadModel(string modelPath, CancellationToken cancellationToken)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            // CRITICAL FIX: Also check if the thread count changed. If so, force a reload.
            if (_session != null && _currentModelPath == modelPath && _currentIntraOpThreads == _settings.IntraOpNumThreads) 
                return;

            _session?.Dispose();
            _session = null;
            _currentModelPath = null;

            if (!File.Exists(modelPath)) throw new FileNotFoundException($"ONNX model not found: {modelPath}");

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
                        ExecutionMode = providerName == "CPU" ? ExecutionMode.ORT_PARALLEL : ExecutionMode.ORT_SEQUENTIAL,
                        IntraOpNumThreads = providerName == "CPU" ? _settings.IntraOpNumThreads : 1,
                        InterOpNumThreads = _settings.InterOpNumThreads,
                        EnableCpuMemArena = false,
                        EnableMemoryPattern = false
                    };

                    testOptions.AddSessionConfigEntry("session.disable_prepacking", "1");
                    configure(testOptions);

                    _session = new InferenceSession(modelPath, testOptions);
                    _currentModelPath = modelPath;
                    _activeExecutionProvider = providerName;
                    _currentIntraOpThreads = _settings.IntraOpNumThreads; // Save current state

                    if (providerName != "CPU")
                    {
                        Log.Information("Performing GPU kernel warmup pass...");
                        var warmupData = new float[4 * 3072 * 256];
                        RunInference(warmupData, _session.InputMetadata.Keys.First(), _session.OutputMetadata.Keys.First());
                    }
                    return;
                }
                catch { continue; }
            }

            throw new InvalidOperationException("Failed to initialize any ONNX execution provider.");
        }

        public async Task SeparateVocalsAsync(
            string inputWavPath,
            string outputVocalsPath,
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

            using var chunker = new AudioChunker(_settings.SampleRate, _settings.Channels);
            using var audioReader = new NAudio.Wave.AudioFileReader(inputWavPath);
            
            long totalSamples = chunker.GetTotalSamples(audioReader);
            int totalChunks = chunker.GetChunkCount(totalSamples);
            long remainingSamples = totalSamples; 

            Log.Information("=== ADVANCED HARDWARE TELEMETRY INITIALIZED ===");
            Log.Information("Format: [Chunk] | Read | ONNX | ISTFT | Write || CPU % | RAM MB");
            progress?.Report((0, 0, totalChunks, _activeExecutionProvider));

            using var outputWriter = chunker.CreateStreamWriter(outputVocalsPath);
            
            Stopwatch telemetrySw = new Stopwatch();
            Stopwatch realTimeSw = Stopwatch.StartNew();
            using Process currentProcess = Process.GetCurrentProcess();

            for (int i = 0; i < totalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(((double)i / totalChunks, i + 1, totalChunks, _activeExecutionProvider));

                TimeSpan startCpuTime = currentProcess.TotalProcessorTime;
                long startRealTime = realTimeSw.ElapsedMilliseconds;

                telemetrySw.Restart();
                float[] tensorData = await Task.Run(() => chunker.ReadAndTransformChunk(audioReader, i), cancellationToken);
                long readTime = telemetrySw.ElapsedMilliseconds;

                telemetrySw.Restart();
                float[] outputTensorData = await Task.Run(() => RunInference(tensorData, inputName, outputName), cancellationToken);
                long onnxTime = telemetrySw.ElapsedMilliseconds;

                telemetrySw.Restart();
                float[] audioChunk = await Task.Run(() => chunker.InverseTransformAndStitch(outputTensorData), cancellationToken);
                long istftTime = telemetrySw.ElapsedMilliseconds;

                telemetrySw.Restart();
                chunker.WriteChunkToStream(outputWriter, audioChunk, ref remainingSamples);
                long writeTime = telemetrySw.ElapsedMilliseconds;

                TimeSpan endCpuTime = currentProcess.TotalProcessorTime;
                long endRealTime = realTimeSw.ElapsedMilliseconds;

                double cpuUsedMs = (endCpuTime - startCpuTime).TotalMilliseconds;
                double realPassedMs = endRealTime - startRealTime;
                double cpuPercent = (cpuUsedMs / (Environment.ProcessorCount * Math.Max(1, realPassedMs))) * 100;

                currentProcess.Refresh();
                long ramMb = currentProcess.WorkingSet64 / (1024 * 1024);

                Log.Information("[TELEMETRY] Chunk {Chunk:D3} | Read: {Read:D4}ms | ONNX: {Onnx:D4}ms | ISTFT: {Istft:D4}ms | Write: {Write:D4}ms || CPU: {Cpu:00.0}% | RAM: {Ram} MB",
                    i + 1, readTime, onnxTime, istftTime, writeTime, cpuPercent, ramMb);
            }

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
