using System.IO.Compression;

namespace TeleSave.Core;

public sealed record IntakeRejection(string Name, IntakeRejection.Reason Why, long Limit = 0, int Count = 0)
{
    public enum Reason { Empty, TooLarge, Unreadable, Duplicate, ZipFailed, TooManyFiles }

    public string Message => Why switch
    {
        Reason.Empty => L10n.T("%@: dosya boş.", Name),
        Reason.TooLarge => L10n.T("%@: %@ sınırını aşıyor.", Name, Format.Bytes(Limit)),
        Reason.Unreadable => L10n.T("%@: okunamadı.", Name),
        Reason.Duplicate => L10n.T("%@: az önce zaten gönderildi.", Name),
        Reason.ZipFailed => L10n.T("%@: klasör sıkıştırılamadı.", Name),
        Reason.TooManyFiles => L10n.T("%@: %@ dosya içeriyor; tek tek en fazla %@ dosya gönderilir. Zip olarak göndermeyi deneyin.",
            Name, Count.ToString(), DropIntake.MaxFilesPerFolder.ToString()),
        _ => Name
    };
}

public sealed record IntakeResult(IReadOnlyList<PreparedFile> Accepted, IReadOnlyList<IntakeRejection> Rejected);

/// <summary>How a dropped folder is sent. Telegram has no folder message: one zip or its files.</summary>
public enum FolderMode { Zip, Expand }

/// <summary>Turns dropped paths into uploadable files: zips folders, validates size, rejects recent duplicates.</summary>
public sealed class DropIntake
{
    public const int MaxFilesPerFolder = 1000;

    private readonly string _staging;
    private readonly TimeSpan _duplicateWindow;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _recent = new();
    private readonly object _gate = new();

    public DropIntake(string stagingDirectory, TimeSpan? duplicateWindow = null, Func<DateTimeOffset>? now = null)
    {
        _staging = stagingDirectory;
        _duplicateWindow = duplicateWindow ?? TimeSpan.FromSeconds(60);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <param name="temporary">The files were created by TeleSave (e.g. a pasted image) and are deleted after upload.</param>
    public IntakeResult Prepare(IEnumerable<string> paths, long maxFileSize, FolderMode folderMode = FolderMode.Zip, bool temporary = false)
    {
        lock (_gate)
        {
            var accepted = new List<PreparedFile>();
            var rejected = new List<IntakeRejection>();
            var current = _now();
            foreach (var key in _recent.Where(p => current - p.Value >= _duplicateWindow).Select(p => p.Key).ToList()) _recent.Remove(key);

            var candidates = new List<string>();
            foreach (var path in paths)
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                if (folderMode == FolderMode.Expand && Directory.Exists(path))
                {
                    var files = FilesInside(path);
                    if (files.Count > MaxFilesPerFolder) rejected.Add(new IntakeRejection(name, IntakeRejection.Reason.TooManyFiles, Count: files.Count));
                    else if (files.Count == 0) rejected.Add(new IntakeRejection(name, IntakeRejection.Reason.Empty));
                    else candidates.AddRange(files);
                }
                else
                {
                    candidates.Add(path);
                }
            }

            foreach (var path in candidates)
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                PreparedFile file;
                if (Directory.Exists(path))
                {
                    var zipped = Zip(path);
                    if (zipped is null) { rejected.Add(new IntakeRejection(name, IntakeRejection.Reason.ZipFailed)); continue; }
                    file = zipped;
                }
                else if (File.Exists(path) && CanRead(path))
                {
                    file = new PreparedFile(path, name, new FileInfo(path).Length, temporary);
                }
                else
                {
                    rejected.Add(new IntakeRejection(name, IntakeRejection.Reason.Unreadable));
                    continue;
                }

                if (file.Size == 0) { rejected.Add(new IntakeRejection(name, IntakeRejection.Reason.Empty)); Discard(file); continue; }
                if (file.Size > maxFileSize) { rejected.Add(new IntakeRejection(file.DisplayName, IntakeRejection.Reason.TooLarge, Limit: maxFileSize)); Discard(file); continue; }
                var key = DuplicateKey(path, file.Size);
                if (_recent.ContainsKey(key)) { rejected.Add(new IntakeRejection(file.DisplayName, IntakeRejection.Reason.Duplicate)); Discard(file); continue; }
                _recent[key] = current;
                accepted.Add(file);
            }
            return new IntakeResult(accepted, rejected);
        }
    }

    /// <summary>Regular files inside a folder, recursively, hidden/system items skipped, sorted by path.</summary>
    internal static List<string> FilesInside(string directory)
    {
        var result = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(directory, "*", options))
        {
            // Unix-style hidden files (".DS_Store") too.
            if (System.IO.Path.GetRelativePath(directory, path).Split(System.IO.Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))) continue;
            result.Add(path);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private PreparedFile? Zip(string directory)
    {
        try
        {
            var folder = System.IO.Path.Combine(_staging, Guid.NewGuid().ToString());
            Directory.CreateDirectory(folder);
            var destination = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(directory.TrimEnd('\\', '/')) + ".zip");
            ZipFile.CreateFromDirectory(directory, destination, CompressionLevel.Optimal, includeBaseDirectory: true);
            return new PreparedFile(destination, System.IO.Path.GetFileName(destination), new FileInfo(destination).Length, true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Discard(PreparedFile file)
    {
        if (!file.IsTemporary) return;
        try { Directory.Delete(System.IO.Path.GetDirectoryName(file.Path)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool CanRead(string path)
    {
        try { using var _ = File.OpenRead(path); return true; } catch (Exception) { return false; }
    }

    private static string DuplicateKey(string path, long size) =>
        $"{System.IO.Path.GetFullPath(path)}|{size}|{File.GetLastWriteTimeUtc(path).Ticks}";
}
