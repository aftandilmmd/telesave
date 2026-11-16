namespace TeleSave.Core;

/// <summary>Secrets (API key, database key). Windows: Credential Manager / DPAPI; Linux later: libsecret.</summary>
public interface ISecretStore
{
    byte[]? Read(string account);
    bool Write(string account, byte[] data);
    void Delete(string account);
}

/// <summary>Keeps the computer awake while uploads run.</summary>
public interface IPowerManager
{
    IDisposable KeepAwake(string reason);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _data = new();
    public byte[]? Read(string account) { lock (_data) return _data.TryGetValue(account, out var v) ? v : null; }
    public bool Write(string account, byte[] data) { lock (_data) _data[account] = data; return true; }
    public void Delete(string account) { lock (_data) _data.Remove(account); }
}
