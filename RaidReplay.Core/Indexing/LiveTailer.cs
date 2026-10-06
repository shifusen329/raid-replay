using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Indexing;

/// <summary>
/// Follows the newest ACT log while it is being written. Keeps one in-memory <see cref="LogIndexer"/> and feeds it
/// only the bytes appended since the last <see cref="Poll"/>, raising <see cref="PullEnded"/> the moment a pull's end
/// (director wipe/victory, combat-off grace, zone change) has been written.
/// </summary>
public sealed class LiveTailer
{
    private readonly string logsDir;
    private readonly string cacheDir;
    private readonly IPullObserverFactory? observers;
    private readonly object gate = new();
    private LogIndexer? indexer;
    private string? file;
    private long offset;

    public LiveTailer(string logsDir, string cacheDir, IPullObserverFactory? observers = null)
    {
        this.logsDir = logsDir;
        this.cacheDir = cacheDir;
        this.observers = observers;
    }

    /// <summary>Raised (on the polling thread) when a new pull starts.</summary>
    public event Action<PullSummary>? PullStarted;

    /// <summary>Raised (on the polling thread) when a pull has ended; its tail may still be incomplete.</summary>
    public event Action<PullSummary>? PullEnded;

    public string? CurrentFile => file;
    public long Offset => offset;
    public PullSummary? ActivePull => indexer?.ActivePull;
    public DateTime LastGrowthUtc { get; private set; }

    /// <summary>Reads newly appended lines; switches to a newer log file when ACT starts one.</summary>
    public void Poll(CancellationToken ct = default)
    {
        lock (gate)
        {
            var newest = LogLibrary.EnumerateLogs(logsDir).FirstOrDefault();
            if (newest == null)
                return;
            if (!string.Equals(newest, file, StringComparison.OrdinalIgnoreCase))
                Attach(newest, ct);
            if (indexer == null || file == null)
                return;

            var length = new FileInfo(file).Length;
            if (length < offset)
            {
                // Truncated/replaced: start over.
                Attach(file, ct);
                return;
            }

            if (length == offset)
                return;

            LastGrowthUtc = DateTime.UtcNow;
            var wasActive = indexer.ActivePull;
            var before = indexer.Pulls.Count;
            var result = LogLineReader.Read(file, offset, length, indexer.Filter, indexer, ct);
            offset = result.EndOffset;

            var active = indexer.ActivePull;
            if (active != null && !ReferenceEquals(active, wasActive))
                PullStarted?.Invoke(active);
            for (var i = before; i < indexer.Pulls.Count; i++)
                PullEnded?.Invoke(indexer.Pulls[i]);
        }
    }

    private void Attach(string path, CancellationToken ct)
    {
        file = path;
        indexer = new LogIndexer(path, observers);
        offset = 0;

        // Resume from the cached index's safe point so only the recent part of the file is rescanned.
        var cached = IndexStore.Load(cacheDir, path);
        var length = new FileInfo(path).Length;
        if (cached?.Resume != null && length >= cached.Resume.Offset && length > 0 &&
            cached.PrefixHash == IndexStore.PrefixHash(path))
        {
            indexer.Resume(cached.Resume, cached.Pulls.Where(p => p.StartOffset < cached.Resume.Offset));
            offset = cached.Resume.Offset;
        }

        // Catch up silently: pulls that ended before we attached are history, not "just ended".
        var result = LogLineReader.Read(path, offset, length, indexer.Filter, indexer, ct);
        offset = result.EndOffset;
    }
}
