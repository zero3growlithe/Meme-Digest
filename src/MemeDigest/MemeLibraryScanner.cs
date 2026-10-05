using System;
using System.Collections.Generic;
using System.IO;

namespace MemeDigest;

/// <summary>
/// Scans the configured media library and draws random memes that the
/// current user has not picked or discarded yet.
/// </summary>
public sealed class MemeLibraryScanner
{
    // ── Scanning ──

    public List<MediaFileInfo> ScanEligibleFiles(AppSettings settings, ISet<string> excludedRelativePaths)
    {
        List<MediaFileInfo> eligible = new List<MediaFileInfo>();
        if (!settings.IsLibraryPathValid)
        {
            return eligible;
        }

        HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedRelativePaths != null)
        {
            foreach (string path in excludedRelativePaths)
            {
                excluded.Add(path.Replace('\\', '/'));
            }
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        foreach (string filePath in Directory.EnumerateFiles(settings.CurrentLibraryPath, "*", enumerationOptions))
        {
            string extension = Path.GetExtension(filePath);
            MediaKind kind = ResolveKind(settings, extension);
            if (kind == MediaKind.Unsupported)
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(settings.CurrentLibraryPath, filePath).Replace('\\', '/');
            if (excluded.Contains(relativePath))
            {
                continue;
            }

            eligible.Add(new MediaFileInfo(filePath, relativePath, kind));
        }

        return eligible;
    }

    private static MediaKind ResolveKind(AppSettings settings, string extension)
    {
        if (extension.Length == 0)
        {
            return MediaKind.Unsupported;
        }

        if (settings.IsKnownImageExtension(extension))
        {
            return MediaKind.Image;
        }

        if (settings.IsKnownVideoExtension(extension))
        {
            return MediaKind.Video;
        }

        return MediaKind.Unsupported;
    }

    // ── Drawing ──

    /// <summary>
    /// Draws up to <paramref name="count"/> entries at random, preserving a
    /// balanced image/video mix whenever both kinds are available.
    /// </summary>
    public List<MediaFileInfo> DrawRandom(List<MediaFileInfo> eligible, int count, Random random)
    {
        if (count <= 0 || eligible.Count == 0)
        {
            return new List<MediaFileInfo>();
        }

        List<MediaFileInfo> shuffled = new List<MediaFileInfo>(eligible);
        Shuffle(shuffled, random);

        List<MediaFileInfo> images = new List<MediaFileInfo>();
        List<MediaFileInfo> videos = new List<MediaFileInfo>();
        foreach (MediaFileInfo item in shuffled)
        {
            if (item.Kind == MediaKind.Image)
            {
                images.Add(item);
            }
            else
            {
                videos.Add(item);
            }
        }

        List<MediaFileInfo> drawn = new List<MediaFileInfo>();
        int desiredVideos = Math.Min(videos.Count, count / 2);
        int desiredImages = Math.Min(images.Count, count - desiredVideos);

        for (int index = 0; index < desiredImages && drawn.Count < count; index++)
        {
            drawn.Add(images[index]);
        }

        for (int index = 0; index < desiredVideos && drawn.Count < count; index++)
        {
            drawn.Add(videos[index]);
        }

        // If one kind ran short, top up from the remaining shuffled entries.
        if (drawn.Count < count)
        {
            foreach (MediaFileInfo item in shuffled)
            {
                if (drawn.Count >= count)
                {
                    break;
                }

                if (!drawn.Contains(item))
                {
                    drawn.Add(item);
                }
            }
        }

        return drawn;
    }

    private static void Shuffle(List<MediaFileInfo> list, Random random)
    {
        for (int index = list.Count - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            (list[index], list[swapIndex]) = (list[swapIndex], list[index]);
        }
    }
}

public enum MediaKind
{
    Image,
    Video,
    Unsupported
}

public sealed record MediaFileInfo(string AbsolutePath, string RelativePath, MediaKind Kind);