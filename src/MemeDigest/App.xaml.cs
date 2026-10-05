using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MemeDigest;

public partial class App : Application
{
    // ── Global exception handling ──

    private void ApplicationDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        try
        {
            string crashLogPath = Path.Combine(AppSettings.SettingsDirectory, "crash.log");
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.AppendAllText(
                crashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] build: {BuildInfo.Sha}{Environment.NewLine}{eventArgs.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Crash logging must never crash.
        }

        MessageBox.Show(
            $"Unexpected error: {eventArgs.Exception.Message}{Environment.NewLine}{Environment.NewLine}A log was written to {AppSettings.SettingsDirectory}\\crash.log",
            nameof(MemeDigest) + " (build " + (BuildInfo.Sha?.Length > 8 ? BuildInfo.Sha[..8] : BuildInfo.Sha ?? "local") + ")",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        eventArgs.Handled = true;
    }
}