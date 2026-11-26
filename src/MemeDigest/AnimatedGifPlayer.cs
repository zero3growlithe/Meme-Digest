using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MemeDigest;

/// <summary>
/// Decoded animated GIF: the full frame list plus per-frame display delays, kept frozen so
/// the viewer can hand single frames to an Image control on a timer.
/// WPF's BitmapImage only ever renders frame 0 of an animation, which is why a plain
/// BitmapImage source shows GIFs as a still picture — the caller steps through these frames.
/// </summary>
public sealed class AnimatedGifPlayer
{
    // ── Constants ──

    /// <summary>GIFs that declare 0–1 hundredths of a second are drawn at 100 ms (browser behaviour).</summary>
    private const int MinimumDelayHundredths = 2;

    private const int DefaultDelayHundredths = 10;

    /// <summary>Total decoded pixels allowed for one animation before frames get downscaled.</summary>
    private const long AnimationPixelBudget = 400_000_000;

    /// <summary>Single-frame pixel ceiling (roughly 1080p) before that frame gets downscaled.</summary>
    private const long FramePixelBudget = 3_000_000;

    /// <summary>Animations above this decoded size are used once but never cached.</summary>
    private const long CacheablePixelBudget = 64_000_000;

    private const int CacheCapacity = 4;

    private static readonly object CacheLock = new object();

    private static readonly Dictionary<string, AnimatedGifPlayer> Cache = new Dictionary<string, AnimatedGifPlayer>(StringComparer.Ordinal);

    private static readonly Queue<string> CacheOrder = new Queue<string>();

    // ── Fields ──

    private readonly List<BitmapSource> frames;

    private readonly List<TimeSpan> delays;

    // ── Construction ──

    private AnimatedGifPlayer(List<BitmapSource> frames, List<TimeSpan> delays)
    {
        this.frames = frames;
        this.delays = delays;
    }

    // ── Properties ──

    public int FrameCount
    {
        get { return frames.Count; }
    }

    /// <summary>false for single-frame GIFs — the viewer then leaves it on screen without a timer.</summary>
    public bool IsAnimated
    {
        get { return frames.Count > 1; }
    }

    // ── Public API ──

    /// <summary>
    /// Decodes the GIF at the given path (background-thread safe — frames come back frozen).
    /// Returns null when the file cannot be decoded; a recent player is reused from the cache.
    /// </summary>
    public static AnimatedGifPlayer? TryLoad(string absolutePath)
    {
        string cacheKey = BuildCacheKey(absolutePath);
        lock (CacheLock)
        {
            AnimatedGifPlayer? cachedPlayer;
            if (Cache.TryGetValue(cacheKey, out cachedPlayer))
            {
                return cachedPlayer;
            }
        }

        try
        {
            GifBitmapDecoder decoder = new GifBitmapDecoder(
                new Uri(absolutePath, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            List<BitmapSource> decodedFrames = new List<BitmapSource>(decoder.Frames.Count);
            List<TimeSpan> decodedDelays = new List<TimeSpan>(decoder.Frames.Count);
            long totalPixels = 0;
            for (int frameIndex = 0; frameIndex < decoder.Frames.Count; frameIndex++)
            {
                // Frames come back as BitmapFrame, which is the type that carries the GIF metadata.
                BitmapFrame frame = decoder.Frames[frameIndex];
                decodedFrames.Add(frame);
                decodedDelays.Add(ReadFrameDelay(frame));
                totalPixels += (long)frame.PixelWidth * frame.PixelHeight;
            }

            double scale = ComputeScale(decodedFrames.Count, totalPixels);
            bool isCacheable = totalPixels * scale * scale <= CacheablePixelBudget;

            List<BitmapSource> scaledFrames = new List<BitmapSource>(decodedFrames.Count);
            for (int frameIndex = 0; frameIndex < decodedFrames.Count; frameIndex++)
            {
                scaledFrames.Add(scale < 1.0 ? Downscale(decodedFrames[frameIndex], scale) : FreezeClone(decodedFrames[frameIndex]));
            }

            AnimatedGifPlayer player = new AnimatedGifPlayer(scaledFrames, decodedDelays);
            if (isCacheable)
            {
                StoreInCache(cacheKey, player);
            }

            return player;
        }
        catch (Exception)
        {
            // Corrupt or non-GIF file: the viewer shows a decode error instead.
            return null;
        }
    }

    public BitmapSource GetFrame(int frameIndex)
    {
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("No frames were decoded.");
        }

        int wrappedIndex = frameIndex % frames.Count;
        if (wrappedIndex < 0)
        {
            wrappedIndex += frames.Count;
        }

        return frames[wrappedIndex];
    }

    /// <summary>How long the given frame stays on screen before the next one is shown.</summary>
    public TimeSpan GetFrameDelay(int frameIndex)
    {
        if (delays.Count == 0)
        {
            return TimeSpan.FromMilliseconds(100);
        }

        int wrappedIndex = frameIndex % delays.Count;
        if (wrappedIndex < 0)
        {
            wrappedIndex += delays.Count;
        }

        return delays[wrappedIndex];
    }

    // ── Decoding helpers ──

    private static TimeSpan ReadFrameDelay(BitmapFrame frame)
    {
        int hundredths = DefaultDelayHundredths;
        try
        {
            BitmapMetadata? metadata = frame.Metadata as BitmapMetadata;
            object? rawDelay = metadata?.GetQuery("/grctlext/Delay");
            if (rawDelay is ushort declaredDelay)
            {
                hundredths = declaredDelay;
            }
            else if (rawDelay is byte declaredByteDelay)
            {
                hundredths = declaredByteDelay;
            }
        }
        catch (Exception)
        {
            // Missing delay metadata: fall back to the 100 ms default.
        }

        if (hundredths < MinimumDelayHundredths)
        {
            hundredths = DefaultDelayHundredths;
        }

        return TimeSpan.FromMilliseconds(hundredths * 10);
    }

    private static double ComputeScale(int frameCount, long totalPixels)
    {
        if (frameCount <= 0)
        {
            return 1.0;
        }

        double pixelsPerFrame = (double)totalPixels / frameCount;
        double scale = 1.0;
        if (pixelsPerFrame > FramePixelBudget)
        {
            scale = Math.Sqrt(FramePixelBudget / pixelsPerFrame);
        }

        double estimatedPixels = totalPixels * scale * scale;
        if (estimatedPixels > AnimationPixelBudget)
        {
            scale *= Math.Sqrt(AnimationPixelBudget / estimatedPixels);
        }

        return scale;
    }

    private static BitmapSource FreezeClone(BitmapSource frame)
    {
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        BitmapSource detachedFrame = frame.Clone();
        detachedFrame.Freeze();
        return detachedFrame;
    }

    private static BitmapSource Downscale(BitmapSource frame, double scale)
    {
        BitmapSource frozenFrame = FreezeClone(frame);
        TransformedBitmap scaledFrame = new TransformedBitmap(frozenFrame, new ScaleTransform(scale, scale));
        scaledFrame.Freeze();
        return scaledFrame;
    }

    // ── Cache ──

    private static string BuildCacheKey(string absolutePath)
    {
        string identity = absolutePath.ToUpperInvariant();
        try
        {
            System.IO.FileInfo fileInfo = new System.IO.FileInfo(absolutePath);
            if (fileInfo.Exists)
            {
                return identity
                    + "|" + fileInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + fileInfo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
            // Fall through to the path-only key.
        }

        return identity;
    }

    private static void StoreInCache(string cacheKey, AnimatedGifPlayer player)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(cacheKey))
            {
                return;
            }

            Cache[cacheKey] = player;
            CacheOrder.Enqueue(cacheKey);
            while (CacheOrder.Count > CacheCapacity)
            {
                string oldestKey = CacheOrder.Dequeue();
                Cache.Remove(oldestKey);
            }
        }
    }
}
