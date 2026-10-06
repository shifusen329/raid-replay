using System.Diagnostics;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>rr watch [--interval ms]: tails the newest log and analyzes each wipe as soon as it ends.</summary>
    private static int Watch(Options o)
    {
        var reg = Registry(o);
        var data = GameDataFor(o);
        var tailer = new LiveTailer(o.LogsDir, o.CacheDir, new EncounterObserverFactory(reg));
        var interval = o.Int("interval", 1000);
        var flushDelay = o.Int("flush-delay", 1500);
        var once = o.Has("once");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var ended = new Queue<(PullSummary Pull, DateTime At)>();
        tailer.PullStarted += p => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] pull #{p.Ordinal} started in {p.ZoneName}");
        tailer.PullEnded += p =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] pull #{p.Ordinal} ended: {p.Outcome} after {FormatDuration(p.DurationMs)}");
            ended.Enqueue((p, DateTime.UtcNow));
        };

        var sw = Stopwatch.StartNew();
        tailer.Poll(cts.Token);
        Console.WriteLine($"watching {Path.GetFileName(tailer.CurrentFile)} (caught up to {tailer.Offset / 1048576.0:N1} MB in {sw.ElapsedMilliseconds} ms)");
        var analyzed = 0;
        while (!cts.IsCancellationRequested)
        {
            Thread.Sleep(interval);
            tailer.Poll(cts.Token);
            while (ended.Count > 0 && DateTime.UtcNow - ended.Peek().At >= TimeSpan.FromMilliseconds(flushDelay))
            {
                var (pull, at) = ended.Dequeue();
                tailer.Poll(cts.Token); // pick up lines flushed since the end
                sw.Restart();
                var replay = PullLoader.Load(pull, data, reg, cts.Token, readToEof: true);
                var profile = replay.Encounter != null ? PositionProfile.Load(o.CacheDir, replay.Encounter.Key) : null;
                var report = WipeAnalyzer.Analyze(replay, profile);
                var latency = (DateTime.UtcNow - at).TotalMilliseconds + flushDelay;
                Console.WriteLine($"  analysis ready {latency:N0} ms after the pull ended (load+analyze {sw.ElapsedMilliseconds} ms)");
                PrintReport(report, false);
                if (profile != null && pull.Outcome != PullOutcome.InProgress)
                {
                    profile.Add(PositionProfile.Extract(replay));
                    profile.Save(o.CacheDir);
                }

                analyzed++;
            }

            if (once && analyzed > 0)
                break;
        }

        return 0;
    }

    /// <summary>
    /// rr simulate &lt;file&gt; &lt;fromPull&gt; &lt;toPull&gt; --out &lt;dir&gt; [--chunk KB] [--delay ms]: writes a log into
    /// a folder progressively (as ACT would) so `rr watch --logs &lt;dir&gt;` can be tested without the game.
    /// </summary>
    private static int Simulate(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var index = IndexOne(o, path);
        var from = index.Pulls.First(p => p.Ordinal == int.Parse(o.Arg(1, "fromPull")));
        var to = index.Pulls.First(p => p.Ordinal == int.Parse(o.Arg(2, "toPull")));
        var outDir = o.Get("out") ?? throw new CliException("--out <dir> required");
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, Path.GetFileName(path));
        var chunk = o.Int("chunk", 256) * 1024;
        var delay = o.Int("delay", 50);

        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var buf = new byte[chunk];

        // Everything before the first pull's checkpoint appears instantly.
        Copy(src, dst, from.CheckpointOffset, buf);
        dst.Flush();
        Console.WriteLine($"wrote {from.CheckpointOffset / 1048576.0:N1} MB prefix; streaming to pull #{to.Ordinal} end...");
        Thread.Sleep(o.Int("start-delay", 3000));
        var end = to.TailEndOffset;
        while (src.Position < end)
        {
            Copy(src, dst, Math.Min(chunk, end - src.Position), buf);
            dst.Flush();
            Thread.Sleep(delay);
        }

        Console.WriteLine("done");
        return 0;
    }

    private static void Copy(Stream src, Stream dst, long count, byte[] buf)
    {
        while (count > 0)
        {
            var n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0)
                break;
            dst.Write(buf, 0, n);
            count -= n;
        }
    }
}
