using System;
using System.IO;
using System.Threading.Tasks; // FIX: Added missing namespace for UnobservedTaskExceptionEventArgs
using System.Windows;

namespace VocalPure
{
    /// <summary>
    /// Application entry point for VocalPure.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set unhandled exception handlers
            Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Ensure engine directory structure exists
            EnsureEngineDirectory();
        }

        private static void EnsureEngineDirectory()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string engineDir = Path.Combine(localAppData, "VocalPure", "Engine");
                Directory.CreateDirectory(engineDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Warning: Could not create engine directory: {ex.Message}",
                    "VocalPure Startup Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "VocalPure Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    $"Fatal error:\n\n{ex.Message}",
                    "VocalPure Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            System.Diagnostics.Debug.WriteLine($"Unobserved task exception: {e.Exception.Message}");
        }
    }
}
