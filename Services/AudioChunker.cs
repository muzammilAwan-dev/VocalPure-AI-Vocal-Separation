using System;
using System.IO;
using NAudio.Wave;

namespace VocalPure.Services
{
    /// <summary>
    /// Handles memory-efficient audio chunking for large files.
    /// Splits Float32 PCM WAV audio into overlapping chunks suitable for ONNX model inference,
    /// then stitches results back together using Hann window crossfading to eliminate boundary artifacts.
    /// </summary>
    public sealed class AudioChunker : IDisposable
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _chunkDurationSeconds;
        private readonly int _overlapSeconds;
        private readonly int _chunkSize;
        private readonly int _overlapSize;
        private readonly int _hopSize;
        private readonly float[] _hannWindow;
        private bool _isDisposed;

        /// <summary>
        /// Number of samples per channel in each chunk (excluding overlap).</summary>
        public int SamplesPerChunk => _chunkSize / _channels;

        /// <summary>
        /// Number of overlapping samples per channel on each side.</summary>
        public int OverlapSamples => _overlapSize / _channels;

        /// <summary>
        /// Number of samples to hop between chunk starts.</summary>
        public int HopSamples => _hopSize / _channels;

        public AudioChunker(
            int sampleRate = 44100,
            int channels = 2,
            int chunkDurationSeconds = 30,
            int overlapSeconds = 1)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _chunkDurationSeconds = chunkDurationSeconds;
            _overlapSeconds = overlapSeconds;

            // Calculate sizes in total float samples (interleaved channels)
            _chunkSize = chunkDurationSeconds * sampleRate * channels;
            _overlapSize = overlapSeconds * sampleRate * channels;
            _hopSize = _chunkSize - (_overlapSize * 2);

            if (_hopSize <= 0)
                throw new ArgumentException("Overlap is too large for chunk size. Reduce overlap or increase chunk duration.");

            // Pre-compute Hann window for crossfading
            _hannWindow = CreateHannWindow(_overlapSize);
        }

        /// <summary>
        /// Reads a WAV file and returns the total number of float samples (interleaved).</summary>
        public long GetTotalSamples(string wavFilePath)
        {
            using var reader = new WaveFileReader(wavFilePath);
            ValidateFormat(reader);
            long sampleCount = reader.Length / sizeof(float);
            return sampleCount;
        }

        /// <summary>
        /// Gets the total number of chunks needed for a given sample count.</summary>
        public int GetChunkCount(long totalSamples)
        {
            if (totalSamples <= _chunkSize)
                return 1;

            // Account for overlap: effective samples per chunk = hop size
            long effectiveSamples = totalSamples - (_overlapSize * 2);
            if (effectiveSamples <= 0) return 1;

            return (int)Math.Ceiling((double)effectiveSamples / _hopSize) + 1;
        }

        /// <summary>
        /// Reads a specific chunk of audio data from a WAV file.
        /// </summary>
        /// <param name="wavFilePath">Source WAV file path.</param>
        /// <param name="chunkIndex">Zero-based chunk index.</param>
        /// <returns>Float array of interleaved audio samples with overlap regions.</returns>
        public float[] ReadChunk(string wavFilePath, int chunkIndex)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            long totalSamples = GetTotalSamples(wavFilePath);

            if (totalSamples <= _chunkSize)
            {
                // File fits in single chunk
                return ReadAllSamples(wavFilePath);
            }

            // Calculate the start position in the file (in samples, accounting for overlap)
            long chunkStartSample = (long)chunkIndex * _hopSize;
            long readStart = Math.Max(0, chunkStartSample - _overlapSize);
            long readEnd = Math.Min(totalSamples, chunkStartSample + _hopSize + _overlapSize);
            long readCount = readEnd - readStart;

            // Pad if this is the first or last chunk
            long paddedStartOffset = 0;
            if (chunkIndex == 0)
            {
                // First chunk: pad at start with zeros if needed
                paddedStartOffset = _overlapSize;
                readCount += paddedStartOffset;
            }

            // Ensure last chunk has full size with padding
            if (readEnd >= totalSamples && readCount < _chunkSize)
            {
                readCount = _chunkSize;
            }

            var chunk = new float[readCount];

            using var reader = new WaveFileReader(wavFilePath);
            ValidateFormat(reader);

            // Skip to read position (in bytes)
            long bytePosition = readStart * sizeof(float);
            reader.Position = Math.Min(bytePosition, reader.Length);

            // Read samples
            int samplesToRead = (int)Math.Min(readCount - paddedStartOffset,
                (totalSamples - (reader.Position / sizeof(float))));

            if (samplesToRead > 0)
            {
                byte[] buffer = new byte[samplesToRead * sizeof(float)];
                int bytesRead = reader.Read(buffer, 0, buffer.Length);
                int samplesRead = bytesRead / sizeof(float);

                Buffer.BlockCopy(buffer, 0, chunk, (int)paddedStartOffset * sizeof(float), samplesRead * sizeof(float));
            }

            return chunk;
        }

        /// <summary>
        /// Applies crossfade windowing to chunk boundaries for smooth stitching.
        /// Modifies the chunk in-place for the overlap regions.
        /// </summary>
        /// <param name="chunk">The audio chunk to window.</param>
        /// <param name="chunkIndex">Index of this chunk.</param>
        /// <param name="totalChunks">Total number of chunks.</param>
        public void ApplyWindow(float[] chunk, int chunkIndex, int totalChunks)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));
            if (chunk.Length < _overlapSize * 2) return; // Too small to window

            // Fade-in at the beginning (except for first chunk)
            if (chunkIndex > 0)
            {
                for (int i = 0; i < _overlapSize; i++)
                {
                    chunk[i] *= _hannWindow[i]; // Rising half of Hann
                }
            }

            // Fade-out at the end (except for last chunk)
            if (chunkIndex < totalChunks - 1)
            {
                int fadeStart = chunk.Length - _overlapSize;
                for (int i = 0; i < _overlapSize; i++)
                {
                    int idx = fadeStart + i;
                    if (idx < chunk.Length)
                    {
                        // Falling half of Hann
                        chunk[idx] *= _hannWindow[_overlapSize - 1 - i];
                    }
                }
            }
        }

        /// <summary>
        /// Stitches processed chunks together into a complete output array.
        /// </summary>
        /// <param name="chunks">Array of processed chunks.</param>
        /// <param name="totalOutputSamples">Expected total output sample count.</param>
        /// <returns>Combined audio data.</returns>
        public float[] StitchChunks(float[][] chunks, long totalOutputSamples)
        {
            if (chunks.Length == 1)
                return chunks[0];

            var result = new float[totalOutputSamples];
            long writePosition = 0;

            for (int i = 0; i < chunks.Length; i++)
            {
                float[] chunk = chunks[i];

                if (i == 0)
                {
                    // First chunk: write everything except trailing overlap
                    int writeLength = chunk.Length - _overlapSize;
                    Array.Copy(chunk, 0, result, 0, writeLength);
                    writePosition = writeLength;
                }
                else if (i == chunks.Length - 1)
                {
                    // Last chunk: write starting from overlap (adds to existing data)
                    int startIdx = _overlapSize;
                    int writeLength = (int)Math.Min(chunk.Length - startIdx, totalOutputSamples - writePosition);

                    for (int j = 0; j < writeLength; j++)
                    {
                        int destIdx = (int)(writePosition + j);
                        if (destIdx < result.Length)
                            result[destIdx] += chunk[startIdx + j];
                    }
                }
                else
                {
                    // Middle chunk: add overlap region, write non-overlap
                    // Add the leading overlap to existing data
                    for (int j = 0; j < _overlapSize && writePosition - _overlapSize + j >= 0; j++)
                    {
                        int destIdx = (int)(writePosition - _overlapSize + j);
                        if (destIdx >= 0 && destIdx < result.Length)
                            result[destIdx] += chunk[j];
                    }

                    // Write the non-overlapping portion
                    int startIdx = _overlapSize;
                    int writeLength = chunk.Length - (_overlapSize * 2);
                    Array.Copy(chunk, startIdx, result, writePosition, writeLength);
                    writePosition += writeLength;
                }
            }

            return result;
        }

        /// <summary>
        /// Writes float audio data to a WAV file.
        /// </summary>
        public void WriteWavFile(string filePath, float[] samples)
        {
            var format = new WaveFormat(_sampleRate, 32, _channels);
            using var writer = new WaveFileWriter(filePath, format);

            byte[] byteBuffer = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, byteBuffer, 0, byteBuffer.Length);
            writer.Write(byteBuffer, 0, byteBuffer.Length);
        }

        /// <summary>
        /// Validates that the WAV file has the expected format (Float32, 44100Hz, Stereo).
        /// </summary>
        private void ValidateFormat(WaveFileReader reader)
        {
            if (reader.WaveFormat.BitsPerSample != 32)
                throw new InvalidOperationException($"Expected 32-bit float WAV, got {reader.WaveFormat.BitsPerSample}-bit.");

            if (reader.WaveFormat.SampleRate != _sampleRate)
                throw new InvalidOperationException($"Expected {_sampleRate}Hz, got {reader.WaveFormat.SampleRate}Hz.");

            if (reader.WaveFormat.Channels != _channels)
                throw new InvalidOperationException($"Expected {_channels} channels, got {reader.WaveFormat.Channels}.");
        }

        /// <summary>
        /// Reads all samples from a WAV file into a float array.
        /// </summary>
        private static float[] ReadAllSamples(string wavFilePath)
        {
            using var reader = new WaveFileReader(wavFilePath);
            byte[] buffer = new byte[reader.Length];
            int bytesRead = reader.Read(buffer, 0, buffer.Length);
            float[] samples = new float[bytesRead / sizeof(float)];
            Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead);
            return samples;
        }

        /// <summary>
        /// Creates a Hann window array for crossfading.
        /// The first half is rising (0->1), second half is falling (1->0).
        /// </summary>
        private static float[] CreateHannWindow(int length)
        {
            var window = new float[length];
            for (int i = 0; i < length; i++)
            {
                // Hann window: w(n) = 0.5 * (1 - cos(2*pi*n / (N-1)))
                window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (length - 1)));
            }
            return window;
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
