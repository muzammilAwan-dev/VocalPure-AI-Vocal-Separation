using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VocalPure.Services
{
    public sealed class MediaProcessor : IDisposable
    {
        private readonly DependencyManager _dependencyManager;
        private bool _isDisposed;

        public MediaProcessor()
        {
            _dependencyManager = new DependencyManager();
        }

        /// <summary>
        /// PIPELINE: Opens a background FFmpeg process that decodes an audio/video file 
        /// and streams raw 32-bit float PCM data directly to stdout.
        /// </summary>
        public Process StartFfmpegDecodeStream(string inputPath)
        {
            EnsureNotDisposed();

            var psi = new ProcessStartInfo
            {
                FileName = GetFfmpegPath(),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // Silenced in background
                WindowStyle = ProcessWindowStyle.Hidden
            };

            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-vn"); // No video
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
            psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_f32le");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-"); // Stream directly to StandardOutput

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            return process;
        }

        /// <summary>
        /// PIPELINE: Opens a background FFmpeg process that waits for raw 32-bit float PCM data 
        /// to be piped into its stdin, encoding it directly to the final file.
        /// </summary>
        public Process StartFfmpegEncodeStream(string originalVideoPath, string outputPath, bool isVideo)
        {
            EnsureNotDisposed();

            var psi = new ProcessStartInfo
            {
                FileName = GetFfmpegPath(),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");

            if (isVideo)
            {
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(originalVideoPath); // Input 0 (Video)
                
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
                psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
                psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("-"); // Input 1 (Stdin Audio)

                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1:a:0");
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("320k");
                psi.ArgumentList.Add("-shortest");
            }
            else
            {
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
                psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
                psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("-"); // Input 0 (Stdin Audio)

                string ext = Path.GetExtension(outputPath).ToLowerInvariant();
                if (ext == ".mp3")
                {
                    psi.ArgumentList.Add("-codec:a"); psi.ArgumentList.Add("libmp3lame");
                    psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("320k");
                }
                else
                {
                    psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_f32le");
                }
            }

            psi.ArgumentList.Add(outputPath);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            return process;
        }

        private string GetFfmpegPath()
        {
            if (File.Exists(_dependencyManager.FFmpegPath)) return _dependencyManager.FFmpegPath;
            return "ffmpeg.exe";
        }

        private void EnsureNotDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(MediaProcessor));
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
