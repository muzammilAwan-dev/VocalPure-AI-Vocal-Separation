using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VocalPure.ViewModels;

namespace VocalPure
{
    /// <summary>
    /// Main application window with drag-and-drop support and reactive UI bindings.
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
        }

        #region Drag & Drop Handlers

        /// <summary>
        /// Handles drag enter - shows visual feedback when files are dragged over the window.
        /// </summary>
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (IsValidDropData(e))
            {
                e.Effects = DragDropEffects.Copy;
                ViewModel.IsDragging = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        /// <summary>
        /// Handles drag over the drop zone.
        /// </summary>
        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (IsValidDropData(e))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles drag leave - clears visual feedback.
        /// </summary>
        private void OnDragLeave(object sender, DragEventArgs e)
        {
            // Check if we're actually leaving the window, not just entering a child element
            var dependencyObject = (DependencyObject)sender;
            if (!IsMouseOverWindow(dependencyObject, e))
            {
                ViewModel.IsDragging = false;
            }
            e.Handled = true;
        }

        /// <summary>
        /// Handles drop - processes the dropped files.
        /// </summary>
        private async void OnDrop(object sender, DragEventArgs e)
        {
            ViewModel.IsDragging = false;

            if (!IsValidDropData(e))
                return;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    await ViewModel.HandleDroppedFilesAsync(files);
                }
            }

            e.Handled = true;
        }

        /// <summary>
        /// Validates that the drag data contains valid file drops.
        /// </summary>
        private static bool IsValidDropData(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return false;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
                return false;

            // Validate at least one file has a supported extension
            var validExtensions = new[] {
                ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
                ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm"
            };

            return files.Any(f => validExtensions.Contains(
                System.IO.Path.GetExtension(f).ToLowerInvariant()));
        }

        /// <summary>
        /// Checks if the mouse is still over the window during drag leave events.
        /// </summary>
        private static bool IsMouseOverWindow(DependencyObject element, DragEventArgs e)
        {
            // Get the window from the element
            var window = Window.GetWindow(element);
            if (window == null) return false;

            // Check if mouse is still within window bounds
            Point mousePos = e.GetPosition(window);
            return mousePos.X >= 0 && mousePos.X <= window.ActualWidth
                && mousePos.Y >= 0 && mousePos.Y <= window.ActualHeight;
        }

        #endregion

        #region Window Event Handlers

        /// <summary>
        /// Handles window closing - ensures cleanup of active processes.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (ViewModel.IsProcessing)
            {
                var result = MessageBox.Show(
                    "A job is currently processing. Cancel and exit?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                // Cancel current operation
                ViewModel.Dispose();
            }

            base.OnClosing(e);
        }

        #endregion
    }
}
