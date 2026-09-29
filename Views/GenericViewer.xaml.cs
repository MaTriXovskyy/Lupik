using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QuickPeek.Core;

namespace QuickPeek.Views;

public partial class GenericViewer : UserControl
{
    private string _currentFilePath = "";

    public GenericViewer()
    {
        InitializeComponent();
    }

    public void LoadFile(string filePath)
    {
        try
        {
            _currentFilePath = filePath;
            var fileInfo = new FileInfo(filePath);

            FileNameText.Text = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath).ToUpperInvariant();
            string sizeStr = FormatFileSize(fileInfo.Length);
            FileTypeAndSizeText.Text = $"{ext} • {sizeStr}";

            ModifiedDateText.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
            CreatedDateText.Text = fileInfo.CreationTime.ToString("yyyy-MM-dd HH:mm:ss");
            FilePathText.Text = filePath;

            // Extract file icon
            using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (sysIcon != null)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(
                    sysIcon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                FileIconImage.Source = bs;
            }
        }
        catch (Exception ex)
        {
            FileNameText.Text = Path.GetFileName(filePath);
            FileTypeAndSizeText.Text = ex.Message;
        }
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_currentFilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_currentFilePath) { UseShellExecute = true });
            }
            catch
            {
                // Ignore
            }
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
