using TeleSave.Core;

namespace TeleSave.Core.Tests;

/// <summary>Scriptable transport: records sends, hands out message ids, optionally throws.</summary>
public sealed class FakeTransport(long firstMessageId = 1001) : IUploadTransport
{
    private readonly object _gate = new();
    private long _next = firstMessageId - 1;
    public List<List<OutgoingFile>> Batches { get; } = [];
    public List<UploadDestination?> Destinations { get; } = [];
    public List<long> Cancelled { get; } = [];
    public Queue<Exception?> ScriptedErrors { get; } = new();
    public int SendDelayMs { get; set; }
    public volatile bool SendStarted;

    public async Task<IReadOnlyList<SentMessage>> SendAsync(IReadOnlyList<OutgoingFile> files, UploadDestination? destination)
    {
        SendStarted = true;
        if (SendDelayMs > 0) await Task.Delay(SendDelayMs);
        Exception? error;
        lock (_gate)
        {
            Batches.Add(files.ToList());
            Destinations.Add(destination);
            error = ScriptedErrors.Count > 0 ? ScriptedErrors.Dequeue() : null;
        }
        if (error is not null) throw error;
        lock (_gate) return files.Select(_ => { _next++; return new SentMessage(_next, (int)_next); }).ToList();
    }

    public Task CancelAsync(IReadOnlyList<long> messageIds, UploadDestination? destination)
    {
        lock (_gate) Cancelled.AddRange(messageIds);
        return Task.CompletedTask;
    }

    public int SendCount { get { lock (_gate) return Batches.Count; } }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TeleSaveTests-" + Guid.NewGuid());
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name, int bytes)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Repeat((byte)0x41, bytes).ToArray());
        return path;
    }
    public PreparedFile Prepared(string name, int bytes = 10)
    {
        var path = File(name, bytes);
        return new PreparedFile(path, name, bytes, false);
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public static class Wait
{
    public static async Task<bool> Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}
