using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

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

            // 1. Initialize Serilog for global debugging
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDirectory = Path.Combine(localAppData, "VocalPure", "Logs");
            Directory.CreateDirectory(logDirectory);
            
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(Path.Combine(logDirectory, "VocalPure_Log_.txt"), 
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("==================================================");
            Log.Information("VocalPure Application Started");
            Log.Information($"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            Log.Information("==================================================");

            // 2. Set global unhandled exception handlers
            Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 3. Ensure engine directory structure exists
            EnsureEngineDirectory(localAppData);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("VocalPure Application Exiting cleanly.");
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private static void EnsureEngineDirectory(string localAppData)
        {
            try
            {
                string engineDir = Path.Combine(localAppData, "VocalPure", "Engine");
                Directory.CreateDirectory(engineDir);
                Log.Debug("Engine directory verified at {EngineDir}", engineDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create engine directory structure.");
                MessageBox.Show(
                    $"Warning: Could not create engine directory: {ex.Message}",
                    "VocalPure Startup Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Fatal(e.Exception, "UI Dispatcher Unhandled Exception");
            MessageBox.Show(
                $"An unexpected error occurred. Check the logs in %LOCALAPPDATA%\\VocalPure\\Logs for details.\n\n{e.Exception.Message}",
                "VocalPure Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "AppDomain Unhandled Exception (IsTerminating: {IsTerminating})", e.IsTerminating);
                MessageBox.Show(
                    $"Fatal error. Check logs.\n\n{ex.Message}",
                    "VocalPure Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Log.CloseAndFlush();
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unobserved Task Exception in background thread");
            e.SetObserved();
        }
    }
}
