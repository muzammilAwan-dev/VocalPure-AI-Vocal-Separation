using System;
using System.Collections.Generic;
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
                bool hasGpu = availableProviders.Contains("Dml") ||
                              availableProviders.Contains("OpenVINO") ||
                              availableProviders.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
                
                Log.Debug("GPU Availability Check: {HasGpu} (Providers: {Providers})", hasGpu, string.Join(", ", availableProviders));
                return hasGpu;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to check ORT execution providers.");
                return false;
            }
        }

        private void LoadModel(string modelPath, CancellationToken cancellationToken)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            if (_session != null && _currentModelPath == modelPath)
            {
                Log.Debug("Model {ModelPath} is already loaded. Reusing session.", modelPath);
                return;
            }

            _session?.Dispose();
            _session = null;
            _currentModelPath = null;

            if (!File.Exists(modelPath))
            {
                Log.Error("Model file not found at path: {ModelPath}", modelPath);
                throw new FileNotFoundException($"ONNX model not found: {modelPath}");
            }

            Log.Information("Loading ONNX Model: {ModelPath}", Path.GetFileName(modelPath));

            var providerPriority = new List<(string Name, Action<SessionOptions> Configure)>
            {
                ("DirectML", new Action<SessionOptions>(opts => opts.AppendExecutionProvider_DML(0))),
                ("OpenVINO", new Action<SessionOptions>(opts => opts.AppendExecutionProvider_OpenVINO("GPU_FP16"))),
                ("CPU", new Action<SessionOptions>(opts => {}))
            };

            Exception? lastException = null;

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

                    Log.Debug("Attempting to load model with {ProviderName}...", providerName);
                    _session = new InferenceSession(modelPath, testOptions);
                    _currentModelPath = modelPath;
                    _activeExecutionProvider = providerName;

                    Log.Information("Successfully initialized model using {ProviderName} execution provider.", providerName);
                    ValidateModelMetadata();
                    return;
                }
                catch (Exception ex) when (providerName != "CPU")
                {
                    lastException = ex;
                    continue;
                }
            }

            throw new InvalidOperationException("Failed to initialize any ONNX execution provider.", lastException);
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

            var inputMetadata = _session!.InputMetadata;
            var outputMetadata = _session.OutputMetadata;

            string inputName = inputMetadata.Keys.First();
            string outputName = outputMetadata.Keys.First();

            using var chunker = new AudioChunker(_settings.SampleRate, _settings.Channels);

            long totalSamples = chunker.GetTotalSamples(inputWavPath);
            int totalChunks = chunker.GetChunkCount(totalSamples);
            long remainingSamples = totalSamples; // Used to track how much data is left to write

            Log.Information("Starting frequency-domain inference. Total Samples: {TotalSamples}, Total Chunks: {TotalChunks}", totalSamples, totalChunks);
            progress?.Report((0, 0, totalChunks, _activeExecutionProvider));

            // NEW: Open the pipeline to the hard drive
            using var outputWriter = chunker.CreateStreamWriter(outputVocalsPath);

            for (int i = 0; i < totalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(((double)i / totalChunks, i + 1, totalChunks, _activeExecutionProvider));

                float[] tensorData = await Task.Run(() => chunker.ReadAndTransformChunk(inputWavPath, i), cancellationToken);

                float[] outputTensorData = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return RunInference(tensorData, inputName, outputName);
                }, cancellationToken);

                float[] audioChunk = await Task.Run(() => chunker.InverseTransformAndStitch(outputTensorData), cancellationToken);

                // NEW: Stream the output directly to the hard drive! Zero RAM buildup!
                chunker.WriteChunkToStream(outputWriter, audioChunk, ref remainingSamples);

                // Aggressive memory sweep after every chunk
                GC.Collect(2, GCCollectionMode.Optimized, false);
            }

            Log.Information("Inference complete. File successfully streamed to disk.");
            progress?.Report((1.0, totalChunks, totalChunks, _activeExecutionProvider));
        }

        private float[] RunInference(float[] tensorData, string inputName, string outputName)
        {
            if (_session == null) throw new InvalidOperationException("Model not loaded.");

            int[] shape = new[] { 1, 4, 3072, 256 };
            var inputTensor = new DenseTensor<float>(tensorData, shape);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
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

        private void ValidateModelMetadata()
        {
            if (_session == null) return;
            try
            {
                var modelMetadata = _session.ModelMetadata;
                var inputMetadata = _session.InputMetadata;
                
                Log.Debug("Model loaded: {Description} | Producer: {ProducerName} | Version: {Version}", 
                    modelMetadata.Description, modelMetadata.ProducerName, modelMetadata.Version);

                foreach (var kvp in inputMetadata)
                {
                    Log.Debug("Model Input '{Key}': [{Dimensions}] ({ElementType})", 
                        kvp.Key, string.Join(", ", kvp.Value.Dimensions), kvp.Value.ElementType);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Model metadata validation warning.");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _session?.Dispose();
                _session = null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Exception during InferenceEngine disposal.");
            }
        }
    }
}
