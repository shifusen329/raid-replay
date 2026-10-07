using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Indexing;

/// <summary>Cached index of one log file.</summary>
public sealed class FileIndex
{
    public const int CurrentFormat = 4; // 4: pulls detected in logs without 260 combat flags

    public int Format { get; set; } = CurrentFormat;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public long MtimeTicks { get; set; }
    public long ScannedBytes { get; set; }
    public string PrefixHash { get; set; } = string.Empty;

    /// <summary>Hash of the encounter packs relevant to <see cref="Zones"/> when this index was built.</summary>
    public string EncounterHash { get; set; } = string.Empty;

    public List<uint> Zones { get; set; } = [];
    public List<PullSummary> Pulls { get; set; } = [];
    public ResumeState? Resume { get; set; }
    public long Lines { get; set; }
    public double IndexSeconds { get; set; }

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FileIndex))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;

public static class IndexStore
{
    public static string CachePathFor(string cacheDir, string logPath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(logPath).ToLowerInvariant())));
        return System.IO.Path.Combine(cacheDir, hash[..16] + ".rrix.gz");
    }

    public static FileIndex? Load(string cacheDir, string logPath)
    {
        var file = CachePathFor(cacheDir, logPath);
        if (!File.Exists(file))
            return null;
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var index = JsonSerializer.Deserialize(gz, IndexJsonContext.Default.FileIndex);
            return index is { Format: FileIndex.CurrentFormat } ? index : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string cacheDir, FileIndex index)
    {
        Directory.CreateDirectory(cacheDir);
        var file = CachePathFor(cacheDir, index.Path);
        var tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, index, IndexJsonContext.Default.FileIndex);
        File.Move(tmp, file, true);
    }

    public static string PrefixHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[Math.Min(65536, fs.Length)];
        fs.ReadExactly(buf);
        return Convert.ToHexString(SHA256.HashData(buf))[..32];
    }

    /// <summary>
    /// Indexes <paramref name="path"/>, reusing or extending <paramref name="previous"/> when it is still valid.
    /// </summary>
    public static FileIndex IndexFile(
        string path, FileIndex? previous, IPullObserverFactory? observers, Func<IEnumerable<uint>, string>? encounterHash,
        CancellationToken ct = default, Action<long, long>? progress = null)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var mtime = info.LastWriteTimeUtc.Ticks;
        var hashFor = encounterHash ?? (_ => string.Empty);

        if (previous != null && previous.Path == path && previous.EncounterHash == hashFor(previous.Zones))
        {
            if (previous.Size == size && previous.MtimeTicks == mtime)
                return previous;
        }
        else
        {
            previous = null;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prefix = size > 0 ? PrefixHash(path) : string.Empty;
        var indexer = new LogIndexer(path, observers);
        long start = 0;
        if (previous?.Resume != null && size >= previous.ScannedBytes && previous.PrefixHash == prefix)
        {
            indexer.Resume(previous.Resume, previous.Pulls.Where(p => p.StartOffset < previous.Resume.Offset));
            start = previous.Resume.Offset;
        }

        var result = LogLineReader.Read(path, start, size, indexer.Filter, indexer, ct,
                                        progress == null ? null : off => progress(off, size));
        ct.ThrowIfCancellationRequested();
        indexer.Finish(result.EndOffset);

        var zones = new HashSet<uint>(previous?.Zones ?? []);
        foreach (var p in indexer.Pulls)
            zones.Add(p.ZoneId);
        var zoneList = zones.Order().ToList();
        return new FileIndex
        {
            Path = path,
            Size = size,
            MtimeTicks = mtime,
            ScannedBytes = result.EndOffset,
            PrefixHash = prefix,
            Zones = zoneList,
            EncounterHash = hashFor(zoneList),
            Pulls = indexer.Pulls,
            Resume = indexer.SafeResume,
            Lines = (previous?.Lines ?? 0) + result.LinesSeen,
            IndexSeconds = sw.Elapsed.TotalSeconds,
        };
    }
}
