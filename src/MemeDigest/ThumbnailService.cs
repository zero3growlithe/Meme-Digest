using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MemeDigest;

public sealed class ThumbnailService
{
    // ── Fields ──

    private readonly AppSettings settings;

    private readonly SemaphoreSlim ffmpegGate = new SemaphoreSlim(1, 1);

    private ImageSource? videoPlaceholder;

    // ── Construction ──

    public ThumbnailService(AppSettings settings)
    {
        this.settings = settings;
    }

    // ── Public API ──

    public async Task<ImageSource?> GetThumbnailAsync(string absolutePath, MediaKind kind)
    {
        if (kind == MediaKind.Image)
        {
            return await Task.Run(() => TryCreateImageThumbnail(absolutePath)).ConfigureAwait(true);
        }

        return await Task.Run(() => TryCreateVideoThumbnail(absolutePath)).ConfigureAwait(true);
    }

    public ImageSource GetVideoPlaceholder()
    {
        if (videoPlaceholder != null)
        {
            return videoPlaceholder;
        }

        videoPlaceholder = RenderVideoPlaceholder();
        return videoPlaceholder;
    }

    /// <summary>
    /// Deletes all cached ffmpeg posters (called when library paths change).
    /// </summary>
    public void InvalidateCache()
    {
        try
        {
            if (!Directory.Exists(settings.ThumbnailDirectory))
            {
                return;
            }

            foreach (string posterPath in Directory.EnumerateFiles(settings.ThumbnailDirectory, "poster-*.jpg"))
            {
                File.Delete(posterPath);
            }
        }
        catch (Exception)
        {
            // Cache cleanup is best-effort; stale posters only cost disk space.
        }
    }

    // ── Image thumbnails (WIC) ──

    private ImageSource? TryCreateImageThumbnail(string absolutePath)
    {
        try
        {
            BitmapImage thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.UriSource = new Uri(absolutePath, UriKind.Absolute);
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.DecodePixelWidth = 320;
            thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            thumbnail.EndInit();
            thumbnail.Freeze();
            return thumbnail;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── Video thumbnails (ffmpeg poster frame, cached on disk) ──

    private ImageSource? TryCreateVideoThumbnail(string absolutePath)
    {
        string? cachedPoster = FindCachedPoster(absolutePath);
        if (cachedPoster != null)
        {
            return TryCreateImageThumbnail(cachedPoster);
        }

        string? generatedPoster = TryGeneratePosterWithFfmpeg(absolutePath);
        if (generatedPoster != null)
        {
            return TryCreateImageThumbnail(generatedPoster);
        }

        return null;
    }

    private string? FindCachedPoster(string absolutePath)
    {
        string posterPath = BuildPosterPath(settings.ThumbnailDirectory, absolutePath);
        if (File.Exists(posterPath))
        {
            return posterPath;
        }

        return null;
    }

    private static string BuildPosterPath(string thumbnailDirectory, string absolutePath)
    {
        var fileInfo = new FileInfo(absolutePath);
        long length = fileInfo.Exists ? fileInfo.Length : 0;
        long lastWriteTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0;

        string fingerprintSource = absolutePath.ToUpperInvariant() + "|" + length.ToString(CultureInfo.InvariantCulture) + "|" + lastWriteTicks.ToString(CultureInfo.InvariantCulture);
        string fingerprint;
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(fingerprintSource));
            fingerprint = Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant();
        }

        return Path.Combine(thumbnailDirectory, "poster-" + fingerprint + ".jpg");
    }

    private string? TryGeneratePosterWithFfmpeg(string absolutePath)
    {
        if (!ffmpegGate.Wait(0))
        {
            // Another poster generation is running; skip so the UI stays responsive.
            return null;
        }

        try
        {
            Directory.CreateDirectory(settings.ThumbnailDirectory);
            string posterPath = BuildPosterPath(settings.ThumbnailDirectory, absolutePath);
            string arguments = "-y -hide_banner -loglevel error -ss 1 -i \"" + absolutePath + "\" -frames:v 1 -vf \"scale='min(320,iw)':-2\" \"" + posterPath + "\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = settings.FfmpegExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The process may already have exited.
                }

                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(posterPath))
            {
                return null;
            }

            return posterPath;
        }
        catch (Exception)
        {
            // ffmpeg missing or failed: the caller falls back to the placeholder tile.
            return null;
        }
        finally
        {
            ffmpegGate.Release();
        }
    }

    // ── Placeholder rendering ──

    private ImageSource RenderVideoPlaceholder()
    {
        const int width = 320;
        const int height = 180;

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A)), null, new System.Windows.Rect(0, 0, width, height));
            var triangleGeometry = new StreamGeometry();
            using (StreamGeometryContext geometry = triangleGeometry.Open())
            {
                geometry.BeginFigure(new System.Windows.Point(width / 2 - 28, height / 2 - 36), true, true);
                geometry.LineTo(new System.Windows.Point(width / 2 - 28, height / 2 + 36), true, false);
                geometry.LineTo(new System.Windows.Point(width / 2 + 40, height / 2), true, false);
            }

            triangleGeometry.Freeze();
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xC0)), null, triangleGeometry);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}