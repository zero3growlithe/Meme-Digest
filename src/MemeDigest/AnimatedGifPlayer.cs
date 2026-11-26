using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MemeDigest;

/// <summary>
/// Decoded animated GIF: fully composited frames plus per-frame display delays, kept frozen so
/// the viewer can hand single frames to an Image control on a timer.
///
/// Two WPF traps make the obvious implementation unusable:
/// - BitmapImage only ever renders frame 0, so a plain image source shows a still picture.
/// - GIF frames are deltas. Each frame paints only its own rectangle and tells the decoder how
///   to dispose of it afterwards; WIC returns the raw frames without applying any of that, so
///   drawing them directly shows the unpainted regions as black patches and turns delta frames
///   into whole-black flashes. This class replays the GIF composition rules onto one canvas.
/// </summary>
public sealed class AnimatedGifPlayer
{
    // ── Constants ──

    /// <summary>GIFs declaring 0–1 hundredths per frame are drawn at 100 ms (browser behaviour).</summary>
    private const int MinimumDelayHundredths = 2;

    private const int DefaultDelayHundredths = 10;

    /// <summary>Single-frame pixel ceiling (roughly 1080p) before frames get downscaled.</summary>
    private const long FramePixelBudget = 3_000_000;

    /// <summary>Whole-animation pixel ceiling — frames are full-canvas bitmaps at 4 bytes each.</summary>
    private const long AnimationPixelBudget = 40_000_000;

    /// <summary>Animations above this decoded size are played once but never cached.</summary>
    private const long CacheablePixelBudget = 6_000_000;

    private const int CacheCapacity = 4;

    private static readonly object CacheLock = new object();

    private static readonly Dictionary<string, AnimatedGifPlayer> Cache = new Dictionary<string, AnimatedGifPlayer>(StringComparer.Ordinal);

    private static readonly Queue<string> CacheOrder = new Queue<string>();

    // ── Fields ──

    private readonly List<BitmapSource> frames;

    private readonly List<TimeSpan> delays;

    // ── Nested types ──

    /// <summary>Where one frame paints and how it must be disposed of afterwards.</summary>
    private readonly struct GifFrameLayout
    {
        public GifFrameLayout(int left, int top, int width, int height, int disposal)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Disposal = disposal;
        }

        public int Left { get; }

        public int Top { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>0 = unspecified, 1 = leave in place, 2 = restore background, 3 = restore previous.</summary>
        public int Disposal { get; }

        public bool RestoresCanvas
        {
            get { return Disposal == 2 || Disposal == 3; }
        }
    }

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
    /// Decodes and composites the GIF at the given path (background-thread safe — everything
    /// comes back frozen). Returns null when the file cannot be decoded; recent players are
    /// reused from the cache.
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
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            int canvasWidth = 0;
            int canvasHeight = 0;
            for (int frameIndex = 0; frameIndex < decoder.Frames.Count; frameIndex++)
            {
                BitmapFrame frame = decoder.Frames[frameIndex];
                canvasWidth = Math.Max(canvasWidth, frame.PixelWidth);
                canvasHeight = Math.Max(canvasHeight, frame.PixelHeight);
            }

            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                return null;
            }

            // Downscale BEFORE compositing: the per-pixel blend then runs on the smaller canvas,
            // which keeps a 100-frame 1080p GIF from costing seconds of CPU and hundreds of MB.
            long totalPixels = (long)canvasWidth * canvasHeight * decoder.Frames.Count;
            double scale = ComputeScale(decoder.Frames.Count, totalPixels);
            int scaledWidth = scale < 1.0 ? Math.Max(1, (int)Math.Round(canvasWidth * scale)) : canvasWidth;
            int scaledHeight = scale < 1.0 ? Math.Max(1, (int)Math.Round(canvasHeight * scale)) : canvasHeight;

            List<TimeSpan> decodedDelays = new List<TimeSpan>(decoder.Frames.Count);
            for (int frameIndex = 0; frameIndex < decoder.Frames.Count; frameIndex++)
            {
                decodedDelays.Add(ReadFrameDelay(decoder.Frames[frameIndex]));
            }

            List<BitmapSource> compositedFrames = CompositeFrames(decoder, canvasWidth, scaledWidth, scaledHeight, scale);
            if (compositedFrames.Count == 0)
            {
                return null;
            }

            bool isCacheable = (long)scaledWidth * scaledHeight * compositedFrames.Count <= CacheablePixelBudget;

            AnimatedGifPlayer player = new AnimatedGifPlayer(compositedFrames, decodedDelays);
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

    // ── Composition ──

    /// <summary>
    /// Replays the GIF composition rules over one premultiplied-BGRA canvas. Frames that leave
    /// the canvas alone simply accumulate (that is how delta frames build an image), while
    /// disposal 2/3 restore the pixels the frame covered before the next frame paints.
    ///
    /// Disposal 2 is treated as "restore the previous pixels" rather than "clear the rectangle
    /// to the background colour": GIF backgrounds are transparent in practice, where the two
    /// rules are identical, and it guarantees a wrong or missing image-descriptor rectangle can
    /// never punch a black hole into the animation.
    /// </summary>
    private static List<BitmapSource> CompositeFrames(GifBitmapDecoder decoder, int canvasWidth, int scaledWidth, int scaledHeight, double scale)
    {
        int stride = scaledWidth * 4;
        byte[] canvas = new byte[stride * scaledHeight];
        byte[]? restoreBuffer = null;
        GifFrameLayout? previousLayout = null;

        List<BitmapSource> composited = new List<BitmapSource>(decoder.Frames.Count);
        for (int frameIndex = 0; frameIndex < decoder.Frames.Count; frameIndex++)
        {
            // Undo the previous frame when it asked to be restored before this frame paints.
            if (previousLayout != null && previousLayout.Value.RestoresCanvas && restoreBuffer != null)
            {
                RestoreRect(canvas, stride, restoreBuffer, previousLayout.Value, scaledWidth, scaledHeight);
            }

            BitmapFrame frame = decoder.Frames[frameIndex];
            BitmapSource sourcePixels = EnsurePremultiplied(frame);
            if (scale < 1.0)
            {
                sourcePixels = Downscale(sourcePixels, scale);
            }

            GifFrameLayout layout = BuildLayout(frame, sourcePixels, canvasWidth, scaledWidth, scaledHeight);

            // Snapshot what this frame is about to cover, for use if it asks to be restored.
            restoreBuffer = layout.RestoresCanvas
                ? CopyRect(canvas, stride, layout, scaledWidth, scaledHeight)
                : null;
            previousLayout = layout;

            BlendFrame(canvas, stride, sourcePixels, layout, scaledWidth, scaledHeight);

            // Hand every frame its own copy of the pixels — BitmapSource.Create may not keep its own.
            byte[] framePixels = (byte[])canvas.Clone();
            BitmapSource composedFrame = BitmapSource.Create(scaledWidth, scaledHeight, 96, 96, PixelFormats.Pbgra32, null, framePixels, stride);
            composedFrame.Freeze();
            composited.Add(composedFrame);
        }

        return composited;
    }

    private static GifFrameLayout BuildLayout(BitmapFrame frame, BitmapSource sourcePixels, int canvasWidth, int scaledWidth, int scaledHeight)
    {
        double coordinateScale = canvasWidth > 0 ? (double)scaledWidth / canvasWidth : 1.0;
        int declaredLeft = ScaleCoordinate(ReadMetadataInt(frame, "/imgdesc/Left"), coordinateScale);
        int declaredTop = ScaleCoordinate(ReadMetadataInt(frame, "/imgdesc/Top"), coordinateScale);

        int frameWidth = sourcePixels.PixelWidth;
        int frameHeight = sourcePixels.PixelHeight;

        // WIC expands frames to the logical screen, so their offsets are already baked in and
        // only genuinely smaller (sub-rect) frames are drawn at their declared position.
        bool isFullScreenFrame = frameWidth >= scaledWidth && frameHeight >= scaledHeight;
        int left = isFullScreenFrame ? 0 : Math.Max(0, declaredLeft);
        int top = isFullScreenFrame ? 0 : Math.Max(0, declaredTop);

        return new GifFrameLayout(left, top, frameWidth, frameHeight, ReadMetadataInt(frame, "/grctlext/Disposal"));
    }

    private static int ScaleCoordinate(int value, double scale)
    {
        if (value <= 0)
        {
            return 0;
        }

        int scaledValue = (int)Math.Round(value * scale);
        return scaledValue < 1 ? 1 : scaledValue;
    }

    private static BitmapSource EnsurePremultiplied(BitmapSource frame)
    {
        if (frame.Format == PixelFormats.Pbgra32)
        {
            return frame;
        }

        // Straight-alpha or palette frames are converted once, premultiplied for clean blending.
        FormatConvertedBitmap converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        if (converted.CanFreeze)
        {
            converted.Freeze();
        }

        return converted;
    }

    private static BitmapSource Downscale(BitmapSource frame, double scale)
    {
        TransformedBitmap scaledFrame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        if (scaledFrame.CanFreeze)
        {
            scaledFrame.Freeze();
        }

        return scaledFrame;
    }

    /// <summary>Premultiplied source-over blend of one frame onto the canvas; transparent pixels keep what is already there.</summary>
    private static void BlendFrame(byte[] canvas, int stride, BitmapSource frame, GifFrameLayout layout, int canvasWidth, int canvasHeight)
    {
        int frameWidth = frame.PixelWidth;
        int frameHeight = frame.PixelHeight;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        int frameStride = frameWidth * 4;
        byte[] framePixels = new byte[frameStride * frameHeight];
        frame.CopyPixels(framePixels, frameStride, 0);

        for (int y = 0; y < frameHeight; y++)
        {
            int canvasY = layout.Top + y;
            if (canvasY < 0 || canvasY >= canvasHeight)
            {
                continue;
            }

            int frameRow = y * frameStride;
            int canvasRow = canvasY * stride;
            for (int x = 0; x < frameWidth; x++)
            {
                int canvasX = layout.Left + x;
                if (canvasX < 0 || canvasX >= canvasWidth)
                {
                    continue;
                }

                int frameOffset = frameRow + (x * 4);
                byte sourceAlpha = framePixels[frameOffset + 3];
                if (sourceAlpha == 0)
                {
                    // Fully transparent: the pixels already on the canvas stay visible.
                    continue;
                }

                int canvasOffset = canvasRow + (canvasX * 4);
                if (sourceAlpha == 255)
                {
                    canvas[canvasOffset] = framePixels[frameOffset];
                    canvas[canvasOffset + 1] = framePixels[frameOffset + 1];
                    canvas[canvasOffset + 2] = framePixels[frameOffset + 2];
                    canvas[canvasOffset + 3] = 255;
                    continue;
                }

                int inverseAlpha = 255 - sourceAlpha;
                canvas[canvasOffset] = CompositeChannel(framePixels[frameOffset], canvas[canvasOffset], inverseAlpha);
                canvas[canvasOffset + 1] = CompositeChannel(framePixels[frameOffset + 1], canvas[canvasOffset + 1], inverseAlpha);
                canvas[canvasOffset + 2] = CompositeChannel(framePixels[frameOffset + 2], canvas[canvasOffset + 2], inverseAlpha);
                canvas[canvasOffset + 3] = CompositeChannel(sourceAlpha, canvas[canvasOffset + 3], inverseAlpha);
            }
        }
    }

    private static byte CompositeChannel(byte source, byte destination, int inverseAlpha)
    {
        int blended = source + ((destination * inverseAlpha) / 255);
        return blended > 255 ? (byte)255 : (byte)blended;
    }

    private static byte[] CopyRect(byte[] canvas, int stride, GifFrameLayout layout, int canvasWidth, int canvasHeight)
    {
        GetClippedRect(layout, canvasWidth, canvasHeight, out int left, out int top, out int width, out int height);
        byte[] buffer = new byte[Math.Max(0, width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(canvas, ((top + y) * stride) + (left * 4), buffer, y * width * 4, width * 4);
        }

        return buffer;
    }

    private static void RestoreRect(byte[] canvas, int stride, byte[] restoreBuffer, GifFrameLayout layout, int canvasWidth, int canvasHeight)
    {
        GetClippedRect(layout, canvasWidth, canvasHeight, out int left, out int top, out int width, out int height);
        int requiredBytes = width * height * 4;
        if (requiredBytes <= 0 || restoreBuffer.Length < requiredBytes)
        {
            // The rectangle changed shape since the snapshot: nothing safe to restore.
            return;
        }

        for (int y = 0; y < height; y++)
        {
            Array.Copy(restoreBuffer, y * width * 4, canvas, ((top + y) * stride) + (left * 4), width * 4);
        }
    }

    private static void GetClippedRect(GifFrameLayout layout, int canvasWidth, int canvasHeight, out int left, out int top, out int width, out int height)
    {
        left = Math.Max(0, Math.Min(layout.Left, canvasWidth));
        top = Math.Max(0, Math.Min(layout.Top, canvasHeight));
        width = Math.Max(0, Math.Min(layout.Width, canvasWidth - left));
        height = Math.Max(0, Math.Min(layout.Height, canvasHeight - top));
    }

    // ── Metadata ──

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

    /// <summary>Reads an integer GIF metadata query, 0 when the query is absent or unreadable.</summary>
    private static int ReadMetadataInt(BitmapFrame frame, string query)
    {
        try
        {
            BitmapMetadata? metadata = frame.Metadata as BitmapMetadata;
            object? rawValue = metadata?.GetQuery(query);
            if (rawValue == null)
            {
                return 0;
            }

            if (rawValue is ushort unsignedShortValue)
            {
                return unsignedShortValue;
            }

            if (rawValue is byte byteValue)
            {
                return byteValue;
            }

            return Convert.ToInt32(rawValue, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ── Scaling ──

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
