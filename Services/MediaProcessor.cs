using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VocalPure.Services
{
    /// <summary>
    /// Handles all FFmpeg-based media operations. Executes FFmpeg as a headless background process
    /// with full stream redirection for granular progress reporting.
    /// </summary>
    public sealed class MediaProcessor : IDisposable
    {
        private Process? _activeProcess;
        private readonly DependencyManager _dependencyManager;
        private bool _isDisposed;

        public MediaProcessor()
        {
            _dependencyManager = new DependencyManager();
        }

        /// <summary>
        /// Extracts audio from any media file to a standardized Float32 PCM WAV (44100Hz, Stereo).
        /// </summary>
        /// <param name="inputPath">Source media file path.</param>
        /// <param name="outputWavPath">Destination WAV file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="progress">Progress reporter (0.0 to 1.0).</param>
        public async Task ExtractAudioToWavAsync(
            string inputPath,
            string outputWavPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            string ffmpegPath = GetFfmpegPath();

            var arguments = new[]
            {
                "-y",                       // Overwrite output
                "-hide_banner",             // Suppress banner
                "-loglevel", "error",       // Only show errors
                "-stats",                   // Show progress stats
                "-i", $"\"{inputPath}\"",   // Input file
                "-vn",                      // No video
                "-acodec", "pcm_f32le",     // Float32 PCM
                "-ar", "44100",             // 44.1kHz sample rate
                "-ac", "2",                 // Stereo
                "-f", "wav",                // WAV format
                $"\"{outputWavPath}\""
            };

            await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken, progress, ParseConvertProgress);
        }

        /// <summary>
        /// Encodes a WAV file to high-quality MP3 format.
        /// </summary>
        public async Task EncodeToMp3Async(
            string inputWavPath,
            string outputMp3Path,
            CancellationToken cancellationToken,
            int bitrateKbps = 320,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            string ffmpegPath = GetFfmpegPath();

            var arguments = new[]
            {
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-stats",
                "-i", $"\"{inputWavPath}\"",
                "-codec:a", "libmp3lame",
                "-b:a", $"{bitrateKbps}k",
                "-q:a", "0",
                "-map_metadata", "-1",
                "-id3v2_version", "3",
                "-write_id3v1", "1",
                $"\"{outputMp3Path}\""
            };

            await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken, progress, ParseConvertProgress);
        }

        /// <summary>
        /// Encodes a WAV file to output format (WAV or MP3 based on extension).
        /// </summary>
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
                // Default to WAV copy
                await EncodeToWavAsync(inputWavPath, outputPath, cancellationToken, progress);
            }
        }

        /// <summary>
        /// Encodes a WAV file to standard PCM WAV.
        /// </summary>
        public async Task EncodeToWavAsync(
            string inputWavPath,
            string outputWavPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            string ffmpegPath = GetFfmpegPath();

            var arguments = new[]
            {
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-stats",
                "-i", $"\"{inputWavPath}\"",
                "-acodec", "pcm_f32le",
                "-ar", "44100",
                "-ac", "2",
                "-f", "wav",
                $"\"{outputWavPath}\""
            };

            await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken, progress, ParseConvertProgress);
        }

        /// <summary>
        /// Merges original video stream with new audio track. Video is copied losslessly.
        /// </summary>
        /// <param name="originalVideoPath">Original video file.</param>
        /// <param name="newAudioPath">New audio track (WAV).</param>
        /// <param name="outputPath">Output video file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="progress">Progress reporter.</param>
        public async Task MergeVideoWithAudioAsync(
            string originalVideoPath,
            string newAudioPath,
            string outputPath,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureNotDisposed();

            string ffmpegPath = GetFfmpegPath();

            var arguments = new[]
            {
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-stats",
                "-i", $"\"{originalVideoPath}\"",   // Input 0: original video
                "-i", $"\"{newAudioPath}\"",       // Input 1: new audio
                "-c:v", "copy",                     // Copy video without re-encoding
                "-c:a", "aac",                      // AAC audio for compatibility
                "-b:a", "320k",
                "-map", "0:v:0",                    // Video from input 0
                "-map", "1:a:0",                    // Audio from input 1
                "-shortest",                        // End when shortest input ends
                "-movflags", "+faststart",          // Web optimization
                "-fflags", "+genpts",               // Generate presentation timestamps
                "-async", "1",                      // Audio sync
                $"\"{outputPath}\""
            };

            await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken, progress, ParseConvertProgress);
        }

        /// <summary>
        /// Gets media duration in seconds using FFprobe via FFmpeg.
        /// </summary>
        public async Task<double> GetMediaDurationAsync(string filePath, CancellationToken cancellationToken)
        {
            string ffmpegPath = GetFfmpegPath();

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -i \"{filePath}\" -f null -",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = new Process { StartInfo = psi };
            var tcs = new TaskCompletionSource<bool>();

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => tcs.TrySetResult(true);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.Token.Register(() => tcs.TrySetCanceled());

            string errorOutput = "";
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    errorOutput += e.Data + "\n";
            };

            process.Start();
            process.BeginErrorReadLine();

            try
            {
                await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }

            // Parse duration from output: "Duration: 00:03:45.12"
            var match = Regex.Match(errorOutput, @"Duration:\s+(\d{2}):(\d{2}):(\d{2}\.\d+)");
            if (match.Success)
            {
                double hours = double.Parse(match.Groups[1].Value);
                double minutes = double.Parse(match.Groups[2].Value);
                double seconds = double.Parse(match.Groups[3].Value);
                return hours * 3600 + minutes * 60 + seconds;
            }

            return 0;
        }

        /// <summary>
        /// Core method to execute FFmpeg with full stream redirection and progress parsing.
        /// </summary>
        private async Task RunFfmpegAsync(
            string ffmpegPath,
            string[] arguments,
            CancellationToken cancellationToken,
            IProgress<double>? progress,
            Func<string, double, double> progressParser)
        {
            string args = string.Join(" ", arguments);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,          // CRITICAL: Headless execution
                UseShellExecute = false,        // CRITICAL: Required for stream redirection
                RedirectStandardOutput = true,
                RedirectStandardError = true,   // FFmpeg writes progress to stderr
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? Environment.CurrentDirectory
            };

            var processCompletion = new TaskCompletionSource<bool>();
            _activeProcess = new Process { StartInfo = psi };
            _activeProcess.EnableRaisingEvents = true;
            _activeProcess.Exited += (_, _) => processCompletion.TrySetResult(true);

            // Setup cancellation
            using (cancellationToken.Register(() =>
            {
                processCompletion.TrySetCanceled();
                KillActiveProcess();
            }))
            {
                // FIX: Use thread-safe StringBuilder for high-frequency async logging
                var errorBuffer = new StringBuilder();
                var lockObj = new object();
                double lastProgress = 0;

                _activeProcess.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    lock (lockObj)
                    {
                        errorBuffer.AppendLine(e.Data);
                    }

                    // Parse progress from FFmpeg stderr output
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

                _activeProcess.Start();
                _activeProcess.BeginErrorReadLine();
                _activeProcess.BeginOutputReadLine();

                try
                {
                    await processCompletion.Task;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                // Check exit code
                if (_activeProcess.ExitCode != 0)
                {
                    string errorMessage;
                    lock (lockObj)
                    {
                        errorMessage = errorBuffer.Length > 500
                            ? errorBuffer.ToString(errorBuffer.Length - 500, 500)
                            : errorBuffer.ToString();
                    }
                    throw new InvalidOperationException(
                        $"FFmpeg exited with code {_activeProcess.ExitCode}. Error: {errorMessage}");
                }

                // Report completion
                progress?.Report(1.0);
            }
        }

        /// <summary>
        /// Parses FFmpeg stderr output for conversion progress.
        /// </summary>
        private static double ParseConvertProgress(string line, double currentProgress)
        {
            var timeMatch = Regex.Match(line, @"time=(\d{2}):(\d{2}):(\d{2}\.\d+)");
            if (timeMatch.Success)
            {
                double hours = double.Parse(timeMatch.Groups[1].Value);
                double minutes = double.Parse(timeMatch.Groups[2].Value);
                double seconds = double.Parse(timeMatch.Groups[3].Value);
                double totalSeconds = hours * 3600 + minutes * 60 + seconds;

                const double assumedMaxDuration = 300; // 5 minutes
                return Math.Min(totalSeconds / assumedMaxDuration, 0.99);
            }

            var frameMatch = Regex.Match(line, @"frame=\s*(\d+)");
            if (frameMatch.Success)
            {
                return currentProgress + 0.001;
            }

            return currentProgress;
        }

        private string GetFfmpegPath()
        {
            if (File.Exists(_dependencyManager.FFmpegPath))
                return _dependencyManager.FFmpegPath;
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

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch { }
        }

        private void EnsureNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(MediaProcessor));
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
