using RaidReplay.Core.Model;

namespace RaidReplay.Core.Indexing;

public enum FileIndexStatus
{
    Pending,
    Indexing,
    Ready,
    Failed,
}

public sealed class LogFileEntry
{
    public required string Path { get; init; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public FileIndexStatus Status { get; set; }
    public FileIndex? Index { get; set; }
    public string? Error { get; set; }
    public double Progress { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The set of ACT network logs in a folder and their cached indexes. <see cref="Refresh"/> is meant to run
/// on a background thread; readers use <see cref="Files"/> / <see cref="AllPulls"/>, which are replaced
/// atomically.
/// </summary>
public sealed class LogLibrary
{
    private readonly IPullObserverFactory? observers;
    private readonly Func<IEnumerable<uint>, string>? encounterHash;
    private volatile LogFileEntry[] files = [];
    private volatile PullSummary[] pulls = [];

    public LogLibrary(string logsDir, string cacheDir, IPullObserverFactory? observers = null,
                      Func<IEnumerable<uint>, string>? encounterHash = null)
    {
        LogsDir = logsDir;
        CacheDir = cacheDir;
        this.observers = observers;
        this.encounterHash = encounterHash;
    }

    public string LogsDir { get; }
    public string CacheDir { get; }
    public IReadOnlyList<LogFileEntry> Files => files;

    /// <summary>All pulls of all indexed files, newest first.</summary>
    public IReadOnlyList<PullSummary> AllPulls => pulls;

    public string? CurrentFile { get; private set; }
    public int Version { get; private set; }

    public static IEnumerable<string> EnumerateLogs(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "Network_*.log").OrderByDescending(File.GetLastWriteTimeUtc)
            : [];

    /// <summary>Scans the folder and (re)indexes stale files, newest first.</summary>
    public void Refresh(CancellationToken ct, int maxFiles = int.MaxValue)
    {
        var existing = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var list = new List<LogFileEntry>();
        foreach (var path in EnumerateLogs(LogsDir).Take(maxFiles))
        {
            var info = new FileInfo(path);
            if (!existing.TryGetValue(path, out var entry))
            {
                entry = new LogFileEntry { Path = path, Status = FileIndexStatus.Pending };
                entry.Index = IndexStore.Load(CacheDir, path);
            }

            if (entry.Size != info.Length || entry.LastWriteUtc != info.LastWriteTimeUtc || entry.Index == null ||
                entry.Status != FileIndexStatus.Ready)
                entry.Status = FileIndexStatus.Pending;
            entry.Size = info.Length;
            entry.LastWriteUtc = info.LastWriteTimeUtc;
            list.Add(entry);
        }

        files = list.ToArray();
        Publish();

        foreach (var entry in list)
        {
            if (ct.IsCancellationRequested)
                return;
            if (entry.Status == FileIndexStatus.Ready)
                continue;

            CurrentFile = entry.Path;
            entry.Status = FileIndexStatus.Indexing;
            try
            {
                var index = IndexStore.IndexFile(entry.Path, entry.Index, observers, encounterHash, ct,
                                                 (done, total) => entry.Progress = total > 0 ? (double)done / total : 0);
                if (!ReferenceEquals(index, entry.Index))
                    IndexStore.Save(CacheDir, index);
                entry.Index = index;
                entry.Status = FileIndexStatus.Ready;
                entry.Error = null;
            }
            catch (OperationCanceledException)
            {
                entry.Status = FileIndexStatus.Pending;
                return;
            }
            catch (Exception e)
            {
                entry.Status = FileIndexStatus.Failed;
                entry.Error = e.Message;
            }
            finally
            {
                entry.Progress = 1;
                CurrentFile = null;
            }

            Publish();
        }
    }

    /// <summary>Forces the given file (or all files) to be reindexed on the next refresh.</summary>
    public void Invalidate(string? path = null)
    {
        foreach (var f in files)
        {
            if (path == null || string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                f.Index = null;
                f.Status = FileIndexStatus.Pending;
            }
        }
    }

    private void Publish()
    {
        var all = new List<PullSummary>();
        foreach (var f in files)
        {
            if (f.Index != null)
                all.AddRange(f.Index.Pulls);
        }

        all.Sort((a, b) => b.StartTicks.CompareTo(a.StartTicks));
        pulls = all.ToArray();
        Version++;
    }
}
