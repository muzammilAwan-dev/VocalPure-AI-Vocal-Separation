using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VocalPure.Services
{
    /// <summary>
    /// Handles removal of the Mark of the Web (MotW) / Zone.Identifier Alternate Data Stream (ADS)
    /// from extracted binaries. Windows attaches MotW to files downloaded from the internet, which
    /// can cause Windows Defender/SmartScreen to block execution of FFmpeg and prevent ONNX models
    /// from loading.
    /// </summary>
    public static class MotWStripper
    {
        // Win32 constants for ADS operations
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>
        /// The Zone Identifier stream name used by Windows for MotW tracking.
        /// </summary>
        private const string ZoneIdentifierStream = ":Zone.Identifier";

        /// <summary>
        /// Zone values in the MotW stream.
        /// </summary>
        private static class SecurityZones
        {
            public const int LocalMachine = 0;
            public const int Intranet = 1;
            public const int Trusted = 2;
            public const int Internet = 3;
            public const int Untrusted = 4;
        }

        /// <summary>
        /// Strips the Mark of the Web from a single file by deleting its Zone.Identifier ADS.
        /// </summary>
        /// <param name="filePath">Path to the file to process.</param>
        /// <returns>True if the MotW was removed or didn't exist.</returns>
        public static bool StripMotW(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                string adsPath = filePath + ZoneIdentifierStream;

                // Method 1: Try using DeleteFileW directly on the ADS path
                if (DeleteFileW(adsPath))
                {
                    return true;
                }

                // Method 2: If DeleteFileW failed, the ADS might not exist
                // Check if ADS exists by trying to open it
                int error = Marshal.GetLastWin32Error();
                if (error == 2) // ERROR_FILE_NOT_FOUND = ADS doesn't exist
                {
                    return true; // Nothing to strip
                }

                // Method 3: Fallback - read and clear via FileStreams
                return StripMotWFallback(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MotW strip failed for {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Strips MotW from all files in a directory (recursive).
        /// </summary>
        /// <param name="directoryPath">Directory to process.</param>
        /// <returns>Count of files successfully processed.</returns>
        public static int StripMotWInDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return 0;

            int processed = 0;
            try
            {
                foreach (string file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    if (StripMotW(file))
                        processed++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Directory MotW strip failed: {ex.Message}");
            }
            return processed;
        }

        /// <summary>
        /// Checks if a file has the Mark of the Web attached.
        /// </summary>
        /// <param name="filePath">Path to check.</param>
        /// <returns>True if MotW is present.</returns>
        public static bool HasMotW(string filePath)
        {
            try
            {
                string adsPath = filePath + ZoneIdentifierStream;
                using var stream = new FileStream(adsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, Encoding.Unicode);
                string content = reader.ReadToEnd();
                return content.Contains("ZoneId=");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fallback method using managed FileStream to strip MotW.
        /// </summary>
        private static bool StripMotWFallback(string filePath)
        {
            try
            {
                // Write an empty Zone.Identifier to overwrite any existing one
                string adsPath = filePath + ZoneIdentifierStream;
                using (var fs = new FileStream(adsPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write("[ZoneTransfer]\r\nZoneId=0\r\n");
                }

                // Now try to delete it
                return DeleteFileW(adsPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Alternative approach using PowerShell (if available) to unblock files.
        /// This is the most thorough method for stubborn MotW cases.
        /// </summary>
        public static bool StripMotWViaPowerShell(string filePath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Unblock-File -LiteralPath '{filePath}'\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };

                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(5000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
