using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using TeleSave.Core;

namespace TeleSave.TdLib;

/// <summary>TDLib JSON interface (tdjson.dll).</summary>
internal static partial class Native
{
    private const string Library = "tdjson";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int td_create_client_id();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void td_send(int clientId, [MarshalAs(UnmanagedType.LPUTF8Str)] string request);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr td_receive(double timeout);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr td_execute([MarshalAs(UnmanagedType.LPUTF8Str)] string request);
}

/// <summary>td_receive is process-global: one thread pulls every response/update and routes it by @client_id.</summary>
internal sealed class TdReceiver
{
    public static readonly TdReceiver Shared = new();
    private readonly ConcurrentDictionary<int, Action<JsonObject>> _handlers = new();
    private Thread? _thread;
    private readonly object _gate = new();

    public void Register(int clientId, Action<JsonObject> handler)
    {
        _handlers[clientId] = handler;
        lock (_gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TeleSave.TdReceiver" };
            _thread.Start();
        }
    }

    public void Unregister(int clientId) => _handlers.TryRemove(clientId, out _);

    private void Loop()
    {
        while (true)
        {
            var raw = Native.td_receive(1.0);
            if (raw == IntPtr.Zero) continue;
            var json = Marshal.PtrToStringUTF8(raw);
            if (json is null) continue;
            JsonObject? obj;
            try { obj = JsonNode.Parse(json) as JsonObject; } catch (Exception) { continue; }
            var clientId = obj?["@client_id"]?.GetValue<int>();
            if (obj is null || clientId is null || !_handlers.TryGetValue(clientId.Value, out var handler)) continue;
            try { handler(obj); } catch (Exception) { /* a bad update must not kill the receiver */ }
        }
    }
}

/// <summary>One TDLib instance. Requests get an @extra tag so responses complete the awaiting caller.</summary>
internal sealed class TdJsonClient : IDisposable
{
    public int ClientId { get; }
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Action<JsonObject> _onUpdate;
    private long _nextExtra;

    public TdJsonClient(Action<JsonObject> onUpdate)
    {
        _onUpdate = onUpdate;
        ClientId = Native.td_create_client_id();
        TdReceiver.Shared.Register(ClientId, Dispatch);
    }

    public void Send(JsonObject request) => Native.td_send(ClientId, request.ToJsonString());

    public Task<JsonObject> RequestAsync(JsonObject request)
    {
        var extra = "ts" + Interlocked.Increment(ref _nextExtra);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[extra] = tcs;
        request["@extra"] = extra;
        Native.td_send(ClientId, request.ToJsonString());
        return tcs.Task;
    }

    public void FailPending(Exception error)
    {
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(error);
    }

    public static void Execute(JsonObject request) => Native.td_execute(request.ToJsonString());

    private void Dispatch(JsonObject obj)
    {
        if (obj["@extra"]?.GetValue<string>() is { } extra && _pending.TryRemove(extra, out var tcs))
        {
            if (obj["@type"]?.GetValue<string>() == "error")
                tcs.TrySetException(new TelegramError(obj["code"]?.GetValue<int>() ?? 0, obj["message"]?.GetValue<string>() ?? "Unknown error"));
            else
                tcs.TrySetResult(obj);
            return;
        }
        _onUpdate(obj);
    }

    public void Dispose() => TdReceiver.Shared.Unregister(ClientId);
}
