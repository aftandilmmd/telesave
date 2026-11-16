using TeleSave.Core;

namespace TeleSave.Core.Tests;

public class UploadQueueTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task StartsPausedAndSendsAfterUnpause()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        await Task.Delay(50);
        Assert.Equal(0, transport.SendCount);
        queue.SetPaused(false);
        Assert.True(await Wait.Until(() => transport.SendCount == 1));
        Assert.Equal(SendKind.Document, transport.Batches[0][0].Kind);
    }

    [Fact]
    public async Task SuccessFlowMarksDone()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport);
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        Assert.True(await Wait.Until(() => queue.Items[0].MessageId is not null));
        var messageId = queue.Items[0].MessageId!.Value;
        queue.Handle(new TelegramEvent.FileProgress((int)messageId, 5, 10));
        Assert.Equal(0.5, queue.Items[0].Progress, 3);
        queue.Handle(new TelegramEvent.SendSucceeded(messageId, 77));
        Assert.Equal(UploadStatus.Done, queue.Items[0].Status);
        Assert.Equal(77, queue.Items[0].MessageId);
    }

    [Fact]
    public async Task ConcurrencyLimit()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport, settings: new UploadQueue.Settings(MaxConcurrent: 2));
        queue.SetPaused(false);
        queue.Enqueue(Enumerable.Range(0, 5).Select(i => _dir.Prepared($"f{i}.txt")).ToList());
        await Wait.Until(() => transport.SendCount == 2);
        await Task.Delay(50);
        Assert.Equal(2, transport.SendCount);
        await Wait.Until(() => queue.Items[0].MessageId is not null);
        queue.Handle(new TelegramEvent.SendSucceeded(queue.Items[0].MessageId!.Value, 1));
        Assert.True(await Wait.Until(() => transport.SendCount == 3));
    }

    [Fact]
    public async Task TransientFailureRetriesThenFails()
    {
        var transport = new FakeTransport();
        for (var i = 0; i < 3; i++) transport.ScriptedErrors.Enqueue(new TelegramError(500, "Internal"));
        using var queue = new UploadQueue(transport, retry: new RetryPolicy(MaxAttempts: 3, BaseDelaySeconds: 0.01, MaxDelaySeconds: 0.05));
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        Assert.True(await Wait.Until(() => queue.Items[0].Status == UploadStatus.Failed));
        Assert.Equal(3, transport.SendCount);
        Assert.Equal(3, queue.Items[0].Attempts);
    }

    [Fact]
    public async Task PermanentFailureDoesNotRetry()
    {
        var transport = new FakeTransport();
        transport.ScriptedErrors.Enqueue(new TelegramError(400, "FILE_PARTS_INVALID"));
        using var queue = new UploadQueue(transport, retry: new RetryPolicy(BaseDelaySeconds: 0.01));
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        Assert.True(await Wait.Until(() => queue.Items[0].FailureReason == "FILE_PARTS_INVALID"));
        await Task.Delay(50);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task FloodWaitDelaysRetry()
    {
        var transport = new FakeTransport();
        transport.ScriptedErrors.Enqueue(new TelegramError(429, "Too Many Requests: retry after 3"));
        using var queue = new UploadQueue(transport, retry: new RetryPolicy(FloodScale: 0.05)); // 0.15 s
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        await Wait.Until(() => transport.SendCount == 1);
        await Task.Delay(60);
        Assert.Equal(1, transport.SendCount);
        Assert.NotNull(queue.Items[0].NotBefore);
        Assert.True(await Wait.Until(() => transport.SendCount == 2));
    }

    [Fact]
    public async Task CancelQueuedAndUploading()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport, settings: new UploadQueue.Settings(MaxConcurrent: 1));
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt"), _dir.Prepared("b.txt")]);
        await Wait.Until(() => queue.Items[0].MessageId is not null);
        var first = queue.Items[0];
        var second = queue.Items[1];
        Assert.Equal(UploadStatus.Queued, second.Status);
        queue.Cancel(second.Id);
        queue.Cancel(first.Id);
        Assert.All(queue.Items, i => Assert.Equal(UploadStatus.Cancelled, i.Status));
        Assert.True(await Wait.Until(() => transport.Cancelled.Count == 1));
        Assert.Equal(first.MessageId, transport.Cancelled[0]);
        queue.Handle(new TelegramEvent.SendSucceeded(first.MessageId!.Value, 5));
        Assert.All(queue.Items, i => Assert.Equal(UploadStatus.Cancelled, i.Status));
    }

    [Fact]
    public async Task CancelWhileSendInFlightCancelsReturnedMessage()
    {
        var transport = new FakeTransport { SendDelayMs = 100 };
        using var queue = new UploadQueue(transport);
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        // Cancel while SendAsync is in flight (no message id yet): the returned message must be cancelled.
        Assert.True(await Wait.Until(() => transport.SendStarted));
        queue.Cancel(queue.Items[0].Id);
        Assert.True(await Wait.Until(() => transport.Cancelled.Count == 1));
    }

    [Fact]
    public async Task EarlySuccessEventIsApplied()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport);
        queue.Handle(new TelegramEvent.SendSucceeded(1001, 9));
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        Assert.True(await Wait.Until(() => queue.Items[0].Status == UploadStatus.Done && queue.Items[0].MessageId == 9));
    }

    [Fact]
    public async Task MissingFileFailsWithoutSending()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport);
        queue.SetPaused(false);
        queue.Enqueue([new PreparedFile(Path.Combine(_dir.Path, "ghost.txt"), "ghost.txt", 5, false)]);
        Assert.True(await Wait.Until(() => queue.Items[0].Status == UploadStatus.Failed));
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task AlbumGroupingSendsTenThenRest()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport, settings: new UploadQueue.Settings(MaxConcurrent: 1, GroupAsAlbum: true));
        queue.SetPaused(false);
        queue.Enqueue(Enumerable.Range(0, 12).Select(i => _dir.Prepared($"d{i}.pdf")).ToList());
        await Wait.Until(() => transport.SendCount == 1);
        Assert.Equal(10, transport.Batches[0].Count);
        await Wait.Until(() => queue.Items.Take(10).All(i => i.MessageId is not null));
        foreach (var item in queue.Items.Take(10)) queue.Handle(new TelegramEvent.SendSucceeded(item.MessageId!.Value, 1));
        Assert.True(await Wait.Until(() => transport.SendCount == 2));
        Assert.Equal(2, transport.Batches[1].Count);
    }

    [Fact]
    public async Task PersistenceRestoresUnfinishedAsQueued()
    {
        var path = Path.Combine(_dir.Path, "queue.json");
        using (var queue = new UploadQueue(new FakeTransport(), new QueueStore(path)))
        {
            queue.SetPaused(false);
            queue.Enqueue([_dir.Prepared("a.txt")]);
            await Wait.Until(() => queue.Items[0].MessageId is not null);
        }
        var restored = new QueueStore(path).Load();
        Assert.Single(restored);
        Assert.Equal(UploadStatus.Queued, restored[0].Status);
        Assert.Null(restored[0].MessageId);
    }

    [Fact]
    public async Task DestinationPassedAndAlbumsNeverMix()
    {
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport, settings: new UploadQueue.Settings(MaxConcurrent: 1, GroupAsAlbum: true));
        var channel = new UploadDestination(-100999, 7, "Kanal");
        queue.Enqueue([_dir.Prepared("a0.pdf"), _dir.Prepared("a1.pdf")]);
        queue.Enqueue([_dir.Prepared("b0.pdf"), _dir.Prepared("b1.pdf")], channel);
        Assert.NotEqual(queue.Items[0].GroupId, queue.Items[2].GroupId);
        queue.SetPaused(false);
        await Wait.Until(() => transport.SendCount == 1);
        Assert.Equal(2, transport.Batches[0].Count);
        Assert.Null(transport.Destinations[0]);
    }

    [Fact]
    public async Task ReplaceTransportRequeuesAndIgnoresOldResults()
    {
        var old = new FakeTransport();
        using var queue = new UploadQueue(old);
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        await Wait.Until(() => queue.Items[0].MessageId is not null);
        var oldMessage = queue.Items[0].MessageId!.Value;
        var fresh = new FakeTransport(5001);
        queue.ReplaceTransport(fresh);
        Assert.True(await Wait.Until(() => fresh.SendCount == 1 && queue.Items[0].MessageId is not null));
        queue.Handle(new TelegramEvent.SendSucceeded(oldMessage, 1));
        Assert.Equal(UploadStatus.Uploading, queue.Items[0].Status);
        queue.Handle(new TelegramEvent.SendSucceeded(queue.Items[0].MessageId!.Value, 2));
        Assert.Equal(UploadStatus.Done, queue.Items[0].Status);
    }

    [Fact]
    public async Task DueRetryWaitsForFreeSlotWithoutSpinning()
    {
        var transport = new FakeTransport();
        transport.ScriptedErrors.Enqueue(null);
        transport.ScriptedErrors.Enqueue(new TelegramError(500, "x"));
        using var queue = new UploadQueue(transport, settings: new UploadQueue.Settings(MaxConcurrent: 1), retry: new RetryPolicy(BaseDelaySeconds: 0));
        queue.SetPaused(false);
        queue.Enqueue([_dir.Prepared("a.txt")]);
        await Wait.Until(() => queue.Items[0].MessageId is not null);
        queue.SetPaused(true);
        queue.Enqueue([_dir.Prepared("b.txt")]);
        queue.SetPaused(false);
        await Task.Delay(100);
        Assert.Equal(1, transport.SendCount);
        queue.Handle(new TelegramEvent.SendSucceeded(queue.Items[0].MessageId!.Value, 1));
        Assert.True(await Wait.Until(() => transport.SendCount >= 2));
    }

    [Fact]
    public async Task TemporaryFileDeletedAfterSuccess()
    {
        var staging = Path.Combine(_dir.Path, "staging", "x");
        Directory.CreateDirectory(staging);
        var zip = Path.Combine(staging, "folder.zip");
        File.WriteAllBytes(zip, new byte[10]);
        var transport = new FakeTransport();
        using var queue = new UploadQueue(transport);
        queue.SetPaused(false);
        queue.Enqueue([new PreparedFile(zip, "folder.zip", 10, true)]);
        await Wait.Until(() => queue.Items[0].MessageId is not null);
        queue.Handle(new TelegramEvent.SendSucceeded(queue.Items[0].MessageId!.Value, 3));
        Assert.False(Directory.Exists(staging));
    }
}
