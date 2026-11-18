using TeleSave.Core;

namespace TeleSave.Core.Tests;

public class ClipboardTextTests
{
    [Fact]
    public void PrepareTrimsAndRejectsEmpty()
    {
        Assert.Null(ClipboardText.Prepare(null));
        Assert.Null(ClipboardText.Prepare(" \n\t "));
        Assert.Equal("hi there", ClipboardText.Prepare("  hi there \n"));
    }

    [Fact]
    public void ShortTextIsOneChunk()
    {
        Assert.Equal(["hello"], ClipboardText.Split("hello"));
        var exact = new string('a', 4096);
        Assert.Equal([exact], ClipboardText.Split(exact));
    }

    [Fact]
    public void HardSplitWithoutSpaces() =>
        Assert.Equal([4096, 1], ClipboardText.Split(new string('a', 4097)).Select(c => c.Length));

    [Fact]
    public void PrefersLineBreakInSecondHalf()
    {
        var first = new string('a', 3000);
        var second = new string('b', 2000);
        Assert.Equal([first, second], ClipboardText.Split(first + "\n" + second));
    }

    [Fact]
    public void IgnoresLineBreakInFirstHalfAndUsesSpace()
    {
        var chunks = ClipboardText.Split("x\n" + new string('a', 3000) + " " + new string('b', 2000));
        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('b', 2000), chunks[1]);
    }

    [Fact]
    public void NeverSplitsAnEmoji()
    {
        // 4095 units, then a 2-unit emoji that would straddle the limit.
        var chunks = ClipboardText.Split(new string('a', 4095) + "😀" + "z");
        Assert.Equal([new string('a', 4095), "😀z"], chunks);
    }

    [Fact]
    public void ChunksAreTrimmedAndCoverAllText()
    {
        var words = string.Join(" ", Enumerable.Repeat("word", 3000));
        var chunks = ClipboardText.Split(words, 100);
        Assert.All(chunks, c => Assert.True(c.Length <= 100 && c[0] != ' ' && c[^1] != ' '));
        Assert.Equal(words, string.Join(" ", chunks));
    }
}

public class UpdateTests
{
    private const string Json = """
        {"tag_name":"v0.5.0","html_url":"https://github.com/a/b/releases/tag/v0.5.0","assets":[
          {"name":"TeleSave-0.5.0-windows-x64-setup.exe","browser_download_url":"https://x.y/setup-x64.exe","size":10,"digest":"sha256:ABCDEF"},
          {"name":"TeleSave-0.5.0-windows-arm64-setup.exe","browser_download_url":"https://x.y/setup-arm64.exe","size":11},
          {"name":"TeleSave-0.5.0-windows-x64.zip","browser_download_url":"https://x.y/x64.zip","size":12},
          {"name":"TeleSave-0.5.0-linux-x64.AppImage","browser_download_url":"https://x.y/x64.AppImage","size":13},
          {"name":"TeleSave-0.5.0-linux-x64.tar.gz","browser_download_url":"https://x.y/x64.tar.gz","size":14},
          {"name":"TeleSave-0.5.0-linux-x64.deb","browser_download_url":"https://x.y/x64.deb","size":15},
          {"name":"TeleSave-0.5.0-linux-arm64.rpm","browser_download_url":"https://x.y/arm64.rpm","size":16},
          {"name":"notes.txt"}
        ]}
        """;

    private static ReleaseInfo Release => ReleaseInfo.Parse(Json, UpdateChecker.IsLinuxAsset)!;

    [Fact]
    public void ParsesAssetUrlSizeAndDigest()
    {
        var setup = Release.Assets[0];
        Assert.Equal(new Uri("https://x.y/setup-x64.exe"), setup.Url);
        Assert.Equal(10, setup.Size);
        Assert.Equal("abcdef", setup.Sha256);
        Assert.Null(Release.Assets[1].Sha256);
        Assert.Null(Release.Assets[^1].Url);
    }

    [Theory]
    [InlineData(InstallKind.WindowsInstaller, "x64", "TeleSave-0.5.0-windows-x64-setup.exe")]
    [InlineData(InstallKind.WindowsInstaller, "arm64", "TeleSave-0.5.0-windows-arm64-setup.exe")]
    [InlineData(InstallKind.WindowsPortable, "x64", "TeleSave-0.5.0-windows-x64.zip")]
    [InlineData(InstallKind.AppImage, "x64", "TeleSave-0.5.0-linux-x64.AppImage")]
    [InlineData(InstallKind.Tarball, "x64", "TeleSave-0.5.0-linux-x64.tar.gz")]
    [InlineData(InstallKind.Deb, "x64", "TeleSave-0.5.0-linux-x64.deb")]
    [InlineData(InstallKind.Rpm, "arm64", "TeleSave-0.5.0-linux-arm64.rpm")]
    public void PicksTheAssetForTheInstall(InstallKind kind, string arch, string expected) =>
        Assert.Equal(expected, UpdateInstall.AssetFor(Release, kind, arch)?.Name);

    [Fact]
    public void FallbackUsesSameFormatElseInstallerOrAppImage()
    {
        Assert.Equal("TeleSave-0.5.0-linux-x64.deb", UpdateInstall.FallbackAsset(Release, InstallKind.Deb, false, "x64")?.Name);
        Assert.Equal("TeleSave-0.5.0-linux-x64.AppImage", UpdateInstall.FallbackAsset(Release, InstallKind.Unknown, false, "x64")?.Name);
        // No arm64 .deb in this release: the AppImage is the next best thing… which arm64 lacks too.
        Assert.Null(UpdateInstall.FallbackAsset(Release, InstallKind.Deb, false, "arm64"));
        Assert.Equal("TeleSave-0.5.0-windows-arm64-setup.exe", UpdateInstall.FallbackAsset(Release, InstallKind.Unknown, true, "arm64")?.Name);
    }

    [Fact]
    public void DetectsWindowsInstallerOrPortable()
    {
        Assert.Equal(InstallKind.WindowsInstaller, UpdateInstall.Detect(true, @"C:\Users\u\AppData\Local\Programs\TeleSave\", null,
            path => path.EndsWith("unins000.exe"), _ => true, () => null));
        Assert.Equal(InstallKind.WindowsPortable, UpdateInstall.Detect(true, @"D:\Tools\TeleSave\", null, _ => false, _ => true, () => null));
        Assert.False(UpdateInstall.CanInstallInPlace(InstallKind.WindowsPortable));
    }

    [Fact]
    public void DetectsLinuxInstallKinds()
    {
        string? NoOwner() => null;
        Assert.Equal(InstallKind.AppImage, UpdateInstall.Detect(false, "/tmp/.mount_x/usr/lib/telesave/", "/home/u/Apps/TeleSave.AppImage",
            File.Exists, dir => dir == "/home/u/Apps", NoOwner));
        Assert.Equal(InstallKind.Unknown, UpdateInstall.Detect(false, "/tmp/.mount_x/usr/lib/telesave/", "/usr/local/bin/TeleSave.AppImage",
            File.Exists, _ => false, NoOwner));
        Assert.Equal(InstallKind.Deb, UpdateInstall.Detect(false, "/opt/telesave/", null, File.Exists, _ => false, () => "dpkg"));
        Assert.Equal(InstallKind.Rpm, UpdateInstall.Detect(false, "/opt/telesave/", null, File.Exists, _ => false, () => "rpm"));
        Assert.Equal(InstallKind.Unknown, UpdateInstall.Detect(false, "/opt/telesave/", null, File.Exists, _ => true, NoOwner));
        Assert.Equal(InstallKind.Tarball, UpdateInstall.Detect(false, "/home/u/telesave/", null, File.Exists, _ => true, NoOwner));
        Assert.Equal(InstallKind.Unknown, UpdateInstall.Detect(false, "/home/u/telesave/", null, File.Exists, dir => dir != "/home/u", NoOwner));
    }

    [Fact]
    public async Task DownloadChecksDigest()
    {
        using var dir = new TempDir();
        var payload = new byte[100_000];
        new Random(1).NextBytes(payload);
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                try
                {
                    var context = await server.GetContextAsync();
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload);
                    context.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
        var url = new Uri($"http://127.0.0.1:{port}/payload.bin");
        var target = Path.Combine(dir.Path, "out", "payload.bin");
        var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var reported = new List<double>();
        await UpdateDownloader.DownloadAsync(new ReleaseAsset("payload.bin", url, payload.Length, good), target, new SyncProgress(reported.Add));
        Assert.Equal(payload, File.ReadAllBytes(target));
        Assert.Equal(1.0, reported[^1]);
        File.Delete(target);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateDownloader.DownloadAsync(new ReleaseAsset("payload.bin", url, payload.Length, new string('0', 64)), target));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task TarballSwapKeepsModesAndLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var install = Path.Combine(dir.Path, "telesave");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "telesave"), "old");

        // Same layout as package.sh: one "telesave" folder, an executable, a library and a symlink to it.
        var source = Path.Combine(dir.Path, "src", "telesave");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "telesave"), "new");
        File.SetUnixFileMode(Path.Combine(source, "telesave"), (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(source, "libicuuc.so.70"), "lib");
        File.CreateSymbolicLink(Path.Combine(source, "libicuuc.so.70.1"), "libicuuc.so.70");
        var archive = Path.Combine(dir.Path, "update.tar.gz");
        if (File.Exists("/usr/bin/tar") || File.Exists("/bin/tar"))
        {
            // The real archive comes from GNU tar (package.sh): tar -C <dir> -czf <out> telesave
            using var tar = System.Diagnostics.Process.Start("tar", ["-C", Path.GetDirectoryName(source)!, "-czf", archive, "telesave"]);
            await tar.WaitForExitAsync();
            Assert.Equal(0, tar.ExitCode);
        }
        else
        {
            await using var output = File.Create(archive);
            await using var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest);
            await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(source, gzip, includeBaseDirectory: true);
        }

        Assert.True(await UpdateInstall.ReplaceFolderFromTarballAsync(install, archive));
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "telesave")));
        Assert.True(File.GetUnixFileMode(Path.Combine(install, "telesave")).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("libicuuc.so.70", new FileInfo(Path.Combine(install, "libicuuc.so.70.1")).LinkTarget);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install + ".old", "telesave")));
        Assert.False(Directory.Exists(install + ".extract"));
    }

    [Fact]
    public async Task TarballWithoutTeleSaveLeavesInstallAlone()
    {
        using var dir = new TempDir();
        var install = Path.Combine(dir.Path, "telesave");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "telesave"), "old");
        var source = Path.Combine(dir.Path, "src", "other");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "x"), "x");
        var archive = Path.Combine(dir.Path, "junk.tar.gz");
        await using (var output = File.Create(archive))
        await using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
            await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(source, gzip, includeBaseDirectory: true);

        Assert.False(await UpdateInstall.ReplaceFolderFromTarballAsync(install, archive));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "telesave")));
        Assert.False(Directory.Exists(install + ".old"));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
