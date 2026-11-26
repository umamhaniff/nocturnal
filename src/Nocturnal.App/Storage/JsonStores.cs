using System.IO;
using System.Text.Json;
using Nocturnal.Core;

namespace Nocturnal.App.Storage;

public sealed class FileJournalStore : IJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public FileJournalStore(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturnal");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "journal.json");
    }

    public JournalEntry? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<JournalEntry>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(JournalEntry entry)
    {
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

public sealed class FileConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public FileConfigStore(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturnal");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }
}

public sealed class FileSessionHistoryStore : ISessionHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public FileSessionHistoryStore(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturnal");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "history.json");
    }

    public IReadOnlyList<SessionHistoryRecord> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<SessionHistoryRecord>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Append(SessionHistoryRecord record, int maxEntries = 10)
    {
        try
        {
            var current = Load().ToList();
            current.Insert(0, record);
            if (current.Count > maxEntries)
            {
                current = current.Take(maxEntries).ToList();
            }

            var temp = _path + ".tmp";
            var json = JsonSerializer.Serialize(current, JsonOptions);
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // History persistence failure must never interrupt power restoration.
        }
    }
}
