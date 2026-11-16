using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeleSave.Core;

/// <summary>Persists the upload queue as JSON so unfinished uploads resume after relaunch.</summary>
public sealed class QueueStore(string path)
{
    private readonly object _gate = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Items that were mid-upload come back as queued: their TDLib message ids are gone.</summary>
    public List<UploadItem> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return [];
                var items = JsonSerializer.Deserialize<List<UploadItem>>(File.ReadAllText(path), Json) ?? [];
                return items.Select(i => i.Status == UploadStatus.Uploading
                    ? i with { Status = UploadStatus.Queued, MessageId = null, Progress = 0, NotBefore = null }
                    : i with { NotBefore = null }).ToList();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<UploadItem> items)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(items, Json));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // Losing one save is not worth crashing over; the next change saves again.
            }
        }
    }
}
