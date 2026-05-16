using System;
using System.IO;
using System.Text.Json;
using Serilog;
using VocalPure.Models;

namespace VocalPure.Services
{
    /// <summary>
    /// Handles loading and saving persistent application settings to disk.
    /// </summary>
    public sealed class AppSettingsManager
    {
        private readonly string _settingsFilePath;
        public AppSettings CurrentSettings { get; private set; }

        public AppSettingsManager()
        {
            // Store settings in the existing user-local AppData directory
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string configDir = Path.Combine(localAppData, "VocalPure", "Config");
            
            Directory.CreateDirectory(configDir);
            _settingsFilePath = Path.Combine(configDir, "settings.json");

            CurrentSettings = new AppSettings();
            LoadSettings();
        }

        /// <summary>
        /// Loads settings from the JSON file, or falls back to defaults if the file is missing/corrupt.
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    CurrentSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    Log.Debug("Application settings loaded from {Path}", _settingsFilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load settings. Reverting to default values.");
                CurrentSettings = new AppSettings();
            }
        }

        /// <summary>
        /// Saves the current settings to the JSON file.
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(CurrentSettings, options);
                File.WriteAllText(_settingsFilePath, json);
                Log.Debug("Application settings saved to {Path}", _settingsFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save application settings.");
            }
        }
    }
}
