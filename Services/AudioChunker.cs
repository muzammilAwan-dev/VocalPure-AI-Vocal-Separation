using System;
using System.IO;
using System.Threading.Tasks;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;

namespace VocalPure.Services
{
    public sealed class AudioChunker : IDisposable
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private bool _isDisposed;

        public const int FftSize = 6144;
        public const int HopSize = 1024;
        public const int TimeFrames = 256;
        public const int FreqBins = 3072;
        
        public const int ChunkSize = (TimeFrames - 1) * HopSize + FftSize;
        public const int Trim = FftSize / 2;
        public const int GenSize = ChunkSize - 2 * Trim;

        private readonly float[] _window;

        // --- ZERO-ALLOCATION MEMORY POOL ---
        private readonly float[] _readBuffer;
        private readonly float[] _leftBuf;
        private readonly float[] _rightBuf;
        private readonly float[] _tensorData;
        private readonly float[] _leftOutBuf;
        private readonly float[] _rightOutBuf;
        private readonly float[] _leftWindowSum;
        private readonly float[] _rightWindowSum;
        
        private readonly Complex32[] _leftComplexBuf;
        private readonly Complex32[] _rightComplexBuf;
        private readonly float[] _finalChunkBuf;

        public AudioChunker(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            
            _window = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / FftSize));
            }

            _readBuffer = new float[ChunkSize * _channels];
            _leftBuf = new float[ChunkSize];
            _rightBuf = new float[ChunkSize];
            _tensorData = new float[4 * FreqBins * TimeFrames];
            _leftOutBuf = new float[ChunkSize];
            _rightOutBuf = new float[ChunkSize];
            _leftWindowSum = new float[ChunkSize];
            _rightWindowSum = new float[ChunkSize];
            _leftComplexBuf = new Complex32[FftSize];
            _rightComplexBuf = new Complex32[FftSize];
            _finalChunkBuf = new float[ChunkSize * _channels];
        }

        // CRITICAL FIX: Accepts the persistent reader instead of reopening the file path
        public long GetTotalSamples(AudioFileReader reader)
        {
            return reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        }

        public int GetChunkCount(long totalSamples)
        {
            long samplesPerChannel = totalSamples / _channels;
            long pad = GenSize - (samplesPerChannel % GenSize);
            long totalPadded = samplesPerChannel + pad;
            return (int)(totalPadded / GenSize);
        }

        // CRITICAL FIX: Utilizes the shared reader, moving the read position seamlessly
        public float[] ReadAndTransformChunk(AudioFileReader reader, int chunkIndex)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            Array.Clear(_leftBuf, 0, ChunkSize);
            Array.Clear(_rightBuf, 0, ChunkSize);
            Array.Clear(_tensorData, 0, _tensorData.Length);

            long startSample = (long)chunkIndex * GenSize;
            long readStart = startSample - Trim;
            
            long fileStart = Math.Max(0, readStart);
            int trimStart = (int)(fileStart - readStart); 

            long fileTotalPerChannel = reader.Length / reader.WaveFormat.BlockAlign;
            long fileSamplesToRead = Math.Min(ChunkSize - trimStart, fileTotalPerChannel - fileStart);
            
            if (fileSamplesToRead > 0)
            {
                reader.Position = fileStart * reader.WaveFormat.BlockAlign;
                int floatsRead = reader.Read(_readBuffer, 0, (int)fileSamplesToRead * _channels);
                
                int validSamples = floatsRead / _channels;
                for (int i = 0; i < validSamples; i++)
                {
                    _leftBuf[trimStart + i] = _readBuffer[i * _channels];
                    _rightBuf[trimStart + i] = _channels > 1 ? _readBuffer[i * _channels + 1] : _readBuffer[i * _channels];
                }
            }

            Parallel.Invoke(
                () => ProcessStftChannel(_leftBuf, _tensorData, 0, 1, _leftComplexBuf),
                () => ProcessStftChannel(_rightBuf, _tensorData, 2, 3, _rightComplexBuf)
            );

            return _tensorData;
        }

        private void ProcessStftChannel(float[] channelAudio, float[] tensorData, int realIdx, int imagIdx, Complex32[] complexBuf)
        {
            for (int t = 0; t < TimeFrames; t++)
            {
                int offset = t * HopSize;
                for (int i = 0; i < FftSize; i++)
                {
                    complexBuf[i] = new Complex32(channelAudio[offset + i] * _window[i], 0f);
                }

                Fourier.Forward(complexBuf, FourierOptions.Matlab);

                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int rLoc = (realIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    int iLoc = (imagIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;

                    tensorData[rLoc] = complexBuf[freq].Real;
                    tensorData[iLoc] = complexBuf[freq].Imaginary;
                }
            }
        }

        public float[] InverseTransformAndStitch(float[] tensorData)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            Array.Clear(_leftOutBuf, 0, ChunkSize);
            Array.Clear(_rightOutBuf, 0, ChunkSize);
            Array.Clear(_leftWindowSum, 0, ChunkSize);
            Array.Clear(_rightWindowSum, 0, ChunkSize);

            Parallel.Invoke(
                () => ProcessIstftChannel(tensorData, _leftOutBuf, _leftWindowSum, 0, 1, _leftComplexBuf),
                () => ProcessIstftChannel(tensorData, _rightOutBuf, _rightWindowSum, 2, 3, _rightComplexBuf)
            );

            for (int i = 0; i < ChunkSize; i++)
            {
                float normL = _leftWindowSum[i] > 1e-7f ? _leftWindowSum[i] : 1f;
                float normR = _rightWindowSum[i] > 1e-7f ? _rightWindowSum[i] : 1f;
                
                _finalChunkBuf[i * 2] = _leftOutBuf[i] / normL;
                _finalChunkBuf[i * 2 + 1] = _rightOutBuf[i] / normR;
            }

            return _finalChunkBuf;
        }

        private void ProcessIstftChannel(float[] tensorData, float[] channelOut, float[] windowSum, int realIdx, int imagIdx, Complex32[] complexBuf)
        {
            for (int t = 0; t < TimeFrames; t++)
            {
                for (int freq = 0; freq < FreqBins; freq++)
                {
                    int rLoc = (realIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    int iLoc = (imagIdx * FreqBins * TimeFrames) + (freq * TimeFrames) + t;
                    complexBuf[freq] = new Complex32(tensorData[rLoc], tensorData[iLoc]);
                }

                complexBuf[FreqBins] = Complex32.Zero;

                for (int f = 1; f < FftSize / 2; f++)
                {
                    complexBuf[FftSize - f] = new Complex32(complexBuf[f].Real, -complexBuf[f].Imaginary);
                }

                Fourier.Inverse(complexBuf, FourierOptions.Matlab);

                int offset = t * HopSize;
                for (int i = 0; i < FftSize; i++)
                {
                    channelOut[offset + i] += complexBuf[i].Real * _window[i];
                    windowSum[offset + i] += _window[i] * _window[i];
                }
            }
        }

        public WaveFileWriter CreateStreamWriter(string filePath)
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            return new WaveFileWriter(filePath, format);
        }

        public void WriteChunkToStream(WaveFileWriter writer, float[] chunk, ref long remainingSamplesToWrite)
        {
            int trimSamples = Trim * _channels;
            int validLength = GenSize * _channels;
            int writeLength = (int)Math.Min(validLength, remainingSamplesToWrite);
            
            if (writeLength > 0)
            {
                writer.WriteSamples(chunk, trimSamples, writeLength);
                remainingSamplesToWrite -= writeLength;
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
