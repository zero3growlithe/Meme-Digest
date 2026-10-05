using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace MemeDigest;

public partial class SettingsWindow : Window
{
    // ── Fields ──

    private readonly AppSettings settings;

    // ── Construction ──

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;

        LibraryPathBox.Text = settings.LibraryPath;
        HistoryDirectoryBox.Text = settings.HistoryDirectory;
        ThumbnailDirectoryBox.Text = settings.ThumbnailDirectory;
        FfmpegPathBox.Text = settings.FfmpegExecutablePath;
        DrawCountBox.Text = settings.DrawCount.ToString(CultureInfo.InvariantCulture);
        ImageExtensionsBox.Text = string.Join(", ", settings.ImageExtensions);
        VideoExtensionsBox.Text = string.Join(", ", settings.VideoExtensions);
    }

    // ── Folder/file pickers ──

    private void BrowseLibraryButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? selected = PromptForFolder("Select the meme library root");
        if (selected != null)
        {
            LibraryPathBox.Text = selected;
        }
    }

    private void BrowseHistoryButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? selected = PromptForFolder("Select the history folder");
        if (selected != null)
        {
            HistoryDirectoryBox.Text = selected;
        }
    }

    private void BrowseThumbnailsButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? selected = PromptForFolder("Select the thumbnail cache folder");
        if (selected != null)
        {
            ThumbnailDirectoryBox.Text = selected;
        }
    }

    private void BrowseFfmpegButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select ffmpeg executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = "ffmpeg.exe"
        };
        bool? dialogResult = dialog.ShowDialog(this);
        if (dialogResult == true)
        {
            FfmpegPathBox.Text = dialog.FileName;
        }
    }

    private string? PromptForFolder(string description)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = true
        };
        System.Windows.Forms.DialogResult dialogResult = dialog.ShowDialog(this);
        if (dialogResult == System.Windows.Forms.DialogResult.OK)
        {
            return dialog.SelectedPath;
        }

        return null;
    }

    // ── Confirm ──

    private void OkButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        string libraryPath = LibraryPathBox.Text.Trim();
        if (libraryPath.Length == 0 || !Directory.Exists(libraryPath))
        {
            MessageBox.Show(this, "The library path must be an existing folder.", nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int drawCount;
        if (!int.TryParse(DrawCountBox.Text.Trim(), out drawCount) || drawCount < 1 || drawCount > 200)
        {
            MessageBox.Show(this, "Memes per draw must be a whole number between 1 and 200.", nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<string> imageExtensions = ParseExtensionList(ImageExtensionsBox.Text);
        if (imageExtensions.Count == 0)
        {
            MessageBox.Show(this, "Provide at least one image extension.", nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        settings.LibraryPath = libraryPath;
        settings.HistoryDirectory = HistoryDirectoryBox.Text.Trim();
        settings.ThumbnailDirectory = ThumbnailDirectoryBox.Text.Trim();
        settings.FfmpegExecutablePath = FfmpegPathBox.Text.Trim();
        settings.DrawCount = drawCount;
        settings.ImageExtensions = imageExtensions;
        settings.VideoExtensions = ParseExtensionList(VideoExtensionsBox.Text);
        settings.FillDefaults();

        DialogResult = true;
        Close();
    }

    private static List<string> ParseExtensionList(string text)
    {
        List<string> extensions = new List<string>();
        string[] fragments = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string fragment in fragments)
        {
            string extension = fragment.Trim().ToLowerInvariant();
            if (extension.Length == 0)
            {
                continue;
            }

            if (!extension.StartsWith(".", StringComparison.Ordinal))
            {
                extension = "." + extension;
            }

            if (!extensions.Contains(extension))
            {
                extensions.Add(extension);
            }
        }

        return extensions;
    }
}