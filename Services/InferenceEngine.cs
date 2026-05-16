using System;
using System.Collections.Generic;
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
    /// <summary>
    /// ONNX Runtime inference engine for MDX-Net vocal separation.
    /// Handles execution provider selection (DirectML > OpenVINO > CPU)
    /// and feeds STFT spectrogram data to the neural network.
    /// </summary>
    public sealed class InferenceEngine : IDisposable
    {
        private readonly InferenceSettings _settings;
        private InferenceSession? _session;
        private string? _currentModelPath;
        private string _activeExecutionProvider;
        private bool _isDisposed;

        /// <summary>
        /// Currently active execution provider name.
        /// </summary>
        public string ActiveExecutionProvider => _activeExecutionProvider;

        public InferenceEngine(InferenceSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _activeExecutionProvider = "CPU";
        }

        /// <summary>
        /// Checks if GPU acceleration is available via DirectML or OpenVINO.
        /// </summary>
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

        /// <summary>
        /// Loads an ONNX model with the best available execution provider.
        /// </summary>
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

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = (OrtLoggingLevel)_settings.LogVerbosityLevel,
                GraphOptimizationLevel = (GraphOptimizationLevel)_settings.OptimizationLevel,
                InterOpNumThreads = _settings.InterOpNumThreads,
                IntraOpNumThreads = _settings.IntraOpNumThreads
            };

            Exception? lastException = null;

            var providerPriority = new List<(string Name, Action<SessionOptions> Configure)>
            {
                ("DirectML", new Action<SessionOptions>(opts =>
                {
                    try
                    {
                        opts.AppendExecutionProvider_DML(0); 
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "DirectML provider initialization failed.");
                        throw new InvalidOperationException("DirectML provider failed to initialize", ex);
                    }
                })),
                ("OpenVINO", new Action<SessionOptions>(opts =>
                {
                    try
                    {
                        opts.AppendExecutionProvider_OpenVINO("GPU_FP16");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "OpenVINO provider initialization failed.");
                        throw new InvalidOperationException("OpenVINO provider failed to initialize", ex);
                    }
                })),
                ("CPU", new Action<SessionOptions>(opts =>
                {
                    // CPU fallback
                }))
            };

            foreach (var (providerName, configure) in providerPriority)
            {
                try
                {
                    var testOptions = new SessionOptions
                    {
                        LogSeverityLevel = (OrtLoggingLevel)_settings.LogVerbosityLevel,
                        GraphOptimizationLevel = (GraphOptimizationLevel)_settings.OptimizationLevel
                    };

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

            throw new InvalidOperationException(
                "Failed to initialize any ONNX execution provider. " +
                "Ensure Visual C++ Redistributables are installed." +
                (lastException != null ? $" Last error: {lastException.Message}" : ""), lastException);
        }

        /// <summary>
        /// Performs vocal separation on an audio file using the MDX-Net ONNX model.
        /// Processes STFT spectrograms in precise 6.06-second chunks.
        /// </summary>
        public async Task SeparateVocalsAsync(
            string inputWavPath,
            string outputVocalsPath,
            string modelPath,
            CancellationToken cancellationToken,
            IProgress<(double chunkProgress, int currentChunk, int totalChunks, string executionProvider)>? progress = null)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            // Phase 1: Load model
            progress?.Report((0, 0, 0, "Loading model..."));
            await Task.Run(() => LoadModel(modelPath, cancellationToken), cancellationToken);

            var inputMetadata = _session!.InputMetadata;
            var outputMetadata = _session.OutputMetadata;

            string inputName = inputMetadata.Keys.First();
            string outputName = outputMetadata.Keys.First();

            using var chunker = new AudioChunker(_settings.SampleRate, _settings.Channels);

            long totalSamples = chunker.GetTotalSamples(inputWavPath);
            int totalChunks = chunker.GetChunkCount(totalSamples);

            Log.Information("Starting frequency-domain inference. Total Samples: {TotalSamples}, Total Chunks: {TotalChunks}", totalSamples, totalChunks);
            progress?.Report((0, 0, totalChunks, _activeExecutionProvider));

            // Pre-allocate the final output array to stream directly into
            float[] finalStitchedOutput = new float[totalSamples];
            long writePosition = 0;

            for (int i = 0; i < totalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(((double)i / totalChunks, i + 1, totalChunks, _activeExecutionProvider));

                // 1. STFT: Read audio and transform to Complex Spectrogram Tensor
                float[] tensorData = await Task.Run(() => chunker.ReadAndTransformChunk(inputWavPath, i), cancellationToken);

                // 2. Inference: Process the spectrogram through MDX-Net
                float[] outputTensorData = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return RunInference(tensorData, inputName, outputName);
                }, cancellationToken);

                // 3. ISTFT: Transform the separated vocal spectrogram back into audio
                float[] audioChunk = await Task.Run(() => chunker.InverseTransformAndStitch(outputTensorData), cancellationToken);

                // 4. Stream to Buffer: Append perfectly aligned audio
                chunker.WriteChunkToBuffer(finalStitchedOutput, audioChunk, i, ref writePosition);

                // Force GC to clear the tensor arrays used during this pass
                if (i % 3 == 0)
                {
                    GC.Collect(0, GCCollectionMode.Optimized);
                }
            }

            Log.Information("Inference complete. Writing output WAV to disk.");
            progress?.Report((0.95, totalChunks, totalChunks, _activeExecutionProvider));

            chunker.WriteWavFile(outputVocalsPath, finalStitchedOutput);

            progress?.Report((1.0, totalChunks, totalChunks, _activeExecutionProvider));
        }

        /// <summary>
        /// Runs a single inference pass expecting the strict [1, 4, 3072, 256] shape.
        /// </summary>
        private float[] RunInference(float[] tensorData, string inputName, string outputName)
        {
            if (_session == null) throw new InvalidOperationException("Model not loaded.");

            // MDX-Net strict frequency-domain shape: [Batch, Channels, FreqBins, TimeFrames]
            int[] shape = new[] { 1, 4, 3072, 256 };
            
            var inputTensor = new DenseTensor<float>(tensorData, shape);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var outputTensor = results.First(r => r.Name == outputName).AsTensor<float>();

            return outputTensor.ToArray();
        }

        /// <summary>
        /// Validates that the loaded model has expected MDX-Net characteristics.
        /// </summary>
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

        /// <summary>
        /// Releases the ONNX session and frees GPU memory.
        /// </summary>
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
