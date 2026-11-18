using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TeleSave.Core;

/// <summary>A downloadable file of a release.</summary>
/// <param name="Sha256">Lowercase hex from GitHub's "digest" field ("sha256:…"), when GitHub provides one.</param>
public sealed record ReleaseAsset(string Name, Uri? Url, long Size, string? Sha256);

/// <summary>How this copy of TeleSave was installed, which decides how an update is put in place.</summary>
public enum InstallKind { Unknown, WindowsInstaller, WindowsPortable, AppImage, Tarball, Deb, Rpm }

public static class UpdateInstall
{
    public static string Architecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <param name="packageOwner">"dpkg" or "rpm" when the package manager owns /opt/telesave, else null.</param>
    public static InstallKind Detect(bool windows, string baseDirectory, string? appImage,
        Func<string, bool> fileExists, Func<string, bool> isWritableDirectory, Func<string?> packageOwner)
    {
        var directory = baseDirectory.TrimEnd('/', '\\');
        if (windows)
            return fileExists(Path.Combine(directory, "unins000.exe")) ? InstallKind.WindowsInstaller : InstallKind.WindowsPortable;
        if (!string.IsNullOrEmpty(appImage))
            return Path.GetDirectoryName(appImage) is { } folder && isWritableDirectory(folder) ? InstallKind.AppImage : InstallKind.Unknown;
        if (directory == "/opt/telesave")
            return packageOwner() switch { "dpkg" => InstallKind.Deb, "rpm" => InstallKind.Rpm, _ => InstallKind.Unknown };
        // tar.gz: the folder is swapped for the new one, so its parent must be writable too.
        return Path.GetDirectoryName(directory) is { } parent && isWritableDirectory(directory) && isWritableDirectory(parent)
            ? InstallKind.Tarball : InstallKind.Unknown;
    }

    public static bool CanInstallInPlace(InstallKind kind) => kind is not (InstallKind.Unknown or InstallKind.WindowsPortable);

    /// <summary>The download that replaces an installation of this kind; null if the release has none.</summary>
    public static ReleaseAsset? AssetFor(ReleaseInfo release, InstallKind kind, string arch) => kind switch
    {
        InstallKind.WindowsInstaller => Find(release, $"-windows-{arch}-setup.exe"),
        InstallKind.WindowsPortable => Find(release, $"-windows-{arch}.zip"),
        InstallKind.AppImage => Find(release, $"-linux-{arch}.AppImage"),
        InstallKind.Tarball => Find(release, $"-linux-{arch}.tar.gz"),
        InstallKind.Deb => Find(release, $"-linux-{arch}.deb"),
        InstallKind.Rpm => Find(release, $"-linux-{arch}.rpm"),
        _ => null
    };

    /// <summary>What goes into Downloads when the update cannot be installed in place: the same format, else the installer / AppImage.</summary>
    public static ReleaseAsset? FallbackAsset(ReleaseInfo release, InstallKind kind, bool windows, string arch) =>
        AssetFor(release, kind, arch) ?? AssetFor(release, windows ? InstallKind.WindowsInstaller : InstallKind.AppImage, arch);

    /// <summary>
    /// tar.gz installs: swaps <paramref name="directory"/> for the archive's "telesave" folder; the old one is left as
    /// "&lt;directory&gt;.old" (still in use until the app exits) and removed at the next start. False if the archive is not a TeleSave build.
    /// </summary>
    public static async Task<bool> ReplaceFolderFromTarballAsync(string directory, string archive)
    {
        var extract = directory + ".extract";
        var old = directory + ".old";
        DeleteDirectory(extract);
        DeleteDirectory(old);
        Directory.CreateDirectory(extract);
        try
        {
            await using (var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, extract, overwriteFiles: true).ConfigureAwait(false);
            var fresh = Path.Combine(extract, "telesave");
            if (!File.Exists(Path.Combine(fresh, "telesave"))) return false;
            Directory.Move(directory, old);
            try { Directory.Move(fresh, directory); }
            catch (Exception) { Directory.Move(old, directory); throw; }
            return true;
        }
        finally { DeleteDirectory(extract); }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static ReleaseAsset? Find(ReleaseInfo release, string suffix) =>
        release.Assets.FirstOrDefault(a => a.Url is not null && a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}

public static class UpdateDownloader
{
    /// <summary>Downloads to <paramref name="path"/> (via a .part file) and checks the SHA-256 when GitHub published one.</summary>
    public static async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (asset.Url is null) throw new InvalidOperationException($"{asset.Name} has no download URL");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeleSave");
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)received / total));
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (asset.Sha256 is { } expected && actual != expected) throw new InvalidDataException($"{asset.Name}: SHA-256 mismatch");
            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception) { }
            throw;
        }
    }
}
