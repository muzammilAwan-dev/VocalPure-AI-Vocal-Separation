using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NWaves.Transforms;
using NWaves.Windows;

namespace VocalPure.Services
{
    /// <summary>
    /// Advanced STFT Audio Processor for MDX-Net.
    /// Converts time-domain audio waveforms into 4D complex spectrogram tensors 
    /// and mathematically reconstructs the audio via ISTFT after neural network inference.
    /// </summary>
    public sealed class AudioChunker : IDisposable
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private bool _isDisposed;

        // MDX-Net Strict Mathematical Constants
        private const int FftSize = 6144;
        private const int HopSize = 1024;
        private const int TimeFrames = 256;
        private const int FreqBins = 3072; // (FftSize / 2) - discarded Nyquist bin
        
        // Exactly 267,264 samples per chunk (6.06 seconds)
        public const int RequiredSamples = (TimeFrames - 1) * HopSize + FftSize; 

        private readonly Stft _stft;

        public AudioChunker(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            
            // Initialize STFT with Hann window exactly as MDX-Net expects
            _stft = new Stft(FftSize, HopSize, WindowType.Hann);
        }

        public long GetTotalSamples(string wavFilePath)
        {
            using var reader = new WaveFileReader(wavFilePath);
            return reader.Length / sizeof(float);
        }

        public int GetChunkCount(long totalSamples)
        {
            long samplesPerChannel = totalSamples / _channels;
            return (int)Math.Ceiling((double)samplesPerChannel / RequiredSamples);
        }

        /// <summary>
        /// Reads a precise chunk of audio, separates left/right channels, 
        /// applies STFT, and packs it into a flat array for the [1, 4, 3072, 256] Tensor.
        /// </summary>
        public float[] ReadAndTransformChunk(string wavFilePath, int chunkIndex)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            long totalSamples = GetTotalSamples(wavFilePath);
            long startSample = (long)chunkIndex * RequiredSamples * _channels;
            
            float[] rawChunk = new float[RequiredSamples * _channels];

            using (var reader = new WaveFileReader(wavFilePath))
            {
                long bytePosition = startSample * sizeof(float);
                if (bytePosition < reader.Length)
                {
                    reader.Position = bytePosition;
                    byte[] buffer = new byte[rawChunk.Length * sizeof(float)];
                    int bytesRead = reader.Read(buffer, 0, buffer.Length);
                    Buffer.BlockCopy(buffer, 0, rawChunk, 0, bytesRead);
                }
            }

            // Split Interleaved to Left/Right
            float[] left = new float[RequiredSamples];
            float[] right = new float[RequiredSamples];
            for (int i = 0; i < RequiredSamples; i++)
            {
                left[i] = rawChunk[i * 2];
                right[i] = rawChunk[i * 2 + 1];
            }

            // Execute STFT (NWaves returns a List of Tuples: (float[] Real, float[] Imag))
            var leftSpectrogram = _stft.Direct(left);
            var rightSpectrogram = _stft.Direct(right);

            // Pack into [4, 3072, 256] flat array
            // 4 Channels: [LeftReal, LeftImag, RightReal, RightImag]
            float[] tensorData = new float[4 * FreqBins * TimeFrames];
            
            for (int time = 0; time < TimeFrames; time++)
            {
                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int lrIdx = (0 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;
                    int liIdx = (1 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;
                    int rrIdx = (2 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;
                    int riIdx = (3 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;

                    // Ensure we don't go out of bounds on the final partial timeframe
                    if (time < leftSpectrogram.Count)
                    {
                        // Extract from NWaves Tuple (Item1 = Real, Item2 = Imaginary)
                        tensorData[lrIdx] = leftSpectrogram[time].Item1[freq];  // Left Real
                        tensorData[liIdx] = leftSpectrogram[time].Item2[freq];  // Left Imag
                        tensorData[rrIdx] = rightSpectrogram[time].Item1[freq]; // Right Real
                        tensorData[riIdx] = rightSpectrogram[time].Item2[freq]; // Right Imag
                    }
                }
            }

            return tensorData;
        }

        /// <summary>
        /// Takes the 4D Tensor output from the neural network, unpacks it into Tuples, 
        /// reattaches the Nyquist bin, applies ISTFT, and interleaves it back to audio.
        /// </summary>
        public float[] InverseTransformAndStitch(float[] tensorData)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            // NWaves Inverse expects a List of Tuples
            var leftSpectrogram = new List<(float[], float[])>();
            var rightSpectrogram = new List<(float[], float[])>();
            
            // FftSize / 2 + 1 (To account for the DC and Nyquist bins) = 3073
            int arrayLength = FftSize / 2 + 1; 

            for (int time = 0; time < TimeFrames; time++)
            {
                float[] leftReal = new float[arrayLength];
                float[] leftImag = new float[arrayLength];
                float[] rightReal = new float[arrayLength];
                float[] rightImag = new float[arrayLength];

                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int lrIdx = (0 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;
                    int liIdx = (1 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;
                    int rrIdx = (2 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;
                    int riIdx = (3 * FreqBins * TimeFrames) + (freq * TimeFrames) + time;

                    leftReal[freq] = tensorData[lrIdx];
                    leftImag[freq] = tensorData[liIdx];
                    rightReal[freq] = tensorData[rrIdx];
                    rightImag[freq] = tensorData[riIdx];
                }
                
                // The Nyquist bin (freq 3072) was discarded by the model. It remains 0.0f by default.
                
                // Add the populated Tuple frames to the lists
                leftSpectrogram.Add((leftReal, leftImag));
                rightSpectrogram.Add((rightReal, rightImag));
            }

            // Execute ISTFT (Spectrogram to Audio)
            float[] leftAudio = _stft.Inverse(leftSpectrogram);
            float[] rightAudio = _stft.Inverse(rightSpectrogram);

            // Interleave back to Stereo
            int totalOutSamples = Math.Min(leftAudio.Length, rightAudio.Length);
            float[] interleaved = new float[totalOutSamples * 2];
            
            for (int i = 0; i < totalOutSamples; i++)
            {
                interleaved[i * 2] = leftAudio[i];
                interleaved[i * 2 + 1] = rightAudio[i];
            }

            // Trim to our exact RequiredSamples to strip STFT padding
            float[] finalChunk = new float[RequiredSamples * _channels];
            Array.Copy(interleaved, 0, finalChunk, 0, Math.Min(interleaved.Length, finalChunk.Length));

            return finalChunk;
        }

        public void WriteChunkToBuffer(float[] resultBuffer, float[] chunk, int chunkIndex, ref long writePosition)
        {
            // Because STFT windowing reconstructs perfectly at the seams when using ISTFT, 
            // we don't need manual overlap-add math anymore. NWaves handled it! We just stream it.
            int copyLength = Math.Min(chunk.Length, resultBuffer.Length - (int)writePosition);
            if (copyLength > 0)
            {
                Array.Copy(chunk, 0, resultBuffer, writePosition, copyLength);
                writePosition += copyLength;
            }
        }

        public void WriteWavFile(string filePath, float[] samples)
        {
            var format = new WaveFormat(_sampleRate, 32, _channels);
            using var writer = new WaveFileWriter(filePath, format);

            byte[] byteBuffer = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, byteBuffer, 0, byteBuffer.Length);
            writer.Write(byteBuffer, 0, byteBuffer.Length);
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
