using System.Windows;
using System.Windows.Controls;
using VocalPure.ViewModels;

namespace VocalPure
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                if (DataContext is MainViewModel vm)
                {
                    vm.IsDragging = true;
                    vm.DropZoneText = "Drop files to add to queue";
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.IsDragging = false;
                vm.DropZoneText = "Drag & Drop Audio/Video Here\nor Click to Browse";
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.IsDragging = false;
                vm.DropZoneText = "Drag & Drop Audio/Video Here\nor Click to Browse";

                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    await vm.HandleDroppedFilesAsync(files);
                }
            }
        }

        // NEW: Automatically scrolls the TextBox whenever new logs are appended
        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.ScrollToEnd();
            }
        }
    }
}
