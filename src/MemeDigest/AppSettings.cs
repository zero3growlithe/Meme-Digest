using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MemeDigest;

public sealed class AppSettings
{
    // ── Constants ──

    public const string SettingsFileName = "settings.json";

    public const int DefaultDrawCount = 12;

    public const string DefaultProfileName = "Default";

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    // ── Persisted properties ──

    /// <summary>Meme library root per profile — each user browses their own source folder.</summary>
    public Dictionary<string, string> ProfileLibraryPaths { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// Library root of the CURRENT profile (the legacy flat `LibraryPath` key from older
    /// settings files is migrated into ProfileLibraryPaths during FillDefaults).
    /// </summary>
    public string CurrentLibraryPath
    {
        get
        {
            string path;
            if (ProfileLibraryPaths != null && ProfileLibraryPaths.TryGetValue(CurrentUserProfile ?? string.Empty, out path))
            {
                return path ?? string.Empty;
            }

            return string.Empty;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(CurrentUserProfile))
            {
                return;
            }

            ProfileLibraryPaths ??= new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                ProfileLibraryPaths.Remove(CurrentUserProfile);
            }
            else
            {
                ProfileLibraryPaths[CurrentUserProfile] = value;
            }
        }
    }

    public string HistoryDirectory { get; set; } = string.Empty;

    public string ThumbnailDirectory { get; set; } = string.Empty;

    public int DrawCount { get; set; } = DefaultDrawCount;

    public string FfmpegExecutablePath { get; set; } = "ffmpeg";

    // ── Legacy migration state ──

    /// <summary>Legacy flat LibraryPath captured from old JSON in FillDefaults; null elsewhere.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    private string? legacyLibraryPath;

    /// <summary>Raw settings JSON captured at load, parsed once in FillDefaults for legacy migration.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    private string? _rawJson;

    public List<string> UserProfiles { get; set; } = new List<string> { DefaultProfileName };

    public string CurrentUserProfile { get; set; } = DefaultProfileName;

    /// <summary>Video playback volume used by the viewer (0.0–1.0); muted by default so gallery browsing stays quiet.</summary>
    public double VideoPlaybackVolume { get; set; } = 0.0;

    public List<string> ImageExtensions { get; set; } = new List<string>
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff"
    };

    public List<string> VideoExtensions { get; set; } = new List<string>
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".mpeg", ".mpg"
    };

    // ── Derived locations ──

    public static string SettingsDirectory
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(MemeDigest)); }
    }

    public static string DefaultSettingsFilePath
    {
        get { return Path.Combine(SettingsDirectory, SettingsFileName); }
    }

    public static string DefaultHistoryDirectory
    {
        get { return Path.Combine(SettingsDirectory, "History"); }
    }

    public static string DefaultThumbnailDirectory
    {
        get { return Path.Combine(SettingsDirectory, "Thumbnails"); }
    }

    public bool IsLibraryPathValid
    {
        get { return !string.IsNullOrWhiteSpace(CurrentLibraryPath) && Directory.Exists(CurrentLibraryPath); }
    }

    public bool HasVideoExtensions
    {
        get { return VideoExtensions != null && VideoExtensions.Count > 0; }
    }

    // ── Persistence ──

    public static AppSettings LoadOrCreate()
    {
        try
        {
            string settingsFilePath = DefaultSettingsFilePath;
            if (File.Exists(settingsFilePath))
            {
                string json = File.ReadAllText(settingsFilePath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (loaded != null)
                {
                    loaded._rawJson = json;
                    loaded.FillDefaults();
                    loaded._rawJson = null;
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt settings fall back to defaults; the user can edit them again.
        }

        AppSettings created = new AppSettings();
        created.FillDefaults();
        return created;
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        string json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(DefaultSettingsFilePath, json);
    }

    public bool IsKnownImageExtension(string extension)
    {
        return ContainsIgnoreCase(ImageExtensions, extension);
    }

    public bool IsKnownVideoExtension(string extension)
    {
        return ContainsIgnoreCase(VideoExtensions, extension);
    }

    public void FillDefaults()
    {
        // Raw JSON capture: the legacy flat key (removed from the model) still arrives
        // here for manual migration when present in an old settings file.
        legacyLibraryPath = null;
        if (_rawJson != null)
        {
            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(_rawJson);
                if (document.RootElement.TryGetProperty("LibraryPath", out System.Text.Json.JsonElement element) && element.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    legacyLibraryPath = element.GetString();
                }
            }
            catch (Exception)
            {
                // Unparseable raw json → no migration.
            }
        }

        if (string.IsNullOrWhiteSpace(HistoryDirectory))
        {
            HistoryDirectory = DefaultHistoryDirectory;
        }

        if (string.IsNullOrWhiteSpace(ThumbnailDirectory))
        {
            ThumbnailDirectory = DefaultThumbnailDirectory;
        }

        if (DrawCount <= 0)
        {
            DrawCount = DefaultDrawCount;
        }

        if (UserProfiles == null || UserProfiles.Count == 0)
        {
            UserProfiles = new List<string> { DefaultProfileName };
        }

        if (string.IsNullOrWhiteSpace(CurrentUserProfile) || !UserProfiles.Contains(CurrentUserProfile))
        {
            CurrentUserProfile = UserProfiles[0];
        }

        // Legacy migration: older settings files kept ONE flat LibraryPath. Copy it into
        // the current profile's per-profile entry once, then it lives per-profile.
        ProfileLibraryPaths ??= new Dictionary<string, string>();
        if (legacyLibraryPath != null && !string.IsNullOrWhiteSpace(legacyLibraryPath))
        {
            if (!ProfileLibraryPaths.ContainsKey(CurrentUserProfile))
            {
                ProfileLibraryPaths[CurrentUserProfile] = legacyLibraryPath;
            }
        }

        if (ImageExtensions == null || ImageExtensions.Count == 0)
        {
            ImageExtensions = new List<string> { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };
        }

        if (VideoExtensions == null)
        {
            VideoExtensions = new List<string>();
        }

        if (VideoPlaybackVolume < 0.0 || VideoPlaybackVolume > 1.0 || double.IsNaN(VideoPlaybackVolume))
        {
            VideoPlaybackVolume = 0.0;
        }
    }

    // ── Helpers ──

    private static bool ContainsIgnoreCase(List<string> values, string value)
    {
        if (values == null || string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (string candidate in values)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}