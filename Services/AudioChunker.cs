using System;
using System.IO;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;

namespace VocalPure.Services
{
    /// <summary>
    /// Advanced STFT Audio Processor strictly matching UVR5 MDX-Net Python architecture.
    /// Safely handles float conversion and precisely pads U-Net boundaries to prevent artifacting.
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
        private const int FreqBins = 3072;
        
        // UVR5 Overlap-Add Architecture
        public const int ChunkSize = (TimeFrames - 1) * HopSize + FftSize; // 267,264 samples
        public const int Trim = FftSize / 2;                               // 3,072 samples
        public const int GenSize = ChunkSize - 2 * Trim;                   // 261,120 samples (The true step size)

        private readonly float[] _window;

        public AudioChunker(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            
            // Generate Hann Window matching torch.hann_window(periodic=True)
            _window = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / FftSize));
            }
        }

        public long GetTotalSamples(string wavFilePath)
        {
            // AudioFileReader safely resolves the bit-depth corruption issue
            using var reader = new AudioFileReader(wavFilePath);
            return reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        }

        public int GetChunkCount(long totalSamples)
        {
            long samplesPerChannel = totalSamples / _channels;
            long pad = GenSize - (samplesPerChannel % GenSize);
            long totalPadded = samplesPerChannel + pad;
            return (int)(totalPadded / GenSize);
        }

        public float[] ReadAndTransformChunk(string wavFilePath, int chunkIndex)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            long startSample = (long)chunkIndex * GenSize;
            long readStart = startSample - Trim; // UVR5 left padding
            
            long fileStart = Math.Max(0, readStart);
            int trimStart = (int)(fileStart - readStart); 
            
            float[] left = new float[ChunkSize];
            float[] right = new float[ChunkSize];

            // 1. Safe Float Extraction
            using (var reader = new AudioFileReader(wavFilePath))
            {
                long fileTotalPerChannel = reader.Length / reader.WaveFormat.BlockAlign;
                long fileSamplesToRead = Math.Min(ChunkSize - trimStart, fileTotalPerChannel - fileStart);
                
                if (fileSamplesToRead > 0)
                {
                    reader.Position = fileStart * reader.WaveFormat.BlockAlign;
                    float[] buffer = new float[fileSamplesToRead * _channels];
                    int floatsRead = reader.Read(buffer, 0, buffer.Length);
                    
                    int validSamples = floatsRead / _channels;
                    for (int i = 0; i < validSamples; i++)
                    {
                        left[trimStart + i] = buffer[i * _channels];
                        right[trimStart + i] = _channels > 1 ? buffer[i * _channels + 1] : buffer[i * _channels];
                    }
                }
            }

            // 2. Prepare PyTorch Tensor: [0: LeftReal, 1: LeftImag, 2: RightReal, 3: RightImag]
            float[] tensorData = new float[4 * FreqBins * TimeFrames];
            Complex32[] complexBuffer = new Complex32[FftSize];

            ProcessStftChannel(left, tensorData, 0, 1, complexBuffer);
            ProcessStftChannel(right, tensorData, 2, 3, complexBuffer);

            return tensorData;
        }

        private void ProcessStftChannel(float[] channelAudio, float[] tensorData, int realChannelIdx, int imagChannelIdx, Complex32[] complexBuffer)
        {
            for (int t = 0; t < TimeFrames; t++)
            {
                int offset = t * HopSize;
                
                for (int i = 0; i < FftSize; i++)
                {
                    complexBuffer[i] = new Complex32(channelAudio[offset + i] * _window[i], 0f);
                }

                Fourier.Forward(complexBuffer, FourierOptions.Matlab);

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

            float[] leftOut = new float[ChunkSize];
            float[] rightOut = new float[ChunkSize];
            float[] windowSum = new float[ChunkSize];
            Complex32[] complexBuffer = new Complex32[FftSize];

            ProcessIstftChannel(tensorData, leftOut, windowSum, 0, 1, complexBuffer);
            ProcessIstftChannel(tensorData, rightOut, null, 2, 3, complexBuffer);

            float[] finalChunk = new float[ChunkSize * _channels];
            for (int i = 0; i < ChunkSize; i++)
            {
                // NOLA Reconstruction matching torch.istft
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
                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int rIdx = (realChannelIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    int iIdx = (imagChannelIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    complexBuffer[freq] = new Complex32(tensorData[rIdx], tensorData[iIdx]);
                }

                complexBuffer[FreqBins] = Complex32.Zero; // Nyquist pad

                for (int f = 1; f < FftSize / 2; f++)
                {
                    complexBuffer[FftSize - f] = new Complex32(complexBuffer[f].Real, -complexBuffer[f].Imaginary);
                }

                Fourier.Inverse(complexBuffer, FourierOptions.Matlab);

                int offset = t * HopSize;
                for (int i = 0; i < FftSize; i++)
                {
                    channelOut[offset + i] += complexBuffer[i].Real * _window[i];
                    if (windowSum != null) windowSum[offset + i] += _window[i] * _window[i];
                }
            }
        }

        public void WriteChunkToBuffer(float[] resultBuffer, float[] chunk, int chunkIndex, ref long writePosition)
        {
            // UVR5 Strip Logic: Drop the padded edges that contain U-Net artifacts
            int trimSamples = Trim * _channels;
            int validLength = GenSize * _channels;

            int copyLength = Math.Min(validLength, resultBuffer.Length - (int)writePosition);
            
            if (copyLength > 0)
            {
                Array.Copy(chunk, trimSamples, resultBuffer, writePosition, copyLength);
                writePosition += copyLength;
            }
        }

        public void WriteWavFile(string filePath, float[] samples)
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
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
