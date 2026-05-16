using System;
using System.IO;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;

namespace VocalPure.Services
{
    /// <summary>
    /// Advanced STFT Audio Processor for MDX-Net using MathNet.Numerics.
    /// Hand-rolled, mathematically precise STFT/ISTFT pipeline that supports 
    /// non-power-of-2 FFTs (like 6144) without padding distortion.
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
        
        /// <summary>
        /// Exactly 267,264 samples per chunk (6.06 seconds).
        /// </summary>
        public const int RequiredSamples = (TimeFrames - 1) * HopSize + FftSize; 

        private readonly float[] _window;

        public AudioChunker(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            
            // Generate Hann Window
            _window = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / FftSize));
            }
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
                    byte[] byteBuffer = new byte[rawChunk.Length * sizeof(float)];
                    int bytesRead = reader.Read(byteBuffer, 0, byteBuffer.Length);
                    Buffer.BlockCopy(byteBuffer, 0, rawChunk, 0, bytesRead);
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

            // Prepare Tensor Data in the precise PyTorch stack format:
            // [0: LeftReal, 1: LeftImag, 2: RightReal, 3: RightImag]
            float[] tensorData = new float[4 * FreqBins * TimeFrames];
            Complex32[] complexBuffer = new Complex32[FftSize];

            // Process Left Channel (Real is 0, Imag is 1)
            ProcessStftChannel(left, tensorData, 0, 1, complexBuffer);
            // Process Right Channel (Real is 2, Imag is 3)
            ProcessStftChannel(right, tensorData, 2, 3, complexBuffer);

            return tensorData;
        }

        private void ProcessStftChannel(float[] channelAudio, float[] tensorData, int realChannelIdx, int imagChannelIdx, Complex32[] complexBuffer)
        {
            for (int t = 0; t < TimeFrames; t++)
            {
                int offset = t * HopSize;
                
                // 1. Apply Window and copy to Complex array
                for (int i = 0; i < FftSize; i++)
                {
                    complexBuffer[i] = new Complex32(channelAudio[offset + i] * _window[i], 0f);
                }

                // 2. Perform MathNet Forward FFT
                Fourier.Forward(complexBuffer, FourierOptions.Matlab);

                // 3. Extract unique frequencies and pack into flat tensor array
                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int rIdx = (realChannelIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    int iIdx = (imagChannelIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;

                    tensorData[rIdx] = complexBuffer[freq].Real;
                    tensorData[iIdx] = complexBuffer[freq].Imaginary;
                }
            }
        }

        public float[] InverseTransformAndStitch(float[] tensorData)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            float[] leftOut = new float[RequiredSamples];
            float[] rightOut = new float[RequiredSamples];
            float[] windowSum = new float[RequiredSamples];
            Complex32[] complexBuffer = new Complex32[FftSize];

            // Process Left Channel (Real is 0, Imag is 1)
            ProcessIstftChannel(tensorData, leftOut, windowSum, 0, 1, complexBuffer);
            // Process Right Channel (Real is 2, Imag is 3)
            ProcessIstftChannel(tensorData, rightOut, null, 2, 3, complexBuffer);

            // Interleave and Normalize
            float[] finalChunk = new float[RequiredSamples * _channels];
            for (int i = 0; i < RequiredSamples; i++)
            {
                // Librosa-style overlap-add normalization (prevents volume swelling)
                float norm = windowSum[i] > 1e-7f ? windowSum[i] : 1f;
                
                finalChunk[i * 2] = leftOut[i] / norm;
                finalChunk[i * 2 + 1] = rightOut[i] / norm;
            }

            return finalChunk;
        }

        private void ProcessIstftChannel(float[] tensorData, float[] channelOut, float[]? windowSum, int realChannelIdx, int imagChannelIdx, Complex32[] complexBuffer)
        {
            for (int t = 0; t < TimeFrames; t++)
            {
                // 1. Reconstruct complex spectrum from tensor
                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int rIdx = (realChannelIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    int iIdx = (imagChannelIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    complexBuffer[freq] = new Complex32(tensorData[rIdx], tensorData[iIdx]);
                }

                // Nyquist bin is 0
                complexBuffer[FreqBins] = Complex32.Zero;

                // Complex conjugate for the mirrored upper frequencies
                for (int f = 1; f < FftSize / 2; f++)
                {
                    complexBuffer[FftSize - f] = new Complex32(complexBuffer[f].Real, -complexBuffer[f].Imaginary);
                }

                // 2. Perform MathNet Inverse FFT
                Fourier.Inverse(complexBuffer, FourierOptions.Matlab);

                // 3. Apply Window and Overlap-Add
                int offset = t * HopSize;
                for (int i = 0; i < FftSize; i++)
                {
                    channelOut[offset + i] += complexBuffer[i].Real * _window[i];
                    
                    if (windowSum != null)
                    {
                        windowSum[offset + i] += _window[i] * _window[i];
                    }
                }
            }
        }

        public void WriteChunkToBuffer(float[] resultBuffer, float[] chunk, int chunkIndex, ref long writePosition)
        {
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
