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
    }
}
