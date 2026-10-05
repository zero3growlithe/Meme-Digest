using System;
using System.IO;
using System.Windows;

namespace MemeDigest;

public partial class ProfileNameWindow : Window
{
    // ── Properties ──

    public string ProfileName
    {
        get { return ProfileNameBox.Text; }
    }

    // ── Construction ──

    public ProfileNameWindow()
    {
        InitializeComponent();
        Loaded += ProfileNameWindow_Loaded;
    }

    private void ProfileNameWindow_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        ProfileNameBox.Focus();
    }

    // ── Confirm ──

    private void OkButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        string profileName = ProfileNameBox.Text.Trim();
        if (profileName.Length == 0)
        {
            MessageBox.Show(this, "Enter a profile name.", nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        if (profileName.IndexOfAny(invalidCharacters) >= 0)
        {
            MessageBox.Show(this, "The profile name contains characters that are not allowed in file names.", nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}