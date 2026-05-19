using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VocalPure.Services
{
    public sealed class MediaProcessor : IDisposable
    {
        private Process? _activeProcess;
        private readonly DependencyManager _dependencyManager;
        private bool _isDisposed;

        public MediaProcessor()
        {
            _dependencyManager = new DependencyManager();
        }

        public async Task ExtractAudioToWavAsync(
            string inputPath,
            string outputWavPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            var arguments = new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-stats",
                "-i", inputPath, "-vn",
                "-acodec", "pcm_f32le", "-ar", "44100", "-ac", "2",
                "-f", "wav", outputWavPath
            };

            await RunFfmpegAsync(arguments, cancellationToken, progress, ParseConvertProgress);
        }

        public async Task EncodeToMp3Async(
            string inputWavPath,
            string outputMp3Path,
            CancellationToken cancellationToken,
            int bitrateKbps = 320,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            var arguments = new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-stats",
                "-i", inputWavPath,
                "-codec:a", "libmp3lame", "-b:a", $"{bitrateKbps}k",
                "-q:a", "0", "-map_metadata", "-1",
                "-id3v2_version", "3", "-write_id3v1", "1",
                outputMp3Path
            };

            await RunFfmpegAsync(arguments, cancellationToken, progress, ParseConvertProgress);
        }

        public async Task EncodeAudioAsync(
            string inputWavPath,
            string outputPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            if (ext == ".mp3")
            {
                await EncodeToMp3Async(inputWavPath, outputPath, cancellationToken, 320, progress);
            }
            else
            {
                await EncodeToWavAsync(inputWavPath, outputPath, cancellationToken, progress);
            }
        }

        public async Task EncodeToWavAsync(
            string inputWavPath,
            string outputWavPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            var arguments = new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-stats",
                "-i", inputWavPath,
                "-acodec", "pcm_f32le", "-ar", "44100", "-ac", "2",
                "-f", "wav", outputWavPath
            };

            await RunFfmpegAsync(arguments, cancellationToken, progress, ParseConvertProgress);
        }

        public async Task MergeVideoWithAudioAsync(
            string originalVideoPath,
            string newAudioPath,
            string outputPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            var arguments = new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-stats",
                "-i", originalVideoPath, "-i", newAudioPath,
                "-c:v", "copy", "-c:a", "aac", "-b:a", "320k",
                "-map", "0:v:0", "-map", "1:a:0",
                "-shortest", "-movflags", "+faststart", "-fflags", "+genpts",
                "-async", "1", outputPath
            };

            await RunFfmpegAsync(arguments, cancellationToken, progress, ParseConvertProgress);
        }

        private async Task RunFfmpegAsync(
            string[] arguments,
            CancellationToken cancellationToken,
            IProgress<double>? progress,
            Func<string, double, double> progressParser)
        {
            string ffmpegPath = GetFfmpegPath();

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? Environment.CurrentDirectory
            };

            foreach (var arg in arguments) psi.ArgumentList.Add(arg);

            var processCompletion = new TaskCompletionSource<bool>();
            _activeProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _activeProcess.Exited += (_, _) => processCompletion.TrySetResult(true);

            using (cancellationToken.Register(() =>
            {
                processCompletion.TrySetCanceled();
                KillActiveProcess();
            }))
            {
                var errorBuffer = new StringBuilder();
                var lockObj = new object();
                double lastProgress = 0;

                _activeProcess.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;

                    lock (lockObj) { errorBuffer.AppendLine(e.Data); }

                    if (progress != null)
                    {
                        double parsedProgress = progressParser(e.Data, lastProgress);
                        if (parsedProgress > lastProgress)
                        {
                            lastProgress = parsedProgress;
                            progress.Report(Math.Min(1.0, parsedProgress));
                        }
                    }
                };

                // CRITICAL FIX: Silently drain the standard output so the 4KB buffer never overflows and hangs the app.
                _activeProcess.OutputDataReceived += (_, _) => { };

                _activeProcess.Start();
                _activeProcess.BeginErrorReadLine();
                _activeProcess.BeginOutputReadLine();

                try { await processCompletion.Task; }
                catch (OperationCanceledException) { throw; }

                if (_activeProcess.ExitCode != 0)
                {
                    string errorMessage;
                    lock (lockObj)
                    {
                        errorMessage = errorBuffer.Length > 500
                            ? errorBuffer.ToString(errorBuffer.Length - 500, 500)
                            : errorBuffer.ToString();
                    }
                    throw new InvalidOperationException($"FFmpeg exited with code {_activeProcess.ExitCode}. Error: {errorMessage}");
                }

                progress?.Report(1.0);
            }
        }

        private static double ParseConvertProgress(string line, double currentProgress)
        {
            var timeMatch = Regex.Match(line, @"time=(\d{2}):(\d{2}):(\d{2}\.\d+)");
            if (timeMatch.Success)
            {
                double hours = double.Parse(timeMatch.Groups[1].Value);
                double minutes = double.Parse(timeMatch.Groups[2].Value);
                double seconds = double.Parse(timeMatch.Groups[3].Value);
                double totalSeconds = hours * 3600 + minutes * 60 + seconds;
                return Math.Min(totalSeconds / 300.0, 0.99); // Fallback assumption
            }
            if (Regex.IsMatch(line, @"frame=\s*(\d+)")) return currentProgress + 0.001;
            return currentProgress;
        }

        private string GetFfmpegPath()
        {
            if (File.Exists(_dependencyManager.FFmpegPath)) return _dependencyManager.FFmpegPath;
            return "ffmpeg.exe";
        }

        private void KillActiveProcess()
        {
            try
            {
                _activeProcess?.Kill();
                _activeProcess?.WaitForExit(5000);
            }
            catch { }
        }

        private void EnsureNotDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(MediaProcessor));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            KillActiveProcess();
            _activeProcess?.Dispose();
        }
    }
}
