using System.IO;

namespace Reima.Services;

/// <summary>File-system helpers that survive what mod archives throw at them.</summary>
public static class Fs
{
    /// <summary>
    /// Recursive delete that works when the archive marked files/folders read-only (7-Zip keeps that attribute,
    /// and Directory.Delete then fails with "Access denied"). Retries briefly for antivirus/indexer locks.
    /// </summary>
    public static void DeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    if ((e.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                        e.Attributes = e is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
                var root = new DirectoryInfo(dir);
                if ((root.Attributes & FileAttributes.ReadOnly) != 0) root.Attributes = FileAttributes.Directory;
                Directory.Delete(dir, true);
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300 * (attempt + 1));
            }
        }
    }

    /// <summary>File.Copy(overwrite) that also replaces a read-only target, and leaves the copy writable.</summary>
    public static void CopyFile(string src, string dest)
    {
        if (File.Exists(dest) && (File.GetAttributes(dest) & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(dest, FileAttributes.Normal);
        File.Copy(src, dest, true);
        var a = File.GetAttributes(dest);
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, a & ~FileAttributes.ReadOnly);
    }
}
