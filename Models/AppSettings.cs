namespace VocalPure.Models
{
    /// <summary>
    /// Represents the user's saved preferences.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// The custom directory where all processed vocals will be saved.
        /// If empty, the application will default to the original file's directory.
        /// </summary>
        public string CustomOutputFolder { get; set; } = string.Empty;

        /// <summary>
        /// If true, forces ONNX to use 100% of logical hyper-threads.
        /// If false, relies on ONNX Auto-Detection (usually limits to physical cores).
        /// </summary>
        public bool EnableAggressiveCpuMode { get; set; } = false;
    }
}
