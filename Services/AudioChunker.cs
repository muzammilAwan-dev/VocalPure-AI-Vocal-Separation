using System;
using System.IO;
using System.Threading.Tasks;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

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
        private readonly float[] _leftBuf;
        private readonly float[] _rightBuf;
        private readonly float[] _leftOutBuf;
        private readonly float[] _rightOutBuf;
        private readonly float[] _leftWindowSum;
        private readonly float[] _rightWindowSum;
        private readonly Complex32[] _leftComplexBuf;
        private readonly Complex32[] _rightComplexBuf;
        private readonly float[] _finalChunkBuf;

        // NEW: Rolling buffer to handle seamless overlap-add streaming from pipes
        private readonly float[] _overlapBuffer;

        public AudioChunker(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            
            _window = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / FftSize));
            }

            _leftBuf = new float[ChunkSize];
            _rightBuf = new float[ChunkSize];
            _leftOutBuf = new float[ChunkSize];
            _rightOutBuf = new float[ChunkSize];
            _leftWindowSum = new float[ChunkSize];
            _rightWindowSum = new float[ChunkSize];
            _leftComplexBuf = new Complex32[FftSize];
            _rightComplexBuf = new Complex32[FftSize];
            _finalChunkBuf = new float[ChunkSize * _channels];
            
            // Overlap is exactly 2x the Trim margin for U-Net edge smoothing
            _overlapBuffer = new float[Trim * 2 * _channels]; 
        }

        /// <summary>
        /// Reads exactly one GenSize step from the raw byte stream, merges it with the rolling overlap buffer, 
        /// and mathematically transforms it into AI-ready Tensors.
        /// </summary>
        public int ReadAndTransformStreamChunk(Stream inputStream, float[] tensorDataOut)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioChunker));

            int floatsToRead = GenSize * _channels;
            int bytesToRead = floatsToRead * sizeof(float);
            byte[] byteBuf = new byte[bytesToRead];
            
            int totalBytesRead = 0;
            while (totalBytesRead < bytesToRead)
            {
                int read = inputStream.Read(byteBuf, totalBytesRead, bytesToRead - totalBytesRead);
                if (read == 0) break; // EOF Reached
                totalBytesRead += read;
            }

            int floatsRead = totalBytesRead / sizeof(float);
            if (floatsRead == 0) return 0; // Stream completely drained

            // Convert raw bytes from FFmpeg into C# floats
            float[] freshFloats = new float[floatsRead];
            Buffer.BlockCopy(byteBuf, 0, freshFloats, 0, totalBytesRead);

            // Stitch the previous chunk's edge to the new chunk's start
            int combinedLength = _overlapBuffer.Length + floatsRead;
            float[] combined = new float[combinedLength];
            Array.Copy(_overlapBuffer, 0, combined, 0, _overlapBuffer.Length);
            Array.Copy(freshFloats, 0, combined, _overlapBuffer.Length, floatsRead);

            // Save the new tail end into the overlap buffer for the NEXT read
            Array.Clear(_overlapBuffer, 0, _overlapBuffer.Length);
            if (combinedLength >= _overlapBuffer.Length)
            {
                Array.Copy(combined, combinedLength - _overlapBuffer.Length, _overlapBuffer, 0, _overlapBuffer.Length);
            }

            Array.Clear(_leftBuf, 0, ChunkSize);
            Array.Clear(_rightBuf, 0, ChunkSize);
            Array.Clear(tensorDataOut, 0, tensorDataOut.Length);

            int validSamplesThisChunk = combinedLength / _channels;
            for (int i = 0; i < validSamplesThisChunk && i < ChunkSize; i++)
            {
                _leftBuf[i] = combined[i * _channels];
                _rightBuf[i] = _channels > 1 ? combined[i * _channels + 1] : combined[i * _channels];
            }

            // Math execution
            Parallel.Invoke(
                () => ProcessStftChannel(_leftBuf, tensorDataOut, 0, 1, _leftComplexBuf),
                () => ProcessStftChannel(_rightBuf, tensorDataOut, 2, 3, _rightComplexBuf)
            );

            return validSamplesThisChunk; // Tell the caller exactly how much data is in this chunk
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

        /// <summary>
        /// Converts the math floats back into raw byte sequences so they can be 
        /// pushed directly into the FFmpeg encoding pipe. Drops the overlap padding.
        /// </summary>
        public byte[] ExtractValidAudioBytes(float[] finalChunk, int validSamplesProcessed)
        {
            int maxValidFloats = GenSize * _channels;
            int startFloatIndex = Trim * _channels; // UVR5 Left Margin Drop
            
            // Calculate how much actual valid audio we created (to handle the very last truncated chunk)
            int lengthToCopy = Math.Min(maxValidFloats, (validSamplesProcessed * _channels) - startFloatIndex);
            if (lengthToCopy < 0) return Array.Empty<byte>();

            byte[] outBytes = new byte[lengthToCopy * sizeof(float)];
            Buffer.BlockCopy(finalChunk, startFloatIndex * sizeof(float), outBytes, 0, outBytes.Length);
            
            return outBytes;
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
