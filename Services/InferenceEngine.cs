using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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

        private readonly float[] _inferenceOutputBuffer = new float[4 * 3072 * 256];

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
            catch { return false; }
        }

        private void LoadModel(string modelPath, CancellationToken cancellationToken)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            if (_session != null && _currentModelPath == modelPath) return;

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
            long totalSamples = chunker.GetTotalSamples(inputWavPath);
            int totalChunks = chunker.GetChunkCount(totalSamples);
            long remainingSamples = totalSamples; 

            Log.Information("=== DIRECT DISK TELEMETRY INITIALIZED ===");
            Log.Information("Format: [Chunk] | Disk Read | ONNX Math | ISTFT Math | Disk Write");
            progress?.Report((0, 0, totalChunks, _activeExecutionProvider));

            using var outputWriter = chunker.CreateStreamWriter(outputVocalsPath);
            Stopwatch telemetrySw = new Stopwatch();

            for (int i = 0; i < totalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(((double)i / totalChunks, i + 1, totalChunks, _activeExecutionProvider));

                telemetrySw.Restart();
                float[] tensorData = await Task.Run(() => chunker.ReadAndTransformChunk(inputWavPath, i), cancellationToken);
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

                Log.Information("[TELEMETRY] Chunk {Chunk:D3} | Read: {Read:D4}ms | ONNX: {Onnx:D4}ms | ISTFT: {Istft:D4}ms | Write: {Write:D4}ms",
                    i + 1, readTime, onnxTime, istftTime, writeTime);

                GC.Collect(2, GCCollectionMode.Optimized, false);
            }

            progress?.Report((1.0, totalChunks, totalChunks, _activeExecutionProvider));
        }

        private float[] RunInference(float[] tensorData, string inputName, string outputName)
        {
            if (_session == null) throw new InvalidOperationException("Model not loaded.");

            var inputTensor = new DenseTensor<float>(tensorData, new[] { 1, 4, 3072, 256 });
            using var results = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) });
            var outputTensor = results.First(r => r.Name == outputName).AsTensor<float>();

            if (outputTensor is DenseTensor<float> denseOutput)
            {
                denseOutput.Buffer.Span.CopyTo(_inferenceOutputBuffer);
            }
            else
            {
                var array = outputTensor.ToArray();
                Array.Copy(array, _inferenceOutputBuffer, array.Length);
            }

            return _inferenceOutputBuffer;
        }

        public void Dispose()
        {
            _isDisposed = true;
            _session?.Dispose();
        }
    }
}
