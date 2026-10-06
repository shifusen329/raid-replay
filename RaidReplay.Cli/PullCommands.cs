using RaidReplay.Core.Indexing;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    private static FileIndex IndexOne(Options o, string path)
    {
        var (factory, hash) = EncounterSupport(o);
        var cached = o.Has("fresh") ? null : IndexStore.Load(o.CacheDir, path);
        var index = IndexStore.IndexFile(path, cached, factory, hash);
        if (!ReferenceEquals(index, cached))
            IndexStore.Save(o.CacheDir, index);
        return index;
    }

    private static int Pulls(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var index = IndexOne(o, path);
        Console.WriteLine($"{index.FileName}: {index.Pulls.Count} pulls (indexed in {index.IndexSeconds:N2}s, {index.Size / 1048576.0:N0} MB)");
        var all = o.Has("all");
        foreach (var p in index.Pulls)
        {
            if (!all && (!p.HasDirector || p.DurationMs < 5000))
                continue;
            Console.WriteLine(FormatPull(p));
        }

        var byOutcome = index.Pulls.Where(p => all || p.HasDirector).GroupBy(p => p.Outcome)
                             .Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine(string.Join(", ", byOutcome));
        return 0;
    }

    internal static string FormatPull(PullSummary p)
    {
        var flags = (p.StartTruncated ? "S" : "-") + (p.EndTruncated ? "E" : "-") + (p.CountdownTicks != 0 ? "C" : "-");
        var hp = p.BossHpPct >= 0 ? $"{p.BossHpPct,5:N1}%" : "     -";
        var phase = p.FurthestPhase ?? "";
        return $"#{p.Ordinal,-4} {p.StartLocal:MM-dd HH:mm:ss} {flags} {FormatDuration(p.DurationMs),8} {p.Outcome,-10} {p.ZoneName,-32} " +
               $"deaths={p.Deaths,-2} boss={p.BossName,-14} {hp} {phase}";
    }

    internal static string FormatDuration(int ms)
    {
        var neg = ms < 0;
        ms = Math.Abs(ms);
        var s = $"{ms / 60000}:{ms / 1000 % 60:00}.{ms / 100 % 10}";
        return neg ? "-" + s : s;
    }

    internal static PullSummary FindPull(Options o, int fileArg, int pullArg, out string path)
    {
        path = o.ResolveFile(o.Arg(fileArg, "file"));
        var index = IndexOne(o, path);
        var n = int.Parse(o.Arg(pullArg, "n"));
        return index.Pulls.FirstOrDefault(p => p.Ordinal == n) ?? throw new CliException($"pull #{n} not found");
    }
}
