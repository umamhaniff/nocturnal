using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturnal.Core;

namespace Nocturnal.App.Storage;

internal static class DurableFileWriter
{
    public static void WriteAtomic(string targetPath, string content, string? backupPath = null)
    {
        var temp = targetPath + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(content);
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        File.Move(temp, targetPath, overwrite: true);
        if (!string.IsNullOrEmpty(backupPath))
        {
            File.Copy(targetPath, backupPath, overwrite: true);
        }
    }
}

internal static class WindowsDpapiProtector
{
    private const string Prefix = "dpapi:";
    private const uint CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)
            || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plaintext;
        }

        var raw = Encoding.UTF8.GetBytes(plaintext);
        var inHandle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        var outBlob = new DataBlob();
        try
        {
            var inBlob = new DataBlob
            {
                cbData = raw.Length,
                pbData = inHandle.AddrOfPinnedObject()
            };

            if (!CryptProtectData(
                ref inBlob,
                "NocturnalWebhook",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptprotectUiForbidden,
                out outBlob))
            {
                return plaintext;
            }

            var protectedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, protectedBytes, 0, outBlob.cbData);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return plaintext;
        }
        finally
        {
            inHandle.Free();
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)
            || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch
        {
            return string.Empty;
        }

        var inHandle = GCHandle.Alloc(cipher, GCHandleType.Pinned);
        var outBlob = new DataBlob();
        try
        {
            var inBlob = new DataBlob
            {
                cbData = cipher.Length,
                pbData = inHandle.AddrOfPinnedObject()
            };

            if (!CryptUnprotectData(
                ref inBlob,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptprotectUiForbidden,
                out outBlob))
            {
                return string.Empty;
            }

            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            inHandle.Free();
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }
}

public sealed class FileJournalStore : IJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _backupPath;

    public bool WasLastLoadCorrupt { get; private set; }

    public FileJournalStore(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturnal");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "journal.json");
        _backupPath = _path + ".bak";
    }

    public JournalEntry? Load()
    {
        WasLastLoadCorrupt = false;
        var primaryExists = File.Exists(_path);
        var backupExists = File.Exists(_backupPath);

        if (!primaryExists && !backupExists)
        {
            return null;
        }

        if (primaryExists && TryReadEntry(_path) is { } primary)
        {
            return primary;
        }

        if (backupExists && TryReadEntry(_backupPath) is { } backup)
        {
            return backup;
        }

        WasLastLoadCorrupt = true;
        Clear();
        return null;
    }

    public void Save(JournalEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        DurableFileWriter.WriteAtomic(_path, json, _backupPath);
    }

    public void Clear()
    {
        SafeDelete(_path);
        SafeDelete(_backupPath);
    }

    private static JournalEntry? TryReadEntry(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<JournalEntry>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors.
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
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            cfg.Normalize();
            if (!string.IsNullOrWhiteSpace(cfg.AlertWebhookUrl))
            {
                cfg.AlertWebhookUrl = WindowsDpapiProtector.Unprotect(cfg.AlertWebhookUrl);
            }
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        config.Normalize();
        var originalUrl = config.AlertWebhookUrl;
        try
        {
            config.AlertWebhookUrl = string.IsNullOrWhiteSpace(originalUrl)
                ? string.Empty
                : WindowsDpapiProtector.Protect(originalUrl);

            var json = JsonSerializer.Serialize(config, JsonOptions);
            DurableFileWriter.WriteAtomic(_path, json);
        }
        finally
        {
            config.AlertWebhookUrl = originalUrl;
        }
    }
}

public sealed class FileSessionHistoryStore : ISessionHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string _path;

    private static JsonSerializerOptions CreateOptions()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }

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

            var json = JsonSerializer.Serialize(current, JsonOptions);
            DurableFileWriter.WriteAtomic(_path, json);
        }
        catch
        {
            // History persistence failure must never interrupt power restoration.
        }
    }
}
