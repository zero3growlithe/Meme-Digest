using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MemeDigest;

public enum MemeHistoryState
{
    Picked,
    Discarded
}

public sealed record MemeHistoryEntry(DateTime TimestampUtc, MemeHistoryState State, string RelativePath);

/// <summary>
/// Per-user history of picked and discarded memes, stored as one
/// human-readable text file per profile so it can be inspected and hand-edited.
/// Format: "YYYY-MM-DD HH:mm:ssZ | PICKED|DISCARDED | relative/path.ext"
/// </summary>
public sealed class UserHistory
{
    public const string PickedHeader = "[PICKED]";
    public const string DiscardedHeader = "[DISCARDED]";

    private static readonly string[] LineSeparator = { Environment.NewLine };

    public string ProfileName { get; }

    public List<MemeHistoryEntry> Entries { get; } = new List<MemeHistoryEntry>();

    public UserHistory(string profileName)
    {
        ProfileName = profileName;
    }

    // ── Queries ──

    public HashSet<string> GetExcludedRelativePaths()
    {
        HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MemeHistoryEntry entry in Entries)
        {
            excluded.Add(entry.RelativePath);
        }

        return excluded;
    }

    public int PickedCount
    {
        get { return CountByState(MemeHistoryState.Picked); }
    }

    public int DiscardedCount
    {
        get { return CountByState(MemeHistoryState.Discarded); }
    }

    private int CountByState(MemeHistoryState state)
    {
        int count = 0;
        foreach (MemeHistoryEntry entry in Entries)
        {
            if (entry.State == state)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Legacy cleanup: files written before the tabs existed carry the same meme in
    /// BOTH sections, which made Kept and Discarded render identical lists. A meme
    /// gets exactly one state — the latest one wins (Entries is appended chronologically).
    /// </summary>
    public void CollapseToLatestState()
    {
        Dictionary<string, (MemeHistoryState State, MemeHistoryEntry Entry)> latestByPath = new Dictionary<string, (MemeHistoryState, MemeHistoryEntry)>(StringComparer.OrdinalIgnoreCase);
        foreach (MemeHistoryEntry entry in Entries)
        {
            latestByPath[entry.RelativePath] = (entry.State, entry);
        }

        if (latestByPath.Count == Entries.Count)
        {
            return;
        }

        Entries.Clear();
        foreach (MemeHistoryEntry entry in latestByPath.Values)
        {
            Entries.Add(entry);
        }
    }

    // ── Mutations ──

    public void Add(MemeHistoryState state, string relativePath, DateTime timestampUtc)
    {
        string normalizedPath = NormalizePath(relativePath);
        foreach (MemeHistoryEntry entry in Entries)
        {
            if (entry.State == state && string.Equals(entry.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        Entries.Add(new MemeHistoryEntry(timestampUtc, state, normalizedPath));
    }

    public bool Contains(MemeHistoryState state, string relativePath)
    {
        string normalizedPath = NormalizePath(relativePath);
        foreach (MemeHistoryEntry entry in Entries)
        {
            if (entry.State == state && string.Equals(entry.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes every entry for a relative path (used by the reset-to-drawable action).</summary>
    public void RemoveAll(MemeHistoryState state, string relativePath)
    {
        string normalizedPath = NormalizePath(relativePath);
        for (int index = Entries.Count - 1; index >= 0; index--)
        {
            MemeHistoryEntry entry = Entries[index];
            if (entry.State == state && string.Equals(entry.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                Entries.RemoveAt(index);
            }
        }
    }

    // ── File I/O ──

    public static string GetHistoryFilePath(string historyDirectory, string profileName)
    {
        return Path.Combine(historyDirectory, "history-" + SanitizeProfileForFileName(profileName) + ".txt");
    }

    public static string SanitizeProfileForFileName(string profileName)
    {
        char[] characters = profileName.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            bool isSafe = char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == ' ';
            if (!isSafe)
            {
                characters[index] = '_';
            }
        }

        string sanitized = new string(characters).Trim();
        if (sanitized.Length == 0)
        {
            sanitized = "profile";
        }

        return sanitized;
    }

    public void SaveToFile(string historyDirectory)
    {
        Directory.CreateDirectory(historyDirectory);
        var grouped = new Dictionary<MemeHistoryState, List<MemeHistoryEntry>>();
        foreach (MemeHistoryEntry entry in Entries)
        {
            if (!grouped.TryGetValue(entry.State, out List<MemeHistoryEntry>? bucket))
            {
                bucket = new List<MemeHistoryEntry>();
                grouped.Add(entry.State, bucket);
            }

            bucket.Add(entry);
        }

        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Meme Digest history");
        builder.AppendLine("Profile: " + ProfileName);
        builder.AppendLine("Updated: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z");
        builder.AppendLine("One line per meme: date | state | path relative to the library root");
        builder.AppendLine();

        WriteSection(builder, MemeHistoryState.Picked, "Picked memes (kept / sent)");
        WriteSection(builder, MemeHistoryState.Discarded, "Discarded memes (never drawn again)");

        string filePath = GetHistoryFilePath(historyDirectory, ProfileName);
        File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(false));
    }

    private void WriteSection(StringBuilder builder, MemeHistoryState state, string sectionTitle)
    {
        builder.AppendLine(state == MemeHistoryState.Picked ? PickedHeader : DiscardedHeader);
        builder.AppendLine("; " + sectionTitle);

        List<MemeHistoryEntry> sectionEntries = new List<MemeHistoryEntry>();
        foreach (MemeHistoryEntry entry in Entries)
        {
            if (entry.State == state)
            {
                sectionEntries.Add(entry);
            }
        }

        sectionEntries.Sort((left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
        foreach (MemeHistoryEntry entry in sectionEntries)
        {
            builder.AppendLine(FormatEntry(entry));
        }

        builder.AppendLine();
    }

    private static string FormatEntry(MemeHistoryEntry entry)
    {
        string stateName = entry.State == MemeHistoryState.Picked ? "PICKED" : "DISCARDED";
        return entry.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z | " + stateName + " | " + entry.RelativePath;
    }

    public static UserHistory LoadFromFile(string historyDirectory, string profileName)
    {
        UserHistory history = new UserHistory(profileName);
        string filePath = GetHistoryFilePath(historyDirectory, profileName);
        if (!File.Exists(filePath))
        {
            return history;
        }

        string[] lines = File.ReadAllLines(filePath);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("["))
            {
                continue;
            }

            MemeHistoryEntry? entry = TryParseEntry(line);
            if (entry != null)
            {
                history.Entries.Add(entry);
            }
        }

        return history;
    }

    private static MemeHistoryEntry? TryParseEntry(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 3)
        {
            return null;
        }

        string timestampText = parts[0].Trim();
        string stateText = parts[1].Trim();
        string pathText = NormalizePath(parts[2]);

        DateTime timestampUtc;
        if (!DateTime.TryParseExact(
                timestampText,
                "yyyy-MM-dd HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestampUtc))
        {
            return null;
        }

        MemeHistoryState state;
        if (string.Equals(stateText, "PICKED", StringComparison.OrdinalIgnoreCase))
        {
            state = MemeHistoryState.Picked;
        }
        else if (string.Equals(stateText, "DISCARDED", StringComparison.OrdinalIgnoreCase))
        {
            state = MemeHistoryState.Discarded;
        }
        else
        {
            return null;
        }

        if (pathText.Length == 0)
        {
            return null;
        }

        return new MemeHistoryEntry(timestampUtc, state, pathText);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
}