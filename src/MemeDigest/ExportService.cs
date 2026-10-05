using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace MemeDigest;

/// <summary>
/// Export helpers: reveal files in Explorer with multi-select, copy a file
/// drop list to the clipboard (paste-ready for Discord), and move or copy
/// files into a target folder with collision-safe names.
/// </summary>
public sealed class ExportService
{
    // ── Reveal in Explorer (multi-select) ──

    public static void RevealInExplorer(IReadOnlyList<string> absolutePaths)
    {
        if (absolutePaths == null || absolutePaths.Count == 0)
        {
            return;
        }

        foreach (string directory in DistinctDirectories(absolutePaths))
        {
            IntPtr folderPidl = NativeMethods.ILCreateFromPath(directory);
            if (folderPidl == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                List<string> inDirectory = new List<string>();
                foreach (string path in absolutePaths)
                {
                    if (string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                    {
                        inDirectory.Add(path);
                    }
                }

                IntPtr[] itemPidls = new IntPtr[inDirectory.Count];
                int validCount = 0;
                for (int index = 0; index < inDirectory.Count; index++)
                {
                    itemPidls[index] = NativeMethods.ILCreateFromPath(inDirectory[index]);
                    if (itemPidls[index] != IntPtr.Zero)
                    {
                        validCount++;
                    }
                }

                try
                {
                    NativeMethods.SHOpenFolderAndSelectItems(folderPidl, (uint)validCount, itemPidls, 0);
                }
                finally
                {
                    for (int index = 0; index < itemPidls.Length; index++)
                    {
                        if (itemPidls[index] != IntPtr.Zero)
                        {
                            NativeMethods.ILFree(itemPidls[index]);
                        }
                    }
                }
            }
            finally
            {
                NativeMethods.ILFree(folderPidl);
            }
        }
    }

    private static List<string> DistinctDirectories(IReadOnlyList<string> absolutePaths)
    {
        List<string> directories = new List<string>();
        foreach (string path in absolutePaths)
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory == null || directories.Contains(directory))
            {
                continue;
            }

            directories.Add(directory);
        }

        return directories;
    }

    // ── Clipboard file drop list ──

    public static bool CopyFileListToClipboard(IReadOnlyList<string> absolutePaths)
    {
        if (absolutePaths == null || absolutePaths.Count == 0)
        {
            return false;
        }

        StringCollection dropList = new StringCollection();
        foreach (string path in absolutePaths)
        {
            dropList.Add(path);
        }

        try
        {
            Clipboard.SetFileDropList(dropList);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Move / copy with collision-safe names ──

    public static int MoveOrCopy(IReadOnlyList<string> absolutePaths, string targetDirectory, bool move)
    {
        if (absolutePaths == null || absolutePaths.Count == 0 || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return 0;
        }

        Directory.CreateDirectory(targetDirectory);
        int completedCount = 0;
        foreach (string sourcePath in absolutePaths)
        {
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            string targetPath = BuildCollisionSafePath(targetDirectory, Path.GetFileName(sourcePath));
            try
            {
                if (move)
                {
                    File.Move(sourcePath, targetPath);
                }
                else
                {
                    File.Copy(sourcePath, targetPath, overwrite: false);
                }

                completedCount++;
            }
            catch (Exception)
            {
                // One failing file must not abort the batch.
            }
        }

        return completedCount;
    }

    private static string BuildCollisionSafePath(string targetDirectory, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(targetDirectory, fileName);
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(targetDirectory, baseName + " (" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + extension);
            suffix++;
        }

        return candidate;
    }

    // ── Native methods ──

    private static class NativeMethods
    {
        [DllImport("shell32.dll", SetLastError = true, EntryPoint = "ILCreateFromPathW")]
        public static extern IntPtr ILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string path);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int SHOpenFolderAndSelectItems(IntPtr folderPidl, uint cidl, IntPtr[] apidl, uint flags);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);
    }
}