namespace TeleSave.Core;

/// <summary>
/// Owns upload state: schedules sends with a concurrency limit, maps TDLib events back to items, retries
/// with backoff, honours FLOOD_WAIT, and persists itself. State changes happen under one lock; sends,
/// cancels and change notifications run outside it.
/// </summary>
public sealed class UploadQueue : IDisposable
{
    public sealed record Settings(int MaxConcurrent = 2, bool SendMediaCompressed = false, bool GroupAsAlbum = false, string Caption = "");

    public delegate Task<MediaDimensions?> DimensionsProbe(string path, SendKind kind);

    /// <summary>Raised (on any thread) with a snapshot after every change.</summary>
    public event Action<IReadOnlyList<UploadItem>>? Changed;

    private readonly object _gate = new();
    private readonly QueueStore? _store;
    private readonly RetryPolicy _retry;
    private readonly DimensionsProbe? _probe;
    private readonly Func<DateTimeOffset> _now;
    private readonly IPowerManager? _power;
    private List<UploadItem> _items;
    private IUploadTransport _transport;
    private Settings _settings;
    private bool _paused = true;
    /// <summary>Bumped when the transport is replaced; results from older sends are ignored.</summary>
    private int _generation;

    private readonly Dictionary<Guid, HashSet<Guid>> _activeJobs = new();
    private readonly Dictionary<Guid, Guid> _itemJob = new();
    private readonly Dictionary<long, Guid> _messageToItem = new();
    private readonly Dictionary<int, Guid> _fileToItem = new();
    /// <summary>Send results that arrived before SendAsync returned the temporary message id.</summary>
    private readonly Dictionary<long, TelegramEvent> _early = new();
    private CancellationTokenSource? _wake;
    private IDisposable? _awake;

    internal const int KeepFinishedCount = 50;
    internal static readonly TimeSpan KeepFinishedFor = TimeSpan.FromHours(24);

    public UploadQueue(IUploadTransport transport, QueueStore? store = null, Settings? settings = null, RetryPolicy? retry = null,
        DimensionsProbe? probe = null, Func<DateTimeOffset>? now = null, IPowerManager? power = null)
    {
        _transport = transport;
        _store = store;
        _settings = settings ?? new Settings();
        _retry = retry ?? new RetryPolicy();
        _probe = probe;
        _now = now ?? (() => DateTimeOffset.Now);
        _power = power;
        _items = store?.Load() ?? [];
    }

    public IReadOnlyList<UploadItem> Items { get { lock (_gate) return _items.ToList(); } }

    public bool IsPaused { get { lock (_gate) return _paused; } }

    // MARK: Effects

    private sealed record Job(Guid[] Ids, int Generation);
    private sealed record CancelRequest(IUploadTransport Transport, long[] Ids, UploadDestination? Destination);

    private sealed class Effects
    {
        public bool Notify = true;
        public bool Persist;
        public bool Pump;
        public readonly List<Job> Jobs = [];
        public readonly List<CancelRequest> Cancels = [];
    }

    private void Mutate(Action<Effects> body)
    {
        var fx = new Effects();
        IReadOnlyList<UploadItem> snapshot;
        lock (_gate)
        {
            body(fx);
            if (fx.Pump) PumpLocked(fx);
            if (fx.Persist) _store?.Save(_items);
            UpdateAwakeLocked();
            ScheduleWakeLocked();
            snapshot = _items.ToList();
        }
        foreach (var job in fx.Jobs) _ = Task.Run(() => RunAsync(job));
        foreach (var cancel in fx.Cancels) _ = SafeCancel(cancel);
        if (fx.Notify) Changed?.Invoke(snapshot);
    }

    private static async Task SafeCancel(CancelRequest request)
    {
        try { await request.Transport.CancelAsync(request.Ids, request.Destination).ConfigureAwait(false); }
        catch (Exception) { /* best effort */ }
    }

    // MARK: Public API

    public void UpdateSettings(Settings settings) => Mutate(fx => { _settings = settings; fx.Pump = true; fx.Notify = false; });

    /// <summary>The queue starts paused; unpause once logged in and online.</summary>
    public void SetPaused(bool paused) => Mutate(fx => { _paused = paused; fx.Pump = !paused; fx.Notify = false; });

    /// <summary>Adds files bound for <paramref name="destination"/> (null = Saved Messages). Albums never mix destinations.</summary>
    public void Enqueue(IReadOnlyList<PreparedFile> files, UploadDestination? destination = null)
    {
        if (files.Count == 0) return;
        if (destination?.IsSavedMessages == true) destination = null;
        Mutate(fx =>
        {
            var assigned = _settings.GroupAsAlbum
                ? AlbumGrouper.AssignGroups(files, _settings.SendMediaCompressed)
                : files.Select(f => (f, (Guid?)null)).ToList();
            var date = _now();
            _items.AddRange(assigned.Select(a => UploadItem.From(a.Item1, a.Item2, destination, date)));
            PruneLocked();
            fx.Persist = true;
            fx.Pump = true;
        });
    }

    public void Cancel(Guid id) => Mutate(fx =>
    {
        var index = IndexOf(id);
        if (index < 0) return;
        var item = _items[index];
        switch (item.Status)
        {
            case UploadStatus.Queued:
                _items[index] = item with { Status = UploadStatus.Cancelled, FinishedAt = _now() };
                CleanUpTemporary(_items[index]);
                fx.Persist = true;
                break;
            case UploadStatus.Uploading:
                if (item.MessageId is { } messageId)
                {
                    _messageToItem.Remove(messageId);
                    fx.Cancels.Add(new CancelRequest(_transport, [messageId], item.Destination));
                }
                // Without a message id the send is still in flight; RunAsync cancels it when it returns.
                _items[index] = item with { Status = UploadStatus.Cancelled, FinishedAt = _now() };
                CompleteLocked(fx, id);
                break;
        }
    });

    public void Retry(Guid id) => Mutate(fx =>
    {
        var index = IndexOf(id);
        if (index < 0 || _items[index].Status != UploadStatus.Failed) return;
        _items[index] = _items[index] with
        {
            Status = UploadStatus.Queued, Attempts = 0, GroupId = null, NotBefore = null, FinishedAt = null, FailureReason = null
        };
        fx.Persist = true;
        fx.Pump = true;
    });

    public void Remove(Guid id) => Mutate(fx =>
    {
        var index = IndexOf(id);
        if (index < 0 || !_items[index].IsTerminal) return;
        CleanUpTemporary(_items[index], force: true);
        _items.RemoveAt(index);
        fx.Persist = true;
    });

    /// <summary>Removes done and cancelled items.</summary>
    public void ClearFinished() => Mutate(fx =>
    {
        foreach (var item in _items.Where(i => i.Status is UploadStatus.Done or UploadStatus.Cancelled)) CleanUpTemporary(item, force: true);
        _items.RemoveAll(i => i.Status is UploadStatus.Done or UploadStatus.Cancelled);
        fx.Persist = true;
    });

    public void Handle(TelegramEvent telegramEvent) => Mutate(fx => HandleLocked(fx, telegramEvent));

    /// <summary>
    /// Swaps the Telegram client (e.g. after an API key change). In-flight uploads go back to the queue
    /// and restart on the new client; anything the old client reports later is ignored.
    /// </summary>
    public void ReplaceTransport(IUploadTransport transport) => Mutate(fx =>
    {
        _generation++;
        _transport = transport;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Status == UploadStatus.Uploading)
                _items[i] = _items[i] with { Status = UploadStatus.Queued, MessageId = null, Progress = 0 };
        }
        _activeJobs.Clear();
        _itemJob.Clear();
        _messageToItem.Clear();
        _fileToItem.Clear();
        _early.Clear();
        fx.Persist = true;
        fx.Pump = true;
    });

    // MARK: Events

    private void HandleLocked(Effects fx, TelegramEvent telegramEvent)
    {
        switch (telegramEvent)
        {
            case TelegramEvent.FileProgress progress:
            {
                if (!_fileToItem.TryGetValue(progress.FileId, out var id)) { fx.Notify = false; return; }
                var index = IndexOf(id);
                if (index < 0 || _items[index].Status != UploadStatus.Uploading) { fx.Notify = false; return; }
                var fraction = progress.Total > 0 ? Math.Min(0.999, (double)progress.Uploaded / progress.Total) : 0;
                _items[index] = _items[index] with { Progress = fraction };
                break;
            }
            case TelegramEvent.SendSucceeded success:
            {
                if (!_messageToItem.Remove(success.OldMessageId, out var id)) { RememberEarly(success.OldMessageId, telegramEvent); fx.Notify = false; return; }
                var index = IndexOf(id);
                if (index < 0 || _items[index].Status != UploadStatus.Uploading) return;
                _items[index] = _items[index] with { Status = UploadStatus.Done, MessageId = success.NewMessageId, Progress = 1, FinishedAt = _now() };
                CompleteLocked(fx, id);
                break;
            }
            case TelegramEvent.SendFailed failure:
            {
                if (!_messageToItem.Remove(failure.OldMessageId, out var id)) { RememberEarly(failure.OldMessageId, telegramEvent); fx.Notify = false; return; }
                FailLocked(fx, id, failure.Error);
                break;
            }
            default:
                fx.Notify = false;
                break;
        }
    }

    private void RememberEarly(long messageId, TelegramEvent telegramEvent)
    {
        if (_early.Count > 200) _early.Clear();
        _early[messageId] = telegramEvent;
    }

    // MARK: Scheduling

    private void PumpLocked(Effects fx)
    {
        if (_paused) return;
        var current = _now();
        bool Ready(UploadItem i) => i.Status == UploadStatus.Queued && (i.NotBefore ?? DateTimeOffset.MinValue) <= current;
        while (_activeJobs.Count < Math.Max(1, _settings.MaxConcurrent))
        {
            var first = _items.FindIndex(Ready);
            if (first < 0) break;
            var batch = new List<int> { first };
            if (_items[first].GroupId is { } group)
                batch = Enumerable.Range(0, _items.Count).Where(i => _items[i].GroupId == group && Ready(_items[i])).ToList();
            var jobId = Guid.NewGuid();
            foreach (var i in batch)
            {
                _items[i] = _items[i] with { Status = UploadStatus.Uploading, Progress = 0, Attempts = _items[i].Attempts + 1, NotBefore = null, MessageId = null };
                _itemJob[_items[i].Id] = jobId;
            }
            var ids = batch.Select(i => _items[i].Id).ToArray();
            _activeJobs[jobId] = ids.ToHashSet();
            fx.Jobs.Add(new Job(ids, _generation));
            fx.Persist = true;
        }
    }

    private async Task RunAsync(Job job)
    {
        IUploadTransport transport;
        Settings settings;
        var pending = new List<(Guid Id, UploadItem Item)>();
        UploadDestination? destination = null;
        lock (_gate)
        {
            if (job.Generation != _generation) return;
            transport = _transport;
            settings = _settings;
            foreach (var id in job.Ids)
            {
                var index = IndexOf(id);
                if (index >= 0) pending.Add((id, _items[index]));
            }
            // Items of one job share a destination: albums come from a single Enqueue call.
            destination = pending.FirstOrDefault().Item?.Destination;
        }

        var prepared = new List<(Guid Id, OutgoingFile File)>();
        foreach (var (id, item) in pending)
        {
            if (!File.Exists(item.Path))
            {
                Mutate(fx => { if (job.Generation == _generation) FailLocked(fx, id, new TelegramError(404, L10n.T("Dosya bulunamadı: %@", item.DisplayName)), retryable: false); });
                continue;
            }
            var kind = MediaClassifier.Kind(item.Path, item.Size, settings.SendMediaCompressed);
            var dimensions = kind != SendKind.Document && _probe is not null ? await _probe(item.Path, kind).ConfigureAwait(false) : null;
            var caption = new CaptionTemplate(settings.Caption).Render(item.DisplayName, item.Size, item.CreatedAt);
            prepared.Add((id, new OutgoingFile(item.Path, kind, caption, dimensions)));
        }

        lock (_gate)
        {
            if (job.Generation != _generation) return;
            // Items may have been cancelled meanwhile.
            prepared = prepared.Where(p => IsUploading(p.Id)).ToList();
        }
        if (prepared.Count == 0) return;

        IReadOnlyList<SentMessage> sent;
        try
        {
            sent = await transport.SendAsync(prepared.Select(p => p.File).ToList(), destination).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Mutate(fx =>
            {
                if (job.Generation != _generation) return;
                foreach (var p in prepared.Where(p => IsUploading(p.Id))) FailLocked(fx, p.Id, error);
            });
            return;
        }

        Mutate(fx =>
        {
            if (job.Generation != _generation) return;
            var toCancel = new List<long>();
            foreach (var ((id, _), message) in prepared.Zip(sent))
            {
                var index = IndexOf(id);
                if (index < 0 || _items[index].Status != UploadStatus.Uploading) { toCancel.Add(message.MessageId); continue; }
                _items[index] = _items[index] with { MessageId = message.MessageId };
                _messageToItem[message.MessageId] = id;
                if (message.FileId is { } fileId) _fileToItem[fileId] = id;
                if (_early.Remove(message.MessageId, out var early)) HandleLocked(fx, early);
            }
            if (toCancel.Count > 0) fx.Cancels.Add(new CancelRequest(transport, toCancel.ToArray(), destination));
            fx.Persist = true;
        });
    }

    private void FailLocked(Effects fx, Guid id, Exception error, bool retryable = true)
    {
        var index = IndexOf(id);
        if (index < 0 || _items[index].Status != UploadStatus.Uploading) return;
        var item = _items[index];
        if (item.MessageId is { } messageId) _messageToItem.Remove(messageId);
        var delay = retryable ? _retry.DelayAfter(error, item.Attempts) : null;
        _items[index] = delay is { } d
            ? item with { Status = UploadStatus.Queued, MessageId = null, Progress = 0, NotBefore = _now() + d, GroupId = null }
            : item with { Status = UploadStatus.Failed, MessageId = null, FailureReason = error.Message, FinishedAt = _now() };
        CompleteLocked(fx, id);
    }

    /// <summary>Releases an item from its job and, when the job is empty, lets the next one start.</summary>
    private void CompleteLocked(Effects fx, Guid id)
    {
        foreach (var key in _fileToItem.Where(p => p.Value == id).Select(p => p.Key).ToList()) _fileToItem.Remove(key);
        var index = IndexOf(id);
        if (index >= 0) CleanUpTemporary(_items[index]);
        if (_itemJob.Remove(id, out var job) && _activeJobs.TryGetValue(job, out var members))
        {
            members.Remove(id);
            if (members.Count == 0) _activeJobs.Remove(job);
        }
        PruneLocked();
        fx.Persist = true;
        fx.Pump = true;
    }

    private void ScheduleWakeLocked()
    {
        _wake?.Cancel();
        _wake = null;
        // With every slot busy, a finishing job pumps; waking now would only spin.
        if (_paused || _activeJobs.Count >= Math.Max(1, _settings.MaxConcurrent)) return;
        var next = _items.Where(i => i.Status == UploadStatus.Queued && i.NotBefore is not null).Select(i => i.NotBefore!.Value).DefaultIfEmpty().Min();
        if (next == default) return;
        var delay = next - _now();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var cts = new CancellationTokenSource();
        _wake = cts;
        _ = Task.Delay(delay + TimeSpan.FromMilliseconds(1), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Mutate(fx => { fx.Pump = true; fx.Notify = false; });
        }, TaskScheduler.Default);
    }

    private void UpdateAwakeLocked()
    {
        if (_activeJobs.Count > 0 && _awake is null) _awake = _power?.KeepAwake("Uploading files to Telegram");
        else if (_activeJobs.Count == 0 && _awake is not null) { _awake.Dispose(); _awake = null; }
    }

    private void PruneLocked()
    {
        var cutoff = _now() - KeepFinishedFor;
        var finished = _items.Where(i => i.Status is UploadStatus.Done or UploadStatus.Cancelled).ToList();
        var keep = finished.OrderByDescending(i => i.FinishedAt ?? DateTimeOffset.MinValue)
            .Take(KeepFinishedCount)
            .Where(i => (i.FinishedAt ?? DateTimeOffset.MaxValue) >= cutoff)
            .Select(i => i.Id).ToHashSet();
        var finishedIds = finished.Select(i => i.Id).ToHashSet();
        _items.RemoveAll(i => finishedIds.Contains(i.Id) && !keep.Contains(i.Id));
    }

    private static void CleanUpTemporary(UploadItem item, bool force = false)
    {
        if (!item.IsTemporary) return;
        if (!(force || item.Status is UploadStatus.Done or UploadStatus.Cancelled)) return;
        // Temporary files live in their own staging subfolder.
        try { Directory.Delete(Path.GetDirectoryName(item.Path)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private int IndexOf(Guid id) => _items.FindIndex(i => i.Id == id);

    private bool IsUploading(Guid id)
    {
        var index = IndexOf(id);
        return index >= 0 && _items[index].Status == UploadStatus.Uploading;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _wake?.Cancel();
            _awake?.Dispose();
            _awake = null;
        }
    }
}
