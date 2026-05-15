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
    /// Handles execution provider selection (DirectML > OpenVINO > CPU),
    /// chunk-based processing for memory efficiency, and tensor management.
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
        /// Processes audio in chunks to prevent OutOfMemoryException.
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

            int[] inputShape = inputMetadata[inputName].Dimensions;
            long modelSampleSize = inputShape[inputShape.Length - 1]; 

            using var chunker = new AudioChunker(
                _settings.SampleRate,
                _settings.Channels,
                _settings.ChunkDurationSeconds,
                _settings.OverlapSeconds);

            long totalSamples = chunker.GetTotalSamples(inputWavPath);
            int totalChunks = chunker.GetChunkCount(totalSamples);

            Log.Information("Starting inference. Total Samples: {TotalSamples}, Total Chunks: {TotalChunks}", totalSamples, totalChunks);
            progress?.Report((0, 0, totalChunks, _activeExecutionProvider));

            // FIX 1 (RAM Time Bomb): Allocate the massive stitched array exactly ONCE instead of holding chunks in a List
            float[] finalStitchedOutput = new float[totalSamples];
            long writePosition = 0;

            // FIX 2 (LOH Fragmentation): Pre-allocate large buffers once to reuse during inference to prevent GC spiking
            int maxBufferSize = chunker.SamplesPerChunk * chunker.Channels * 3; // Safety margin
            float[] paddedBuffer = new float[maxBufferSize];
            float[] planarBuffer = new float[maxBufferSize];
            float[] interleavedBuffer = new float[maxBufferSize];

            for (int i = 0; i < totalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(((double)i / totalChunks, i + 1, totalChunks, _activeExecutionProvider));

                float[] chunk = await Task.Run(() => chunker.ReadChunk(inputWavPath, i), cancellationToken);

                float[] outputChunk = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Pass reusable buffers to prevent LOH allocations
                    return RunInference(chunk, inputName, outputName, paddedBuffer, planarBuffer, interleavedBuffer);
                }, cancellationToken);

                chunker.ApplyWindow(outputChunk, i, totalChunks);

                // Stream directly to the massive array, abandoning the small chunk
                chunker.WriteChunkToBuffer(finalStitchedOutput, outputChunk, i, totalChunks, ref writePosition);

                // Force GC to clear the small abandoned objects
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
        /// Runs a single inference pass on an audio chunk using pre-allocated buffers.
        /// </summary>
        private float[] RunInference(
            float[] audioChunk, 
            string inputName, 
            string outputName, 
            float[] paddedBuffer, 
            float[] planarBuffer, 
            float[] interleavedBuffer)
        {
            if (_session == null) throw new InvalidOperationException("Model not loaded.");

            var inputMetadata = _session.InputMetadata[inputName];
            int[] expectedDims = inputMetadata.Dimensions;

            Tensor<float> inputTensor;
            int[] actualShape;

            if (expectedDims.Length == 3)
            {
                int channelDim = expectedDims[1] == -1 ? _settings.Channels : expectedDims[1];
                long sampleDim = expectedDims[2] == -1 ? audioChunk.Length / channelDim : expectedDims[2];

                actualShape = new[] { 1, channelDim, (int)sampleDim };
                int requiredLength = (int)(channelDim * sampleDim);

                // Reusing paddedBuffer
                Array.Clear(paddedBuffer, 0, requiredLength);
                Array.Copy(audioChunk, 0, paddedBuffer, 0, Math.Min(audioChunk.Length, requiredLength));

                // Reusing planarBuffer
                InterleavedToPlanar(paddedBuffer, planarBuffer, channelDim, requiredLength);

                inputTensor = new DenseTensor<float>(planarBuffer.AsMemory(0, requiredLength), actualShape);
            }
            else if (expectedDims.Length == 2)
            {
                long sampleDim = expectedDims[1] == -1 ? audioChunk.Length : expectedDims[1];
                actualShape = new[] { 1, (int)sampleDim };
                int requiredLength = (int)sampleDim;

                // Reusing paddedBuffer
                Array.Clear(paddedBuffer, 0, requiredLength);
                Array.Copy(audioChunk, 0, paddedBuffer, 0, Math.Min(audioChunk.Length, requiredLength));

                inputTensor = new DenseTensor<float>(paddedBuffer.AsMemory(0, requiredLength), actualShape);
            }
            else
            {
                actualShape = new[] { audioChunk.Length };
                inputTensor = new DenseTensor<float>(audioChunk, actualShape);
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var outputTensor = results.First(r => r.Name == outputName).AsTensor<float>();

            float[] rawOutput = outputTensor.ToArray();

            if (expectedDims.Length == 3 && _settings.Channels == 2)
            {
                // Reusing interleavedBuffer
                PlanarToInterleaved(rawOutput, interleavedBuffer, outputTensor.Dimensions);
                
                float[] finalChunk = new float[audioChunk.Length];
                Array.Copy(interleavedBuffer, 0, finalChunk, 0, audioChunk.Length);
                return finalChunk;
            }
            else
            {
                float[] finalChunk = new float[audioChunk.Length];
                Array.Copy(rawOutput, 0, finalChunk, 0, Math.Min(rawOutput.Length, audioChunk.Length));
                return finalChunk;
            }
        }

        /// <summary>
        /// Converts interleaved audio (L,R,L,R) to planar format (L...,R...) into a pre-allocated buffer.
        /// </summary>
        private static void InterleavedToPlanar(float[] interleaved, float[] planar, int channels, int requiredLength)
        {
            int samplesPerChannel = requiredLength / channels;
            for (int ch = 0; ch < channels; ch++)
            {
                for (int s = 0; s < samplesPerChannel; s++)
                {
                    planar[ch * samplesPerChannel + s] = interleaved[s * channels + ch];
                }
            }
        }

        /// <summary>
        /// Converts planar audio (L...,R...) back to interleaved format (L,R,L,R) into a pre-allocated buffer.
        /// </summary>
        private static void PlanarToInterleaved(float[] planar, float[] interleaved, ReadOnlySpan<int> outputShape)
        {
            if (outputShape.Length < 3) 
            {
                Array.Copy(planar, interleaved, planar.Length);
                return;
            }

            int batchSize = outputShape[0];
            int channels = outputShape[1];
            int samplesPerChannel = outputShape[2];

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    for (int s = 0; s < samplesPerChannel; s++)
                    {
                        int planarIdx = b * channels * samplesPerChannel + ch * samplesPerChannel + s;
                        int interleavedIdx = b * channels * samplesPerChannel + s * channels + ch;
                        interleaved[interleavedIdx] = planar[planarIdx];
                    }
                }
            }
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
                var outputMetadata = _session.OutputMetadata;

                if (inputMetadata.Count == 0)
                    throw new InvalidOperationException("Model has no input nodes.");

                if (outputMetadata.Count == 0)
                    throw new InvalidOperationException("Model has no output nodes.");

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
