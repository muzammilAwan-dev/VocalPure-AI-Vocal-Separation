using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VocalPure.Models;
using VocalPure.Services;

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
                return availableProviders.Contains("Dml") ||
                       availableProviders.Contains("OpenVINO") ||
                       availableProviders.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Loads an ONNX model with the best available execution provider.
        /// </summary>
        /// <param name="modelPath">Path to the .onnx model file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private void LoadModel(string modelPath, CancellationToken cancellationToken)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(InferenceEngine));
            cancellationToken.ThrowIfCancellationRequested();

            // Reuse existing session if same model
            if (_session != null && _currentModelPath == modelPath)
                return;

            // Dispose existing session
            _session?.Dispose();
            _session = null;
            _currentModelPath = null;

            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"ONNX model not found: {modelPath}");

            // Build session options with execution provider priority
            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = (OrtLoggingLevel)_settings.LogVerbosityLevel,
                GraphOptimizationLevel = (GraphOptimizationLevel)_settings.OptimizationLevel,
                InterOpNumThreads = _settings.InterOpNumThreads,
                IntraOpNumThreads = _settings.IntraOpNumThreads
            };

            // Try execution providers in priority order
            Exception? lastException = null;
            var providerPriority = new List<(string Name, Action<SessionOptions> Configure)>
            {
                ("DirectML", opts =>
                {
                    try
                    {
                        // DirectML GPU acceleration
                        opts.AppendExecutionProvider_DML(0); // GPU device 0
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("DirectML provider failed to initialize", ex);
                    }
                }),
                ("OpenVINO", opts =>
                {
                    try
                    {
                        // OpenVINO for Intel GPUs/VPUs
                        var ovOptions = new Dictionary<string, string>
                        {
                            { "device_id", "GPU" },
                            { "device_type", "GPU.0" },
                            { "precision", "FP16" },
                            { "cache_dir", "" },
                            { "num_of_threads", _settings.IntraOpNumThreads.ToString() }
                        };
                        opts.AppendExecutionProvider_OpenVINO(ovOptions);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("OpenVINO provider failed to initialize", ex);
                    }
                }),
                ("CPU", opts =>
                {
                    // CPU is the default fallback - no configuration needed
                    // ORT will automatically use CPU if no GPU EP is appended
                })
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

                    // Test-load the model
                    _session = new InferenceSession(modelPath, testOptions);
                    _currentModelPath = modelPath;
                    _activeExecutionProvider = providerName;

                    // Verify model metadata
                    ValidateModelMetadata();

                    return;
                }
                catch (Exception ex) when (providerName != "CPU")
                {
                    lastException = ex;
                    // Try next provider
                    continue;
                }
            }

            // If we get here, all providers failed
            throw new InvalidOperationException(
                "Failed to initialize any ONNX execution provider. " +
                "Ensure Visual C++ Redistributables are installed." +
                (lastException != null ? $" Last error: {lastException.Message}" : ""), lastException);
        }

        /// <summary>
        /// Performs vocal separation on an audio file using the MDX-Net ONNX model.
        /// Processes audio in chunks to prevent OutOfMemoryException.
        /// </summary>
        /// <param name="inputWavPath">Input Float32 PCM WAV file path.</param>
        /// <param name="outputVocalsPath">Output vocals WAV file path.</param>
        /// <param name="modelPath">ONNX model file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="progress">Progress reporter with chunk info.</param>
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

            // Get model input info
            var inputMetadata = _session!.InputMetadata;
            var outputMetadata = _session.OutputMetadata;

            string inputName = inputMetadata.Keys.First();
            string outputName = outputMetadata.Keys.First();

            // Parse expected input shape
            int[] inputShape = inputMetadata[inputName].Dimensions;
            long modelSampleSize = inputShape[inputShape.Length - 1]; // Last dimension is typically sample count

            // Phase 2: Chunk and process audio
            using var chunker = new AudioChunker(
                _settings.SampleRate,
                _settings.Channels,
                _settings.ChunkDurationSeconds,
                _settings.OverlapSeconds);

            long totalSamples = chunker.GetTotalSamples(inputWavPath);
            int totalChunks = chunker.GetChunkCount(totalSamples);

            progress?.Report((0, 0, totalChunks, _activeExecutionProvider));

            var processedChunks = new List<float[]>();

            for (int i = 0; i < totalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(((double)i / totalChunks, i + 1, totalChunks, _activeExecutionProvider));

                // Read chunk from file
                float[] chunk = await Task.Run(() => chunker.ReadChunk(inputWavPath, i), cancellationToken);

                // Run inference on this chunk
                float[] outputChunk = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return RunInference(chunk, inputName, outputName, modelSampleSize);
                }, cancellationToken);

                // Apply crossfade windowing
                chunker.ApplyWindow(outputChunk, i, totalChunks);

                processedChunks.Add(outputChunk);

                // Force GC between chunks to manage memory
                if (i % 3 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            progress?.Report((0.95, totalChunks, totalChunks, _activeExecutionProvider));

            // Phase 3: Stitch chunks and write output
            float[] stitchedOutput = chunker.StitchChunks(processedChunks.ToArray(), totalSamples);
            chunker.WriteWavFile(outputVocalsPath, stitchedOutput);

            progress?.Report((1.0, totalChunks, totalChunks, _activeExecutionProvider));
        }

        /// <summary>
        /// Runs a single inference pass on an audio chunk.
        /// </summary>
        private float[] RunInference(float[] audioChunk, string inputName, string outputName, long modelSampleSize)
        {
            if (_session == null) throw new InvalidOperationException("Model not loaded.");

            // Prepare input tensor with correct shape
            // MDX-Net expects (batch, channels, samples) or (batch, samples)
            // We detect the expected shape and adapt
            var inputMetadata = _session.InputMetadata[inputName];
            int[] expectedDims = inputMetadata.Dimensions;

            // Create tensor with correct shape
            Tensor<float> inputTensor;
            int[] actualShape;

            if (expectedDims.Length == 3)
            {
                // Shape: (batch=1, channels=2, samples)
                int channelDim = expectedDims[1] == -1 ? _settings.Channels : expectedDims[1];
                long sampleDim = expectedDims[2] == -1 ? audioChunk.Length / channelDim : expectedDims[2];

                actualShape = new[] { 1, channelDim, (int)sampleDim };

                // If chunk is smaller than expected, pad with zeros
                int requiredLength = (int)(channelDim * sampleDim);
                float[] paddedChunk = PadOrTrimChunk(audioChunk, requiredLength);

                // Reshape from interleaved (L,R,L,R) to planar (L...,R...)
                float[] planarData = InterleavedToPlanar(paddedChunk, channelDim);

                inputTensor = new DenseTensor<float>(planarData, actualShape);
            }
            else if (expectedDims.Length == 2)
            {
                // Shape: (batch=1, samples)
                long sampleDim = expectedDims[1] == -1 ? audioChunk.Length : expectedDims[1];
                actualShape = new[] { 1, (int)sampleDim };

                int requiredLength = (int)sampleDim;
                float[] paddedChunk = PadOrTrimChunk(audioChunk, requiredLength);

                inputTensor = new DenseTensor<float>(paddedChunk, actualShape);
            }
            else
            {
                // Fallback: 1D tensor
                actualShape = new[] { audioChunk.Length };
                inputTensor = new DenseTensor<float>(audioChunk, actualShape);
            }

            // Create input container
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            // Run inference
            using var results = _session.Run(inputs);
            var outputTensor = results.First(r => r.Name == outputName).AsTensor<float>();

            // Extract output data
            float[] outputData = outputTensor.ToArray();

            // If output is planar (channels, samples), convert back to interleaved
            if (expectedDims.Length == 3 && _settings.Channels == 2)
            {
                outputData = PlanarToInterleaved(outputData, outputTensor.Dimensions);
            }

            // Trim back to original chunk size (remove padding)
            outputData = TrimToLength(outputData, audioChunk.Length);

            return outputData;
        }

        /// <summary>
        /// Converts interleaved audio (L,R,L,R) to planar format (L...,R...).
        /// </summary>
        private static float[] InterleavedToPlanar(float[] interleaved, int channels)
        {
            int samplesPerChannel = interleaved.Length / channels;
            float[] planar = new float[interleaved.Length];

            for (int ch = 0; ch < channels; ch++)
            {
                for (int s = 0; s < samplesPerChannel; s++)
                {
                    planar[ch * samplesPerChannel + s] = interleaved[s * channels + ch];
                }
            }

            return planar;
        }

        /// <summary>
        /// Converts planar audio (L...,R...) back to interleaved format (L,R,L,R).
        /// </summary>
        private static float[] PlanarToInterleaved(float[] planar, ReadOnlySpan<int> outputShape)
        {
            if (outputShape.Length < 3) return planar;

            int batchSize = outputShape[0];
            int channels = outputShape[1];
            int samplesPerChannel = outputShape[2];

            int totalSamples = batchSize * channels * samplesPerChannel;
            float[] interleaved = new float[totalSamples];

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

            return interleaved;
        }

        /// <summary>
        /// Pads or trims a chunk to the required length.
        /// </summary>
        private static float[] PadOrTrimChunk(float[] chunk, int requiredLength)
        {
            if (chunk.Length == requiredLength)
                return chunk;

            var result = new float[requiredLength];
            int copyLength = Math.Min(chunk.Length, requiredLength);
            Array.Copy(chunk, 0, result, 0, copyLength);
            // Remainder is zero-padded (silence)
            return result;
        }

        /// <summary>
        /// Trims output back to the original chunk length.
        /// </summary>
        private static float[] TrimToLength(float[] data, int targetLength)
        {
            if (data.Length <= targetLength)
                return data;

            float[] trimmed = new float[targetLength];
            Array.Copy(data, 0, trimmed, 0, targetLength);
            return trimmed;
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

                // Log model info for debugging
                System.Diagnostics.Debug.WriteLine($"Model loaded: {modelMetadata.Description}");
                System.Diagnostics.Debug.WriteLine($"Producer: {modelMetadata.ProducerName}");
                System.Diagnostics.Debug.WriteLine($"Version: {modelMetadata.Version}");
                System.Diagnostics.Debug.WriteLine($"Inputs: {string.Join(", ", inputMetadata.Keys)}");
                System.Diagnostics.Debug.WriteLine($"Outputs: {string.Join(", ", outputMetadata.Keys)}");

                foreach (var kvp in inputMetadata)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  Input '{kvp.Key}': [{string.Join(", ", kvp.Value.Dimensions)}] ({kvp.Value.ElementType})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Model metadata validation warning: {ex.Message}");
                // Don't fail on metadata warnings
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
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
