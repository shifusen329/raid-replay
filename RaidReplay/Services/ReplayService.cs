using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Services;

/// <summary>
/// Owns all background work: indexing the log folder, tailing the live log, loading pulls, wipe analysis and
/// learning position profiles. UI code only reads the volatile snapshot properties.
/// </summary>
public sealed class ReplayService : IDisposable
{
    private readonly Configuration config;
    private readonly IGameData gameData;
    private readonly string cacheDir;
    private readonly string packsDir;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AutoResetEvent refreshSignal = new(false);
    private readonly AutoResetEvent liveSignal = new(false);
    private readonly ConcurrentQueue<(PullSummary Pull, DateTime At)> ended = new();
    private readonly Dictionary<string, PositionProfile> profiles = new();
    private readonly object profileGate = new();
    private CancellationTokenSource? loadCts;
    private Thread? indexThread;
    private Thread? liveThread;
    private Thread? learnThread;
    private volatile LogLibrary library;
    private volatile LiveTailer tailer;
    private volatile EncounterRegistry registry;

    public ReplayService(Configuration config, IGameData gameData, string configDir)
    {
        this.config = config;
        this.gameData = gameData;
        cacheDir = Path.Combine(configDir, "cache");
        packsDir = Path.Combine(configDir, "encounters");
        registry = EncounterRegistry.Load(packsDir);
        library = CreateLibrary();
        tailer = CreateTailer();
    }

    /// <summary>Raised on a background thread when a live wipe report is ready.</summary>
    public event Action<WipeReport>? LiveReportReady;

    public LogLibrary Library => library;
    public EncounterRegistry Registry => registry;
    public IGameData GameData => gameData;
    public string PacksDirectory => packsDir;

    public PullReplay? Current { get; private set; }
    public WipeReport? CurrentReport { get; private set; }
    public WipeReport? LastLiveReport { get; private set; }
    public PullSummary? Loading { get; private set; }
    public string? LoadError { get; private set; }
    public PullSummary? ActiveLivePull => tailer.ActivePull;
    public string? LiveFile => tailer.CurrentFile;
    public DateTime LastLogGrowthUtc => tailer.LastGrowthUtc;
    public string LearnStatus { get; private set; } = "idle";
    public string? LastError { get; private set; }

    /// <summary>Set from the framework thread; controls the live polling rate.</summary>
    public volatile bool InDuty;

    public void Start()
    {
        indexThread = StartThread("RaidReplay.Index", IndexLoop, ThreadPriority.BelowNormal);
        liveThread = StartThread("RaidReplay.Live", LiveLoop, ThreadPriority.Normal);
        learnThread = StartThread("RaidReplay.Learn", LearnLoop, ThreadPriority.Lowest);
    }

    private static Thread StartThread(string name, Action body, ThreadPriority priority)
    {
        var t = new Thread(() => body()) { Name = name, IsBackground = true, Priority = priority };
        t.Start();
        return t;
    }

    public void RequestRefresh() => refreshSignal.Set();

    public void NudgeLive() => liveSignal.Set();

    /// <summary>Re-reads encounter packs and rebuilds the library/tailer (indexes revalidate by pack hash).</summary>
    public void ReloadPacks()
    {
        registry = EncounterRegistry.Load(packsDir);
        library = CreateLibrary();
        tailer = CreateTailer();
        RequestRefresh();
    }

    public void ChangeLogsDirectory()
    {
        library = CreateLibrary();
        tailer = CreateTailer();
        RequestRefresh();
    }

    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, true);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }

        lock (profileGate)
            profiles.Clear();
        library = CreateLibrary();
        RequestRefresh();
    }

    private LogLibrary CreateLibrary() =>
        new(config.LogsDirectory, Path.Combine(cacheDir, "index"), new EncounterObserverFactory(registry), registry.HashFor);

    private LiveTailer CreateTailer()
    {
        var t = new LiveTailer(config.LogsDirectory, Path.Combine(cacheDir, "index"), new EncounterObserverFactory(registry));
        t.PullEnded += p => ended.Enqueue((p, DateTime.UtcNow));
        return t;
    }

    // ---- loading ---------------------------------------------------------------------------------------------

    public void Load(PullSummary summary, int? seekMs = null)
    {
        loadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loadCts = cts;
        Loading = summary;
        LoadError = null;
        Task.Run(() =>
        {
            try
            {
                var replay = PullLoader.Load(summary, gameData, registry, cts.Token, readToEof: summary.EndTruncated);
                var report = WipeAnalyzer.Analyze(replay, ProfileFor(replay));
                if (cts.IsCancellationRequested)
                    return;
                Current = replay;
                CurrentReport = report;
                PendingSeekMs = seekMs;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                LoadError = e.Message;
                Plugin.Log.Error(e, "Failed to load pull");
            }
            finally
            {
                if (ReferenceEquals(loadCts, cts))
                    Loading = null;
            }
        }, cts.Token);
    }

    /// <summary>A seek requested together with a load; consumed by the replay window.</summary>
    public int? PendingSeekMs { get; set; }

    public void ShowReport(WipeReport report)
    {
        Current = report.Pull;
        CurrentReport = report;
    }

    private PositionProfile? ProfileFor(PullReplay replay)
    {
        if (replay.Encounter == null)
            return null;
        lock (profileGate)
        {
            if (!profiles.TryGetValue(replay.Encounter.Key, out var p))
            {
                p = PositionProfile.Load(cacheDir, replay.Encounter.Key);
                profiles[replay.Encounter.Key] = p;
            }

            return p;
        }
    }

    // ---- background loops ------------------------------------------------------------------------------------

    private void IndexLoop()
    {
        var ct = lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                library.Refresh(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Plugin.Log.Error(e, "Indexing failed");
            }

            WaitHandle.WaitAny([refreshSignal, ct.WaitHandle], TimeSpan.FromSeconds(InDuty ? 20 : 60));
        }
    }

    private void LiveLoop()
    {
        var ct = lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            var interval = InDuty ? Math.Clamp(config.LivePollMs, 250, 5000) : 5000;
            WaitHandle.WaitAny([liveSignal, ct.WaitHandle], interval);
            if (ct.IsCancellationRequested || !config.LiveEnabled)
                continue;
            try
            {
                var t = tailer;
                t.Poll(ct);
                while (ended.TryPeek(out var next) && DateTime.UtcNow - next.At >= TimeSpan.FromMilliseconds(config.FlushDelayMs))
                {
                    ended.TryDequeue(out _);
                    t.Poll(ct); // pick up lines ACT flushed after the end
                    AnalyzeLive(next.Pull, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Plugin.Log.Error(e, "Live tailing failed");
            }
        }
    }

    private void AnalyzeLive(PullSummary pull, CancellationToken ct)
    {
        if (config.InstancedOnly && !pull.HasDirector)
            return;
        if (pull.DurationMs < config.MinPullSeconds * 1000)
            return;
        var replay = PullLoader.Load(pull, gameData, registry, ct, readToEof: true);
        var profile = ProfileFor(replay);
        var report = WipeAnalyzer.Analyze(replay, profile);
        LastLiveReport = report;
        if (config.AutoLoadLivePull)
        {
            Current = replay;
            CurrentReport = report;
            PendingSeekMs = report.RootCause?.T;
        }

        LiveReportReady?.Invoke(report);
        RequestRefresh();

        if (profile != null && config.LearnInBackground)
        {
            profile.Add(PositionProfile.Extract(replay));
            profile.Save(cacheDir);
        }
    }

    private void LearnLoop()
    {
        var ct = lifetime.Token;
        // Give indexing a head start.
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
        while (!ct.IsCancellationRequested)
        {
            if (!config.LearnInBackground)
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                continue;
            }

            try
            {
                var learned = 0;
                foreach (var pull in library.AllPulls.Where(p => p.EncounterKey != null && p.DurationMs > 10000 && !p.EndTruncated))
                {
                    if (ct.IsCancellationRequested)
                        return;
                    PositionProfile profile;
                    lock (profileGate)
                    {
                        if (!profiles.TryGetValue(pull.EncounterKey!, out profile!))
                            profiles[pull.EncounterKey!] = profile = PositionProfile.Load(cacheDir, pull.EncounterKey!);
                    }

                    if (profile.Contains(pull.Key))
                        continue;
                    LearnStatus = $"learning {pull.EncounterKey}: {profile.PullCount} pulls";
                    var replay = PullLoader.Load(pull, gameData, registry, ct);
                    profile.Add(PositionProfile.Extract(replay));
                    if (++learned % 20 == 0)
                        profile.Save(cacheDir);
                    Thread.Sleep(InDuty ? 200 : 20);
                }

                lock (profileGate)
                {
                    if (learned > 0)
                    {
                        foreach (var p in profiles.Values)
                            p.Save(cacheDir);
                    }

                    LearnStatus = string.Join(", ", profiles.Values.Select(p => $"{p.Encounter}: {p.PullCount} pulls"));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Plugin.Log.Error(e, "Profile learning failed");
            }

            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(60));
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        loadCts?.Cancel();
        refreshSignal.Set();
        liveSignal.Set();
        indexThread?.Join(2000);
        liveThread?.Join(2000);
        learnThread?.Join(2000);
        lifetime.Dispose();
        refreshSignal.Dispose();
        liveSignal.Dispose();
    }
}
